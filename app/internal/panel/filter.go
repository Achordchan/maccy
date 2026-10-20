// Package panel is Klip's main window: the search box and the list of
// history items. The view is plain MyGo UI over a Model, so it runs in a
// window and, in tests, without one.
package panel

import (
	"fmt"
	"path/filepath"
	"strings"
	"time"

	"klip/internal/model"
)

// Match is MainWindowViewModel.Matches: the query is found, ignoring case, in
// the text, the note, the image's path or any file path.
func Match(it model.Item, query string) bool {
	q := strings.ToLower(query)
	has := func(s string) bool { return s != "" && strings.Contains(strings.ToLower(s), q) }
	if has(model.Str(it.Text)) || has(model.Str(it.Note)) || has(model.Str(it.ImageFilePath)) {
		return true
	}
	for _, p := range it.FilePaths {
		if has(p) {
			return true
		}
	}
	return false
}

// Rows is what the list shows: the items that match the query (trimmed; blank
// matches all), the pinned ones first, each group in history order.
func Rows(items []model.Item, query string) []model.Item {
	q := strings.TrimSpace(query)
	var pinned, rest []model.Item
	for _, it := range items {
		if q != "" && !Match(it, q) {
			continue
		}
		if it.Pinned {
			pinned = append(pinned, it)
		} else {
			rest = append(rest, it)
		}
	}
	return append(pinned, rest...)
}

// Preview is the first line of an item: its text on one line and cut, the
// names of its files, or a word for an image.
func Preview(it model.Item) string {
	switch it.Kind {
	case model.KindImage:
		return "图片"
	case model.KindFileList:
		var names []string
		for _, p := range it.FilePaths {
			names = append(names, baseName(p))
		}
		return strings.Join(names, "、")
	}
	return cut(oneLine(model.Str(it.Text)), 140)
}

// baseName is the last element of a path of either kind.
func baseName(p string) string {
	p = strings.ReplaceAll(p, "\\", "/")
	return filepath.Base(p)
}

func oneLine(s string) string { return strings.Join(strings.Fields(s), " ") }

func cut(s string, n int) string {
	r := []rune(s)
	if len(r) <= n {
		return s
	}
	return string(r[:n]) + "…"
}

// Detail is the second line: when, from where, how often, how big.
func Detail(it model.Item, now time.Time) string {
	var parts []string
	t := it.CapturedAt.Time.In(now.Location())
	switch {
	case sameDay(t, now):
		parts = append(parts, t.Format("15:04"))
	case t.Year() == now.Year():
		parts = append(parts, t.Format("01-02 15:04"))
	default:
		parts = append(parts, t.Format("2006-01-02 15:04"))
	}
	if app := strings.TrimSpace(model.Str(it.SourceAppName)); app != "" {
		parts = append(parts, app)
	}
	if it.CopyCount > 1 {
		parts = append(parts, fmt.Sprintf("复制 %d 次", it.CopyCount))
	}
	if it.Kind == model.KindFileList && len(it.FilePaths) > 1 {
		parts = append(parts, fmt.Sprintf("%d 个文件", len(it.FilePaths)))
	}
	if it.ApproxBytes > 0 && it.Kind != model.KindText {
		parts = append(parts, Size(it.ApproxBytes))
	}
	return strings.Join(parts, " · ")
}

func sameDay(a, b time.Time) bool {
	ay, am, ad := a.Date()
	by, bm, bd := b.Date()
	return ay == by && am == bm && ad == bd
}

// Size writes a byte count for people.
func Size(n int64) string {
	switch {
	case n >= 1<<20:
		return fmt.Sprintf("%.1f MB", float64(n)/(1<<20))
	case n >= 1<<10:
		return fmt.Sprintf("%.0f KB", float64(n)/(1<<10))
	}
	return fmt.Sprintf("%d B", n)
}
