// Package snapshot writes and reads the sync snapshot: a SQLite file that
// holds the history and the portable settings, in the format of
// SnapshotSqliteService (schema 2; schema 1, which carried the file bytes
// inside, is still read). Klip 1.0.10 and 1.0.11 devices exchange these
// files, so the tables, the column types and the text formats below are the
// C# ones, not a free choice.
package snapshot

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/egoist/mygo/plugins/sqlite"

	"klip/internal/model"
	"klip/internal/paths"
)

// SchemaVersion is the schema this package writes.
const SchemaVersion = 2

// ddl is the text SnapshotSqliteService runs, kept as it is.
const ddl = `
PRAGMA journal_mode=MEMORY;
PRAGMA synchronous=OFF;
PRAGMA temp_store=MEMORY;
PRAGMA locking_mode=EXCLUSIVE;

CREATE TABLE meta (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE settings (
  json TEXT NOT NULL
);

CREATE TABLE items (
  position INTEGER NOT NULL,
  id TEXT PRIMARY KEY,
  kind INTEGER NOT NULL,
  capturedAt TEXT NOT NULL,
  approxBytes INTEGER NOT NULL,
  text TEXT,
  filePathsJson TEXT,
  pinned INTEGER NOT NULL,
  copyCount INTEGER NOT NULL,
  firstCapturedAt TEXT,
  note TEXT,
  contentHash TEXT,
  sourceAppName TEXT,
  sourceAppPath TEXT
);

CREATE TABLE item_images (
  itemId TEXT PRIMARY KEY,
  fileName TEXT NOT NULL,
  blobSha256 TEXT,
  sizeBytes INTEGER,
  bytes BLOB
);

CREATE TABLE item_files (
  itemId TEXT NOT NULL,
  idx INTEGER NOT NULL,
  fileName TEXT NOT NULL,
  blobSha256 TEXT,
  sizeBytes INTEGER,
  bytes BLOB,
  PRIMARY KEY(itemId, idx)
);
`

// Blob is a file that the snapshot refers to by hash and that has to be
// uploaded next to it.
type Blob struct {
	SHA256     string
	SourcePath string
	Size       int64
}

// ExportResult describes the snapshot file that Export wrote.
type ExportResult struct {
	Size   int64
	SHA256 string
	Blobs  []Blob
}

// Export writes the snapshot of items and settings to snapshotPath. The
// image and file contents are not inside it: it names them by SHA-256, and
// Blobs lists where they are. It fails when an item's content is missing.
func Export(ctx context.Context, l paths.Layout, snapshotPath string, items []model.Item, s model.Settings) (ExportResult, error) {
	if strings.TrimSpace(snapshotPath) == "" {
		return ExportResult{}, errors.New("snapshot: path is empty")
	}
	if err := os.MkdirAll(filepath.Dir(snapshotPath), 0o755); err != nil {
		return ExportResult{}, err
	}
	for _, suffix := range []string{"", "-journal", "-wal", "-shm"} {
		if err := os.Remove(snapshotPath + suffix); err != nil && !errors.Is(err, os.ErrNotExist) {
			return ExportResult{}, err
		}
	}

	blobs := map[string]Blob{}
	if err := writeDB(ctx, l, snapshotPath, items, s, blobs); err != nil {
		return ExportResult{}, err
	}

	st, err := os.Stat(snapshotPath)
	if err != nil {
		return ExportResult{}, err
	}
	sum, err := fileSHA256(snapshotPath)
	if err != nil {
		return ExportResult{}, err
	}
	list := make([]Blob, 0, len(blobs))
	for _, b := range blobs {
		list = append(list, b)
	}
	sort.Slice(list, func(i, j int) bool { return list[i].SHA256 < list[j].SHA256 })
	return ExportResult{Size: st.Size(), SHA256: sum, Blobs: list}, nil
}

