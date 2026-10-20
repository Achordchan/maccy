package app

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"klip/internal/model"
	"klip/internal/nastest"
	"klip/internal/paths"
	"klip/internal/settings"
	"klip/internal/syncclient"
	"klip/internal/syncer"
)

type fakeClip struct {
	mu    sync.Mutex
	seq   atomic.Int64
	text  string
	wrote string
}

func (c *fakeClip) Formats() []string {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.text != "" {
		return []string{"text/plain"}
	}
	return nil
}
func (c *fakeClip) ReadText() string             { c.mu.Lock(); defer c.mu.Unlock(); return c.text }
func (c *fakeClip) ReadFiles() ([]string, error) { return nil, nil }
func (c *fakeClip) ReadImage() []byte            { return nil }
func (c *fakeClip) WriteText(s string) error {
	c.mu.Lock()
	c.text, c.wrote = s, s
	c.mu.Unlock()
	c.seq.Add(1)
	return nil
}
func (c *fakeClip) WriteFiles([]string) error       { return nil }
func (c *fakeClip) WriteImage([]byte, string) error { return nil }

// copy is another program copying text.
func (c *fakeClip) copy(s string) {
	c.mu.Lock()
	c.text = s
	c.mu.Unlock()
	c.seq.Add(1)
}

func newApp(t *testing.T, root string, clip *fakeClip, base string) *App {
	t.Helper()
	a, err := New(Config{
		Layout:    paths.Layout{Root: root},
		Clipboard: clip,
		ChangeSeq: clip.seq.Load,
		Interval:  5 * time.Millisecond,
		BaseURL:   base,
		Logf:      t.Logf,
	})
	if err != nil {
		t.Fatal(err)
	}
	return a
}

