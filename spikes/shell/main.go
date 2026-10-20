// Spike for Klip 1.0.11: a tray icon, a panel summoned by Ctrl+Alt+2, a
// clipboard poller and a long virtualized list, to measure MyGo against what
// the clipboard manager needs. Not shipped.
package main

import (
	"bytes"
	"crypto/sha256"
	_ "embed"
	"encoding/hex"
	"flag"
	"fmt"
	"image"
	"image/color"
	"image/png"
	"log"
	"runtime"
	"strings"
	"sync/atomic"
	"time"

	"github.com/egoist/mygo"
	"github.com/egoist/mygo/ui"
)

//go:embed resources/icon.png
var trayIcon []byte

var (
	pollEvery = flag.Duration("poll", 400*time.Millisecond, "clipboard poll interval")
	useSeq    = flag.Bool("seq", true, "ask the platform's change counter before reading the clipboard")
)

var shortcuts = []string{"Ctrl+Alt+2", "CmdOrCtrl+Alt+Shift+2"}

type itemKind int

const (
	kindText itemKind = iota
	kindImage
	kindFiles
)

type item struct {
	n    int
	kind itemKind
	text string // text, or the file paths joined
	png  []byte
	bm   *ui.Bitmap
	at   time.Time
}

type klip struct {
	panel *mygo.Window
	tray  *mygo.Tray

	items   []*item // newest first
	results []*item
	query   string
	chosen  int
	list    ui.ListState
	input   ui.Handle
	nextN   int

	shortcut string

	// poller bookkeeping, read by the footer
	reads     int // clipboard reads made
	skipped   atomic.Int64 // polls that the change counter spared; counted off the UI thread, so no redraw per poll
	captures  int
	lastFmts  string
	lastHash  string
	ownHash   string // what the app just wrote itself: not captured again
	seqLast   int64
	seqActive bool

	// frame timing
	frames    int
	viewTotal time.Duration
	viewMax   time.Duration
	mem       runtime.MemStats
	status    string
}

func (k *klip) view(c *ui.Context) {
	start := time.Now()
	defer func() {
		d := time.Since(start)
		k.frames++
		k.viewTotal += d
		k.viewMax = max(k.viewMax, d)
	}()

	t := c.Theme()
	c.Root().Background(t.Background)

	if k.query != "" && c.Shortcut(0, ui.KeyEscape) {
		k.query = ""
		k.filter()
	}
	if c.Shortcut(0, ui.KeyDown) {
		k.choose(k.chosen + 1)
	}
	if c.Shortcut(0, ui.KeyUp) {
		k.choose(k.chosen - 1)
	}
	if c.Shortcut(0, ui.KeyEnter) {
		k.apply(k.chosen)
	}
	c.OnShortcut(ui.Cmd, ui.KeyQ, mygo.App.Quit)

	ui.Column(c).Fill().Children(func() {
		ui.Row(c).Padding(12, 14).Gap(8).AlignItems(ui.Center).Children(func() {
			if ui.TextInputBase(c, &k.query).Bind(&k.input).Grow(1).FontSize(18).
				Placeholder("Search clipboard history").Label("Search").Changed() {
				k.filter()
			}
		})
		ui.Row(c).Padding(0, 14, 8).Gap(8).Children(func() {
			if ui.Button(c, "+1k").Clicked() {
				k.fake(1000)
			}
			if ui.Button(c, "+10k").Clicked() {
				k.fake(10000)
			}
			if ui.Button(c, "+100k").Clicked() {
				k.fake(100000)
			}
			if ui.Button(c, "Clear").Clicked() {
				k.items = nil
				k.filter()
			}
		})
		ui.Divider(c)

		k.list.Selected = &k.chosen
		k.list.Key = func(i int) any { return k.results[i].n }
		k.list.Label = func(i int) string { return k.results[i].text }
		list := ui.List(c, &k.list, len(k.results), func(i int) { k.row(c, i) }).Grow(1).Padding(6).Children(func() {
			if len(k.results) == 0 {
				ui.Text(c, "Copy something").TextColor(t.TextMuted).Padding(16)
			}
		})
		if list.Submitted() {
			k.apply(k.chosen)
		}

		ui.Divider(c)
		ui.Column(c).Padding(6, 14).Gap(2).Children(func() {
			ui.Text(c, k.statsLine()).FontSize(11).TextColor(t.TextMuted)
			ui.Text(c, k.status).FontSize(11).TextColor(t.TextMuted).SingleLine()
		})
	})
}

func (k *klip) row(c *ui.Context, i int) {
	it := k.results[i]
	t := c.Theme()
	bg := ui.Transparent
	text, muted := t.Text, t.TextMuted
	if i == k.chosen {
		bg = t.Accent.Alpha(0.85)
		text, muted = t.AccentText, t.AccentText.Alpha(0.75)
	}
	ui.Row(c).Padding(6, 10).Gap(10).AlignItems(ui.Center).Radius(8).Background(bg).Children(func() {
		if it.kind == kindImage && it.bm != nil {
			ui.Image(c, it.bm).Size(36, 36)
		}
		ui.Column(c).Grow(1).Gap(1).Children(func() {
			label := it.text
			switch it.kind {
			case kindImage:
				label = fmt.Sprintf("Image %d KB", len(it.png)/1024)
			case kindFiles:
				label = "Files: " + it.text
			}
			ui.Text(c, oneLine(label)).TextColor(text).SingleLine()
			ui.Text(c, it.at.Format("15:04:05")).FontSize(11).TextColor(muted).SingleLine()
		})
	})
}

