package panel

import (
	"fmt"
	"testing"
	"time"

	"github.com/egoist/mygo/ui"

	"klip/internal/history"
	"klip/internal/model"
	"klip/internal/paths"
)

func ti(id, text string) model.Item {
	t := model.Time{Time: time.Date(2026, 5, 30, 12, 0, 0, 0, time.UTC)}
	return model.Item{ID: id, Kind: model.KindText, CapturedAt: t, Text: model.Ptr(text), CopyCount: 1,
		ContentHash: model.Ptr("text:" + text)}
}

func TestMatch(t *testing.T) {
	it := ti("1", "Hello World")
	it.Note = model.Ptr("买菜清单")
	it.FilePaths = []string{`C:\Docs\Report.PDF`}
	it.ImageFilePath = model.Ptr("/x/img/abc.png")
	for q, want := range map[string]bool{
		"hello": true, "WORLD": true, "清单": true, "report.pdf": true, "ABC.png": true, "nope": false,
	} {
		if got := Match(it, q); got != want {
			t.Errorf("Match(%q) = %v, want %v", q, got, want)
		}
	}
}

func TestRowsOrderAndQuery(t *testing.T) {
	a, b, c, d := ti("a", "alpha"), ti("b", "beta"), ti("c", "gamma alpha"), ti("d", "delta")
	b.Pinned, d.Pinned = true, true
	ids := func(in []model.Item) string {
		s := ""
		for _, it := range in {
			s += it.ID
		}
		return s
	}
	items := []model.Item{a, b, c, d}
	if got := ids(Rows(items, "")); got != "bdac" {
		t.Fatalf("pinned first, each in history order: %q", got)
	}
	if got := ids(Rows(items, "  alpha ")); got != "ac" {
		t.Fatalf("query is trimmed: %q", got)
	}
	if got := ids(Rows(items, "   ")); got != "bdac" {
		t.Fatalf("blank query matches all: %q", got)
	}
}

func TestPreviewAndDetail(t *testing.T) {
	long := ""
	for i := 0; i < 300; i++ {
		long += "字"
	}
	it := ti("1", "  line one\n\n   line   two\t")
	if got := Preview(it); got != "line one line two" {
		t.Fatalf("Preview = %q", got)
	}
	if got := []rune(Preview(ti("2", long))); len(got) != 141 || got[140] != '…' {
		t.Fatalf("long text cut to %d runes", len(got))
	}
	f := model.Item{Kind: model.KindFileList, FilePaths: []string{`C:\a\b.txt`, "/x/y/c.pdf"}}
	if got := Preview(f); got != "b.txt、c.pdf" {
		t.Fatalf("files = %q", got)
	}
	if Preview(model.Item{Kind: model.KindImage}) != "图片" {
		t.Fatal("image preview")
	}

	now := time.Date(2026, 5, 30, 18, 0, 0, 0, time.UTC)
	d := ti("3", "x")
	d.SourceAppName = model.Ptr("Notes")
	d.CopyCount = 3
	if got := Detail(d, now); got != "12:00 · Notes · 复制 3 次" {
		t.Fatalf("Detail today = %q", got)
	}
	d.CapturedAt = model.Time{Time: time.Date(2026, 5, 2, 9, 5, 0, 0, time.UTC)}
	if got := Detail(d, now); got != "05-02 09:05 · Notes · 复制 3 次" {
		t.Fatalf("Detail this year = %q", got)
	}
	d.CapturedAt = model.Time{Time: time.Date(2025, 5, 2, 9, 5, 0, 0, time.UTC)}
	d.SourceAppName, d.CopyCount = nil, 1
	if got := Detail(d, now); got != "2025-05-02 09:05" {
		t.Fatalf("Detail other year = %q", got)
	}
	img := model.Item{Kind: model.KindImage, CapturedAt: d.CapturedAt, ApproxBytes: 1536}
	if got := Detail(img, now); got != "2025-05-02 09:05 · 2 KB" {
		t.Fatalf("Detail image = %q", got)
	}
}

