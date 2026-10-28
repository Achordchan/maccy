package history

import (
	"klip/internal/model"
	"klip/internal/paths"
	"os"
	"path/filepath"
	"testing"
)

func TestRepeatedFileCaptureReplacesCachedBytes(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	s := NewService(l)
	for i, id := range []string{"old", "new"} {
		dir := filepath.Join(l.Files(), id)
		if err := os.MkdirAll(dir, 0700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(dir, "0000_doc.txt"), []byte{byte('a' + i)}, 0600); err != nil {
			t.Fatal(err)
		}
		s.Add(model.Item{ID: id, Kind: model.KindFileList, ContentHash: model.Ptr("files:same"), FilePaths: []string{"/original/doc.txt"}, CopyCount: 1})
	}
	if s.Len() != 1 {
		t.Fatal("duplicate not merged")
	}
	b, err := os.ReadFile(filepath.Join(l.Files(), "old", "0000_doc.txt"))
	if err != nil || string(b) != "b" {
		t.Fatalf("stale cached bytes: %q %v", b, err)
	}
	if err := s.RemoveFiles([]string{filepath.Join(l.Files(), "old", "0000_doc.txt")}); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(l.Files(), "old", "0000_doc.txt")); err != nil {
		t.Fatal("implicit live cache deleted")
	}
}

func TestIncompleteReplacementKeepsOldCache(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	s := NewService(l)
	for _, id := range []string{"old", "new"} {
		if err := os.MkdirAll(filepath.Join(l.Files(), id), 0700); err != nil {
			t.Fatal(err)
		}
	}
	old := filepath.Join(l.Files(), "old", "0000_doc.txt")
	if err := os.WriteFile(old, []byte("old"), 0600); err != nil {
		t.Fatal(err)
	}
	if s.replaceCapturedFiles(model.Item{ID: "old"}, model.Item{ID: "new", FilePaths: []string{"/original/doc.txt"}}) {
		t.Fatal("partial cache accepted")
	}
	if b, err := os.ReadFile(old); err != nil || string(b) != "old" {
		t.Fatal("old cache lost")
	}
}

func TestDuplicateImageRepairsMissingPayload(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	s := NewService(l)
	missing := filepath.Join(t.TempDir(), "missing.png")
	fresh := filepath.Join(t.TempDir(), "fresh.png")
	if err := os.WriteFile(fresh, []byte("image"), 0600); err != nil {
		t.Fatal(err)
	}
	for i, p := range []string{missing, fresh} {
		s.Add(model.Item{ID: []string{"old", "new"}[i], Kind: model.KindImage, ImageFilePath: &p, ContentHash: model.Ptr("same"), CopyCount: 1})
	}
	if s.Len() != 1 || model.Str(s.Items()[0].ImageFilePath) != fresh {
		t.Fatal("fresh image not retained")
	}
	if _, err := os.Stat(fresh); err != nil {
		t.Fatal("fresh image deleted")
	}
}