func waitFor(t *testing.T, what string, f func() bool) {
	t.Helper()
	deadline := time.Now().Add(10 * time.Second)
	for time.Now().Before(deadline) {
		if f() {
			return
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for %s", what)
}

func top(a *App) string {
	if g := texts(a); len(g) > 0 {
		return g[0]
	}
	return ""
}

func texts(a *App) []string {
	var out []string
	for _, it := range a.History.Items() {
		out = append(out, model.Str(it.Text))
	}
	return out
}

func TestCaptureSurvivesRestart(t *testing.T) {
	root := t.TempDir()
	clip := &fakeClip{}
	a := newApp(t, root, clip, "http://127.0.0.1:1") // no server: sync is off without a login
	a.Start()
	time.Sleep(40 * time.Millisecond) // the watcher records the counter
	clip.copy("first")
	waitFor(t, "first captured", func() bool { return len(texts(a)) == 1 })
	clip.copy("second")
	waitFor(t, "second captured", func() bool { return len(texts(a)) == 2 })
	a.Stop()

	if _, err := os.Stat(filepath.Join(root, "history.json")); err != nil {
		t.Fatalf("history.json not written: %v", err)
	}
	b := newApp(t, root, &fakeClip{}, "http://127.0.0.1:1")
	if got := texts(b); len(got) != 2 || got[0] != "second" || got[1] != "first" {
		t.Fatalf("after restart: %v", got)
	}
	// What was on the clipboard before start is not captured.
	pre := &fakeClip{text: "was there before"}
	c := newApp(t, t.TempDir(), pre, "http://127.0.0.1:1")
	c.Start()
	time.Sleep(60 * time.Millisecond)
	c.Stop()
	if len(texts(c)) != 0 {
		t.Fatalf("captured the clipboard from before the start: %v", texts(c))
	}
}

func TestApplyMovesToTopAndIsNotCapturedBack(t *testing.T) {
	clip := &fakeClip{}
	a := newApp(t, t.TempDir(), clip, "http://127.0.0.1:1")
	a.Start()
	defer a.Stop()
	time.Sleep(40 * time.Millisecond)
	for _, s := range []string{"a", "b", "c"} {
		clip.copy(s)
		want := s
		waitFor(t, want, func() bool { return top(a) == want })
	}
	var itemA model.Item
	for _, it := range a.History.Items() {
		if model.Str(it.Text) == "a" {
			itemA = it
		}
	}
	a.Apply(itemA)
	clip.mu.Lock()
	wrote := clip.wrote
	clip.mu.Unlock()
	if wrote != "a" {
		t.Fatalf("clipboard got %q", wrote)
	}
	if got := texts(a); got[0] != "a" || len(got) != 3 {
		t.Fatalf("applied item not on top: %v", got)
	}
	time.Sleep(500 * time.Millisecond)
	// Applying is a use (Touch counts one more copy); a capture of our own
	// write would count another.
	if it := a.History.Items()[0]; it.CopyCount != 2 {
		t.Fatalf("copy count %d, want 2 (one use, no capture of our own write)", it.CopyCount)
	}
	if len(texts(a)) != 3 {
		t.Fatalf("history: %v", texts(a))
	}
}

func TestSettingsChangesTakeEffect(t *testing.T) {
	clip := &fakeClip{}
	root := t.TempDir()
	a := newApp(t, root, clip, "http://127.0.0.1:1")
	a.Start()
	defer a.Stop()
	time.Sleep(40 * time.Millisecond)
	for _, s := range []string{"1", "2", "3", "4"} {
		clip.copy(s)
		want := s
		waitFor(t, want, func() bool { return top(a) == want })
	}
	_ = a.Settings.Update(func(s *model.Settings) { s.MaxItems = 2 })
	// As in C#, the list is trimmed by the next change, not at once.
	clip.copy("5")
	waitFor(t, "5", func() bool { return top(a) == "5" })
	if got := texts(a); len(got) != 2 || got[1] != "4" {
		t.Fatalf("limit not applied: %v", got)
	}
	_ = a.Settings.Update(func(s *model.Settings) { s.CaptureText = false })
	clip.copy("ignored")
	time.Sleep(100 * time.Millisecond)
	if got := texts(a); got[0] != "5" {
		t.Fatalf("captured with text capture off: %v", got)
	}
	data, _ := os.ReadFile(filepath.Join(root, "settings.json"))
	if !strings.Contains(string(data), `"MaxItems":2`) || !strings.Contains(string(data), `"CaptureText":false`) {
		t.Fatalf("settings.json: %s", data)
	}
}

func TestSyncNowWithoutLogin(t *testing.T) {
	var changed atomic.Int32
	a, _ := New(Config{Layout: paths.Layout{Root: t.TempDir()}, Clipboard: &fakeClip{}, Changed: func() { changed.Add(1) }, Logf: t.Logf, BaseURL: "http://127.0.0.1:1"})
	a.SyncNow()
	waitFor(t, "status", func() bool { return a.SyncStatus() == "请先登录" })
	if changed.Load() == 0 {
		t.Fatal("the windows were not told")
	}
}

func TestBrokenFilesGiveDefaults(t *testing.T) {
	root := t.TempDir()
	_ = os.WriteFile(filepath.Join(root, "settings.json"), []byte("{broken"), 0o644)
	_ = os.WriteFile(filepath.Join(root, "history.json"), []byte("[broken"), 0o644)
	a := newApp(t, root, &fakeClip{}, "http://127.0.0.1:1")
	if a.Settings.Get().MaxItems != 200 || a.History.Len() != 0 {
		t.Fatal("not the defaults")
	}
	// The files are not overwritten until something changes.
	if b, _ := os.ReadFile(filepath.Join(root, "history.json")); string(b) != "[broken" {
		t.Fatalf("history.json was touched: %q", b)
	}
}

// Two programs, one account, the real server: a copy on A shows up on B.
func TestTwoDevicesThroughServer(t *testing.T) {
	srv := nastest.Start(t)
	ctx := context.Background()
	tokA := srv.Subscribe(ctx, "owner@example.com", "password1")
	tokB, err := syncclient.New(srv.URL).Login(ctx, "owner@example.com", "password1")
	if err != nil {
		t.Fatal(err)
	}
	signedIn := func(root string, tok syncclient.TokenSet) {
		st := model.DefaultSettings()
		st.AuthAccessToken, st.AuthRefreshToken = &tok.AccessToken, &tok.RefreshToken
		st.AuthExpiresAtUnixMs = tok.ExpiresAt.UnixMilli()
		if err := settings.Save(filepath.Join(root, "settings.json"), st); err != nil {
			t.Fatal(err)
		}
	}
	rootA, rootB := t.TempDir(), t.TempDir()
	signedIn(rootA, tokA)
	signedIn(rootB, tokB)

	clipA, clipB := &fakeClip{}, &fakeClip{}
	a := newApp(t, rootA, clipA, srv.URL)
	a.Start()
	defer a.Stop()
	waitFor(t, "A's first sync", func() bool { return strings.HasPrefix(a.SyncStatus(), "已同步") })
	remote := syncclient.New(srv.URL)
	// An empty history has nothing to upload: the server has no snapshot yet.
	m0, _ := remote.GetManifestJSON(ctx, tokA.AccessToken)

	time.Sleep(40 * time.Millisecond)
	clipA.copy("from device A")
	waitFor(t, "A captured", func() bool { return len(texts(a)) == 1 })

	b := newApp(t, rootB, clipB, srv.URL)
	// B starts only after A's change has gone up (the pause is 1.2 s).
	waitFor(t, "A uploaded", func() bool {
		m, err := remote.GetManifestJSON(ctx, tokA.AccessToken)
		return err == nil && m != "" && m != m0
	})
	b.Start()
	defer b.Stop()
	waitFor(t, "B downloaded", func() bool {
		g := texts(b)
		return len(g) == 1 && g[0] == "from device A"
	})

	// And back: B copies, A learns of it through the event stream.
	time.Sleep(40 * time.Millisecond)
	clipB.copy("from device B")
	waitFor(t, "A got B's copy", func() bool {
		g := texts(a)
		return len(g) == 2 && g[0] == "from device B"
	})
}

// Signing in through the account service, then telling the app: it syncs at
// once, without a restart.
func TestSignInStartsSync(t *testing.T) {
	srv := nastest.Start(t)
	ctx := context.Background()
	clip := &fakeClip{}
	a := newApp(t, t.TempDir(), clip, srv.URL)
	a.Start()
	defer a.Stop()
	time.Sleep(40 * time.Millisecond)
	clip.copy("before signing in")
	waitFor(t, "captured", func() bool { return len(texts(a)) == 1 })
	if a.SyncStatus() != "" {
		t.Fatalf("status while signed out: %q", a.SyncStatus())
	}
	if r := a.Account.Register(ctx, "new@example.com", "password1"); !r.OK {
		t.Fatal(r)
	}
	srv.Admin("/admin/user/upgrade", map[string]any{"emailOrUserId": "new@example.com", "durationDays": 30, "tier": "Mini"})
	a.AccountChanged(true)
	waitFor(t, "synced", func() bool { return strings.HasPrefix(a.SyncStatus(), "已同步") })
	m, err := syncclient.New(srv.URL).GetManifestJSON(ctx, model.Str(a.Settings.Get().AuthAccessToken))
	if err != nil || m == "" {
		t.Fatalf("nothing uploaded after sign-in: %q %v", m, err)
	}
}

func TestPrefsAndAccountAreWired(t *testing.T) {
	a := newApp(t, t.TempDir(), &fakeClip{}, "http://127.0.0.1:1")
	if a.Account == nil || a.Prefs == nil {
		t.Fatal("not wired")
	}
	if e := a.Prefs.SetMaxItems("7"); !e.Changed || a.History.Limits().MaxItems == nil || *a.History.Limits().MaxItems != 7 {
		t.Fatalf("a setting changed in the preferences must reach the history: %+v", e)
	}
}

// A newer version on the manifest is found a little after the start, quietly,
// and the files of the old program are cleaned once.
func TestStartChecksForUpdatesAndCleansLegacyFiles(t *testing.T) {
	manifest := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte(`{"appId":"maccy","latest":{"version":"99.0.0","notes":"n","installer":{"url":"http://x/setup.exe"}}}`))
	}))
	defer manifest.Close()

	root := t.TempDir()
	install := t.TempDir()
	exe := filepath.Join(install, "maccy.exe")
	_ = os.WriteFile(exe, []byte("exe"), 0o755)
	_ = os.WriteFile(filepath.Join(install, "maccy.dll"), []byte("net"), 0o644)
	_ = os.WriteFile(filepath.Join(install, "settings.json"), []byte("{}"), 0o644)
	_ = os.MkdirAll(filepath.Join(root, "updates", "helper"), 0o755)
	_ = os.WriteFile(filepath.Join(root, "updates", "helper", "old.exe"), []byte("x"), 0o755)
	list := `{"appId":"maccy","version":"1.0.11","runtime":"win-x64","files":[{"path":"maccy.exe"}],"legacyFiles":["maccy.dll","settings.json"]}`
	_ = os.WriteFile(filepath.Join(root, "updates", "installed-package-files.json"), []byte(list), 0o644)

	var changed atomic.Int32
	a, err := New(Config{Layout: paths.Layout{Root: root}, Clipboard: &fakeClip{}, ChangeSeq: (&fakeClip{}).seq.Load,
		Interval: 5 * time.Millisecond, BaseURL: "http://127.0.0.1:1", Logf: t.Logf,
		Exe: exe, ManifestURL: manifest.URL, UpdateFirst: 20 * time.Millisecond, UpdateEvery: time.Hour,
		Changed: func() { changed.Add(1) }})
	if err != nil {
		t.Fatal(err)
	}
	a.Start()
	defer a.Stop()
	waitFor(t, "update found", func() bool { return a.Updates.Available() != nil })
	if v := a.Updates.Available().Version; v != "99.0.0" {
		t.Fatalf("version %q", v)
	}
	if changed.Load() == 0 {
		t.Fatal("the windows were not told")
	}
	waitFor(t, "legacy cleanup", func() bool { _, err := os.Stat(filepath.Join(install, "maccy.dll")); return err != nil })
	if b, _ := os.ReadFile(filepath.Join(install, "settings.json")); string(b) != "{}" {
		t.Fatal("data removed")
	}
	waitFor(t, "helper dir cleaned", func() bool { _, err := os.Stat(filepath.Join(root, "updates", "helper")); return err != nil })
}