type fakeHost struct {
	applied []string
	hidden  int
	synced  int
	prefs   int
	quit    int
	update  string
	updates int
}

func (h *fakeHost) Apply(it model.Item)     { h.applied = append(h.applied, model.Str(it.Text)) }
func (h *fakeHost) Hide()                   { h.hidden++ }
func (h *fakeHost) SyncNow()                { h.synced++ }
func (h *fakeHost) ShowPrefs()              { h.prefs++ }
func (h *fakeHost) UpdateAvailable() string { return h.update }
func (h *fakeHost) ShowUpdate()             { h.updates++ }
func (h *fakeHost) Quit()                   { h.quit++ }

type rig struct {
	t  *testing.T
	h  *history.Service
	m  *Model
	tt *ui.Tester
	ho *fakeHost
}

func newRig(t *testing.T, texts ...string) *rig {
	h := history.NewService(paths.Layout{Root: t.TempDir()})
	for i := len(texts) - 1; i >= 0; i-- {
		h.Add(ti(fmt.Sprintf("00000000-0000-0000-0000-%012d", i+1), texts[i]))
	}
	ho := &fakeHost{}
	m := New(h, ho)
	m.Now = func() time.Time { return time.Date(2026, 5, 30, 18, 0, 0, 0, time.UTC) }
	tt := ui.NewTester(m.View, 560, 520)
	return &rig{t: t, h: h, m: m, tt: tt, ho: ho}
}

func (r *rig) texts() []string {
	var out []string
	for _, it := range r.h.Items() {
		out = append(out, model.Str(it.Text))
	}
	return out
}

func TestViewShowsItemsAndEmptyState(t *testing.T) {
	r := newRig(t, "first", "second")
	for _, s := range []string{"first", "second", "2 条"} {
		if !r.tt.HasText(s) {
			t.Fatalf("missing %q in %q", s, r.tt.Texts())
		}
	}
	e := newRig(t)
	if !e.tt.HasText("复制点什么，它会出现在这里") {
		t.Fatalf("empty state: %q", e.tt.Texts())
	}
}

func TestViewSearch(t *testing.T) {
	r := newRig(t, "apple pie", "banana", "apple tart")
	r.m.Reset()
	r.tt.Frame()
	r.tt.Type("apple")
	if r.tt.HasText("banana") || !r.tt.HasText("apple pie") || !r.tt.HasText("apple tart") {
		t.Fatalf("after searching: %q", r.tt.Texts())
	}
	r.tt.Type("zzz")
	if !r.tt.HasText("没有匹配的记录") {
		t.Fatalf("no-match state: %q", r.tt.Texts())
	}
	r.tt.Key(0, ui.KeyEscape) // clears the query, does not hide
	if r.ho.hidden != 0 || !r.tt.HasText("banana") {
		t.Fatalf("escape with a query: hidden=%d texts=%q", r.ho.hidden, r.tt.Texts())
	}
	r.tt.Key(0, ui.KeyEscape) // now hides
	if r.ho.hidden != 1 {
		t.Fatalf("escape without a query: hidden=%d", r.ho.hidden)
	}
}

func TestViewClickApplies(t *testing.T) {
	r := newRig(t, "one", "two", "three")
	if err := r.tt.Click("two"); err != nil {
		t.Fatal(err)
	}
	if len(r.ho.applied) != 1 || r.ho.applied[0] != "two" || r.ho.hidden != 1 {
		t.Fatalf("applied=%v hidden=%d", r.ho.applied, r.ho.hidden)
	}
}

func TestViewKeyboard(t *testing.T) {
	r := newRig(t, "one", "two", "three")
	r.tt.Frame()
	r.tt.Key(0, ui.KeyDown)
	r.tt.Key(0, ui.KeyDown)
	r.tt.Key(0, ui.KeyDown) // past the end stays on the last
	r.tt.Key(0, ui.KeyUp)
	r.tt.Key(0, ui.KeyEnter)
	if len(r.ho.applied) != 1 || r.ho.applied[0] != "two" {
		t.Fatalf("applied = %v", r.ho.applied)
	}
}