func (k *klip) statsLine() string {
	avg := time.Duration(0)
	if k.frames > 0 {
		avg = k.viewTotal / time.Duration(k.frames)
	}
	return fmt.Sprintf("items %d  shown %d | polls: reads %d spared %d captured %d | view avg %v max %v (%d frames) | heap %d MB sys %d MB",
		len(k.items), len(k.results), k.reads, k.skipped.Load(), k.captures,
		avg.Round(time.Microsecond), k.viewMax.Round(time.Microsecond), k.frames,
		k.mem.HeapAlloc>>20, k.mem.Sys>>20)
}

func oneLine(s string) string {
	s = strings.Join(strings.Fields(s), " ")
	if r := []rune(s); len(r) > 120 {
		return string(r[:120]) + "…"
	}
	return s
}

func (k *klip) filter() {
	q := strings.ToLower(strings.TrimSpace(k.query))
	clear(k.results) // drop the references the backing array still holds
	k.results = k.results[:0]
	for _, it := range k.items {
		if q == "" || strings.Contains(strings.ToLower(it.text), q) {
			k.results = append(k.results, it)
		}
	}
	k.choose(0)
}

func (k *klip) choose(i int) {
	k.chosen = max(0, min(i, len(k.results)-1))
	if len(k.results) > 0 {
		k.list.ScrollIntoView(k.chosen)
	}
}

// fake adds n text items (and a thumbnail on every tenth) to measure the
// list.
func (k *klip) fake(n int) {
	thumb := thumbnail()
	bm, _ := ui.DecodeBitmap(thumb)
	now := time.Now()
	add := make([]*item, 0, n)
	for i := 0; i < n; i++ {
		k.nextN++
		it := &item{n: k.nextN, kind: kindText, at: now,
			text: fmt.Sprintf("Fake clipboard entry %d — the quick brown fox jumps over the lazy dog, 剪贴板条目 %d", k.nextN, k.nextN)}
		if i%10 == 0 {
			it.kind, it.png, it.bm = kindImage, thumb, bm
		}
		add = append(add, it)
	}
	k.items = append(add, k.items...)
	k.filter()
}

func thumbnail() []byte {
	img := image.NewRGBA(image.Rect(0, 0, 64, 64))
	for y := 0; y < 64; y++ {
		for x := 0; x < 64; x++ {
			img.Set(x, y, color.RGBA{uint8(x * 4), uint8(y * 4), 160, 255})
		}
	}
	var b bytes.Buffer
	_ = png.Encode(&b, img)
	return b.Bytes()
}

// apply puts an item back on the clipboard: what the real app does when the
// user picks a history entry.
func (k *klip) apply(i int) {
	if i < 0 || i >= len(k.results) {
		return
	}
	it := k.results[i]
	var err error
	switch it.kind {
	case kindText:
		k.ownHash = hashOf(fmt.Sprint(kindText), it.text)
		mygo.Clipboard.WriteText(it.text)
	case kindImage:
		k.ownHash = hashOf(fmt.Sprint(kindImage), string(it.png))
		err = mygo.Clipboard.WriteImage(it.png)
	case kindFiles:
		paths := strings.Split(it.text, "\n")
		k.ownHash = hashOf(fmt.Sprint(kindFiles), it.text)
		err = mygo.Clipboard.WriteFiles(paths...)
	}
	k.status = fmt.Sprintf("applied item %d (err=%v)", it.n, err)
	k.panel.Hide()
}

func hashOf(kind, s string) string {
	sum := sha256.Sum256([]byte(kind + "\x00" + s))
	return hex.EncodeToString(sum[:8])
}

// poll watches the clipboard. With a change counter it reads only when the
// counter moved; without one it reads the text and hashes it every time.
func (k *klip) poll() {
	k.seqActive = *useSeq && changeSeq() >= 0
	k.seqLast = changeSeq()
	log.Printf("poller start: seqActive=%v seq=%d", k.seqActive, k.seqLast)
	k.panel.Update(func() { k.status = fmt.Sprintf("poll every %v, change counter: %v", *pollEvery, k.seqActive) })
	tick := time.NewTicker(*pollEvery)
	defer tick.Stop()
	for range tick.C {
		if k.seqActive {
			s := changeSeq()
			if s == k.seqLast {
				k.skipped.Add(1)
				continue
			}
			log.Printf("change counter %d -> %d", k.seqLast, s)
			prev := k.seqLast
			k.seqLast = s
			if !k.capture() {
				k.seqLast = prev // the clipboard could not be read yet: try again at the next tick
			}
			continue
		}
		k.capture()
	}
}

