package previewwin

import (
	"os"
	"sync"

	"github.com/egoist/mygo"
	"github.com/egoist/mygo/ui"

	"klip/internal/model"
)

// Manager owns the preview window: made when first needed, then shown, moved
// and hidden. Show and Hide may be called from any goroutine.
type Manager struct {
	// IconOf gives the icon of a program (may be nil).
	IconOf func(path string) *ui.Bitmap
	// Left is called when the pointer leaves the window.
	Left func()

	mu    sync.Mutex
	model *Model
	win   *mygo.Window
	id    string
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

// Show opens the window for an item beside anchor, which is in screen pixels.
func (mg *Manager) Show(it model.Item, anchor Rect) {
	var bm *ui.Bitmap
	var pw, ph int
	if it.Kind == model.KindImage {
		path := model.Str(it.ImageFilePath)
		data, err := os.ReadFile(path)
		if err != nil {
			mg.Hide() // the file is gone: nothing to show
			return
		}
		if bm, err = ui.DecodeBitmap(data); err == nil && bm != nil {
			pw, ph = bm.Size()
		}
	}
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
			mg.win.OnClose(func(e *mygo.CloseEvent) { e.PreventDefault(); mg.win.Hide() })
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