func TestViewContextMenu(t *testing.T) {
	r := newRig(t, "one", "two", "three")
	if err := r.tt.RightClick("two"); err != nil {
		t.Fatal(err)
	}
	menu := r.tt.Menu()
	for _, want := range []string{"粘贴", "置顶", "备注…", "删除"} {
		found := false
		for _, m := range menu {
			if m == want {
				found = true
			}
		}
		if !found {
			t.Fatalf("menu %q lacks %q", menu, want)
		}
	}
	if err := r.tt.ChooseMenuItem("置顶"); err != nil {
		t.Fatal(err)
	}
	items := r.h.Items()
	if !items[1].Pinned || model.Str(items[1].Text) != "two" {
		t.Fatalf("not pinned: %+v", items)
	}
	r.tt.Frame()
	if !r.tt.HasText("置顶") {
		t.Fatalf("no pin badge: %q", r.tt.Texts())
	}
	// pinned rows come first on screen: the first text row is "two"
	if rows := r.m.rows; model.Str(rows[0].Text) != "two" {
		t.Fatalf("pinned not first: %v", rows)
	}

	if err := r.tt.RightClick("one"); err != nil {
		t.Fatal(err)
	}
	if err := r.tt.ChooseMenuItem("删除"); err != nil {
		t.Fatal(err)
	}
	if got := r.texts(); len(got) != 2 || got[0] != "two" && got[0] != "one" {
		t.Fatalf("after delete: %v", got)
	}
	for _, s := range r.texts() {
		if s == "one" {
			t.Fatalf("not deleted: %v", r.texts())
		}
	}
}

func TestViewNoteEditing(t *testing.T) {
	r := newRig(t, "one", "two")
	if err := r.tt.RightClick("one"); err != nil {
		t.Fatal(err)
	}
	if err := r.tt.ChooseMenuItem("备注…"); err != nil {
		t.Fatal(err)
	}
	r.tt.Frame()
	if !r.tt.HasText("保存") {
		t.Fatalf("no note bar: %q", r.tt.Texts())
	}
	r.tt.Type("买菜")
	if err := r.tt.Click("保存"); err != nil {
		t.Fatal(err)
	}
	var note string
	for _, it := range r.h.Items() {
		if model.Str(it.Text) == "one" {
			note = model.Str(it.Note)
		}
	}
	if note != "买菜" {
		t.Fatalf("note = %q", note)
	}
	r.tt.Frame()
	if r.tt.HasText("保存") || !r.tt.HasText("备注：买菜") {
		t.Fatalf("after saving: %q", r.tt.Texts())
	}

	// A note is found by search.
	r.m.query = "买菜"
	r.tt.Frame()
	if r.tt.HasText("two") || !r.tt.HasText("one") {
		t.Fatalf("search by note: %q", r.tt.Texts())
	}
}

func TestViewNoteCancelAndClear(t *testing.T) {
	r := newRig(t, "one")
	r.h.UpdateNote(r.h.Items()[0].ID, "old")
	r.tt.Frame()
	if err := r.tt.RightClick("one"); err != nil {
		t.Fatal(err)
	}
	if err := r.tt.ChooseMenuItem("备注…"); err != nil {
		t.Fatal(err)
	}
	r.tt.Frame()
	r.tt.Type("zz")
	if err := r.tt.Click("取消"); err != nil {
		t.Fatal(err)
	}
	if got := model.Str(r.h.Items()[0].Note); got != "old" {
		t.Fatalf("cancel changed the note: %q", got)
	}
}

