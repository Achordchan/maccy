package syncclient

import (
	"context"
	"errors"
	"strings"
	"time"

	"klip/internal/model"
)

// Settings is where a Session keeps the sign-in: the app's settings, behind
// two functions so that the owner can lock and save them.
type Settings struct {
	Get    func() model.Settings
	Update func(func(*model.Settings))
}

// Session is the signed-in state kept in the settings: it hands out a valid
// access token and renews the tokens, as SyncEventStreamService and
// SyncService do. It is a TokenSource.
type Session struct {
	Client   *Client
	Settings Settings
	// Now is time.Now unless a test sets it.
	Now func() time.Time
	// BaseURL is the address to refresh against (the official server).
	BaseURL string
}

func (s *Session) now() time.Time {
	if s.Now != nil {
		return s.Now()
	}
	return time.Now()
}

// AccessToken returns the stored token when it has more than 30 seconds
// left, else renews it.
func (s *Session) AccessToken(ctx context.Context) (string, error) {
	cur := s.Settings.Get()
	access := strings.TrimSpace(model.Str(cur.AuthAccessToken))
	if access != "" && cur.AuthExpiresAtUnixMs > s.now().UnixMilli()+30_000 {
		return access, nil
	}
	ok, err := s.Refresh(ctx)
	if err != nil || !ok {
		return "", err
	}
	return model.Str(s.Settings.Get().AuthAccessToken), nil
}

// refreshMu lets one refresh at a time use the refresh token: the server
// revokes it when it is used, so a second request with the same token would be
// refused and could sign the user out of a session that was just renewed.
var refreshSem = make(chan struct{}, 1)

// refreshTimeout bounds one refresh request, and so how long the lock is held.
const refreshTimeout = 30 * time.Second

// LockRefresh takes the refresh lock that Session.Refresh takes; whoever else
// refreshes the stored tokens (the account window) holds it too, and looks at
// the stored tokens again after getting it. Waiting for it ends with ctx.
func LockRefresh(ctx context.Context) (unlock func(), err error) {
	select {
	case refreshSem <- struct{}{}:
		return func() { <-refreshSem }, nil
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

// Refresh trades the refresh token for new tokens and stores them. A refusal
// from the server signs the user out (the tokens are cleared) and says
// false; a network error leaves everything as it is and returns the error.
func (s *Session) Refresh(ctx context.Context) (bool, error) {
	ctx, cancel := context.WithTimeout(ctx, refreshTimeout)
	defer cancel()
	unlock, lerr := LockRefresh(ctx)
	if lerr != nil {
		return false, lerr
	}
	defer unlock()
	cur := s.Settings.Get()
	refresh := strings.TrimSpace(model.Str(cur.AuthRefreshToken))
	if refresh == "" {
		return false, nil
	}
	token, err := s.Client.Refresh(ctx, refresh)
	if err != nil {
		if ctx.Err() != nil {
			return false, ctx.Err()
		}
		var ae *APIError
		if !errors.As(err, &ae) {
			return false, err // no answer: not a reason to sign out
		}
		s.Settings.Update(func(m *model.Settings) {
			if strings.TrimSpace(model.Str(m.AuthRefreshToken)) == refresh { // still the session that asked
				ClearAuthSession(m, false)
			}
		})
		return false, nil
	}
	stored := false
	s.Settings.Update(func(m *model.Settings) {
		if strings.TrimSpace(model.Str(m.AuthRefreshToken)) != refresh {
			return // signed out, or in as somebody else, while the request was on its way
		}
		stored = true
		m.AuthAccessToken = &token.AccessToken
		if strings.TrimSpace(token.RefreshToken) != "" {
			m.AuthRefreshToken = &token.RefreshToken
		}
		if strings.TrimSpace(token.IDToken) != "" {
			m.AuthIdToken = &token.IDToken
		}
		m.AuthExpiresAtUnixMs = token.ExpiresAt.UnixMilli()
		if strings.TrimSpace(token.Email) != "" {
			m.AuthUserEmail = &token.Email
		}
		m.NasAgentBaseUrl = model.OfficialSyncBaseUrl
	})
	return stored, nil
}
