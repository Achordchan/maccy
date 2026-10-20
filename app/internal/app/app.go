// Package app wires Klip's parts into one running program, without any window:
// settings, the history and its file, clipboard capture, and the sync with
// the server. The windows (package panel, cmd/klip) sit on top of it.
package app

import (
	"context"
	"errors"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"time"

	"klip/internal/account"
	"klip/internal/autostart"
	"klip/internal/capture"
	"klip/internal/history"
	"klip/internal/model"
	"klip/internal/paths"
	"klip/internal/platform"
	"klip/internal/prefs"
	"klip/internal/settings"
	"klip/internal/syncclient"
	"klip/internal/syncer"
	"klip/internal/update"
	"klip/internal/version"
)

// Config says what the App is made of. Clipboard is required; the rest has
// defaults for the real program.
type Config struct {
	Layout    paths.Layout
	Clipboard capture.Clipboard
	// Foreground says which program is in front when something is captured.
	Foreground func() *capture.AppIdentity
	// ChangeSeq and Interval drive the clipboard watcher.
	ChangeSeq func() int64
	Interval  time.Duration
	// StartupRetry is the first pause before the first sync is tried again
	// (30 s unless a test sets it).
	StartupRetry time.Duration
	// BaseURL is the sync server: the official one unless a test sets it.
	BaseURL string
	// Autostart is the start at sign-in (autostart.Default when nil).
	Autostart autostart.Service
	// Quit ends the program (an update asks for it); nil does nothing.
	Quit func()
	// Exe is the running program (os.Executable when empty).
	Exe string
	// ManifestURL is the update manifest (UpdateManifestURL when empty).
	ManifestURL string
	// UpdateFirst and UpdateEvery time the check for a new version (6 s and
	// 12 h unless a test sets them).
	UpdateFirst, UpdateEvery time.Duration
	// Changed is called, from any goroutine, when something the windows show
	// changed (the sync status; history changes are seen through the history).
	Changed func()
	Logf    func(format string, args ...any)
}

// UpdateManifestURL is where the update manifest is published: the address
// that 1.0.10 and older read, which stays the release channel.
const UpdateManifestURL = "https://gitee.com/Achordchan/maccy/raw/master/docs/updates/manifest.json"

// App is the program without windows.
type App struct {
	cfg       Config
	Layout    paths.Layout
	Settings  *settings.Manager
	History   *history.Service
	Persister *history.Persister
	Capture   *capture.Service
	Syncer    *syncer.Syncer
	Auto      *syncer.Auto
	Account   *account.Service
	Prefs     *prefs.Controller
	Updates   *update.Coordinator

	mu          sync.Mutex
	status      string
	cancel      context.CancelFunc
	done        sync.WaitGroup
	started     bool
	ctx         context.Context    // lives as long as the program, once started
	syncing     atomic.Int32       // syncs running, background or asked for
	retrying    atomic.Bool        // the loop that tries the first sync again is running
	loginWanted atomic.Bool        // a sync for a new sign-in is owed (one was running when it was asked for)
	initialOK   atomic.Bool        // a background sync has gone through
	stopWatch   context.CancelFunc // ends the event stream of the current account
}

// New reads the settings and the history and connects the parts. A settings
// or history file that cannot be read is logged and replaced by the defaults
// in memory (the file is left alone until something changes), as the C#
// client does.
func New(cfg Config) (*App, error) {
	if cfg.Clipboard == nil {
		return nil, fmt.Errorf("app: no clipboard")
	}
	if cfg.Logf == nil {
		cfg.Logf = log.Printf
	}
	if cfg.BaseURL == "" {
		cfg.BaseURL = model.OfficialSyncBaseUrl
	}
	a := &App{cfg: cfg, Layout: cfg.Layout}

	var err error
	a.Settings, err = settings.Open(cfg.Layout.Settings())
	if err != nil {
		cfg.Logf("settings: %v", err)
	}
	a.History = history.NewService(cfg.Layout)
	a.Persister = history.NewPersister(a.History, cfg.Layout)
	if err := a.Persister.Load(context.Background()); err != nil {
		cfg.Logf("history: %v", err)
	}
	a.Capture = capture.New(cfg.Layout, a.History, cfg.Clipboard)
	a.Capture.ForegroundApp = cfg.Foreground
	// The change counter lets Capture tell the change Apply made from a copy
	// by somebody else; where there is none, the content token does it.
	seq := cfg.ChangeSeq
	if seq == nil {
		seq = platform.ChangeSeq
	}
	a.Capture.Seq = seq
	if seq() < 0 {
		a.Capture.Token = platform.ContentFingerprint
	}
	// Without a counter a write is seen up to one poll late.
	iv := cfg.Interval
	if iv <= 0 {
		iv = platform.DefaultInterval
	}
	a.Capture.OwnWindow = iv + 250*time.Millisecond

	st := a.Settings.Get()
	a.applySettings(st)
	a.Settings.OnChange(a.applySettings)

	a.Syncer = syncer.New(syncer.Config{
		Layout: cfg.Layout,
		Settings: syncclient.Settings{
			Get:    a.Settings.Get,
			Update: func(f func(*model.Settings)) { _ = a.Settings.Update(f) },
		},
		History:          a.History,
		Flush:            a.Persister.Flush,
		Client:           syncclient.New(cfg.BaseURL),
		BaseURL:          cfg.BaseURL,
		SuppressAutoSync: func(on bool) { a.Auto.Suppress(on) },
	})
	a.Auto = syncer.NewAuto(a.Syncer, a.syncDone)
	// Every sync is counted while it runs, so that Stop can wait for them (they
	// start their own goroutines inside Auto).
	run := a.Auto.Run
	a.Auto.Run = func(ctx context.Context, reason string, manual bool) error {
		a.syncing.Add(1)
		defer a.syncing.Add(-1)
		return run(ctx, reason, manual)
	}

	a.Account = &account.Service{Client: syncclient.New(cfg.BaseURL), Store: a.Settings}
	if cfg.Autostart == nil {
		cfg.Autostart = autostart.Default()
	}
	a.Prefs = &prefs.Controller{Settings: a.Settings, Autostart: cfg.Autostart}

	manifestURL := cfg.ManifestURL
	if manifestURL == "" {
		manifestURL = UpdateManifestURL
	}
	exe := cfg.Exe
	if exe == "" {
		exe, _ = os.Executable()
	}
	a.Updates = update.NewCoordinator(&update.Client{ManifestURL: manifestURL, Current: version.Version},
		filepath.Join(cfg.Layout.Root, "updates"), exe, func() {
			if cfg.Quit != nil {
				cfg.Quit()
			}
		})
	a.Updates.OnChange = a.changed
	return a, nil
}