func writeDB(ctx context.Context, l paths.Layout, path string, items []model.Item, s model.Settings, blobs map[string]Blob) error {
	db, err := sqlite.Open(ctx, path, sqlite.OpenOptions{})
	if err != nil {
		return err
	}
	defer db.Close()

	// The plugin runs one statement at a time, so the script is cut into
	// its statements. The PRAGMAs only tune how the file is written.
	for _, stmt := range splitStatements(ddl) {
		if _, err := db.Execute(ctx, stmt); err != nil {
			return fmt.Errorf("snapshot: %q: %w", firstLine(stmt), err)
		}
	}

	stmts := []sqlite.Statement{{SQL: "INSERT INTO meta(key, value) VALUES (?, ?)", Args: []any{"schema", strconv.Itoa(SchemaVersion)}}}
	if j, err := s.Portable().Marshal(); err == nil && len(j) > 0 {
		stmts = append(stmts, sqlite.Statement{SQL: "INSERT INTO settings(json) VALUES (?)", Args: []any{string(j)}})
	}
	itemStmts, err := itemStatements(ctx, l, items, blobs)
	if err != nil {
		return err
	}
	stmts = append(stmts, itemStmts...)

	// One transaction: the file is complete or Export fails.
	if _, err := db.Transaction(ctx, stmts); err != nil {
		return err
	}
	return db.Close()
}

// splitStatements cuts a script at its semicolons. The DDL has no semicolon
// inside a string or a comment.
func splitStatements(script string) []string {
	var out []string
	for _, part := range strings.Split(script, ";") {
		if p := strings.TrimSpace(part); p != "" {
			out = append(out, p)
		}
	}
	return out
}

func firstLine(s string) string {
	line, _, _ := strings.Cut(s, "\n")
	return line
}

