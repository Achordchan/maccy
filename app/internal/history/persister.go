package history

import (
	"context"
	"sync"
	"sync/atomic"
	"time"

	"klip/internal/paths"
)

// Persister keeps history.json up to date: it saves the list a short while
// after it changed (ClipboardPersistenceService).
type Persister struct {
	svc    *Service
	layout paths.Layout
	// Delay is the wait before a save, 350 ms.
	Delay time.Duration

	scheduled atomic.Bool
	mu        sync.Mutex
	timer     *time.Timer
	closed    bool
	saveMu    sync.Mutex
}

// NewPersister saves svc's list under layout whenever it changes.
func NewPersister(svc *Service, layout paths.Layout) *Persister {
	p := &Persister{svc: svc, layout: layout, Delay: 350 * time.Millisecond}
	svc.OnChanged(p.onChanged)
	return p
}

// Load reads history.json into the service (the items whose content is gone
// are dropped, see Normalize). A missing file is an empty history.
func (p *Persister) Load(ctx context.Context) error {
	items, err := Load(p.layout)
	if err != nil || len(items) == 0 {
		return err
	}
	return p.svc.ReplaceAll(ctx, items)
}

// Flush saves now.
func (p *Persister) Flush(context.Context) error { return p.save() }

// Close stops the timer and saves once more.
func (p *Persister) Close() error {
	p.mu.Lock()
	p.closed = true
	if p.timer != nil {
		p.timer.Stop()
	}
	p.mu.Unlock()
	return p.save()
}

func (p *Persister) save() error {
	p.saveMu.Lock()
	defer p.saveMu.Unlock()
	return Save(p.layout, p.svc.Items())
}

// onChanged schedules one save: changes during the wait are in that save,
// they do not start another (and do not push it back).
func (p *Persister) onChanged() {
	if !p.scheduled.CompareAndSwap(false, true) {
		return
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.closed {
		p.scheduled.Store(false)
		return
	}
	d := p.Delay
	if d <= 0 {
		d = 350 * time.Millisecond
	}
	p.timer = time.AfterFunc(d, func() {
		defer p.scheduled.Store(false)
		_ = p.save()
	})
}
