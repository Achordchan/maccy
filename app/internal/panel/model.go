package panel

import (
	"os"
	"sync"
	"sync/atomic"
	"time"

	"github.com/egoist/mygo/ui"

	"klip/internal/history"
	"klip/internal/model"
)

// Host is what the panel asks of the program around it.
type Host interface {
	// Apply puts the item on the clipboard; the panel hides itself after.
	Apply(it model.Item)
	// Hide hides the panel.
	Hide()
	// SyncNow starts a sync the user asked for.
	SyncNow()
	// ShowPrefs opens the preferences window.
	ShowPrefs()
	// Quit ends the program.
	Quit()
}

// Model is the state of the panel. History is the list it shows and edits;
// Status says how the sync is doing (may be nil).
type Model struct {
	History *history.Service
	Host    Host
	Status  func() string
	// Redraw asks for a new frame from any goroutine (Window.Update).
	Redraw func()
	// Now is time.Now unless a test sets it.
	Now func() time.Time

	query  string
	rows   []model.Item
	chosen int
	list   ui.ListState
	input  ui.Handle
	dirty  atomic.Bool
	last   string // query rows were built for

	editID   string // item whose note is being edited
	noteBuf  string
	noteHand ui.Handle
	focusNew bool

	thumbs thumbCache
}

// New returns a Model and hooks it to the history.
func New(h *history.Service, host Host) *Model {
	m := &Model{History: h, Host: host, Now: time.Now}
	m.dirty.Store(true)
	h.OnChanged(func() {
		m.dirty.Store(true)
		if m.Redraw != nil {
			m.Redraw()
		}
	})
	return m
}

// Reset is for each time the panel is shown: no query, the first row chosen,
// the search box focused. Call it on the UI thread.
func (m *Model) Reset() {
	m.query, m.editID = "", ""
	m.dirty.Store(true)
	m.rows, m.chosen = nil, 0 // the first row, not the one that was chosen before
	m.input.Focus()
}

// refresh rebuilds the rows when the history or the query changed, and keeps
// the chosen item chosen.
func (m *Model) refresh() {
	if !m.dirty.Swap(false) && m.last == m.query {
		return
	}
	var keep string
	if m.chosen >= 0 && m.chosen < len(m.rows) && m.last == m.query {
		keep = m.rows[m.chosen].ID
	}
	m.rows = Rows(m.History.Items(), m.query)
	m.last = m.query
	m.chosen = 0
	if keep != "" {
		for i, it := range m.rows {
			if it.ID == keep {
				m.chosen = i
			}
		}
	}
}

func (m *Model) choose(i int) {
	m.chosen = max(0, min(i, len(m.rows)-1))
	if len(m.rows) > 0 {
		m.list.ScrollIntoView(m.chosen)
	}
}

// current is the chosen item.
func (m *Model) current() (model.Item, bool) {
	if m.chosen < 0 || m.chosen >= len(m.rows) {
		return model.Item{}, false
	}
	return m.rows[m.chosen], true
}

// ---- thumbnails

// thumbCache decodes the pictures of the rows in view, a few at a time kept.
type thumbCache struct {
	mu    sync.Mutex
	items map[string]*ui.Bitmap
	order []string
}

const thumbLimit = 64

func (t *thumbCache) get(path string) *ui.Bitmap {
	t.mu.Lock()
	defer t.mu.Unlock()
	if bm, ok := t.items[path]; ok {
		return bm
	}
	var bm *ui.Bitmap
	if data, err := os.ReadFile(path); err == nil {
		bm, _ = ui.DecodeBitmap(data)
	}
	if t.items == nil {
		t.items = map[string]*ui.Bitmap{}
	}
	t.items[path] = bm
	t.order = append(t.order, path)
	if len(t.order) > thumbLimit {
		delete(t.items, t.order[0])
		t.order = t.order[1:]
	}
	return bm
}
