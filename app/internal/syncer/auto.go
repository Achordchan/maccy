package syncer

import (
	"context"
	"sync"
	"sync/atomic"
	"time"
)

// Auto decides when a background sync starts: at startup, when the server
// says another device uploaded, and a moment after the history changed. It
// is the part of MainWindowViewModel around RunAutoSyncAsync: one sync at a
// time, none for a change the sync itself made, none for a change before the
// first sync has succeeded, and a pause so that a burst of changes is one
// sync.
type Auto struct {
	// Debounce is the pause after a change before the sync starts (1.2 s).
	Debounce time.Duration
	// CanSync says whether a sync can be tried (Syncer.CanAutoSync).
	CanSync func() bool
	// Run does the sync; manual says that the user asked for it.
	Run func(ctx context.Context, reason string, manual bool) error
	// Done is called after each sync, with its error (nil on success).
	Done func(reason string, manual bool, err error)

	busy        atomic.Bool
	suppressed  atomic.Bool
	initialDone atomic.Bool
	// pending says a history change came while a sync was running; another
	// sync follows when it ends, so that the change is not left unsynced.
	pending atomic.Bool

	mu    sync.Mutex
	timer *time.Timer
}

// NewAuto returns an Auto that runs s.
func NewAuto(s *Syncer, done func(reason string, manual bool, err error)) *Auto {
	return &Auto{
		CanSync: s.CanAutoSync,
		Run: func(ctx context.Context, reason string, manual bool) error {
			_, err := s.Run(ctx, nil, reason)
			return err
		},
		Done: done,
	}
}

func (a *Auto) debounce() time.Duration {
	if a.Debounce > 0 {
		return a.Debounce
	}
	return 1200 * time.Millisecond
}

// Suppress turns off the reaction to history changes while the sync changes
// the history itself.
func (a *Auto) Suppress(on bool) { a.suppressed.Store(on) }

// Trigger runs a sync now, unless one is running. A manual one that cannot
// start because there is no sign-in reports ErrNotLoggedIn.
func (a *Auto) Trigger(ctx context.Context, reason string, manual bool) {
	if !a.busy.CompareAndSwap(false, true) {
		return
	}
	go func() {
		if !a.CanSync() {
			if manual && a.Done != nil {
				a.Done(reason, manual, ErrNotLoggedIn)
			}
			a.busy.Store(false)
			return
		}
		err := a.Run(ctx, reason, manual)
		if err == nil && !manual {
			a.initialDone.Store(true)
		}
		if a.Done != nil {
			a.Done(reason, manual, err)
		}
		a.busy.Store(false)
		// A change that came while the sync was running was remembered:
		// sync it now. A suppressed change (the sync's own write) is not.
		if a.pending.CompareAndSwap(true, false) && !a.suppressed.Load() && a.CanSync() {
			a.Trigger(ctx, "auto", false)
		}
	}()
}

// Startup is the first background sync.
func (a *Auto) Startup(ctx context.Context) { a.Trigger(ctx, "startup", false) }

// OnRemoteUpdated is for the event stream: another device uploaded. An event
// that arrives while a sync runs is kept and synced after it: the running
// sync may have read an older manifest.
func (a *Auto) OnRemoteUpdated(ctx context.Context) {
	if a.busy.Load() {
		a.pending.Store(true)
		return
	}
	a.Trigger(ctx, "remote_event", false)
}

// OnHistoryChanged is for each change of the history. A sync follows after
// the pause, unless another change comes first. A change that arrives while
// a sync is running makes that sync run again when it ends: the sync has
// already read the history it uploaded.
func (a *Auto) OnHistoryChanged(ctx context.Context) {
	if !a.initialDone.Load() || a.suppressed.Load() || !a.CanSync() {
		return
	}
	if a.busy.Load() {
		a.pending.Store(true)
		return
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.timer != nil {
		a.timer.Stop()
	}
	a.timer = time.AfterFunc(a.debounce(), func() { a.Trigger(ctx, "auto", false) })
}
