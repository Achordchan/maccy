// Package app wires Klip's parts into one running program, without any window:
// settings, the history and its file, clipboard capture, and the sync with
// the server. The windows (package panel, cmd/klip) sit on top of it.
package app

import (
	"context"
	"errors"
	"fmt"
	"log"
	"sync"
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
	// BaseURL is the sync server: the official one unless a test sets it.
	BaseURL string
	// Autostart is the start at sign-in (autostart.Default when nil).
	Autostart autostart.Service
	// Changed is called, from any goroutine, when something the windows show
	// changed (the sync status; history changes are seen through the history).
	Changed func()
	Logf    func(format string, args ...any)
}

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

	mu      sync.Mutex
	status  string
	cancel  context.CancelFunc
	done    sync.WaitGroup
	started bool
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

	a.Account = &account.Service{Client: syncclient.New(cfg.BaseURL), Store: a.Settings}
	if cfg.Autostart == nil {
		cfg.Autostart = autostart.Default()
	}
	a.Prefs = &prefs.Controller{Settings: a.Settings, Autostart: cfg.Autostart}
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
	a.mu.Unlock()

	// A change of the history is a reason to sync, a moment later.
	a.History.OnChanged(func() { a.Auto.OnHistoryChanged(ctx) })

	w := &platform.Watcher{Interval: a.cfg.Interval, Seq: a.cfg.ChangeSeq, OnChange: func() {
		if err := a.Capture.Capture(ctx); err != nil {
			a.cfg.Logf("capture: %v", err)
		}
	}, Retry: a.Capture.FoundNothing}
	a.done.Add(2)
	go func() { defer a.done.Done(); w.Run(ctx) }()
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
		sess.Client.Watch(ctx, sess, syncclient.WatchOptions{}, func() { a.Auto.OnRemoteUpdated(ctx) })
	}()
	a.Auto.Startup(ctx)
}

// Stop ends the background work and writes the history.
func (a *App) Stop() {
	a.mu.Lock()
	cancel := a.cancel
	a.mu.Unlock()
	if cancel != nil {
		cancel()
		a.done.Wait()
	}
	if err := a.Persister.Close(); err != nil {
		a.cfg.Logf("history: %v", err)
	}
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
	a.Auto.Trigger(context.Background(), "manual", true)
}

// AccountChanged is for the preferences window: after a sign-in the history
// is synchronized at once, as the C# client does.
func (a *App) AccountChanged(signedIn bool) {
	if signedIn {
		a.Auto.Trigger(context.Background(), "login", false)
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
