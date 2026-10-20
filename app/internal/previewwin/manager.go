package previewwin

import (
	"sync"

	"github.com/egoist/mygo"
	"github.com/egoist/mygo/ui"

	"klip/internal/model"
	"klip/internal/panel"
)

// Manager owns the preview window: made when first needed, then shown, moved
// and hidden. Show and Hide may be called from any goroutine.
type Manager struct {
	// IconOf gives the icon of a program (may be nil).
	IconOf func(path string) *ui.Bitmap
	// Left is called when the pointer leaves the window.
	Left func()
	// Quitting says the program is quitting: the window then lets itself be
	// closed instead of hiding, or Quit would wait for it for ever.
	Quitting func() bool

	mu    sync.Mutex
	model *Model
	win   *mygo.Window
	id    string
	seq   int // moves with each Show of a picture and each Hide
}

// Preview says which item the window shows ("" when hidden) and whether the
// pointer is over it.
func (mg *Manager) Preview() (string, bool) {
	mg.mu.Lock()
	defer mg.mu.Unlock()
	if mg.model == nil {
		return "", false
	}
	return mg.id, mg.model.Over()
}

// maxPicture is the longest side a picture is kept at for the window: the
// largest window shows it at 980 points, twice that for sharp screens.
const maxPicture = 1960

// Show opens the window for an item beside anchor, which is in screen pixels.
// A picture is read and cut down in the background; the window opens when it
// is ready, unless the pointer has moved on by then. Any goroutine.
func (mg *Manager) Show(it model.Item, anchor Rect) {
	if it.Kind != model.KindImage {
		mg.mu.Lock()
		mg.seq++ // a picture that is still being made is not wanted
		mg.mu.Unlock()
		mg.present(it, nil, 0, 0, anchor)
		return
	}
	mg.mu.Lock()
	mg.id = it.ID // the panel asks for each item once
	mg.seq++
	seq := mg.seq
	mg.mu.Unlock()
	go func() {
		bm, pw, ph := panel.ScaledBitmap(model.Str(it.ImageFilePath), maxPicture)
		mg.mu.Lock()
		current := mg.seq == seq
		mg.mu.Unlock()
		if !current {
			return // another item, or none, since
		}
		if bm == nil {
			mg.Hide() // the file is gone, or too big to show: nothing to show
			return
		}
		mg.present(it, bm, pw, ph, anchor)
	}()
}

// present puts the window up for an item whose content is ready.
func (mg *Manager) present(it model.Item, bm *ui.Bitmap, pw, ph int, anchor Rect) {
	mygo.RunOnMain(func() {
		mg.mu.Lock()
		defer mg.mu.Unlock()
		if mg.model == nil {
			mg.model = &Model{IconOf: mg.IconOf, Left: mg.Left, Open: func(p string) { _ = mygo.Shell.OpenPath(p) }}
		}
		mg.model.Item, mg.model.Bitmap, mg.model.Width, mg.model.Height = it, bm, pw, ph
		mg.id = it.ID
		w, h := Size(it, pw, ph)
		d := mygo.Screen.DisplayNearestPoint(mygo.Point{X: anchor.X + anchor.W/2, Y: anchor.Y + anchor.H/2})
		wa := d.WorkArea
		x, y, w, h := Place(anchor, w, h, Rect{wa.X, wa.Y, wa.Width, wa.Height})
		if mg.win == nil {
			mg.win = mygo.NewWindow(mygo.WindowOptions{
				Title:         "Klip 预览",
				Panel:         true, // never takes the focus from the main window
				Frameless:     true,
				Transparent:   true,
				AlwaysOnTop:   true,
				SkipTaskbar:   true,
				DisableResize: true,
				Hidden:        true,
				Width:         w,
				Height:        h,
				Content:       ui.View(mg.model.View),
			})
			mg.win.OnClose(func(e *mygo.CloseEvent) {
				if mg.Quitting != nil && mg.Quitting() {
					return
				}
				e.PreventDefault()
				mg.win.Hide()
			})
		}
		mg.win.SetSize(w, h)
		mg.win.SetPosition(x, y)
		mg.win.ShowInactive()
		mg.win.Update(func() {})
	})
}

// Hide closes the window.
func (mg *Manager) Hide() {
	mygo.RunOnMain(func() {
		mg.mu.Lock()
		defer mg.mu.Unlock()
		mg.id = ""
		mg.seq++ // a picture that is still being made is not wanted
		if mg.win != nil && mg.win.IsVisible() {
			mg.win.Hide()
		}
		if mg.model != nil {
			mg.model.over, mg.model.wasOver = false, false
		}
	})
}

// Close destroys the window (when the program quits).
func (mg *Manager) Close() {
	mg.mu.Lock()
	win := mg.win
	mg.win = nil
	mg.mu.Unlock()
	if win != nil {
		win.Close()
	}
}
