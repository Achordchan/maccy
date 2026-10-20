// Package capture turns what is on the system clipboard into history items,
// and puts a history item back on the clipboard: ClipboardCaptureService and
// ClipboardApplyService. The clipboard itself is behind an interface, so the
// rules are the same on every platform and testable without a window.
package capture

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"image"
	"image/png"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync/atomic"
	"time"

	_ "image/gif"
	_ "image/jpeg"

	_ "golang.org/x/image/bmp"
	_ "golang.org/x/image/webp"

	"klip/internal/history"
	"klip/internal/model"
	"klip/internal/paths"
)

// Clipboard is the system clipboard, as much of it as capture needs.
type Clipboard interface {
	// Formats names what is on the clipboard, in portable names such as
	// "text/plain", "text/uri-list", "image/png" (MyGo's).
	Formats() []string
	ReadText() string
	// ReadFiles returns the paths of copied files, nil when there are none.
	ReadFiles() ([]string, error)
	// ReadImage returns the image as PNG, nil when there is none.
	ReadImage() []byte
	WriteText(text string) error
	WriteFiles(paths []string) error
	// WriteImage puts the PNG on the clipboard; filePath, when not empty, is
	// the file it came from, offered as a file too.
	WriteImage(png []byte, filePath string) error
}

// AppIdentity is the program a copy came from.
type AppIdentity struct{ Name, Path string }

// Service captures the clipboard into the history.
type Service struct {
	Layout    paths.Layout
	History   *history.Service
	Clipboard Clipboard
	// ForegroundApp says which program is in front, or nil. May be nil.
	ForegroundApp func() *AppIdentity

	CaptureText         bool
	CaptureImages       bool
	CaptureFiles        bool
	CaptureFileMaxBytes int64

	// Now and NewID are time.Now and a random GUID unless a test sets them.
	Now   func() time.Time
	NewID func() string

	allowed     map[string]bool
	lastOwnWrit atomic.Int64
}

// New returns a Service with the defaults of ClipboardCaptureService: all
// three kinds on, files up to 20 MB, any file extension (but see
// dangerousExtensions).
func New(layout paths.Layout, h *history.Service, c Clipboard) *Service {
	return &Service{
		Layout: layout, History: h, Clipboard: c,
		CaptureText: true, CaptureImages: true, CaptureFiles: true, CaptureFileMaxBytes: 20 << 20,
		Now: time.Now, NewID: newGUID,
	}
}

// ApplySettings sets the capture options from the settings, as App.ApplySettings
// does: the file size limit is the smaller of its own and the history's.
func (s *Service) ApplySettings(st model.Settings) {
	s.CaptureText, s.CaptureImages, s.CaptureFiles = st.CaptureText, st.CaptureImages, st.CaptureFiles
	s.SetFileExtensions(st.CaptureFileExtensions)
	s.CaptureFileMaxBytes = int64(min(st.CaptureFileMaxMegabytes, st.MaxMegabytes)) * 1024 * 1024
}

// SetFileExtensions sets which file extensions are captured: a list separated
// by commas, semicolons or spaces. An empty list is any extension.
func (s *Service) SetFileExtensions(list string) { s.allowed = parseExtensions(list) }

// MarkWriteByUs says that the app has just written to the clipboard, so that
// the change that follows is not captured again.
func (s *Service) MarkWriteByUs() { s.lastOwnWrit.Store(s.Now().UnixNano()) }

// dangerousExtensions are never captured, whatever the list says.
var dangerousExtensions = map[string]bool{
	".exe": true, ".msi": true, ".msp": true, ".msix": true, ".appx": true,
	".bat": true, ".cmd": true, ".com": true, ".scr": true, ".pif": true,
	".ps1": true, ".psm1": true, ".psd1": true, ".vbs": true, ".vbe": true, ".js": true, ".jse": true, ".wsf": true, ".wsh": true, ".hta": true,
	".jar": true, ".reg": true, ".lnk": true, ".url": true, ".cpl": true, ".dll": true,
}

func parseExtensions(list string) map[string]bool {
	fields := strings.FieldsFunc(list, func(r rune) bool { return strings.ContainsRune(",; \t\r\n", r) })
	set := map[string]bool{}
	for _, f := range fields {
		ext := strings.TrimSpace(f)
		if ext == "" {
			continue
		}
		if !strings.HasPrefix(ext, ".") {
			ext = "." + ext
		}
		ext = strings.ToLower(ext)
		if dangerousExtensions[ext] {
			continue
		}
		set[ext] = true
	}
	if len(set) == 0 {
		return nil
	}
	return set
}

var textFormats = []string{"text/plain", "text", "unicodetext"}
var fileFormats = []string{"application/x-mygo-file-list", "text/uri-list", "filenames", "files"}
var imageFormats = []string{"image/png", "png", "bitmap", "deviceindependentbitmap", "dib", "image/bmp", "image/tiff", "image/jpeg"}

