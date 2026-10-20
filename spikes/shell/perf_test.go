package main

import (
	"image/png"
	"os"
	"runtime"
	"testing"
	"time"

	"github.com/egoist/mygo/ui"
)

// TestListPerf renders the panel headlessly with a long history and times
// the frame, a scroll and a search. Set KLIP_SHOT to save a screenshot.
func TestListPerf(t *testing.T) {
	for _, n := range []int{1000, 10000, 100000} {
		k := &klip{}
		tt := ui.NewTester(k.view, 560, 520)
		k.fake(n)

		t0 := time.Now()
		tt.Frame()
		first := time.Since(t0)

		var frames time.Duration
		const scrolls = 60
		for i := 0; i < scrolls; i++ {
			t1 := time.Now()
			tt.Scroll(280, 300, 0, -240)
			tt.Frame()
			frames += time.Since(t1)
		}

		t2 := time.Now()
		k.query = "entry 9999"
		k.filter()
		tt.Frame()
		search := time.Since(t2)

		var m runtime.MemStats
		runtime.GC()
		runtime.ReadMemStats(&m)
		t.Logf("n=%-6d first frame %-10v scroll frame avg %-10v search %-10v results %-4d heap %d MB",
			n, first.Round(time.Microsecond), (frames / scrolls).Round(time.Microsecond),
			search.Round(time.Microsecond), len(k.results), m.HeapAlloc>>20)

		if p := os.Getenv("KLIP_SHOT"); p != "" && n == 1000 {
			k.query = ""
			k.filter()
			tt.Frame()
			f, _ := os.Create(p)
			_ = png.Encode(f, tt.Image())
			f.Close()
		}
	}
}