// capture reads the clipboard once and adds what it finds. It runs on the
// poll goroutine: MyGo hops each clipboard call to the main thread.
// capture reads the clipboard and adds it to the list. It reports false when
// nothing could be read (another program may still hold the clipboard open,
// which also empties Formats), so the caller can try again; an emptied
// clipboard is retried at every tick until something is copied, which is
// cheap and good enough for a spike.
func (k *klip) capture() bool {
	formats := mygo.Clipboard.Formats()
	var (
		kind  itemKind
		text  string
		img   []byte
		files []string
	)
	if f, err := mygo.Clipboard.ReadFiles(); err == nil && len(f) > 0 {
		kind, files = kindFiles, f
		text = strings.Join(f, "\n")
	} else if b := mygo.Clipboard.ReadImage(); len(b) > 0 {
		kind, img = kindImage, b
	} else {
		kind, text = kindText, mygo.Clipboard.ReadText()
	}
	_ = files
	if kind == kindText && text == "" {
		return false
	}
	h := hashOf(fmt.Sprint(kind), text+string(img))
	var bm *ui.Bitmap
	if kind == kindImage {
		bm, _ = ui.DecodeBitmap(img)
	}

	k.panel.Update(func() {
		k.reads++
		k.lastFmts = fmt.Sprint(formats)
		if h == k.ownHash {
			// our own write, seen once: remember it as the last content, but
			// do not let it hide a later copy of the same thing
			k.ownHash, k.lastHash = "", h
			return
		}
		k.ownHash = "" // the next observation is not our own write, whatever it is
		if h == k.lastHash || (kind == kindText && strings.TrimSpace(text) == "") {
			return
		}
		k.lastHash = h
		k.nextN++
		k.captures++
		k.items = append([]*item{{n: k.nextN, kind: kind, text: text, png: img, bm: bm, at: time.Now()}}, k.items...)
		k.filter()
		k.status = fmt.Sprintf("captured #%d kind=%d formats=%s", k.nextN, kind, k.lastFmts)
		log.Printf("captured kind=%d formats=%s bytes=%d", kind, k.lastFmts, len(text)+len(img))
	})
	return true
}

func (k *klip) show() {
	log.Printf("show: visible=%v", k.panel.IsVisible())
	d := mygo.Screen.DisplayNearestPoint(mygo.Screen.CursorScreenPoint())
	w, h := k.panel.Size()
	k.panel.SetPosition(d.WorkArea.X+(d.WorkArea.Width-w)/2, d.WorkArea.Y+(d.WorkArea.Height-h)/3)
	k.query = ""
	k.filter()
	k.input.Focus()
	k.panel.Show()
	log.Printf("shown: visible=%v", k.panel.IsVisible())
}

func (k *klip) toggle() {
	if k.panel.IsVisible() {
		k.panel.Hide()
	} else {
		k.show()
	}
}

func (k *klip) ready() {
	k.panel = mygo.NewWindow(mygo.WindowOptions{
		Title:         "Klip spike",
		Panel:         true,
		Hidden:        true,
		Frameless:     true,
		Transparent:   true,
		Vibrancy:      mygo.VibrancyPopover,
		Width:         560,
		Height:        520,
		DisableResize: true,
		Content:       ui.View(k.view),
	})
	k.panel.OnClose(func(e *mygo.CloseEvent) {
		e.PreventDefault()
		k.panel.Hide()
	})
	k.panel.OnBlur(func() { log.Print("blur: hide"); k.panel.Hide() })

	for _, s := range shortcuts {
		if err := mygo.GlobalShortcut.Register(s, k.toggle); err == nil {
			k.shortcut = s
			break
		} else {
			log.Printf("shortcut %s: %v", s, err)
		}
	}
	log.Printf("global shortcut: %q", k.shortcut)

	tray, err := mygo.NewTray(mygo.TrayOptions{
		Icon:           trayIcon,
		IconIsTemplate: true,
		ToolTip:        "Klip spike",
		Menu: mygo.NewMenu([]*mygo.MenuItem{
			{Label: "Show", Click: func(*mygo.MenuItem, *mygo.Window) { k.show() }},
			mygo.Separator(),
			{Role: mygo.RoleQuit},
		}),
	})
	if err != nil {
		log.Println("tray:", err)
	}
	k.tray = tray

	go k.poll()
	go func() { // refresh memory numbers while the panel shows
		for range time.Tick(2 * time.Second) {
			var m runtime.MemStats
			runtime.ReadMemStats(&m)
			if k.panel.IsVisible() {
				k.panel.Update(func() { k.mem = m })
			}
		}
	}()
}

func main() {
	flag.Parse()
	if !mygo.App.RequestSingleInstanceLock() {
		return
	}
	mygo.App.SetActivationPolicy(mygo.ActivationPolicyAccessory)
	mygo.App.OnWindowAllClosed(func() {})
	k := &klip{}
	mygo.App.OnSecondInstance(func([]string, string) { k.show() })
	mygo.App.WhenReady(k.ready)
	if err := mygo.App.Run(); err != nil {
		log.Fatal(err)
	}
}