func (a *App) applySettings(st model.Settings) {
	a.Capture.ApplySettings(st)
	a.History.ApplyLimits(st)
}

func (a *App) changed() {
	if a.cfg.Changed != nil {
		a.cfg.Changed()
	}
}

// Start begins capturing, watching the server and the first sync. It returns
// at once. The clipboard must be usable by then (MyGo: after App.WhenReady).
func (a *App) Start() {
	a.mu.Lock()
	if a.started {
		a.mu.Unlock()
		return
	}
	a.started = true
	ctx, cancel := context.WithCancel(context.Background())
	a.cancel = cancel
	a.ctx = ctx
	a.mu.Unlock()

	// A change of the history is a reason to sync, a moment later.
	a.History.OnChanged(func() { a.Auto.OnHistoryChanged(ctx) })

	// A capture that gave way to an Apply, or could not read a busy
	// clipboard, comes back here.
	a.Capture.Retry = func() {
		if err := a.Capture.Capture(ctx); err != nil {
			a.cfg.Logf("capture: %v", err)
		}
	}
	w := &platform.Watcher{Interval: a.cfg.Interval, Seq: a.Capture.Seq, OnChange: func() {
		if err := a.Capture.Capture(ctx); err != nil {
			a.cfg.Logf("capture: %v", err)
		}
	}, Retry: a.Capture.FoundNothing}
	a.done.Add(1)
	go func() { defer a.done.Done(); w.Run(ctx) }()
	a.watchRemote(ctx)
	a.Auto.Startup(ctx)
	a.retryStartup(ctx)

	// What earlier updates left, and what the .NET program left; then the
	// check for a new version, a little after the start and every 12 hours,
	// without a word to the user.
	go func() {
		a.Updates.CleanOld()
		installDir := filepath.Dir(a.Updates.Exe)
		if removed, err := update.CleanLegacy(installDir, filepath.Join(a.Updates.Root, "installed-package-files.json")); err != nil {
			a.cfg.Logf("legacy files: %v", err)
		} else if len(removed) > 0 {
			a.cfg.Logf("removed %d files of the old program", len(removed))
		}
	}()
	a.done.Add(1)
	go func() {
		defer a.done.Done()
		first, every := a.cfg.UpdateFirst, a.cfg.UpdateEvery
		if first <= 0 {
			first = 6 * time.Second
		}
		if every <= 0 {
			every = 12 * time.Hour
		}
		t := time.NewTimer(first)
		defer t.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-t.C:
				a.Updates.Refresh(ctx)
				t.Reset(every)
			}
		}
	}()
}

// stopWait is how long Stop waits for the background work: a worker that is
// inside a clipboard call needs the UI thread, which is where Stop may be
// running (it is a quit callback), so waiting for ever could be waiting for
// ourselves.
const stopWait = 3 * time.Second

// retryStartup tries the first background sync again, 30 s, 1 min, 2 min ...
// up to every 10 minutes, until one has gone through: history-triggered syncing
// only starts after it, and a program started offline would otherwise wait for
// another device to speak first.
func (a *App) retryStartup(ctx context.Context) {
	if !a.retrying.CompareAndSwap(false, true) {
		return // one loop is enough
	}
	a.done.Add(1)
	go func() {
		defer a.done.Done()
		defer a.retrying.Store(false)
		wait := 30 * time.Second
		if a.cfg.StartupRetry > 0 {
			wait = a.cfg.StartupRetry
		}
		for {
			select {
			case <-ctx.Done():
				return
			case <-time.After(wait):
			}
			if a.initialOK.Load() {
				return
			}
			a.Auto.Trigger(ctx, "startup", false)
			wait = min(wait*2, 10*time.Minute)
		}
	}()
}

