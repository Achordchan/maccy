// Command klip is the clipboard manager: a tray icon, a panel that a global
// shortcut brings up, and the capture and sync running behind them.
package main

import (
	_ "embed"
	"log"
	"os"
	"runtime"
	"strings"
	"sync/atomic"

	"github.com/egoist/mygo"
	"github.com/egoist/mygo/ui"

	"klip/internal/app"
	"klip/internal/model"
	"klip/internal/panel"
	"klip/internal/paths"
	"klip/internal/platform"
	"klip/internal/prefswin"
	"klip/internal/shelf"
	"klip/internal/shelfwin"
	"klip/internal/update"
	"klip/internal/updatewin"
)

//go:embed icon.png
var trayIcon []byte

// trayTemplate is a black ring on a transparent ground: the menu bar of macOS
// draws a template image in its own colour, and a coloured square would show
// as a solid white block.
//
//go:embed tray.png
var trayTemplate []byte

// shortcuts are tried in turn: the one of the C# client first.
var shortcuts = []string{"Ctrl+Alt+2", "CmdOrCtrl+Alt+Shift+2"}

type program struct {
	app   *app.App
	model *panel.Model
	win   *mygo.Window
	tray  *mygo.Tray

	prefs    *prefswin.Model
	prefsWin *mygo.Window
	upd      *updatewin.Model
	updWin   *mygo.Window

	shelves *shelfwin.Manager
	shelf   *shelf.Service
	layout  paths.Layout

	redrawPending  atomic.Bool // a redraw is queued for the UI thread
	followShelf    func()      // starts or stops the shelf as the settings say
	mandatoryShown atomic.Bool // the window for a mandatory update was put up
	quitting       atomic.Bool // set when the program is quitting, see OnBeforeQuit
}

func main() {
	// The name keys the single-instance lock; by default it is the file name
	// of the executable, so Klip.app and a binary of another name would not
	// see each other.
	mygo.App.SetName("Klip")
	// Started as the helper of an update: replace the files and start the
	// program again. This comes before everything else, as a second
	// instance must not be told to show itself.
	if handled, code := update.HelperMain(os.Args, update.Hooks{}); handled {
		os.Exit(code)
	}
	if !mygo.App.RequestSingleInstanceLock() {
		return // the Klip that runs shows itself (OnSecondInstance)
	}
	if platform.HoldAppMutex() {
		return // another instance holds the named mutex of 1.0.10 and the installer
	}
	mygo.App.SetActivationPolicy(mygo.ActivationPolicyAccessory)
	mygo.App.OnWindowAllClosed(func() {})
	p := &program{}
	mygo.App.OnSecondInstance(func([]string, string) { p.show() })
	mygo.App.OnBeforeQuit(func(*mygo.QuitEvent) { p.quitting.Store(true) })
	mygo.App.WhenReady(p.ready)
	if err := mygo.App.Run(); err != nil {
		log.Fatal(err)
	}
}

func (p *program) ready() {
	layout, err := paths.Default()
	if err != nil {
		log.Fatal(err)
	}
	p.app, err = app.New(app.Config{
		Layout:     layout,
		Clipboard:  platform.Clipboard{},
		Foreground: platform.ForegroundApp,
		ChangeSeq:  platform.ChangeSeq,
		Autostart:  platform.Autostart(),
		Quit:       func() { mygo.App.Quit() },
		Changed: func() {
			p.redraw()
			p.gateMandatory()
		},
	})
	if err != nil {
		log.Fatal(err)
	}
	p.layout = layout
	applyTheme(p.app.Settings.Get().Theme)
	p.app.Settings.OnChange(func(st model.Settings) {
		applyTheme(st.Theme)
		p.redraw() // a window shows the settings (the login among them)
	})
	p.model = panel.New(p.app.History, host{p})
	p.model.Status = p.app.SyncStatus
	p.model.Redraw = p.redraw

	p.win = mygo.NewWindow(mygo.WindowOptions{
		Title:         "Klip",
		Panel:         true,
		Hidden:        true,
		Frameless:     true,
		Transparent:   true,
		Vibrancy:      mygo.VibrancyPopover,
		Width:         560,
		Height:        520,
		DisableResize: true,
		Content:       ui.View(p.model.View),
	})
	p.win.OnClose(func(e *mygo.CloseEvent) {
		if p.quitting.Load() {
			return // Quit closes every window; one that refuses stops it
		}
		e.PreventDefault()
		p.win.Hide()
	})
	p.win.OnBlur(p.win.Hide)

	registered := ""
	for _, s := range shortcuts {
		if err := mygo.GlobalShortcut.Register(s, p.toggle); err == nil {
			registered = s
			break
		} else {
			log.Printf("shortcut %s: %v", s, err)
		}
	}

	tip := "Klip"
	if registered != "" {
		tip += " (" + registered + ")"
	}
	icon, template := trayIcon, false
	if runtime.GOOS == "darwin" {
		icon, template = trayTemplate, true
	}
	p.tray, err = mygo.NewTray(mygo.TrayOptions{
		Icon:           icon,
		IconIsTemplate: template,
		ToolTip:        tip,
		Menu: mygo.NewMenu([]*mygo.MenuItem{
			{Label: "显示 Klip", Click: func(*mygo.MenuItem, *mygo.Window) { p.show() }},
			{Label: "立即同步", Click: func(*mygo.MenuItem, *mygo.Window) { p.app.SyncNow() }},
			{Label: "偏好设置…", Click: func(*mygo.MenuItem, *mygo.Window) { p.showPrefs() }},
			{Label: "检查更新…", Click: func(*mygo.MenuItem, *mygo.Window) { p.checkUpdates() }},
			mygo.Separator(),
			{Role: mygo.RoleQuit},
		}),
	})
	if err != nil {
		log.Println("tray:", err)
	}

	p.startShelf()
	mygo.App.OnQuit(p.stopShelf)
	mygo.App.OnQuit(p.app.Stop)
	p.app.Start()
}

