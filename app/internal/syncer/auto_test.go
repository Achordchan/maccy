package syncer

import (
	"context"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

type autoRig struct {
	a     *Auto
	runs  atomic.Int32
	mu    sync.Mutex
	why   []string
	done  []error
	can   atomic.Bool
	block chan struct{}
	fail  atomic.Bool
}

func (r *autoRig) gate() chan struct{} { r.mu.Lock(); defer r.mu.Unlock(); return r.block }

func (r *autoRig) setGate(c chan struct{}) { r.mu.Lock(); r.block = c; r.mu.Unlock() }

func newRig() *autoRig {
	r := &autoRig{block: make(chan struct{})}
	r.can.Store(true)
	close(r.block)
	r.a = &Auto{
		Debounce: 30 * time.Millisecond,
		CanSync:  r.can.Load,
		Run: func(ctx context.Context, reason string, manual bool) error {
			r.runs.Add(1)
			r.mu.Lock()
			r.why = append(r.why, reason)
			r.mu.Unlock()
			<-r.gate()
			if r.fail.Load() {
				return ErrNotLoggedIn
			}
			return nil
		},
		Done: func(reason string, manual bool, err error) {
			r.mu.Lock()
			r.done = append(r.done, err)
			r.mu.Unlock()
		},
	}
	return r
}

func waitFor(t *testing.T, what string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(2 * time.Second)
	for !cond() {
		if time.Now().After(deadline) {
			t.Fatalf("timed out waiting for %s", what)
		}
		time.Sleep(2 * time.Millisecond)
	}
}

func TestAutoChangesWaitForTheFirstSync(t *testing.T) {
	r := newRig()
	ctx := context.Background()
	r.a.OnHistoryChanged(ctx) // before the first sync: ignored
	time.Sleep(80 * time.Millisecond)
	if r.runs.Load() != 0 {
		t.Fatal("a change before the first sync started one")
	}
	r.a.Startup(ctx)
	waitFor(t, "startup sync", func() bool { return r.runs.Load() == 1 })
	waitFor(t, "idle", func() bool { return !r.a.busy.Load() })

	// A burst of changes is one sync, after the pause.
	for i := 0; i < 5; i++ {
		r.a.OnHistoryChanged(ctx)
		time.Sleep(5 * time.Millisecond)
	}
	waitFor(t, "auto sync", func() bool { return r.runs.Load() == 2 })
	time.Sleep(80 * time.Millisecond)
	if r.runs.Load() != 2 {
		t.Errorf("runs %d: the burst should be one sync", r.runs.Load())
	}
	r.mu.Lock()
	why := append([]string(nil), r.why...)
	r.mu.Unlock()
	if why[0] != "startup" || why[1] != "auto" {
		t.Errorf("reasons %v", why)
	}
}

func TestAutoSuppressedBusyAndSignedOut(t *testing.T) {
	r := newRig()
	ctx := context.Background()
	r.a.Startup(ctx)
	waitFor(t, "startup", func() bool { return r.runs.Load() == 1 && !r.a.busy.Load() })

	// The sync's own changes do not start a sync.
	r.a.Suppress(true)
	r.a.OnHistoryChanged(ctx)
	time.Sleep(80 * time.Millisecond)
	r.a.Suppress(false)
	if r.runs.Load() != 1 {
		t.Error("a suppressed change started a sync")
	}

	// Signed out: no sync for a change; a manual one says why.
	r.can.Store(false)
	r.a.OnHistoryChanged(ctx)
	time.Sleep(80 * time.Millisecond)
	if r.runs.Load() != 1 {
		t.Error("a sync without a sign-in")
	}
	r.a.Trigger(ctx, "manual", true)
	waitFor(t, "the manual refusal", func() bool { r.mu.Lock(); defer r.mu.Unlock(); return len(r.done) == 2 })
	r.mu.Lock()
	second := r.done[1]
	r.mu.Unlock()
	if second != ErrNotLoggedIn || r.runs.Load() != 1 {
		t.Errorf("manual while signed out: %v, runs %d", second, r.runs.Load())
	}
	// A background trigger while signed out is silent.
	r.a.Trigger(ctx, "sse", false)
	time.Sleep(50 * time.Millisecond)
	r.mu.Lock()
	n := len(r.done)
	r.mu.Unlock()
	if n != 2 {
		t.Errorf("done calls %d", n)
	}
	r.can.Store(true)

	// One sync at a time.
	gate := make(chan struct{})
	r.setGate(gate)
	r.a.Trigger(ctx, "one", true)
	waitFor(t, "the running sync", func() bool { return r.runs.Load() == 2 })
	r.a.Trigger(ctx, "two", true)
	r.a.Trigger(ctx, "three", true)
	time.Sleep(50 * time.Millisecond)
	if r.runs.Load() != 2 {
		t.Errorf("runs %d: triggers during a sync are dropped", r.runs.Load())
	}
	close(gate)
	waitFor(t, "end", func() bool { return !r.a.busy.Load() })
}

func TestAutoFailedStartupKeepsChangesQuiet(t *testing.T) {
	r := newRig()
	ctx := context.Background()
	r.fail.Store(true)
	r.a.Startup(ctx)
	waitFor(t, "startup", func() bool { return r.runs.Load() == 1 && !r.a.busy.Load() })
	r.a.OnHistoryChanged(ctx) // the first sync failed: changes still wait
	time.Sleep(80 * time.Millisecond)
	if r.runs.Load() != 1 {
		t.Error("a change after a failed first sync started one")
	}
	// A manual success does not count as the first background sync either.
	r.fail.Store(false)
	r.a.Trigger(ctx, "manual", true)
	waitFor(t, "manual", func() bool { return r.runs.Load() == 2 && !r.a.busy.Load() })
	r.a.OnHistoryChanged(ctx)
	time.Sleep(80 * time.Millisecond)
	if r.runs.Load() != 2 {
		t.Error("only a successful background sync opens the gate (C# _initialSyncCompleted)")
	}
}

func TestExclusiveWaitsForTheRunningSyncAndKeepsOthersOut(t *testing.T) {
	a := &Auto{CanSync: func() bool { return true }}
	release := make(chan struct{})
	started := make(chan struct{})
	a.Run = func(ctx context.Context, reason string, manual bool) error {
		close(started)
		<-release
		return nil
	}
	a.Trigger(context.Background(), "auto", false)
	<-started
	var ran atomic.Bool
	done := make(chan bool, 1)
	go func() { done <- a.Exclusive(context.Background(), func() { ran.Store(true) }) }()
	time.Sleep(80 * time.Millisecond)
	if ran.Load() {
		t.Fatal("it ran while a sync was running")
	}
	close(release)
	if !<-done || !ran.Load() {
		t.Fatal("it never got its turn")
	}
	// While it runs, a sync is turned away.
	hold, in := make(chan struct{}), make(chan struct{})
	go a.Exclusive(context.Background(), func() { close(in); <-hold })
	<-in
	if a.Trigger(context.Background(), "auto", false) {
		t.Fatal("a sync started in the middle of it")
	}
	close(hold)
	for a.busy.Load() {
		time.Sleep(5 * time.Millisecond)
	}
	// A caller that gives up is not left waiting.
	a.busy.Store(true)
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Millisecond)
	defer cancel()
	if a.Exclusive(ctx, func() { t.Error("ran although the gate was shut") }) {
		t.Fatal("reported a run")
	}
}