func has(set map[string]bool, names []string) bool {
	for _, n := range names {
		if set[n] {
			return true
		}
	}
	return false
}

// Capture reads the clipboard once and adds what it finds to the history.
// It does nothing for the 250 ms after the app wrote to the clipboard itself.
// Text comes first, then files, then images; the first that yields something
// is the item.
func (s *Service) Capture(ctx context.Context) error {
	if s.Now().UnixNano()-s.lastOwnWrit.Load() < int64(250*time.Millisecond) {
		return nil
	}
	formats := s.Clipboard.Formats()
	set := map[string]bool{}
	for _, f := range formats {
		set[strings.ToLower(f)] = true
	}
	var app *AppIdentity
	if s.ForegroundApp != nil {
		app = s.ForegroundApp()
	}
	add := func(it model.Item) {
		now := model.Time{Time: s.Now()}
		it.CapturedAt, it.FirstCapturedAt = now, &now
		if app != nil {
			it.SourceAppName, it.SourceAppPath = &app.Name, &app.Path
		}
		s.History.Add(it)
	}

	if s.CaptureText && has(set, textFormats) {
		text := s.Clipboard.ReadText()
		if strings.TrimSpace(text) != "" {
			items := s.History.Items()
			if s.History.Limits().MergeDuplicates && len(items) > 0 && items[0].Kind == model.KindText && model.Str(items[0].Text) == text {
				return nil
			}
			add(model.Item{
				ID: s.NewID(), Kind: model.KindText, ApproxBytes: int64(model.UTF16Len(text)) * 2, Text: &text, CopyCount: 1,
				ContentHash: model.Ptr(hashOf("text", []byte(text))),
			})
			return nil
		}
	}

	if s.CaptureFiles && has(set, fileFormats) {
		files, _ := s.Clipboard.ReadFiles()
		files = nonBlank(files)
		if len(files) > 0 {
			if s.CaptureImages && len(files) == 1 && isImagePath(files[0]) && isFile(files[0]) {
				if data, err := os.ReadFile(files[0]); err == nil {
					if p, h, size, err := s.savePNG(data); err == nil {
						add(model.Item{ID: s.NewID(), Kind: model.KindImage, ApproxBytes: size, ImageFilePath: &p, CopyCount: 1, ContentHash: &h})
						return nil
					}
				}
			}
			files = s.filterFiles(files)
			if len(files) == 0 {
				return nil
			}
			id := s.NewID()
			s.cacheCopies(id, files)
			add(model.Item{
				ID: id, Kind: model.KindFileList, ApproxBytes: s.fileListBytes(id, files), FilePaths: files, CopyCount: 1,
				ContentHash: model.Ptr(hashOf("files", []byte(strings.Join(files, "\n")))),
			})
			return nil
		}
	}

	if s.CaptureImages {
		// The portable image formats first; if none is named, still try once,
		// since screenshot tools use odd names (the C# fallback loop). Nothing
		// is tried when the clipboard holds text or files only.
		if has(set, imageFormats) || (!has(set, textFormats) && !has(set, fileFormats)) {
			if data := s.Clipboard.ReadImage(); len(data) > 0 {
				if p, h, size, err := s.savePNG(data); err == nil {
					add(model.Item{ID: s.NewID(), Kind: model.KindImage, ApproxBytes: size, ImageFilePath: &p, CopyCount: 1, ContentHash: &h})
					return nil
				}
			}
		}
	}
	return ctx.Err()
}

func nonBlank(in []string) []string {
	var out []string
	for _, p := range in {
		if strings.TrimSpace(p) != "" {
			out = append(out, p)
		}
	}
	return out
}

// filterFiles drops directories, dangerous types, types not in the list and
// files over the size limit (a size of 0 or unknown passes).
func (s *Service) filterFiles(in []string) []string {
	out := make([]string, 0, len(in))
	for _, p := range in {
		if strings.TrimSpace(p) == "" {
			continue
		}
		if st, err := os.Stat(p); err == nil && st.IsDir() {
			continue
		}
		ext := strings.ToLower(extension(p))
		if ext != "" && !strings.HasPrefix(ext, ".") {
			ext = "." + ext
		}
		if ext != "" && dangerousExtensions[ext] {
			continue
		}
		if s.allowed != nil {
			if ext == "" || !s.allowed[ext] {
				continue
			}
		}
		if s.CaptureFileMaxBytes > 0 {
			if size := fileSize(p); size > 0 && size > s.CaptureFileMaxBytes {
				continue
			}
		}
		out = append(out, p)
	}
	return out
}

// cacheCopies copies the files into files/<id>/NNNN_name, so that the item
// still has them when the originals move or go.
func (s *Service) cacheCopies(id string, files []string) {
	dir := filepath.Join(s.Layout.Files(), strings.ReplaceAll(strings.ToLower(id), "-", ""))
	if os.MkdirAll(dir, 0o755) != nil {
		return
	}
	for i, src := range files {
		name := baseName(src)
		if strings.TrimSpace(src) == "" || !isFile(src) || strings.TrimSpace(name) == "" {
			continue
		}
		_ = copyFile(src, filepath.Join(dir, fmt.Sprintf("%04d_%s", i, name)))
	}
}