// startShelf makes the shelf follow the settings. It is a Windows feature: it
// listens to the mouse everywhere.
func (p *program) startShelf() {
	if runtime.GOOS != "windows" {
		return
	}
	p.shelves = shelfwin.NewManager()
	p.shelf = &shelf.Service{
		Settings: p.shelfSettings,
		Spawn:    p.shelves.Spawn,
	}
	follow := func() {
		if p.app.Updates.MustUpdate() != nil {
			p.shelf.Stop() // a mandatory update is waiting: no shelf until it is dealt with
			return
		}
		if err := p.shelf.Follow(p.shelfSettings()); err != nil {
			log.Println("shelf:", err)
		}
	}
	p.followShelf = follow
	follow()
	p.app.Settings.OnChange(func(model.Settings) { follow() })
}

func (p *program) shelfSettings() shelf.Settings {
	st := p.app.Settings.Get()
	return shelf.FromSettings(st.ShelfEnabled, st.ShelfTriggerModifier)
}

func (p *program) stopShelf() {
	if p.shelf != nil {
		p.shelf.Stop()
	}
	if p.shelves != nil {
		p.shelves.CloseAll()
	}
}

// redraw asks the visible windows for a frame; any goroutine may call it.
func (p *program) redraw() {
	// One request waits at a time; the window fields are read on the UI thread,
	// and the caller (a settings handler, a sync, the clipboard) is not held up
	// by it, nor can it hold the UI thread up.
	if !p.redrawPending.CompareAndSwap(false, true) {
		return
	}
	go mygo.RunOnMain(func() {
		p.redrawPending.Store(false)
		if p.win != nil && p.win.IsVisible() {
			p.win.Update(func() {})
		}
		if p.prefsWin != nil && p.prefsWin.IsVisible() {
			p.prefsWin.Update(func() {})
		}
		if p.updWin != nil && p.updWin.IsVisible() {
			p.updWin.Update(func() {})
		}
	})
}

// applyTheme sets the look of the windows from the setting: "System",
// "Dark" or "Light".
func applyTheme(v string) {
	switch strings.ToLower(strings.TrimSpace(v)) {
	case "dark":
		mygo.Theme.SetSource(mygo.ThemeDark)
	case "light":
		mygo.Theme.SetSource(mygo.ThemeLight)
	default:
		mygo.Theme.SetSource(mygo.ThemeSystem)
	}
}

// mandatoryPending says whether a mandatory update is known: until it is
// installed (or the program quit) the rest of the program stays shut, and the
// update window is what shows.
func (p *program) mandatoryPending() bool {
	if p.app == nil || p.app.Updates == nil {
		return false
	}
	if p.app.Updates.MustUpdate() == nil {
		return false
	}
	p.showUpdate()
	return true
}