// itemStatements builds the statements that insert the items, with their
// images and files, and records the blobs they refer to.
func itemStatements(ctx context.Context, l paths.Layout, items []model.Item, blobs map[string]Blob) ([]sqlite.Statement, error) {
	var stmts []sqlite.Statement
	seen := map[string]bool{}
	position := 0
	for _, it := range items {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		id := strings.ToLower(it.ID)
		if seen[id] {
			continue
		}
		seen[id] = true

		var filePathsJSON any
		if it.FilePaths != nil {
			b, err := json.Marshal(it.FilePaths)
			if err != nil {
				return nil, err
			}
			filePathsJSON = string(b)
		}
		var firstCaptured any
		if it.FirstCapturedAt != nil {
			firstCaptured = model.FormatO(it.FirstCapturedAt.Time)
		}
		stmts = append(stmts, sqlite.Statement{SQL: `
INSERT OR REPLACE INTO items(
  position, id, kind, capturedAt, approxBytes, text, filePathsJson, pinned, copyCount,
  firstCapturedAt, note, contentHash, sourceAppName, sourceAppPath
) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
			Args: []any{position, id, int(it.Kind), model.FormatO(it.CapturedAt.Time), it.ApproxBytes,
				nullable(it.Text), filePathsJSON, boolInt(it.Pinned), it.CopyCount, firstCaptured,
				nullable(it.Note), nullable(it.ContentHash), nullable(it.SourceAppName), nullable(it.SourceAppPath)}})

		var err error
		switch it.Kind {
		case model.KindImage:
			var st sqlite.Statement
			if st, err = imageStatement(it, id, blobs); err == nil {
				stmts = append(stmts, st)
			}
		case model.KindFileList:
			var sts []sqlite.Statement
			if sts, err = fileStatements(l, it, id, blobs); err == nil {
				stmts = append(stmts, sts...)
			}
		}
		if err != nil {
			return nil, err
		}
		position++
	}
	return stmts, nil
}

func imageStatement(it model.Item, id string, blobs map[string]Blob) (sqlite.Statement, error) {
	p := model.Str(it.ImageFilePath)
	size, sum, ok := hashFile(p)
	if !ok {
		return sqlite.Statement{}, errors.New("snapshot: local image payload missing")
	}
	name := filepath.Base(p)
	if name == "" || name == "." {
		name = "image.png"
	}
	blobs[sum] = Blob{SHA256: sum, SourcePath: p, Size: size}
	return sqlite.Statement{
		SQL:  "INSERT OR REPLACE INTO item_images(itemId, fileName, blobSha256, sizeBytes, bytes) VALUES (?, ?, ?, ?, NULL)",
		Args: []any{id, name, sum, size},
	}, nil
}

type cachedFile struct {
	idx  int
	name string
	path string
}

func fileStatements(l paths.Layout, it model.Item, id string, blobs map[string]Blob) ([]sqlite.Statement, error) {
	files := cachedFiles(filepath.Join(l.Files(), it.IDN()))
	if len(files) == 0 {
		var srcs []string
		for _, p := range it.FilePaths {
			if strings.TrimSpace(p) != "" {
				srcs = append(srcs, p)
			}
		}
		if len(srcs) == 0 {
			return nil, errors.New("snapshot: local file payload missing")
		}
		for i, p := range srcs {
			if st, err := os.Stat(p); err != nil || st.IsDir() {
				continue
			}
			files = append(files, cachedFile{i, filepath.Base(p), p})
		}
		if len(files) != len(srcs) {
			return nil, errors.New("snapshot: local file payload missing")
		}
	}
	sort.SliceStable(files, func(i, j int) bool { return files[i].idx < files[j].idx })

	var stmts []sqlite.Statement
	for _, f := range files {
		size, sum, ok := hashFile(f.path)
		if !ok {
			continue
		}
		blobs[sum] = Blob{SHA256: sum, SourcePath: f.path, Size: size}
		stmts = append(stmts, sqlite.Statement{
			SQL:  "INSERT OR REPLACE INTO item_files(itemId, idx, fileName, blobSha256, sizeBytes, bytes) VALUES (?, ?, ?, ?, ?, NULL)",
			Args: []any{id, f.idx, f.name, sum, size},
		})
	}
	// Like the C# client: a file that cannot be exported fails the item.
	if len(stmts) != len(files) {
		return nil, errors.New("snapshot: local file payload missing")
	}
	return stmts, nil
}

// cachedFiles lists dir/NNNN_name files: the copies the client keeps of the
// files of a file-list item.
func cachedFiles(dir string) []cachedFile {
	ents, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	var out []cachedFile
	for _, e := range ents {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		idx := indexPrefix(name)
		if idx < 0 {
			continue
		}
		short := name[5:]
		if strings.TrimSpace(short) == "" {
			short = name
		}
		out = append(out, cachedFile{idx, short, filepath.Join(dir, name)})
	}
	return out
}

// indexPrefix returns N of a name that starts with "NNNN_", or -1.
func indexPrefix(name string) int {
	if len(name) < 5 || name[4] != '_' {
		return -1
	}
	for _, c := range name[:4] {
		if c < '0' || c > '9' {
			return -1
		}
	}
	n, err := strconv.Atoi(name[:4])
	if err != nil {
		return -1
	}
	return n
}

// ReadResult is what a snapshot holds, with its images and files written
// into the data folder.
type ReadResult struct {
	Settings   *model.Settings
	Items      []model.Item
	ImageCount int
	FileCount  int
}

// EnsureBlob fetches a blob that is not in the local store, and returns where
// it put it. It may be nil.
type EnsureBlob func(sha256 string) (string, error)

// Read reads a snapshot. Images and files are put in the data folder (from
// the bytes in the file, or from the blob store, asking ensure for a blob
// that is not there), and the items point to them.
func Read(ctx context.Context, l paths.Layout, snapshotPath string, ensure EnsureBlob) (ReadResult, error) {
	if _, err := os.Stat(snapshotPath); err != nil {
		return ReadResult{}, fmt.Errorf("snapshot not found: %w", err)
	}
	db, err := sqlite.Open(ctx, snapshotPath, sqlite.OpenOptions{ReadOnly: true})
	if err != nil {
		return ReadResult{}, err
	}
	defer db.Close()

	schemaRows, err := db.Query(ctx, "SELECT value FROM meta WHERE key = ? LIMIT 1", "schema")
	if err != nil {
		return ReadResult{}, err
	}
	schema, err := strconv.Atoi(strings.TrimSpace(cellString(first(schemaRows), 0)))
	if err != nil || (schema != 1 && schema != SchemaVersion) {
		return ReadResult{}, errors.New("unsupported snapshot schema")
	}

	settingsRows, err := db.Query(ctx, "SELECT json FROM settings LIMIT 1")
	if err != nil {
		return ReadResult{}, err
	}
	settingsJSON := cellString(first(settingsRows), 0)
	items, err := readItems(ctx, db)
	if err != nil {
		return ReadResult{}, err
	}

	var res ReadResult
	for i, it := range items {
		switch it.Kind {
		case model.KindImage:
			img, err := readImage(ctx, db, it.ID, schema)
			if err != nil {
				return ReadResult{}, err
			}
			p, err := writeImportedImage(l, img, ensure)
			if err != nil {
				return ReadResult{}, err
			}
			res.ImageCount++
			items[i].ImageFilePath = &p

		case model.KindFileList:
			files, err := readFiles(ctx, db, it.ID, schema)
			if err != nil {
				return ReadResult{}, err
			}
			if len(files) == 0 {
				return ReadResult{}, errors.New("snapshot file payload missing")
			}
			dir := filepath.Join(l.Files(), it.IDN())
			if err := os.MkdirAll(dir, 0o755); err != nil {
				return ReadResult{}, err
			}
			var local []string
			for _, f := range files {
				dest := filepath.Join(dir, fmt.Sprintf("%04d_%s", f.idx, sanitizeFileName(f.name)))
				if err := writeImportedFile(l, dest, f, ensure); err != nil {
					return ReadResult{}, err
				}
				res.FileCount++
				local = append(local, dest)
			}
			items[i].FilePaths = local
		}
	}
	res.Items = items

	if strings.TrimSpace(settingsJSON) != "" {
		if s, err := model.ParseSettings([]byte(settingsJSON)); err == nil {
			res.Settings = &s
		}
	}
	return res, nil
}

func readItems(ctx context.Context, db *sqlite.DB) ([]model.Item, error) {
	rows, err := db.Query(ctx, `
SELECT position, id, kind, capturedAt, approxBytes, text, filePathsJson, pinned, copyCount,
       firstCapturedAt, note, contentHash, sourceAppName, sourceAppPath
FROM items
ORDER BY position ASC`)
	if err != nil {
		return nil, err
	}

	var items []model.Item
	for _, r := range rows.Values {
		at, err := model.ParseTime(cellString(r, 3))
		if err != nil {
			return nil, err
		}
		it := model.Item{
			ID:            strings.ToLower(cellString(r, 1)),
			Kind:          model.Kind(cellInt(r, 2)),
			CapturedAt:    model.Time{Time: at},
			ApproxBytes:   cellInt(r, 4),
			Text:          cellStringPtr(r, 5),
			Pinned:        cellInt(r, 7) != 0,
			CopyCount:     int32(cellInt(r, 8)),
			Note:          cellStringPtr(r, 10),
			ContentHash:   cellStringPtr(r, 11),
			SourceAppName: cellStringPtr(r, 12),
			SourceAppPath: cellStringPtr(r, 13),
		}
		if f := cellStringPtr(r, 9); f != nil {
			t, err := model.ParseTime(*f)
			if err != nil {
				return nil, err
			}
			it.FirstCapturedAt = &model.Time{Time: t}
		}
		if j := cellString(r, 6); strings.TrimSpace(j) != "" {
			var paths []string
			if json.Unmarshal([]byte(j), &paths) == nil && len(paths) > 0 {
				it.FilePaths = paths
			}
		}
		items = append(items, it)
	}
	return items, nil
}

type imageRecord struct {
	fileName string
	sha256   string
	bytes    []byte
}

type fileRecord struct {
	idx    int
	name   string
	sha256 string
	bytes  []byte
}

func readImage(ctx context.Context, db *sqlite.DB, id string, schema int) (imageRecord, error) {
	q := "SELECT fileName, bytes FROM item_images WHERE itemId = ? LIMIT 1"
	if schema >= 2 {
		q = "SELECT fileName, blobSha256, sizeBytes, bytes FROM item_images WHERE itemId = ? LIMIT 1"
	}
	rows, err := db.Query(ctx, q, id)
	if err != nil {
		return imageRecord{}, err
	}
	r := first(rows)
	if r == nil {
		return imageRecord{}, errors.New("snapshot image payload missing")
	}
	if schema >= 2 {
		return imageRecord{fileName: cellString(r, 0), sha256: cellString(r, 1), bytes: cellBytes(r, 3)}, nil
	}
	return imageRecord{fileName: cellString(r, 0), bytes: cellBytes(r, 1)}, nil
}

func readFiles(ctx context.Context, db *sqlite.DB, id string, schema int) ([]fileRecord, error) {
	q := "SELECT idx, fileName, bytes FROM item_files WHERE itemId = ? ORDER BY idx ASC"
	if schema >= 2 {
		q = "SELECT idx, fileName, blobSha256, sizeBytes, bytes FROM item_files WHERE itemId = ? ORDER BY idx ASC"
	}
	rows, err := db.Query(ctx, q, id)
	if err != nil {
		return nil, err
	}
	var out []fileRecord
	for _, r := range rows.Values {
		f := fileRecord{idx: int(cellInt(r, 0)), name: cellString(r, 1)}
		if schema >= 2 {
			f.sha256, f.bytes = cellString(r, 2), cellBytes(r, 4)
		} else {
			f.bytes = cellBytes(r, 2)
		}
		out = append(out, f)
	}
	return out, nil
}

// writeImportedImage stores an image the way the C# client does: in the
// images folder as <unix ms>_<guid>.<ext>.
func writeImportedImage(l paths.Layout, img imageRecord, ensure EnsureBlob) (string, error) {
	data := img.bytes
	if len(data) == 0 {
		if strings.TrimSpace(img.sha256) == "" {
			return "", errors.New("snapshot image blob missing")
		}
		blob, err := localBlobPath(l, img.sha256, ensure)
		if err != nil {
			return "", err
		}
		if data, err = os.ReadFile(blob); err != nil {
			return "", err
		}
	}
	ext := filepath.Ext(img.fileName)
	if ext == "" {
		ext = ".png"
	}
	name := fmt.Sprintf("%d_%s%s", time.Now().UnixMilli(), newGUIDN(), ext)
	if err := os.MkdirAll(l.Images(), 0o755); err != nil {
		return "", err
	}
	p := filepath.Join(l.Images(), name)
	return p, os.WriteFile(p, data, 0o644)
}

func writeImportedFile(l paths.Layout, dest string, f fileRecord, ensure EnsureBlob) error {
	if len(f.bytes) > 0 {
		return os.WriteFile(dest, f.bytes, 0o644)
	}
	if strings.TrimSpace(f.sha256) == "" {
		return errors.New("snapshot file blob missing")
	}
	blob, err := localBlobPath(l, f.sha256, ensure)
	if err != nil {
		return err
	}
	in, err := os.Open(blob)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.Create(dest)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

// localBlobPath finds a blob in the local store, or asks ensure for it.
func localBlobPath(l paths.Layout, sha256 string, ensure EnsureBlob) (string, error) {
	local := l.BlobPath(sha256)
	if local == "" {
		return "", errors.New("snapshot: blob hash is empty")
	}
	if isFile(local) {
		return local, nil
	}
	if ensure != nil {
		p, err := ensure(sha256)
		if err != nil {
			return "", err
		}
		if p != "" && isFile(p) {
			return p, nil
		}
	}
	if isFile(local) {
		return local, nil
	}
	return "", errors.New("snapshot blob missing: " + sha256)
}

// sanitizeFileName replaces what Windows does not allow in a file name. The
// set is Windows' on every platform, so a snapshot imported anywhere gives
// the same names.
func sanitizeFileName(name string) string {
	if strings.TrimSpace(name) == "" {
		return "file"
	}
	return strings.Map(func(r rune) rune {
		if r < 32 || strings.ContainsRune(`"<>|:*?\/`, r) {
			return '_'
		}
		return r
	}, name)
}