func TestFirstSyncIsTriedAgainUntilItGoesThrough(t *testing.T) {
	a, err := New(Config{
		Layout: paths.Layout{Root: t.TempDir()}, Clipboard: &fakeClip{}, ChangeSeq: (&fakeClip{}).seq.Load,
		Interval: 5 * time.Millisecond, BaseURL: "http://127.0.0.1:1", Logf: t.Logf,
		StartupRetry: 10 * time.Millisecond,
	})
	if err != nil {
		t.Fatal(err)
	}
	var runs atomic.Int32
	a.Auto.CanSync = func() bool { return true }
	a.Auto.Run = func(ctx context.Context, reason string, manual bool) error {
		if runs.Add(1) < 3 {
			return errors.New("offline") // the first two tries find no network
		}
		return nil
	}
	a.Start()
	defer a.Stop()
	waitFor(t, "third try", func() bool { return runs.Load() >= 3 })
	waitFor(t, "initial sync counted", func() bool { return a.initialOK.Load() })
	time.Sleep(100 * time.Millisecond)
	if n := runs.Load(); n != 3 {
		t.Fatalf("%d syncs: once one has gone through, the retries stop", n)
	}
}

func TestLoginSyncAskedForDuringASyncIsNotLost(t *testing.T) {
	a, err := New(Config{
		Layout: paths.Layout{Root: t.TempDir()}, Clipboard: &fakeClip{}, ChangeSeq: (&fakeClip{}).seq.Load,
		Interval: 5 * time.Millisecond, BaseURL: "http://127.0.0.1:1", Logf: t.Logf,
		StartupRetry: time.Hour,
	})
	if err != nil {
		t.Fatal(err)
	}
	release := make(chan struct{})
	var mu sync.Mutex
	var reasons []string
	a.Auto.CanSync = func() bool { return true }
	a.Auto.Run = func(ctx context.Context, reason string, manual bool) error {
		mu.Lock()
		reasons = append(reasons, reason)
		first := len(reasons) == 1
		mu.Unlock()
		if first {
			<-release // a long sync of the old account
		}
		return nil
	}
	a.Start()
	defer a.Stop()
	waitFor(t, "first sync running", func() bool { mu.Lock(); defer mu.Unlock(); return len(reasons) == 1 })
	a.AccountChanged(true) // a new sign-in while that runs: its sync is turned away
	close(release)
	waitFor(t, "login sync after it", func() bool {
		mu.Lock()
		defer mu.Unlock()
		return len(reasons) >= 2 && reasons[len(reasons)-1] == "login"
	})
}

