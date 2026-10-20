package updatewin

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/egoist/mygo/ui"

	"klip/internal/update"
)

type host struct{ closed, quit int }

func (h *host) Close() { h.closed++ }
func (h *host) Quit()  { h.quit++ }

type rig struct {
	t       *testing.T
	m       *Model
	tt      *ui.Tester
	h       *host
	co      *update.Coordinator
	srv     *httptest.Server
	shut    int
	helpers [][2]string
	wg      sync.WaitGroup

	mu      sync.Mutex
	release update.Release
	gate    chan struct{} // when set, the installer download waits here after half
}

func newRig(t *testing.T, current string, rel update.Release) *rig {
	r := &rig{t: t, h: &host{}, release: rel}
	setup := []byte("SETUP-CONTENT-0123456789")
	sum := sha256.Sum256(setup)
	r.srv = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		switch req.URL.Path {
		case "/manifest.json":
			r.mu.Lock()
			rel := r.release
			r.mu.Unlock()
			rel.Installer = update.Installer{URL: r.srv.URL + "/setup.exe", SHA256: strings.ToUpper(hex.EncodeToString(sum[:])), Size: int64(len(setup))}
			b, _ := json.Marshal(update.Manifest{AppID: "maccy", Latest: rel})
			w.Write(b)
		case "/setup.exe":
			w.Header().Set("Content-Length", "24")
			w.Write(setup[:12])
			w.(http.Flusher).Flush()
			if r.gate != nil {
				<-r.gate
			}
			w.Write(setup[12:])
		default:
			http.NotFound(w, req)
		}
	}))
	t.Cleanup(r.srv.Close)
	exe := filepath.Join(t.TempDir(), "maccy.exe")
	_ = os.WriteFile(exe, []byte("EXE"), 0o755)
	cl := &update.Client{HTTP: r.srv.Client(), ManifestURL: r.srv.URL + "/manifest.json", Current: current, Runtime: "win-x64",
		ManifestPause: func(int) time.Duration { return time.Millisecond }, DownloadPause: func(int) time.Duration { return time.Millisecond }}
	r.co = update.NewCoordinator(cl, filepath.Join(t.TempDir(), "updates"), exe, func() { r.shut++ })
	r.co.Supported = true
	r.co.StartHelper = func(h, p string) error { r.helpers = append(r.helpers, [2]string{h, p}); return nil }
	r.m = New(r.co, r.h)
	r.m.Go = func(f func()) { r.wg.Add(1); go func() { defer r.wg.Done(); f() }() }
	r.tt = ui.NewTester(r.m.View, 460, 380)
	return r
}

func (r *rig) wait() { r.wg.Wait(); r.tt.Frame() }

func (r *rig) click(s string) {
	r.t.Helper()
	if err := r.tt.Click(s); err != nil {
		r.t.Fatalf("click %q: %v (%q)", s, err, r.tt.Texts())
	}
}

func TestManualCheckUpToDate(t *testing.T) {
	r := newRig(t, "1.0.11", update.Release{Version: "1.0.11"})
	r.m.CheckNow()
	r.wait()
	if !r.tt.HasText("当前已经是最新版本。") {
		t.Fatalf("%q", r.tt.Texts())
	}
	r.click("好")
	if r.h.closed != 1 || r.co.State().Phase != update.Idle {
		t.Fatalf("closed %d, %+v", r.h.closed, r.co.State())
	}
}

func TestAvailableShowsNotesAndLater(t *testing.T) {
	r := newRig(t, "1.0.10", update.Release{Version: "1.0.11", Notes: "- 全新的内核\n- 更小更快"})
	r.m.CheckNow()
	r.wait()
	for _, s := range []string{"发现新版本", "版本：1.0.11", "更新内容：", "稍后", "立即更新"} {
		if !r.tt.HasText(s) {
			t.Fatalf("missing %q: %q", s, r.tt.Texts())
		}
	}
	if !strings.Contains(strings.Join(r.tt.Texts(), "|"), "全新的内核") {
		t.Fatalf("notes not shown: %q", r.tt.Texts())
	}
	if r.tt.HasText("退出") {
		t.Fatal("a normal update has no quit button")
	}
	r.click("稍后")
	if r.h.closed != 1 || r.co.Available() == nil {
		t.Fatalf("later must keep the version known: closed %d, %+v", r.h.closed, r.co.State())
	}
}

func TestMandatoryUpdateCanBeDeclinedOnlyByQuitting(t *testing.T) {
	r := newRig(t, "1.0.10", update.Release{Version: "1.0.11", Mandatory: true})
	r.m.CheckNow()
	r.wait()
	if r.tt.HasText("稍后") || !r.tt.HasText("退出") || !r.tt.HasText("这是必须安装的更新。") {
		t.Fatalf("%q", r.tt.Texts())
	}
	r.click("退出")
	if r.h.quit != 1 {
		t.Fatal("quit")
	}
}

func TestApplyShowsProgressThenRestarts(t *testing.T) {
	r := newRig(t, "1.0.10", update.Release{Version: "1.0.11", Notes: "n"})
	r.m.CheckNow()
	r.wait()
	r.gate = make(chan struct{})
	r.click("立即更新")
	deadline := time.Now().Add(3 * time.Second)
	for r.co.State().Read < 12 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	r.tt.Frame()
	if !r.tt.HasText("正在下载更新") || !r.tt.HasText("12 B / 24 B（50%）") {
		t.Fatalf("progress: %q", r.tt.Texts())
	}
	close(r.gate)
	r.wait()
	if r.shut != 1 || len(r.helpers) != 1 || !r.tt.HasText("正在更新") || !r.tt.HasText("正在重启以完成更新...") {
		t.Fatalf("shut %d helpers %v: %q", r.shut, r.helpers, r.tt.Texts())
	}
}

func TestFailureIsShownAndCanBeRetried(t *testing.T) {
	r := newRig(t, "1.0.10", update.Release{Version: "1.0.11"})
	r.m.CheckNow()
	r.wait()
	// A manifest that promises another size than the file has.
	info := r.co.Available()
	_ = info
	st := r.co.State()
	st.Info.InstallerSize = 3 // st.Info is the coordinator's own
	r.click("立即更新")
	r.wait()
	if r.shut != 0 || !r.tt.HasText("更新失败") || !r.tt.HasText("下载失败：file size mismatch") || !r.tt.HasText("重试") {
		t.Fatalf("shut %d: %q", r.shut, r.tt.Texts())
	}
	// Put right, and again.
	st.Info.InstallerSize = 24
	r.click("重试")
	r.wait()
	if r.shut != 1 || !r.tt.HasText("正在更新") {
		t.Fatalf("shut %d: %q", r.shut, r.tt.Texts())
	}
}

func TestCheckFailureShowsTheMessage(t *testing.T) {
	r := newRig(t, "1.0.10", update.Release{Version: "1.0.11"})
	r.co.Client.ManifestURL = r.srv.URL + "/nope.json"
	r.m.CheckNow()
	r.wait()
	if !r.tt.HasText("更新失败") || !strings.Contains(strings.Join(r.tt.Texts(), "|"), "检查更新失败：") {
		t.Fatalf("%q", r.tt.Texts())
	}
	r.click("关闭")
	if r.h.closed != 1 || r.co.State().Phase != update.Idle {
		t.Fatalf("%d %+v", r.h.closed, r.co.State())
	}
}
