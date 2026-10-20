package syncclient

import (
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"klip/internal/model"
)

// A server that cannot answer is not a reason to sign the user out: the
// tokens stay and the caller sees the failure.
func TestSessionRefreshKeepsTheTokensWhenTheServerIsUnwell(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusServiceUnavailable)
		io.WriteString(w, `{"error":"down for maintenance"}`)
	}))
	defer srv.Close()

	s := model.DefaultSettings()
	s.AuthAccessToken, s.AuthRefreshToken = model.Ptr("access"), model.Ptr("refresh")
	s.AuthExpiresAtUnixMs = 1234
	sess := &Session{
		Client:   New(srv.URL),
		Settings: Settings{Get: func() model.Settings { return s }, Update: func(f func(*model.Settings)) { f(&s) }},
	}

	ok, err := sess.Refresh(context.Background())
	if ok || err == nil {
		t.Fatalf("a 503: %v %v", ok, err)
	}
	if model.Str(s.AuthRefreshToken) != "refresh" || model.Str(s.AuthAccessToken) != "access" || s.AuthExpiresAtUnixMs != 1234 {
		t.Errorf("the tokens were cleared: %+v", s)
	}
}

// Two sessions of one account renew together: the server revokes the refresh
// token when it is used, so the second must wait and use the new tokens.
func TestSessionRefreshIsCoordinatedAcrossSessions(t *testing.T) {
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		io.WriteString(w, `{"accessToken":"new-access","refreshToken":"new-refresh","email":"me@x.com",`+
			`"accessTokenExpiresAt":"2030-01-01T00:00:00+00:00"}`)
	}))
	defer srv.Close()

	var mu sync.Mutex
	s := model.DefaultSettings()
	s.AuthAccessToken, s.AuthRefreshToken = model.Ptr("old-access"), model.Ptr("old-refresh")
	s.AuthExpiresAtUnixMs = time.Now().Add(-time.Hour).UnixMilli()
	settings := Settings{
		Get:    func() model.Settings { mu.Lock(); defer mu.Unlock(); return s },
		Update: func(f func(*model.Settings)) { mu.Lock(); defer mu.Unlock(); f(&s) },
	}
	session := func() *Session { return &Session{Client: New(srv.URL), Settings: settings} }

	var wg sync.WaitGroup
	tokens := make([]string, 2)
	errs := make([]error, 2)
	for i := range tokens {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			tokens[i], errs[i] = session().AccessToken(context.Background())
		}(i)
	}
	wg.Wait()

	if n := calls.Load(); n != 1 {
		t.Errorf("the refresh token was spent %d times", n)
	}
	for i := range tokens {
		if errs[i] != nil || tokens[i] != "new-access" {
			t.Errorf("session %d: %q %v", i, tokens[i], errs[i])
		}
	}
	if got := model.Str(s.AuthRefreshToken); got != "new-refresh" {
		t.Errorf("stored refresh token %q", got)
	}
}

// Account calls are bounded: a server that accepts the request and never
// answers must not hold the sync gate forever.
func TestAccountCallsHaveADeadline(t *testing.T) {
	release := make(chan struct{})
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		<-release
	}))
	defer srv.Close()
	defer close(release)

	c := New(srv.URL)
	c.accountTimeout = 30 * time.Millisecond
	started := time.Now()
	if _, err := c.Login(context.Background(), "a@b.com", "pw"); err == nil {
		t.Fatal("a hanging account call must fail")
	}
	if d := time.Since(started); d > time.Second {
		t.Errorf("the call took %v", d)
	}
	if _, err := c.CurrentEmail(context.Background(), "token"); err == nil {
		t.Error("a hanging /auth/me must fail too")
	}
}

// A blob check that cannot be read, or that has no list, must not look like
// "all blobs are there": the snapshot would be published without them.
func TestCheckMissingBlobsRejectsMalformedResponses(t *testing.T) {
	for _, body := range []string{
		`{"missing":`,
		`{}`,
		`{"missing":null}`,
		`{"missing":"aa"}`,
	} {
		t.Run(body, func(t *testing.T) {
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				io.WriteString(w, body)
			}))
			defer srv.Close()
			if _, err := New(srv.URL).CheckMissingBlobs(bg, "t", []string{"aa"}); err == nil {
				t.Errorf("%s: an invalid answer was taken as an empty list", body)
			}
		})
	}
}