func TestChangeDuringASyncIsSyncedAfterIt(t *testing.T) {
	a, err := New(Config{
		Layout: paths.Layout{Root: t.TempDir()}, Clipboard: &fakeClip{}, ChangeSeq: (&fakeClip{}).seq.Load,
		Interval: 5 * time.Millisecond, BaseURL: "http://127.0.0.1:1", Logf: t.Logf,
		StartupRetry: time.Hour,
	})
	if err != nil {
		t.Fatal(err)
	}
	a.Auto.Debounce = 20 * time.Millisecond
	release := make(chan struct{})
	var mu sync.Mutex
	var reasons []string
	a.Auto.CanSync = func() bool { return true }
	a.Auto.Run = func(ctx context.Context, reason string, manual bool) error {
		a.syncing.Add(1) // as the Run that New installs does
		defer a.syncing.Add(-1)
		mu.Lock()
		reasons = append(reasons, reason)
		first := len(reasons) == 1
		mu.Unlock()
		if first {
			<-release
		}
		return nil
	}
	a.Start()
	defer a.Stop()
	waitFor(t, "first sync running", func() bool { mu.Lock(); defer mu.Unlock(); return len(reasons) == 1 })
	a.History.Add(model.Item{ID: "x", Kind: model.KindText, Text: model.Ptr("new while syncing"), CopyCount: 1})
	close(release)
	waitFor(t, "a follow-up sync for the change", func() bool {
		mu.Lock()
		defer mu.Unlock()
		return len(reasons) >= 2
	})
}

