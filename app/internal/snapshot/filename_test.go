package snapshot

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"unicode/utf8"
)

func TestImportedFileNameReservesPrefix(t *testing.T) {
	for _, name := range []string{strings.Repeat("a", 251) + ".txt", strings.Repeat("文", 83) + ".txt", "short.txt", strings.Repeat("x", 260) + "." + strings.Repeat("z", 260)} {
		got := importedFileName(12345, name)
		if len(got) > 255 || !utf8.ValidString(got) {
			t.Fatalf("invalid filename: %q", got)
		}
		if strings.HasSuffix(name, ".txt") && !strings.HasSuffix(got, ".txt") {
			t.Fatal("extension lost")
		}
		if err := os.WriteFile(filepath.Join(t.TempDir(), got), []byte("payload"), 0600); err != nil {
			t.Fatal(err)
		}
	}
}
