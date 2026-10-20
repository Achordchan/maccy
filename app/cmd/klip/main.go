// Command klip is the clipboard manager: a tray icon, a panel that a global
// shortcut brings up, and the capture and sync running behind them.
package main

import (
	_ "embed"
	"log"

	"github.com/egoist/mygo"
	"github.com/egoist/mygo/ui"

	"klip/internal/app"
	"klip/internal/model"
	"klip/internal/panel"
	"klip/internal/paths"
	"klip/internal/platform"
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
}

func main() {
	if !mygo.App.RequestSingleInstanceLock() {
		return // the Klip that runs shows itself (OnSecondInstance)
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
		Changed:    func() { p.redraw() },
	})
	if err != nil {
		log.Fatal(err)
	}
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
			mygo.Separator(),
			{Role: mygo.RoleQuit},
		}),
	})
	if err != nil {
		log.Println("tray:", err)
	}

	mygo.App.OnQuit(p.app.Stop)
	p.app.Start()
}

// redraw asks the panel for a frame; any goroutine may call it.
func (p *program) redraw() {
	if p.win != nil && p.win.IsVisible() {
		p.win.Update(func() {})
	}
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
func (h host) Quit()               { mygo.App.Quit() }