// fileListBytes is the size of the cached copies, else of the originals, else
// twice the length of the paths (so that it is never 0).
func (s *Service) fileListBytes(id string, files []string) int64 {
	dir := filepath.Join(s.Layout.Files(), strings.ReplaceAll(strings.ToLower(id), "-", ""))
	var n int64
	if ents, err := os.ReadDir(dir); err == nil {
		for _, e := range ents {
			if !e.IsDir() {
				n += fileSize(filepath.Join(dir, e.Name()))
			}
		}
	}
	if n <= 0 {
		for _, p := range files {
			n += fileSize(p)
		}
	}
	if n <= 0 {
		for _, p := range files {
			n += int64(model.UTF16Len(p)) * 2
		}
	}
	return n
}

// savePNG stores an image in the images folder as <unix ms>_<guid>.png, and
// returns its path, its content hash ("img:" and the SHA-256 of the file)
// and its size. A PNG is stored as it is; any other format the standard
// decoders know is converted.
func (s *Service) savePNG(data []byte) (path, hash string, size int64, err error) {
	if !bytes.HasPrefix(data, []byte("\x89PNG\r\n\x1a\n")) {
		img, _, derr := image.Decode(bytes.NewReader(data))
		if derr != nil {
			return "", "", 0, derr
		}
		var b bytes.Buffer
		if err := png.Encode(&b, img); err != nil {
			return "", "", 0, err
		}
		data = b.Bytes()
	} else if _, derr := png.DecodeConfig(bytes.NewReader(data)); derr != nil {
		return "", "", 0, derr
	}
	if err := os.MkdirAll(s.Layout.Images(), 0o755); err != nil {
		return "", "", 0, err
	}
	path = filepath.Join(s.Layout.Images(), fmt.Sprintf("%d_%s.png", s.Now().UnixMilli(), strings.ReplaceAll(newGUID(), "-", "")))
	if err := os.WriteFile(path, data, 0o644); err != nil {
		return "", "", 0, err
	}
	return path, hashOf("img", data), int64(len(data)), nil
}

// hashOf is "<prefix>:<SHA-256 in upper case hex>", what C# stores in
// ContentHash.
func hashOf(prefix string, payload []byte) string {
	sum := sha256.Sum256(payload)
	return prefix + ":" + strings.ToUpper(hex.EncodeToString(sum[:]))
}

func isImagePath(p string) bool {
	switch strings.ToLower(extension(p)) {
	case ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp":
		return true
	}
	return false
}

// baseName is Path.GetFileName: on Windows it cuts at / and \, elsewhere at
// / only.
func baseName(p string) string {
	cut := strings.LastIndex(p, "/")
	if runtime.GOOS == "windows" {
		if i := strings.LastIndex(p, `\`); i > cut {
			cut = i
		}
	}
	return p[cut+1:]
}

// extension is Path.GetExtension: the last dot of the file name and what
// follows, "" when there is none or it ends the name.
func extension(p string) string {
	name := baseName(p)
	i := strings.LastIndex(name, ".")
	if i < 0 || i == len(name)-1 {
		return ""
	}
	return name[i:]
}

func fileSize(p string) int64 {
	st, err := os.Stat(p)
	if err != nil || st.IsDir() {
		return 0
	}
	return st.Size()
}

func isFile(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

func newGUID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	b[6] = b[6]&0x0f | 0x40
	b[8] = b[8]&0x3f | 0x80
	h := hex.EncodeToString(b)
	return h[0:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:]
}

// ---- apply

// Apply puts an item back on the clipboard (ClipboardApplyService). Content
// that is gone is not an error: nothing is written.
func (s *Service) Apply(it model.Item) error {
	s.MarkWriteByUs()
	switch it.Kind {
	case model.KindText:
		return s.Clipboard.WriteText(model.Str(it.Text))

	case model.KindFileList:
		var resolved []string
		for i, p := range it.FilePaths {
			if strings.TrimSpace(p) == "" {
				continue
			}
			name := baseName(p)
			if strings.TrimSpace(name) == "" {
				continue
			}
			cached := filepath.Join(s.Layout.Files(), it.IDN(), fmt.Sprintf("%04d_%s", i, name))
			switch {
			case isFile(cached):
				resolved = append(resolved, cached)
			case isFile(p):
				resolved = append(resolved, p)
			}
		}
		if len(resolved) == 0 {
			return nil
		}
		return s.Clipboard.WriteFiles(resolved)

	case model.KindImage:
		p := model.Str(it.ImageFilePath)
		if strings.TrimSpace(p) == "" || !isFile(p) {
			return nil
		}
		data, err := os.ReadFile(p)
		if err != nil {
			return err
		}
		return s.Clipboard.WriteImage(data, p)
	}
	return errors.New("capture: unknown item kind")
}
