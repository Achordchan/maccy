package syncclient

import (
	"context"
	"errors"
	"strings"
	"sync"
	"time"

	"klip/internal/model"
)

// refreshMu serializes token renewals of the process. Sessions that share
// the account (the syncer's and the event stream's) read the same refresh
// token; the server revokes it on first use, so the second request would
// fail with 401 and could clear the tokens the first one just stored.
var refreshMu sync.Mutex

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

// Refresh trades the refresh token for new tokens and stores them. A refusal
// of the refresh token itself signs the user out (the tokens are cleared)
// and says false; a temporary failure, and a network error, leave everything
// as it is and return the error.
//
// Renewals of sessions that share the account are serialized, and a session
// that waited for another one uses the tokens it stored instead of spending
// a refresh token that is already gone.
func (s *Session) Refresh(ctx context.Context) (bool, error) {
	before := s.Settings.Get()
	refresh := strings.TrimSpace(model.Str(before.AuthRefreshToken))
	if refresh == "" {
		return false, nil
	}

	refreshMu.Lock()
	defer refreshMu.Unlock()

	// Another session may have renewed while this one waited.
	cur := s.Settings.Get()
	access := strings.TrimSpace(model.Str(cur.AuthAccessToken))
	if access != "" && access != strings.TrimSpace(model.Str(before.AuthAccessToken)) &&
		cur.AuthExpiresAtUnixMs > s.now().UnixMilli()+30_000 {
		return true, nil
	}
	refresh = strings.TrimSpace(model.Str(cur.AuthRefreshToken))
	if refresh == "" {
		return false, nil
	}

	token, err := s.Client.Refresh(ctx, refresh)
	if err != nil {
		if ctx.Err() != nil {
			return false, ctx.Err()
		}
		var ae *APIError
		if !errors.As(err, &ae) || !refreshRejected(ae) {
			return false, err // no answer, or a server that is unwell: keep the tokens
		}
		s.Settings.Update(func(m *model.Settings) { ClearAuthSession(m, false) })
		return false, nil
	}
	s.Settings.Update(func(m *model.Settings) {
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
	return true, nil
}

// refreshRejected says the server refused the refresh token itself: 400 for
// a malformed one, 401 for an unknown, revoked or expired one. Anything else
// (429, 5xx, a proxy error) is temporary and must not sign the user out.
func refreshRejected(e *APIError) bool {
	return e.StatusCode == 400 || e.StatusCode == 401
}