func hashFile(p string) (size int64, sum string, ok bool) {
	st, err := os.Stat(p)
	if err != nil || st.IsDir() || st.Size() <= 0 {
		return 0, "", false
	}
	sum, err = fileSHA256(p)
	if err != nil {
		return 0, "", false
	}
	return st.Size(), sum, true
}

func fileSHA256(p string) (string, error) {
	f, err := os.Open(p)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

func isFile(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}

func nullable(p *string) any {
	if p == nil {
		return nil
	}
	return *p
}

// first returns the first row, or nil.
func first(r sqlite.Rows) []any {
	if len(r.Values) == 0 {
		return nil
	}
	return r.Values[0]
}

// The cell helpers read a column of a row the plugin returned: values are
// nil, int64, float64, string or []byte.
func cell(row []any, i int) any {
	if i < 0 || i >= len(row) {
		return nil
	}
	return row[i]
}

func cellString(row []any, i int) string {
	switch v := cell(row, i).(type) {
	case string:
		return v
	case []byte:
		return string(v)
	}
	return ""
}

func cellStringPtr(row []any, i int) *string {
	if cell(row, i) == nil {
		return nil
	}
	s := cellString(row, i)
	return &s
}

func cellInt(row []any, i int) int64 {
	switch v := cell(row, i).(type) {
	case int64:
		return v
	case float64:
		return int64(v)
	}
	return 0
}

func cellBytes(row []any, i int) []byte {
	switch v := cell(row, i).(type) {
	case []byte:
		return v
	case string:
		return []byte(v)
	}
	return nil
}

func boolInt(b bool) int {
	if b {
		return 1
	}
	return 0
}