// gateMandatory is called when the state of the program changed: a mandatory
// update found by the quiet check puts its window up at once. Any goroutine.
func (p *program) gateMandatory() {
	if p.app == nil || p.app.Updates == nil {
		return
	}
	if p.app.Updates.MustUpdate() == nil {
		// The mandatory update is gone (withdrawn, or installed): the gate opens
		// again and the shelf comes back as the settings have it.
		if p.mandatoryShown.Swap(false) && p.followShelf != nil {
			p.followShelf()
		}
		return
	}
	if p.mandatoryShown.CompareAndSwap(false, true) {
		mygo.RunOnMain(func() {
			// Whatever is open is shut: preferences, the shelves, the shelf
			// trigger; the update window is all that is left.
			p.win.Hide()
			if p.prefsWin != nil {
				p.prefsWin.Hide()
			}
			p.stopShelf()
			p.showUpdate()
		})
	}
}

// showUpdate opens the update window: it shows what the coordinator knows.
func (p *program) showUpdate() {
	if p.updWin == nil {
		p.upd = updatewin.New(p.app.Updates, updHost{p})
		p.updWin = mygo.NewWindow(mygo.WindowOptions{
			Title:         "Klip 更新",
			Width:         480,
			Height:        400,
			DisableResize: true,
			Content:       ui.View(p.upd.View),
		})
		p.updWin.OnClose(func(e *mygo.CloseEvent) {
			if p.quitting.Load() {
				return
			}
			e.PreventDefault()
			// A mandatory update leaves two ways out, the window's own
			// buttons: update, or quit. The close box is the second.
			if p.app.Updates.MustUpdate() != nil {
				mygo.App.Quit()
				return
			}
			p.app.Updates.Dismiss()
			p.updWin.Hide()
		})
	}
	p.win.Hide()
	p.updWin.Show()
	p.updWin.Focus()
}

// checkUpdates is the user asking: the window shows the check.
func (p *program) checkUpdates() {
	p.showUpdate()
	p.upd.CheckNow()
}

// showPrefs opens the preferences window, made on first use and then only
// hidden when closed.
func (p *program) showPrefs() {
	if p.mandatoryPending() {
		return
	}
	if p.prefsWin == nil {
		p.prefs = prefswin.New(p.app.Prefs, p.app.Account, prefsHost{p})
		p.prefs.Post = func(f func()) { p.prefsWin.Update(f) }
		p.prefs.Redraw = p.redraw
		p.prefs.SyncStatus = p.app.SyncStatus
		p.prefsWin = mygo.NewWindow(mygo.WindowOptions{
			Title:   "Klip 偏好设置",
			Width:   640,
			Height:  640,
			Content: ui.View(p.prefs.View),
		})
		p.prefsWin.OnClose(func(e *mygo.CloseEvent) {
			if p.quitting.Load() {
				return
			}
			e.PreventDefault()
			p.prefsWin.Hide()
		})
	}
	p.win.Hide()
	p.prefs.Open()
	p.prefsWin.Show()
	p.prefsWin.Focus()
}

func (p *program) show() {
	if p.mandatoryPending() {
		return // the update window is up: nothing else until it is dealt with
	}
	d := mygo.Screen.DisplayNearestPoint(mygo.Screen.CursorScreenPoint())
	w, h := p.win.Size()
	p.win.SetPosition(d.WorkArea.X+(d.WorkArea.Width-w)/2, d.WorkArea.Y+(d.WorkArea.Height-h)/3)
	p.model.Reset()
	p.win.Show()
}

func (p *program) toggle() {
	if p.win.IsVisible() {
		p.win.Hide()
	} else {
		p.show()
	}
}

// host is what the panel asks of the program.
type host struct{ p *program }

func (h host) Apply(it model.Item) { h.p.app.Apply(it) }
func (h host) Hide()               { h.p.win.Hide() }
func (h host) SyncNow()            { h.p.app.SyncNow() }
func (h host) ShowPrefs()          { h.p.showPrefs() }
func (h host) ShowUpdate()         { h.p.showUpdate() }
func (h host) UpdateAvailable() string {
	if i := h.p.app.Updates.Available(); i != nil {
		return i.Version
	}
	return ""
}
func (h host) Quit() { mygo.App.Quit() }

// prefsHost is what the preferences window asks of the program.
type prefsHost struct{ p *program }

func (h prefsHost) OpenStorage()           { _ = mygo.Shell.OpenPath(h.p.layout.Root) }
func (h prefsHost) CheckUpdates() func()   { return h.p.checkUpdates }
func (h prefsHost) SyncNow()               { h.p.app.SyncNow() }
func (h prefsHost) AccountChanged(in bool) { h.p.app.AccountChanged(in) }
func (h prefsHost) Close()                 { h.p.prefsWin.Hide() }

// updHost is what the update window asks of the program.
type updHost struct{ p *program }

func (h updHost) Close() { h.p.updWin.Hide() }
func (h updHost) Quit()  { mygo.App.Quit() }