func TestAccountChangeCancelsTheSyncOfTheOldAccount(t *testing.T) {
	a, err := New(Config{
		Layout: paths.Layout{Root: t.TempDir()}, Clipboard: &fakeClip{}, ChangeSeq: (&fakeClip{}).seq.Load,
		Interval: 5 * time.Millisecond, BaseURL: "http://127.0.0.1:1", Logf: t.Logf,
		StartupRetry: time.Hour,
	})
	if err != nil {
		t.Fatal(err)
	}
	started := make(chan context.Context, 1)
	a.Auto.CanSync = func() bool { return true }
	a.Auto.Run = func(ctx context.Context, reason string, manual bool) error {
		a.syncing.Add(1)
		a.runActive.Store(true)
		a.runGen.Store(a.acctGen.Load())
		select {
		case started <- ctx:
		default:
		}
		<-ctx.Done() // a long sync of the old account, until it is told to stop
		return ctx.Err()
	}
	a.Start()
	defer a.Stop()
	var ctx context.Context
	select {
	case ctx = <-started:
	case <-time.After(5 * time.Second):
		t.Fatal("no sync started")
	}
	a.endAccountWork() // what the account service does just before it replaces the login
	if a.acct().Err() == nil {
		t.Fatal("between the old login and the new one no account work may start")
	}
	a.beginAccountWork() // and once the new one is stored
	if a.acct().Err() != nil {
		t.Fatal("the new account must get a live context")
	}
	a.AccountChanged(true)
	select {
	case <-ctx.Done():
	case <-time.After(5 * time.Second):
		t.Fatal("the sync of the old account was left running")
	}
}

