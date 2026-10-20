package platform

import (
	"errors"
	"net/url"
	"os"
	"path/filepath"
	"strings"

	"github.com/egoist/mygo"
	"github.com/egoist/mygo/transfer"
)

// Clipboard is MyGo's clipboard as capture.Clipboard. MyGo hops every call to
// its main thread, so it is safe from the watcher goroutine, but only once the
// application is running (App.WhenReady).
type Clipboard struct{}

// Formats lists the portable format names on the clipboard.
func (Clipboard) Formats() []string {
	var out []string
	for _, f := range mygo.Clipboard.Formats() {
		out = append(out, string(f))
	}
	return out
}

func (Clipboard) ReadText() string { return mygo.Clipboard.ReadText() }

// ReadFiles returns the local paths of copied files; "nothing there" is not an
// error.
func (Clipboard) ReadFiles() ([]string, error) {
	files, err := mygo.Clipboard.ReadFiles()
	if err != nil && errors.Is(err, transfer.ErrFormat) {
		return nil, nil
	}
	return files, err
}

func (Clipboard) ReadImage() []byte { return mygo.Clipboard.ReadImage() }

func (Clipboard) WriteText(text string) error {
	return mygo.Clipboard.Write(transfer.TextData(text))
}

func (Clipboard) WriteFiles(paths []string) error {
	return mygo.Clipboard.WriteFiles(paths...)
}

// WriteImage puts the PNG on the clipboard, and, when filePath names an
// existing file, offers that file too, so a paste into a folder or a chat
// window gets the file and a paste into an editor gets the picture (the
// C# client does the same).
func (Clipboard) WriteImage(png []byte, filePath string) error {
	if filePath != "" && filepath.IsAbs(filePath) {
		if _, err := os.Stat(filePath); err == nil {
			p := filepath.ToSlash(filePath)
			if !strings.HasPrefix(p, "/") {
				p = "/" + p
			}
			u := (&url.URL{Scheme: "file", Path: p}).String()
			item := transfer.NewItem(
				transfer.Bytes(transfer.PNG, png),
				transfer.Bytes(transfer.FileList, []byte(u)),
				transfer.Bytes(transfer.URIList, []byte(u)),
			)
			if err := mygo.Clipboard.Write(transfer.New(item)); err == nil {
				return nil
			}
		}
	}
	return mygo.Clipboard.WriteImage(png)
}
