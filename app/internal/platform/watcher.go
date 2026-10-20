// Package platform is where Klip meets the operating system: the MyGo
// clipboard behind capture.Clipboard, the clipboard change counter, and the
// program that is in front. Everything that needs a window or a native call
// lives here, so the other packages stay testable without one.
package platform

import (
	"context"
	"sync"
	"time"
)

// DefaultInterval is how often the change counter is asked. The counter is
// cheap (about 0.03% CPU at this rate, measured in spikes/), so this is the
// delay between a copy and its capture.
const DefaultInterval = 400 * time.Millisecond

// Watcher tells when the clipboard changed. It asks Seq every Interval and
// calls OnChange when the number moved. A negative Seq means the platform has
// no counter; then OnChange is called every tick and the capture rules
// (which drop a repeat of the newest item) keep the history clean.
//
// The first tick only records the counter: whatever was on the clipboard
// before Klip started is not captured, as in the C# client.
type Watcher struct {
	Interval time.Duration
	Seq      func() int64
	OnChange func()

	mu   sync.Mutex
	last int64
	seen bool
}

// Run blocks until ctx ends. OnChange runs on the calling goroutine, one at
// a time; MyGo hops each clipboard call to its main thread.
func (w *Watcher) Run(ctx context.Context) {
	iv := w.Interval
	if iv <= 0 {
		iv = DefaultInterval
	}
	seq := w.Seq
	if seq == nil {
		seq = ChangeSeq
	}
	t := time.NewTicker(iv)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			if w.moved(seq()) && w.OnChange != nil {
				w.OnChange()
			}
		}
	}
}

// moved says whether the counter shows a change since the last call.
func (w *Watcher) moved(s int64) bool {
	w.mu.Lock()
	defer w.mu.Unlock()
	if s < 0 {
		was := w.seen // no counter: after the first tick, always look
		w.seen = true
		return was
	}
	if !w.seen {
		w.seen, w.last = true, s
		return false
	}
	if s == w.last {
		return false
	}
	w.last = s
	return true
}
