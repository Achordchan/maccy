package history

import (
	"context"
	"klip/internal/model"
	"klip/internal/paths"
	"os"
	"path/filepath"
	"testing"
)

func TestSyncMutationAndDeferredCleanup(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	s := NewService(l)
	if err := os.MkdirAll(l.Images(), 0700); err != nil {
		t.Fatal(err)
	}
	payload := filepath.Join(l.Images(), "kept.png")
	if err := os.WriteFile(payload, []byte("x"), 0600); err != nil {
		t.Fatal(err)
	}
	item := model.Item{ID: "one", Kind: model.KindImage, ImageFilePath: &payload}
	if err := s.Mutate(context.Background(), func([]model.Item) ([]model.Item, error) { return []model.Item{item}, nil }); err != nil {
		t.Fatal(err)
	}
	if err := s.RemoveFiles([]string{payload}); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(payload); err != nil {
		t.Fatal("live payload deleted")
	}
	st := model.DefaultSettings()
	st.MaxItems = 0
	st.ExcludePinnedFromLimits = false
	items, removed := s.ApplyLimitsAndSnapshot(st)
	if len(items) != 0 || len(removed) != 1 {
		t.Fatalf("items=%v removed=%v", items, removed)
	}
	if _, err := os.Stat(payload); err != nil {
		t.Fatal("payload deleted before persistence")
	}
	if err := s.RemoveFiles([]string{payload}); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(payload); !os.IsNotExist(err) {
		t.Fatal("unreferenced payload retained")
	}
}