func TestFailedLaterSyncIsTriedAgain(t *testing.T) {
	a, err := New(Config{
		Layout: paths.Layout{Root: t.TempDir()}, Clipboard: &fakeClip{}, ChangeSeq: (&fakeClip{}).seq.Load,
		Interval: 5 * time.Millisecond, BaseURL: "http://127.0.0.1:1", Logf: t.Logf,
		StartupRetry: 10 * time.Millisecond,
	})
	if err != nil {
		t.Fatal(err)
	}
	var runs atomic.Int32
	a.Auto.CanSync = func() bool { return true }
	a.Auto.Debounce = 5 * time.Millisecond
	a.Auto.Run = func(ctx context.Context, reason string, manual bool) error {
		if runs.Add(1) == 2 {
			return errors.New("offline") // the upload of a copied item finds no network
		}
		return nil
	}
	a.Start()
	defer a.Stop()
	waitFor(t, "initial sync counted", func() bool { return a.initialOK.Load() })
	a.Auto.Trigger(a.acct(), "auto", false)
	waitFor(t, "the failed sync is tried again", func() bool { return runs.Load() >= 3 })
}

func TestManualSyncTurnedAwayDoesNotLeaveTheFooterSyncing(t *testing.T) {
	a, err := New(Config{
		Layout: paths.Layout{Root: t.TempDir()}, Clipboard: &fakeClip{}, ChangeSeq: (&fakeClip{}).seq.Load,
		BaseURL: "http://127.0.0.1:1", Logf: t.Logf, StartupRetry: time.Hour,
	})
	if err != nil {
		t.Fatal(err)
	}
	release := make(chan struct{})
	started := make(chan struct{})
	a.Auto.CanSync = func() bool { return true }
	a.Auto.Run = func(ctx context.Context, reason string, manual bool) error {
		close(started)
		<-release
		return nil
	}
	a.Auto.Trigger(context.Background(), "auto", false) // one is still running
	<-started
	a.setStatus("已同步 10:00") // it has published its result and is still unwinding
	a.SyncNow()              // turned away: nothing will ever follow it
	if got := a.SyncStatus(); got != "已同步 10:00" {
		t.Fatalf("footer %q: a request that was refused must not show syncing", got)
	}
	close(release)
}

func TestManualSyncThatFindsItselfSignedOutIsCounted(t *testing.T) {
	a, err := New(Config{
		Layout: paths.Layout{Root: t.TempDir()}, Clipboard: &fakeClip{}, ChangeSeq: (&fakeClip{}).seq.Load,
		BaseURL: "http://127.0.0.1:1", Logf: t.Logf, StartupRetry: time.Hour,
	})
	if err != nil {
		t.Fatal(err)
	}
	a.Auto.CanSync = func() bool { return true } // signed in when asked...
	a.Auto.Run = func(ctx context.Context, reason string, manual bool) error {
		a.syncing.Add(1) // as the wrapper that New installs does
		a.runActive.Store(true)
		a.runEntered.Store(true)
		return syncer.ErrNotLoggedIn // ...and refused by the server when it ran
	}
	a.Auto.Trigger(a.acct(), "manual", true)
	waitFor(t, "the sync is over", func() bool { return a.SyncStatus() == "请先登录" })
	waitFor(t, "counted out", func() bool { return a.syncing.Load() == 0 && !a.runActive.Load() })
}
