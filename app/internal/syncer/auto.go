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
	// Begin is called when a sync starts, Progress as it goes (both may be
	// nil). They are how the main window shows the cloud icon at work and,
	// for a sync the user asked for, the progress bar.
	Begin    func(reason string, manual bool)
	Progress func(p Progress, reason string, manual bool)

	busy        atomic.Bool
	suppressed  atomic.Bool
	initialDone atomic.Bool

	mu    sync.Mutex
	timer *time.Timer
}

// NewAuto returns an Auto that runs s.
func NewAuto(s *Syncer, done func(reason string, manual bool, err error)) *Auto {
	a := &Auto{CanSync: s.CanAutoSync, Done: done}
	a.Run = func(ctx context.Context, reason string, manual bool) error {
		var progress func(Progress)
		if a.Progress != nil {
			progress = func(p Progress) { a.Progress(p, reason, manual) }
		}
		_, err := s.Run(ctx, progress, reason)
		return err
	}
	return a
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

// Suppressed says whether history changes are being ignored because the sync
// itself is making them.
func (a *Auto) Suppressed() bool { return a.suppressed.Load() }

// Trigger runs a sync now, unless one is running. A manual one that cannot
// start because there is no sign-in reports ErrNotLoggedIn.
func (a *Auto) Trigger(ctx context.Context, reason string, manual bool) bool {
	if !a.busy.CompareAndSwap(false, true) {
		return false
	}
	go func() {
		defer a.busy.Store(false)
		if !a.CanSync() {
			if manual && a.Done != nil {
				a.Done(reason, manual, ErrNotLoggedIn)
			}
			return
		}
		if a.Begin != nil {
			a.Begin(reason, manual)
		}
		err := a.Run(ctx, reason, manual)
		if err == nil && !manual {
			a.initialDone.Store(true)
		}
		if a.Done != nil {
			a.Done(reason, manual, err)
		}
	}()
	return true
}

// Exclusive runs f once nothing else is running (a sync, another exclusive
// piece of work) and keeps everything else out until it returns. It waits for
// its turn, as long as ctx lives, and says whether f ran.
func (a *Auto) Exclusive(ctx context.Context, f func()) bool {
	for !a.busy.CompareAndSwap(false, true) {
		select {
		case <-ctx.Done():
			return false
		case <-time.After(20 * time.Millisecond):
		}
	}
	defer a.busy.Store(false)
	f()
	return true
}

// Startup is the first background sync.
func (a *Auto) Startup(ctx context.Context) { a.Trigger(ctx, "startup", false) }

// OnRemoteUpdated is for the event stream: another device uploaded.
func (a *Auto) OnRemoteUpdated(ctx context.Context) { a.Trigger(ctx, "sse", false) }

// OnHistoryChanged is for each change of the history. A sync follows after
// the pause, unless another change comes first.
func (a *Auto) OnHistoryChanged(ctx context.Context) {
	if !a.initialDone.Load() || a.suppressed.Load() || a.busy.Load() || !a.CanSync() {
		return
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.timer != nil {
		a.timer.Stop()
	}
	a.timer = time.AfterFunc(a.debounce(), func() { a.Trigger(ctx, "auto", false) })
}
