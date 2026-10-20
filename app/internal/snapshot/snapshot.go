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
	"database/sql"
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

	_ "modernc.org/sqlite"

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
	db, err := sql.Open("sqlite", path)
	if err != nil {
		return err
	}
	defer db.Close()
	// One connection: the PRAGMAs above are per connection, and the lock is
	// exclusive.
	db.SetMaxOpenConns(1)

	if _, err := db.ExecContext(ctx, ddl); err != nil {
		return err
	}
	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()

	if _, err := tx.ExecContext(ctx, "INSERT INTO meta(key, value) VALUES (?, ?)", "schema", strconv.Itoa(SchemaVersion)); err != nil {
		return err
	}
	if j, err := s.Portable().Marshal(); err == nil && len(j) > 0 {
		if _, err := tx.ExecContext(ctx, "INSERT INTO settings(json) VALUES (?)", string(j)); err != nil {
			return err
		}
	}
	if err := insertItems(ctx, tx, l, items, blobs); err != nil {
		return err
	}
	if err := tx.Commit(); err != nil {
		return err
	}
	return db.Close()
}

func insertItems(ctx context.Context, tx *sql.Tx, l paths.Layout, items []model.Item, blobs map[string]Blob) error {
	seen := map[string]bool{}
	position := 0
	for _, it := range items {
		if err := ctx.Err(); err != nil {
			return err
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
				return err
			}
			filePathsJSON = string(b)
		}
		var firstCaptured any
		if it.FirstCapturedAt != nil {
			firstCaptured = model.FormatO(it.FirstCapturedAt.Time)
		}
		_, err := tx.ExecContext(ctx, `
INSERT OR REPLACE INTO items(
  position, id, kind, capturedAt, approxBytes, text, filePathsJson, pinned, copyCount,
  firstCapturedAt, note, contentHash, sourceAppName, sourceAppPath
) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
			position, id, int(it.Kind), model.FormatO(it.CapturedAt.Time), it.ApproxBytes,
			nullable(it.Text), filePathsJSON, boolInt(it.Pinned), it.CopyCount, firstCaptured,
			nullable(it.Note), nullable(it.ContentHash), nullable(it.SourceAppName), nullable(it.SourceAppPath))
		if err != nil {
			return err
		}

		switch it.Kind {
		case model.KindImage:
			if err := insertImage(ctx, tx, it, id, blobs); err != nil {
				return err
			}
		case model.KindFileList:
			if err := insertFiles(ctx, tx, l, it, id, blobs); err != nil {
				return err
			}
		}
		position++
	}
	return nil
}

func insertImage(ctx context.Context, tx *sql.Tx, it model.Item, id string, blobs map[string]Blob) error {
	p := model.Str(it.ImageFilePath)
	size, sum, ok := hashFile(p)
	if !ok {
		return errors.New("snapshot: local image payload missing")
	}
	name := filepath.Base(p)
	if name == "" || name == "." {
		name = "image.png"
	}
	if _, err := tx.ExecContext(ctx,
		"INSERT OR REPLACE INTO item_images(itemId, fileName, blobSha256, sizeBytes, bytes) VALUES (?, ?, ?, ?, NULL)",
		id, name, sum, size); err != nil {
		return err
	}
	blobs[sum] = Blob{SHA256: sum, SourcePath: p, Size: size}
	return nil
}

type cachedFile struct {
	idx  int
	name string
	path string
}

func insertFiles(ctx context.Context, tx *sql.Tx, l paths.Layout, it model.Item, id string, blobs map[string]Blob) error {
	files := cachedFiles(filepath.Join(l.Files(), it.IDN()))
	if len(files) == 0 {
		var srcs []string
		for _, p := range it.FilePaths {
			if strings.TrimSpace(p) != "" {
				srcs = append(srcs, p)
			}
		}
		if len(srcs) == 0 {
			return errors.New("snapshot: local file payload missing")
		}
		for i, p := range srcs {
			if st, err := os.Stat(p); err != nil || st.IsDir() {
				continue
			}
			files = append(files, cachedFile{i, filepath.Base(p), p})
		}
		if len(files) != len(srcs) {
			return errors.New("snapshot: local file payload missing")
		}
	}
	sort.SliceStable(files, func(i, j int) bool { return files[i].idx < files[j].idx })

	exported := 0
	for _, f := range files {
		if err := ctx.Err(); err != nil {
			return err
		}
		size, sum, ok := hashFile(f.path)
		if !ok {
			continue
		}
		if _, err := tx.ExecContext(ctx,
			"INSERT OR REPLACE INTO item_files(itemId, idx, fileName, blobSha256, sizeBytes, bytes) VALUES (?, ?, ?, ?, ?, NULL)",
			id, f.idx, f.name, sum, size); err != nil {
			return err
		}
		blobs[sum] = Blob{SHA256: sum, SourcePath: f.path, Size: size}
		exported++
	}
	if exported != len(files) {
		return errors.New("snapshot: local file payload missing")
	}
	return nil
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
	db, err := sql.Open("sqlite", "file:"+filepath.ToSlash(snapshotPath)+"?mode=ro")
	if err != nil {
		return ReadResult{}, err
	}
	defer db.Close()

	var schemaText sql.NullString
	if err := db.QueryRowContext(ctx, "SELECT value FROM meta WHERE key = ? LIMIT 1", "schema").Scan(&schemaText); err != nil && !errors.Is(err, sql.ErrNoRows) {
		return ReadResult{}, err
	}
	schema, err := strconv.Atoi(strings.TrimSpace(schemaText.String))
	if err != nil || (schema != 1 && schema != SchemaVersion) {
		return ReadResult{}, errors.New("unsupported snapshot schema")
	}

	var settingsJSON sql.NullString
	if err := db.QueryRowContext(ctx, "SELECT json FROM settings LIMIT 1").Scan(&settingsJSON); err != nil && !errors.Is(err, sql.ErrNoRows) {
		return ReadResult{}, err
	}
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

	if strings.TrimSpace(settingsJSON.String) != "" {
		if s, err := model.ParseSettings([]byte(settingsJSON.String)); err == nil {
			res.Settings = &s
		}
	}
	return res, nil
}

func readItems(ctx context.Context, db *sql.DB) ([]model.Item, error) {
	rows, err := db.QueryContext(ctx, `
SELECT position, id, kind, capturedAt, approxBytes, text, filePathsJson, pinned, copyCount,
       firstCapturedAt, note, contentHash, sourceAppName, sourceAppPath
FROM items
ORDER BY position ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var items []model.Item
	for rows.Next() {
		var (
			pos                                 int64
			id, captured                        string
			kind, copyCount, pinned             int64
			approx                              int64
			text, filePathsJSON, first, note    sql.NullString
			contentHash, sourceName, sourcePath sql.NullString
		)
		if err := rows.Scan(&pos, &id, &kind, &captured, &approx, &text, &filePathsJSON, &pinned, &copyCount,
			&first, &note, &contentHash, &sourceName, &sourcePath); err != nil {
			return nil, err
		}
		at, err := model.ParseTime(captured)
		if err != nil {
			return nil, err
		}
		it := model.Item{
			ID:            strings.ToLower(id),
			Kind:          model.Kind(kind),
			CapturedAt:    model.Time{Time: at},
			ApproxBytes:   approx,
			Text:          nstr(text),
			Pinned:        pinned != 0,
			CopyCount:     int32(copyCount),
			Note:          nstr(note),
			ContentHash:   nstr(contentHash),
			SourceAppName: nstr(sourceName),
			SourceAppPath: nstr(sourcePath),
		}
		if first.Valid {
			t, err := model.ParseTime(first.String)
			if err != nil {
				return nil, err
			}
			it.FirstCapturedAt = &model.Time{Time: t}
		}
		if strings.TrimSpace(filePathsJSON.String) != "" {
			var paths []string
			if json.Unmarshal([]byte(filePathsJSON.String), &paths) == nil && len(paths) > 0 {
				it.FilePaths = paths
			}
		}
		items = append(items, it)
	}
	return items, rows.Err()
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

func readImage(ctx context.Context, db *sql.DB, id string, schema int) (imageRecord, error) {
	var (
		rec   imageRecord
		sum   sql.NullString
		bytes []byte
		err   error
	)
	if schema >= 2 {
		var size sql.NullInt64
		err = db.QueryRowContext(ctx, "SELECT fileName, blobSha256, sizeBytes, bytes FROM item_images WHERE itemId = ? LIMIT 1", id).
			Scan(&rec.fileName, &sum, &size, &bytes)
	} else {
		err = db.QueryRowContext(ctx, "SELECT fileName, bytes FROM item_images WHERE itemId = ? LIMIT 1", id).
			Scan(&rec.fileName, &bytes)
	}
	if errors.Is(err, sql.ErrNoRows) {
		return rec, errors.New("snapshot image payload missing")
	}
	if err != nil {
		return rec, err
	}
	rec.sha256, rec.bytes = sum.String, bytes
	return rec, nil
}

func readFiles(ctx context.Context, db *sql.DB, id string, schema int) ([]fileRecord, error) {
	q := "SELECT idx, fileName, bytes FROM item_files WHERE itemId = ? ORDER BY idx ASC"
	if schema >= 2 {
		q = "SELECT idx, fileName, blobSha256, sizeBytes, bytes FROM item_files WHERE itemId = ? ORDER BY idx ASC"
	}
	rows, err := db.QueryContext(ctx, q, id)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []fileRecord
	for rows.Next() {
		var f fileRecord
		var sum sql.NullString
		var size sql.NullInt64
		if schema >= 2 {
			err = rows.Scan(&f.idx, &f.name, &sum, &size, &f.bytes)
		} else {
			err = rows.Scan(&f.idx, &f.name, &f.bytes)
		}
		if err != nil {
			return nil, err
		}
		f.sha256 = sum.String
		out = append(out, f)
	}
	return out, rows.Err()
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

func nstr(n sql.NullString) *string {
	if !n.Valid {
		return nil
	}
	s := n.String
	return &s
}

func boolInt(b bool) int {
	if b {
		return 1
	}
	return 0
}
