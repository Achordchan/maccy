// Package history reads and writes history.json, the list of clipboard
// items, newest first.
package history

import (
	"bytes"
	"encoding/json"
	"errors"
	"io/fs"
	"os"
	"path/filepath"
	"strings"

	"klip/internal/model"
	"klip/internal/paths"
	"klip/internal/snapshot"
)

// Load reads the file and drops what no longer has its content, as
// ClipboardPersistenceService.Normalize does. A missing file is an empty
// history.
func Load(l paths.Layout) ([]model.Item, error) {
	data, err := os.ReadFile(l.History())
	if err == nil {
		_ = os.Chmod(filepath.Dir(l.History()), 0o700)
		_ = os.Chmod(l.History(), 0o600)
	}
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return nil, nil
		}
		return nil, err
	}
	var items []model.Item
	if err := json.Unmarshal(bytes.TrimPrefix(data, []byte("\xef\xbb\xbf")), &items); err != nil {
		return nil, err
	}
	// An interrupted cache replacement is undone before Normalize decides
	// that an item has no content.
	snapshot.RecoverPayloads(l, items)
	out := make([]model.Item, 0, len(items))
	for _, it := range items {
		if n, ok := Normalize(l, it); ok {
			out = append(out, n)
		}
	}
	return out, nil
}

// Save writes the items as System.Text.Json writes a list of
// ClipboardItem: compact, nulls included.
func Save(l paths.Layout, items []model.Item) error {
	if items == nil {
		items = []model.Item{}
	}
	data, err := marshal(items)
	if err != nil {
		return err
	}
	dir := filepath.Dir(l.History())
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	// Clipboard history holds what the user copied: it is for this user only,
	// also when an older version created the folder world-readable.
	_ = os.Chmod(dir, 0o700)
	tmp := l.History() + ".tmp"
	_ = os.Remove(tmp)
	f, err := os.OpenFile(tmp, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return err
	}
	if _, err := f.Write(data); err != nil {
		f.Close()
		os.Remove(tmp)
		return err
	}
	if err := f.Close(); err != nil {
		os.Remove(tmp)
		return err
	}
	if err := os.Rename(tmp, l.History()); err != nil {
		os.Remove(tmp)
		return err
	}
	_ = os.Chmod(l.History(), 0o600)
	return nil
}

// marshal does not escape <, > and &, which Go's encoder does by default;
// both read back the same.
func marshal(items []model.Item) ([]byte, error) {
	var b bytes.Buffer
	enc := json.NewEncoder(&b)
	enc.SetEscapeHTML(false)
	if err := enc.Encode(items); err != nil {
		return nil, err
	}
	return bytes.TrimRight(b.Bytes(), "\n"), nil
}

// Normalize checks an item against the disk and corrects its size, as the
// C# client does at start. It says false for an item to drop.
func Normalize(l paths.Layout, it model.Item) (model.Item, bool) {
	switch it.Kind {
	case model.KindImage:
		p := model.Str(it.ImageFilePath)
		if strings.TrimSpace(p) == "" {
			return it, false
		}
		st, err := os.Stat(p)
		if err != nil || st.IsDir() {
			return it, false
		}
		it.ApproxBytes = st.Size()
		return it, true

	case model.KindText:
		t := model.Str(it.Text)
		if strings.TrimSpace(t) == "" {
			return it, false
		}
		// The C# code counts UTF-16 units times two.
		it.ApproxBytes = int64(utf16Len(t)) * 2
		return it, true

	case model.KindFileList:
		var paths []string
		for _, p := range it.FilePaths {
			if strings.TrimSpace(p) != "" {
				paths = append(paths, p)
			}
		}
		if len(paths) == 0 {
			return it, false
		}
		hasSource := false
		for _, p := range paths {
			if isFile(p) {
				hasSource = true
				break
			}
		}
		cacheDir := filepath.Join(l.Files(), it.IDN())
		cached := listFiles(cacheDir)
		if !hasSource && len(cached) == 0 {
			return it, false
		}
		var size int64
		if len(cached) > 0 {
			for _, p := range cached {
				size += fileSize(p)
			}
		}
		if size <= 0 {
			for _, p := range paths {
				size += fileSize(p)
			}
		}
		if size <= 0 {
			return it, false
		}
		it.ApproxBytes = size
		it.FilePaths = paths
		return it, true
	}
	return it, true
}

func utf16Len(s string) int {
	n := 0
	for _, r := range s {
		if r >= 0x10000 {
			n += 2
		} else {
			n++
		}
	}
	return n
}

func isFile(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}

func fileSize(p string) int64 {
	st, err := os.Stat(p)
	if err != nil || st.IsDir() {
		return 0
	}
	return st.Size()
}

// listFiles returns the files directly in dir, like Directory.GetFiles.
func listFiles(dir string) []string {
	ents, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	var out []string
	for _, e := range ents {
		if !e.IsDir() {
			out = append(out, filepath.Join(dir, e.Name()))
		}
	}
	return out
}
