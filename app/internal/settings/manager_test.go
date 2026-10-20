package settings

import (
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"

	"klip/internal/model"
)

func TestManagerUpdateSavesAndNotifies(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	m, err := Open(path)
	if err != nil {
		t.Fatal(err)
	}
	if got := m.Get(); got.MaxItems != 200 {
		t.Fatalf("defaults: %+v", got)
	}
	var seen []int32
	m.OnChange(func(s model.Settings) { seen = append(seen, s.MaxItems) })
	if err := m.Update(func(s *model.Settings) { s.MaxItems = 50 }); err != nil {
		t.Fatal(err)
	}
	if m.Get().MaxItems != 50 || len(seen) != 1 || seen[0] != 50 {
		t.Fatalf("get=%d seen=%v", m.Get().MaxItems, seen)
	}
	data, err := os.ReadFile(path)
	if err != nil || !strings.Contains(string(data), `"MaxItems":50`) {
		t.Fatalf("file: %s %v", data, err)
	}
	m2, _ := Open(path)
	if m2.Get().MaxItems != 50 {
		t.Fatal("not read back")
	}
}

func TestManagerKeepsUnknownFieldsAndSurvivesBadFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	if err := os.WriteFile(path, []byte(`{"MaxItems":7,"FromTheFuture":{"a":1}}`), 0o644); err != nil {
		t.Fatal(err)
	}
	m, err := Open(path)
	if err != nil {
		t.Fatal(err)
	}
	_ = m.Update(func(s *model.Settings) { s.Theme = "Dark" })
	data, _ := os.ReadFile(path)
	if !strings.Contains(string(data), `"FromTheFuture":{"a":1}`) || !strings.Contains(string(data), `"MaxItems":7`) {
		t.Fatalf("lost fields: %s", data)
	}

	bad := filepath.Join(t.TempDir(), "settings.json")
	_ = os.WriteFile(bad, []byte("{not json"), 0o644)
	m, err = Open(bad)
	if err == nil {
		t.Fatal("a broken file should be reported")
	}
	if m.Get().MaxItems != 200 {
		t.Fatal("a broken file should give the defaults")
	}
}

func TestManagerConcurrent(t *testing.T) {
	m, _ := Open(filepath.Join(t.TempDir(), "s.json"))
	var wg sync.WaitGroup
	for i := 0; i < 20; i++ {
		wg.Add(2)
		go func() { defer wg.Done(); _ = m.Update(func(s *model.Settings) { s.MaxItems++ }) }()
		go func() { defer wg.Done(); _ = m.Get() }()
	}
	wg.Wait()
	if m.Get().MaxItems != 220 {
		t.Fatalf("lost updates: %d", m.Get().MaxItems)
	}
}

func TestHandlersGetTheSettingsAsTheyAreNow(t *testing.T) {
	m, _ := Open(filepath.Join(t.TempDir(), "settings.json"))
	var seen []bool
	m.OnChange(func(s model.Settings) {
		if len(seen) == 0 {
			// A second update lands while the first is still being delivered.
			_ = m.Update(func(s *model.Settings) { s.CaptureText = false })
		}
		seen = append(seen, s.CaptureText)
	})
	_ = m.Update(func(s *model.Settings) { s.CaptureText = true })
	// The first delivery was handed the newest settings, not the ones its own
	// update made, so it cannot undo the second.
	if len(seen) != 2 || seen[0] != false || seen[1] != false {
		t.Fatalf("handlers saw %v, want the newest (false) both times", seen)
	}
}
