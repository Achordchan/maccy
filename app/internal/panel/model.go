package panel

import (
	"bytes"
	"image"
	"image/color"
	"image/png"
	"io"
	"os"
	"sync"
	"sync/atomic"
	"time"

	_ "image/gif"
	_ "image/jpeg"

	_ "golang.org/x/image/bmp"
	_ "golang.org/x/image/webp"

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
	// UpdateAvailable is the version of a newer release that is known, or "".
	UpdateAvailable() string
	// ShowUpdate opens the update window.
	ShowUpdate()
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
	m.list.ScrollIntoView(0)  // and the list shows it, whatever it was scrolled to
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

// thumbCache keeps small pictures of the rows in view. The pictures are made
// in the background, one at a time, and cut down to the size they are drawn
// at: a screenshot is never held in memory as a whole.
type thumbCache struct {
	mu      sync.Mutex
	items   map[string]*ui.Bitmap // nil: could not be made
	pending map[string]bool
	order   []string
	slot    chan struct{} // one picture is decoded at a time
	redraw  func()
}

const (
	thumbLimit = 64
	thumbSize  = 72 // twice the 36 points it is drawn at, for sharp screens
	// thumbMaxPixels is the largest picture that is decoded for a thumbnail
	// (an 8K screen is 33 million): decoding costs four bytes a pixel.
	thumbMaxPixels = 40_000_000
	// thumbMaxBytes is the largest file that is read for one: a bitmap of
	// thumbMaxPixels is about this big; a format that only MyGo knows (its size
	// cannot be told beforehand) gets a tenth of it.
	thumbMaxBytes        = 256 << 20
	thumbMaxUnknownBytes = 32 << 20
	// thumbMaxUnknownPixels is the largest picture of such a format that is
	// kept: MyGo's bitmap cannot be cut down, so the cache would hold it whole
	// (thumbLimit of them), and its size is only known once it is decoded.
	thumbMaxUnknownPixels = 2_000_000
)

// get returns the thumbnail, or nil while it is being made (the window is
// redrawn when it is ready) or when the picture cannot be read.
func (t *thumbCache) get(path string, redraw func()) *ui.Bitmap {
	t.mu.Lock()
	defer t.mu.Unlock()
	if bm, ok := t.items[path]; ok {
		return bm
	}
	if t.pending[path] || path == "" {
		return nil
	}
	if t.pending == nil {
		t.pending = map[string]bool{}
		t.slot = make(chan struct{}, 1)
	}
	t.pending[path] = true
	go t.make(path, redraw)
	return nil
}

func (t *thumbCache) make(path string, redraw func()) {
	t.slot <- struct{}{}
	bm := smallBitmap(path)
	<-t.slot
	t.mu.Lock()
	delete(t.pending, path)
	if t.items == nil {
		t.items = map[string]*ui.Bitmap{}
	}
	t.items[path] = bm
	t.order = append(t.order, path)
	if len(t.order) > thumbLimit {
		delete(t.items, t.order[0])
		t.order = t.order[1:]
	}
	t.mu.Unlock()
	if redraw != nil {
		redraw()
	}
}

// smallBitmap reads a picture and cuts it down to fit thumbSize.
func smallBitmap(path string) *ui.Bitmap {
	f, err := os.Open(path)
	if err != nil {
		return nil
	}
	defer f.Close()
	// A small file can hold a huge picture, and a big one can be a huge bitmap:
	// the header says which, before anything is read into memory.
	known := false
	if cfg, _, err := image.DecodeConfig(f); err == nil {
		if cfg.Width*cfg.Height > thumbMaxPixels {
			return nil
		}
		known = true
	}
	limit := int64(thumbMaxBytes)
	if !known {
		limit = thumbMaxUnknownBytes
	}
	st, err := f.Stat()
	if err != nil || st.Size() > limit {
		return nil
	}
	if _, err := f.Seek(0, io.SeekStart); err != nil {
		return nil
	}
	data, err := io.ReadAll(io.LimitReader(f, limit+1))
	if err != nil || int64(len(data)) > limit {
		return nil
	}
	var img image.Image
	if known {
		img, _, err = image.Decode(bytes.NewReader(data))
	}
	if !known || err != nil {
		bm, _ := ui.DecodeBitmap(data) // a format only MyGo knows
		if bm != nil {
			if w, h := bm.Size(); w*h > thumbMaxUnknownPixels {
				return nil
			}
		}
		return bm
	}
	b := img.Bounds()
	w, h := b.Dx(), b.Dy()
	if w <= 0 || h <= 0 {
		return nil
	}
	scale := float64(thumbSize) / float64(max(w, h))
	if scale > 1 {
		scale = 1
	}
	tw, th := max(1, int(float64(w)*scale)), max(1, int(float64(h)*scale))
	small := image.NewNRGBA(image.Rect(0, 0, tw, th))
	for y := 0; y < th; y++ {
		for x := 0; x < tw; x++ {
			// the average of the source block that this pixel stands for
			x0, x1 := b.Min.X+x*w/tw, b.Min.X+max(x*w/tw+1, (x+1)*w/tw)
			y0, y1 := b.Min.Y+y*h/th, b.Min.Y+max(y*h/th+1, (y+1)*h/th)
			var r, g, bl, a, n uint64
			for yy := y0; yy < y1; yy++ {
				for xx := x0; xx < x1; xx++ {
					pr, pg, pb, pa := img.At(xx, yy).RGBA()
					r, g, bl, a, n = r+uint64(pr), g+uint64(pg), bl+uint64(pb), a+uint64(pa), n+1
				}
			}
			if n == 0 || a == 0 {
				continue
			}
			small.SetNRGBA(x, y, color.NRGBA{
				R: uint8((r * 0xffff / a) >> 8), G: uint8((g * 0xffff / a) >> 8), B: uint8((bl * 0xffff / a) >> 8), A: uint8((a / n) >> 8),
			})
		}
	}
	var out bytes.Buffer
	if png.Encode(&out, small) != nil {
		return nil
	}
	bm, _ := ui.DecodeBitmap(out.Bytes())
	return bm
}
