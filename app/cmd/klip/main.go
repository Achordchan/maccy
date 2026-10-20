// Command klip is the clipboard manager: a tray icon, a panel that a global
// shortcut brings up, and the capture and sync running behind them.
package main

import (
	_ "embed"
	"log"
	"os"
	"runtime"
	"strings"

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
}

func main() {
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
		Changed:    func() { p.redraw() },
	})
	if err != nil {
		log.Fatal(err)
	}
	p.layout = layout
	applyTheme(p.app.Settings.Get().Theme)
	p.app.Settings.OnChange(func(st model.Settings) { applyTheme(st.Theme) })
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
	p.tray, err = mygo.NewTray(mygo.TrayOptions{
		Icon:           trayIcon,
		IconIsTemplate: true,
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
		if err := p.shelf.Follow(p.shelfSettings()); err != nil {
			log.Println("shelf:", err)
		}
	}
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
	if p.win != nil && p.win.IsVisible() {
		p.win.Update(func() {})
	}
	if p.prefsWin != nil && p.prefsWin.IsVisible() {
		p.prefsWin.Update(func() {})
	}
	if p.updWin != nil && p.updWin.IsVisible() {
		p.updWin.Update(func() {})
	}
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
			e.PreventDefault()
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
func (h prefsHost) CheckUpdates() func()   { return nil } // the update module comes later
func (h prefsHost) SyncNow()               { h.p.app.SyncNow() }
func (h prefsHost) AccountChanged(in bool) { h.p.app.AccountChanged(in) }
func (h prefsHost) Close()                 { h.p.prefsWin.Hide() }

// updHost is what the update window asks of the program.
type updHost struct{ p *program }

func (h updHost) Close() { h.p.updWin.Hide() }
func (h updHost) Quit()  { mygo.App.Quit() }
