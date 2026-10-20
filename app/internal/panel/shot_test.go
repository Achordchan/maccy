package panel

import (
	"image/png"
	"os"
	"testing"

	"github.com/egoist/mygo/ui"

	"klip/internal/model"
)

func TestShot(t *testing.T) {
	out := os.Getenv("KLIP_SHOT")
	if out == "" {
		t.Skip("set KLIP_SHOT=file.png")
	}
	r := newRig(t, "最近复制的一段文字，用来看排版是否正常", "func main() { fmt.Println(\"hello\") }", "https://example.com/some/long/url/that/goes/on/and/on/and/on/forever/and/ever")
	items := r.h.Items()
	r.h.TogglePinned(items[1].ID)
	r.h.UpdateNote(items[0].ID, "买菜清单")
	r.h.Add(model.Item{ID: "00000000-0000-0000-0000-0000000000aa", Kind: model.KindFileList, CapturedAt: items[0].CapturedAt,
		FilePaths: []string{"/Users/x/报告.pdf", "/Users/x/data.csv"}, CopyCount: 3, ApproxBytes: 2 << 20, ContentHash: model.Ptr("files:AA"), SourceAppName: model.Ptr("访达")})
	r.m.Reset()
	r.tt.SetSize(560, 520)
	r.tt.Frame()
	if dark := os.Getenv("KLIP_DARK") == "1"; dark {
		r.tt.SetDark(true)
		r.tt.Frame()
	}
	f, _ := os.Create(out)
	defer f.Close()
	png.Encode(f, r.tt.Image())
	_ = ui.Cmd
}