func TestViewFollowsHistoryChanges(t *testing.T) {
	r := newRig(t, "one")
	redraws := 0
	r.m.Redraw = func() { redraws++ }
	r.h.Add(ti("00000000-0000-0000-0000-0000000000ff", "fresh from the clipboard"))
	if redraws == 0 {
		t.Fatal("a change did not ask for a frame")
	}
	r.tt.Frame()
	if !r.tt.HasText("fresh from the clipboard") {
		t.Fatalf("new item not shown: %q", r.tt.Texts())
	}
}

func TestViewButtons(t *testing.T) {
	r := newRig(t, "one")
	if err := r.tt.Click("同步"); err != nil {
		t.Fatal(err)
	}
	if r.ho.synced != 1 {
		t.Fatal("sync button")
	}
	r.tt.Key(ui.Cmd, ui.KeyQ)
	if r.ho.quit != 1 {
		t.Fatal("quit shortcut")
	}
	if err := r.tt.Click("设置"); err != nil {
		t.Fatal(err)
	}
	r.tt.Key(ui.Cmd, ui.KeyComma)
	if r.ho.prefs != 2 {
		t.Fatalf("preferences opened %d times, want 2 (button and Cmd+,)", r.ho.prefs)
	}
}

func TestResetChoosesFirstRow(t *testing.T) {
	r := newRig(t, "one", "two", "three")
	r.h.TogglePinned(r.h.Items()[2].ID) // "three" is pinned: first on screen
	r.tt.Frame()
	r.tt.Key(0, ui.KeyDown)
	r.tt.Key(0, ui.KeyDown)
	r.m.Reset()
	r.tt.Frame()
	r.tt.Key(0, ui.KeyEnter)
	if len(r.ho.applied) != 1 || r.ho.applied[0] != "three" {
		t.Fatalf("applied %v after Reset, want the first row (three)", r.ho.applied)
	}
}

func TestSearchBoxKeepsFocusAndDrivesList(t *testing.T) {
	r := newRig(t, "one", "two", "three")
	r.m.Reset() // focuses the search box, as showing the panel does
	r.tt.Frame()
	r.tt.Key(0, ui.KeyDown)
	r.tt.Key(0, ui.KeyDown)
	r.tt.Key(0, ui.KeyUp)
	r.tt.Key(0, ui.KeyEnter)
	if len(r.ho.applied) != 1 || r.ho.applied[0] != "two" {
		t.Fatalf("arrows and Enter in the search box: applied %v", r.ho.applied)
	}
	// Typing still goes to the box.
	r.m.Reset()
	r.tt.Frame()
	r.tt.Type("thr")
	if r.m.query != "thr" || len(r.m.rows) != 1 {
		t.Fatalf("query=%q rows=%d", r.m.query, len(r.m.rows))
	}
}

func TestKeyboardPinAndDelete(t *testing.T) {
	r := newRig(t, "one", "two", "three")
	r.m.Reset()
	r.tt.Frame()
	r.tt.Key(0, ui.KeyDown) // "two"
	r.tt.Key(ui.Cmd, ui.KeyP)
	if it := r.h.Items()[1]; model.Str(it.Text) != "two" || !it.Pinned {
		t.Fatalf("Cmd+P: %+v", r.h.Items())
	}
	r.tt.Frame()
	// "two" is pinned and first on screen, and still the chosen row.
	r.tt.Key(ui.Cmd, ui.KeyD)
	for _, s := range r.texts() {
		if s == "two" {
			t.Fatalf("Cmd+D did not delete: %v", r.texts())
		}
	}
	if len(r.texts()) != 2 {
		t.Fatalf("texts = %v", r.texts())
	}
}

func TestUpdateButtonShowsOnlyWhenThereIsAnUpdate(t *testing.T) {
	r := newRig(t, "one")
	if r.tt.HasText("新版本 1.0.12") {
		t.Fatal("a button without an update")
	}
	r.ho.update = "1.0.12"
	r.tt.Frame()
	if err := r.tt.Click("新版本 1.0.12"); err != nil {
		t.Fatalf("%v (%q)", err, r.tt.Texts())
	}
	if r.ho.updates != 1 {
		t.Fatal("not opened")
	}
}