// watchRemote listens for uploads from other devices. The stream belongs to
// the signed-in account, so it has a context of its own, ended and started
// again when the account changes (the old connection would go on telling
// about the old account).
func (a *App) watchRemote(ctx context.Context) {
	wctx, cancel := context.WithCancel(ctx)
	a.mu.Lock()
	if a.stopWatch != nil {
		a.stopWatch()
	}
	a.stopWatch = cancel
	a.mu.Unlock()
	a.done.Add(1)
	go func() {
		defer a.done.Done()
		sess := &syncclient.Session{
			Client: syncclient.New(a.cfg.BaseURL),
			Settings: syncclient.Settings{
				Get:    a.Settings.Get,
				Update: func(f func(*model.Settings)) { _ = a.Settings.Update(f) },
			},
			BaseURL: a.cfg.BaseURL,
		}
		sess.Client.Watch(wctx, sess, syncclient.WatchOptions{}, func() { a.Auto.OnRemoteUpdated(ctx) })
	}()
}

// Stop ends the background work and writes the history.
func (a *App) Stop() {
	a.mu.Lock()
	cancel := a.cancel
	a.mu.Unlock()
	if cancel != nil {
		cancel()
		until := time.Now().Add(stopWait)
		done := make(chan struct{})
		go func() { a.done.Wait(); close(done) }()
		select {
		case <-done:
		case <-time.After(stopWait):
			a.cfg.Logf("stop: background work did not end in %v", stopWait)
		}
		// The syncs started from Auto are not in done; they were cancelled
		// with the context and end soon.
		for a.syncing.Load() > 0 && time.Now().Before(until) {
			time.Sleep(10 * time.Millisecond)
		}
		if a.syncing.Load() > 0 {
			a.cfg.Logf("stop: a sync did not end")
		}
	}
	if err := a.Persister.Close(); err != nil {
		a.cfg.Logf("history: %v", err)
	}
}

// lifetime is the context of work that belongs to the program: the one made at
// Start, else Background (before Start there is nothing to cancel).
func (a *App) lifetime() context.Context {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.ctx != nil {
		return a.ctx
	}
	return context.Background()
}

// Apply puts an item on the clipboard and moves it to the top, as the main
// window does when one is chosen.
func (a *App) Apply(it model.Item) {
	if err := a.Capture.Apply(it); err != nil {
		a.cfg.Logf("apply: %v", err)
		return
	}
	a.History.Touch(it.ID)
}

// SyncNow is the user asking for a sync.
func (a *App) SyncNow() {
	a.setStatus("同步中…")
	// Only the syncs started in the background count towards "the first sync
	// is done" (MainWindowViewModel does the same: !manual); a manual one
	// that succeeds leaves history-triggered syncing to the next background one.
	a.Auto.Trigger(a.lifetime(), "manual", true)
}

// AccountChanged is for the preferences window: after a sign-in the history
// is synchronized at once, as the C# client does.
func (a *App) AccountChanged(signedIn bool) {
	a.mu.Lock()
	ctx, started := a.ctx, a.started
	a.mu.Unlock()
	if started {
		a.watchRemote(ctx) // the stream of the old account is dropped, a new one opened
	}
	if signedIn {
		a.loginWanted.Store(true)
		a.initialOK.Store(false)
		a.Auto.Trigger(a.lifetime(), "login", false)
		if started {
			a.retryStartup(ctx) // and again later if that sync does not go through
		}
	}
	a.changed()
}

// SyncStatus is the text for the window's footer: how the last sync went.
func (a *App) SyncStatus() string {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.status
}

func (a *App) setStatus(s string) {
	a.mu.Lock()
	a.status = s
	a.mu.Unlock()
	a.changed()
}

func (a *App) syncDone(reason string, manual bool, err error) {
	if err == nil && !manual {
		a.initialOK.Store(true)
	}
	if reason == "login" {
		a.loginWanted.Store(false)
	} else if a.loginWanted.Load() {
		// The sync for the new sign-in was asked for while this one ran and was
		// turned away. The runner is still marked busy until this returns, so
		// ask again a moment later.
		time.AfterFunc(100*time.Millisecond, func() { a.Auto.Trigger(a.lifetime(), "login", false) })
	}
	switch {
	case err == nil:
		a.setStatus("已同步 " + time.Now().Format("15:04"))
	case manual && errors.Is(err, syncer.ErrNotLoggedIn):
		a.setStatus("请先登录")
	case manual:
		a.setStatus(syncer.Explain(err, syncer.InMainWindow))
	default:
		// A background failure is not shown as a message in the C# client
		// either; it is kept for the log and the footer.
		a.cfg.Logf("sync (%s): %v", reason, err)
		a.setStatus("同步失败")
	}
}
