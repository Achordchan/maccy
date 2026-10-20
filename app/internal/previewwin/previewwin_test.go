package previewwin

import (
	"image"
	"image/color"
	"image/png"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/egoist/mygo/ui"

	"klip/internal/model"
)

func item(kind model.Kind) model.Item {
	t := model.Time{Time: time.Date(2026, 5, 30, 18, 5, 9, 0, time.UTC)}
	first := model.Time{Time: time.Date(2026, 5, 1, 9, 0, 0, 0, time.UTC)}
	return model.Item{ID: "1", Kind: kind, CapturedAt: t, FirstCapturedAt: &first, CopyCount: 3}
}

func TestSize(t *testing.T) {
	if w, h := Size(item(model.KindText), 0, 0); w != 360 || h != 320 {
		t.Fatalf("text %dx%d", w, h)
	}
	if w, h := Size(item(model.KindFileList), 0, 0); w != 320 || h != 170 {
		t.Fatalf("files %dx%d", w, h)
	}
	img := item(model.KindImage)
	// small pictures are enlarged to a short side of 152: 100x50 -> 304x152, window 324 x 260
	if w, h := Size(img, 100, 50); w != 324 || h != 260 {
		t.Fatalf("small image %dx%d", w, h)
	}
	// large ones are scaled to the largest picture (980-20-88 = 872 on its long side)
	if w, h := Size(img, 4000, 3000); w != 892 || h != 762 {
		t.Fatalf("large image %dx%d", w, h)
	}
	if w, h := Size(img, 0, 0); w != 260 || h != 260 {
		t.Fatalf("unknown image %dx%d", w, h)
	}
}

func TestPlace(t *testing.T) {
	wa := Rect{0, 0, 1920, 1080}
	// to the right of the row, centred on its height
	x, y, w, h := Place(Rect{100, 400, 360, 30}, 360, 320, wa)
	if x != 100+360+12 || y != 400+15-160 || w != 360 || h != 320 {
		t.Fatalf("right: %d,%d %dx%d", x, y, w, h)
	}
	// no room on the right: to the left
	x, _, _, _ = Place(Rect{1500, 400, 360, 30}, 360, 320, wa)
	if x != 1500-12-360 {
		t.Fatalf("left: x=%d", x)
	}
	// kept inside the area at the top and the bottom
	_, y, _, _ = Place(Rect{100, 5, 360, 30}, 360, 320, wa)
	if y != 0 {
		t.Fatalf("top: y=%d", y)
	}
	_, y, _, _ = Place(Rect{100, 1060, 360, 30}, 360, 320, wa)
	if y != 1080-320 {
		t.Fatalf("bottom: y=%d", y)
	}
	// larger than the area: made smaller
	_, _, w, h = Place(Rect{0, 0, 10, 10}, 980, 980, Rect{0, 0, 800, 600})
	if w != 776 || h != 576 {
		t.Fatalf("shrunk to %dx%d", w, h)
	}
}

func TestFormatBytes(t *testing.T) {
	for n, want := range map[int64]string{0: "0 KB", 1: "1 KB", 1500: "1 KB", 2048: "2 KB", 1 << 20: "1 MB", 3 << 19: "1.5 MB", 5 << 30: "5 GB"} {
		if got := FormatBytes(n); got != want {
			t.Errorf("FormatBytes(%d) = %q, want %q", n, got, want)
		}
	}
}

func TestLines(t *testing.T) {
	m := &Model{Item: item(model.KindText)}
	if got := m.Meta(); got != "复制次数：3" {
		t.Fatalf("meta = %q", got)
	}
	if got := m.Times(); got != "首次：2026-05-01 09:00:00\n最后：2026-05-30 18:05:09" {
		t.Fatalf("times = %q", got)
	}
	if m.NoteLine() != "备注：（无备注）" {
		t.Fatalf("note = %q", m.NoteLine())
	}
	m.Item.Note = model.Ptr("买菜")
	if m.NoteLine() != "备注：买菜" {
		t.Fatalf("note = %q", m.NoteLine())
	}
	m.Item.FirstCapturedAt = nil
	if got := m.Times(); got != "首次：2026-05-30 18:05:09\n最后：2026-05-30 18:05:09" {
		t.Fatalf("without a first time: %q", got)
	}

	dir := t.TempDir()
	a := filepath.Join(dir, "a.txt")
	_ = os.WriteFile(a, make([]byte, 3000), 0o644)
	f := &Model{Item: item(model.KindFileList)}
	f.Item.FilePaths = []string{a, filepath.Join(dir, "gone.txt")}
	if got := f.Meta(); got != "复制次数：3 · 2 个文件 · 3 KB" {
		t.Fatalf("files meta = %q", got)
	}
	f.Item.FilePaths = []string{filepath.Join(dir, "gone.txt")}
	f.Item.ApproxBytes = 2 << 20
	if got := f.Meta(); got != "复制次数：3 · 1 个文件 · 2 MB" {
		t.Fatalf("files that are gone use the recorded size: %q", got)
	}

	if (&Model{}).Source() != "" {
		t.Fatal("no source")
	}
	s := &Model{Item: item(model.KindText)}
	s.Item.SourceAppPath = model.Ptr(`C:\Program Files\Notepad++\notepad++.exe`)
	if s.Source() != "notepad++" {
		t.Fatalf("source from the path = %q", s.Source())
	}
	s.Item.SourceAppName = model.Ptr("记事本")
	if s.Source() != "记事本" {
		t.Fatalf("source = %q", s.Source())
	}
}

func TestViewAndLeft(t *testing.T) {
	m := &Model{Item: item(model.KindText)}
	m.Item.Text = model.Ptr("hello preview")
	left := 0
	m.Left = func() { left++ }
	tt := ui.NewTester(m.View, 360, 320)
	for _, s := range []string{"hello preview", "复制次数：3", "备注：（无备注）"} {
		if !tt.HasText(s) {
			t.Fatalf("missing %q in %q", s, tt.Texts())
		}
	}
	tt.Move(100, 100)
	tt.Frame()
	if !m.Over() {
		t.Fatal("not over")
	}
	tt.Move(-5, -5)
	tt.Frame()
	if m.Over() || left != 1 {
		t.Fatalf("over=%v left=%d", m.Over(), left)
	}
}

func TestImageButtonOpens(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "a.png")
	img := image.NewRGBA(image.Rect(0, 0, 40, 20))
	for x := 0; x < 40; x++ {
		for y := 0; y < 20; y++ {
			img.Set(x, y, color.RGBA{200, 50, 50, 255})
		}
	}
	f, _ := os.Create(p)
	_ = png.Encode(f, img)
	f.Close()
	data, _ := os.ReadFile(p)
	bm, err := ui.DecodeBitmap(data)
	if err != nil {
		t.Fatal(err)
	}
	m := &Model{Item: item(model.KindImage), Bitmap: bm, Width: 40, Height: 20}
	m.Item.ImageFilePath = model.Ptr(p)
	var opened string
	m.Open = func(path string) { opened = path }
	tt := ui.NewTester(m.View, 324, 260)
	if !tt.HasText("复制次数：3 · 40×20 · 1 KB") {
		t.Fatalf("texts: %q", tt.Texts())
	}
	if err := tt.Click("查看原图"); err != nil {
		t.Fatal(err)
	}
	if opened != p {
		t.Fatalf("opened %q", opened)
	}
}
