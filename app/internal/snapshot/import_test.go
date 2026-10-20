package snapshot

import (
	"bytes"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"klip/internal/model"
	"klip/internal/paths"
)

// newSnapshot builds a schema 2 snapshot with raw SQL, so that a test can
// put in it what Export never writes (a forged item id, a forged blob hash).
func newSnapshot(t *testing.T, fill func(exec func(string, ...any))) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "snapshot.sqlite")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	exec := func(q string, args ...any) {
		t.Helper()
		if _, err := db.Exec(q, args...); err != nil {
			t.Fatalf("%s: %v", q, err)
		}
	}
	exec(`CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)`)
	exec(`CREATE TABLE settings (json TEXT NOT NULL)`)
	exec(`CREATE TABLE items (position INTEGER NOT NULL, id TEXT PRIMARY KEY, kind INTEGER NOT NULL, capturedAt TEXT NOT NULL, approxBytes INTEGER NOT NULL, text TEXT, filePathsJson TEXT, pinned INTEGER NOT NULL, copyCount INTEGER NOT NULL, firstCapturedAt TEXT, note TEXT, contentHash TEXT, sourceAppName TEXT, sourceAppPath TEXT)`)
	exec(`CREATE TABLE item_images (itemId TEXT PRIMARY KEY, fileName TEXT NOT NULL, blobSha256 TEXT, sizeBytes INTEGER, bytes BLOB)`)
	exec(`CREATE TABLE item_files (itemId TEXT NOT NULL, idx INTEGER NOT NULL, fileName TEXT NOT NULL, blobSha256 TEXT, sizeBytes INTEGER, bytes BLOB, PRIMARY KEY(itemId, idx))`)
	exec(`INSERT INTO meta VALUES ('schema', '2')`)
	fill(exec)
	return path
}

func insertItem(exec func(string, ...any), position int, id string, kind int, filePathsJSON any) {
	exec(`INSERT INTO items VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)`,
		position, id, kind, "2026-05-30T12:10:00.0000000+00:00", 3, nil, filePathsJSON, 0, 1, nil, nil, nil, nil, nil)
}

// An item id is a GUID, never a path: a snapshot that says otherwise is
// refused before anything is written.
func TestReadRefusesAnItemIDThatIsAPath(t *testing.T) {
	root := t.TempDir()
	data := filepath.Join(root, "data")
	if err := os.MkdirAll(data, 0o755); err != nil {
		t.Fatal(err)
	}
	for _, id := range []string{
		"../../.config/autostart",
		"..",
		"0f8fad5b-d9cb-469f-a165-70867728950e/../../evil",
		"",
		"not a guid at all",
	} {
		t.Run(id, func(t *testing.T) {
			snap := newSnapshot(t, func(exec func(string, ...any)) {
				insertItem(exec, 0, id, 2, `["C:\\src\\a.txt"]`)
				exec(`INSERT INTO item_files VALUES (?,0,'evil.desktop',NULL,3,?)`, id, []byte("payload"))
			})
			_, err := Read(ctx, paths.Layout{Root: data}, snap, nil)
			if err == nil || !strings.Contains(err.Error(), "not a GUID") {
				t.Fatalf("id %q: %v", id, err)
			}
		})
	}
	if _, err := os.Stat(filepath.Join(root, ".config")); err == nil {
		t.Error("a path outside the data folder was created")
	}
}

// A blob hash is a SHA-256, never a path either.
func TestReadRefusesABlobHashThatIsAPath(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	snap := newSnapshot(t, func(exec func(string, ...any)) {
		insertItem(exec, 0, "7c9e6679-7425-40de-944b-e07fc1f90ae7", 1, nil)
		exec(`INSERT INTO item_images VALUES (?, 'pic.png', ?, 3, NULL)`,
			"7c9e6679-7425-40de-944b-e07fc1f90ae7", "../../../../tmp/evil")
	})
	_, err := Read(ctx, l, snap, nil)
	if err == nil || !strings.Contains(err.Error(), "blob hash") {
		t.Fatalf("a forged blob hash: %v", err)
	}
}

// The payloads are staged first: when a blob is missing, the history of the
// item and the files of its cache are as they were.
func TestReadLeavesTheCacheAloneWhenAPayloadIsMissing(t *testing.T) {
	const id3 = "16fd2706-8baf-433b-82eb-8c7fada847da"
	l := paths.Layout{Root: t.TempDir()}
	old := filepath.Join(l.Files(), strings.ReplaceAll(id3, "-", ""))
	if err := os.MkdirAll(old, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(old, "0000_old.txt"), []byte("old"), 0o644); err != nil {
		t.Fatal(err)
	}
	snap := newSnapshot(t, func(exec func(string, ...any)) {
		// An image that carries its bytes, so that it is staged before the
		// missing file blob fails the import.
		insertItem(exec, 0, "7c9e6679-7425-40de-944b-e07fc1f90ae7", 1, nil)
		exec(`INSERT INTO item_images VALUES (?, 'pic.png', NULL, 3, ?)`,
			"7c9e6679-7425-40de-944b-e07fc1f90ae7", []byte("img"))
		insertItem(exec, 1, id3, 2, `["C:\\src\\a.txt"]`)
		exec(`INSERT INTO item_files VALUES (?,0,'a.txt',?,3,NULL)`, id3, strings.Repeat("a", 64))
	})
	if _, err := Read(ctx, l, snap, nil); err == nil || !strings.Contains(err.Error(), "blob missing") {
		t.Fatalf("a missing blob: %v", err)
	}
	if b, err := os.ReadFile(filepath.Join(old, "0000_old.txt")); err != nil || string(b) != "old" {
		t.Errorf("the old cache file: %q %v", b, err)
	}
	if ents, err := os.ReadDir(l.Images()); err == nil && len(ents) != 0 {
		t.Errorf("an image was committed before the import failed: %v", ents)
	}
	ents, err := os.ReadDir(l.Root)
	if err != nil {
		t.Fatal(err)
	}
	for _, e := range ents {
		if strings.HasPrefix(e.Name(), ".import-") {
			t.Errorf("staging folder left behind: %s", e.Name())
		}
	}
}

// A directory of an item is replaced by the imported files, not overlaid:
// files of an older copy must not come back in the next export.
func TestReadReplacesTheFilesOfAnItem(t *testing.T) {
	const id3 = "16fd2706-8baf-433b-82eb-8c7fada847da"
	l := paths.Layout{Root: t.TempDir()}
	dir := filepath.Join(l.Files(), strings.ReplaceAll(id3, "-", ""))
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"0000_old.txt", "0001_gone.txt"} {
		if err := os.WriteFile(filepath.Join(dir, name), []byte("old"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	snap := newSnapshot(t, func(exec func(string, ...any)) {
		insertItem(exec, 0, id3, 2, `["C:\\src\\new.txt"]`)
		exec(`INSERT INTO item_files VALUES (?,0,'new.txt',NULL,3,?)`, id3, []byte("new"))
	})
	res, err := Read(ctx, l, snap, nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Items) != 1 || len(res.Items[0].FilePaths) != 1 {
		t.Fatalf("items %+v", res.Items)
	}
	want := filepath.Join(dir, "0000_new.txt")
	if res.Items[0].FilePaths[0] != want {
		t.Errorf("path %q, want %q", res.Items[0].FilePaths[0], want)
	}
	if b, err := os.ReadFile(want); err != nil || string(b) != "new" {
		t.Errorf("imported file %q %v", b, err)
	}
	for _, name := range []string{"0000_old.txt", "0001_gone.txt"} {
		if _, err := os.Stat(filepath.Join(dir, name)); !os.IsNotExist(err) {
			t.Errorf("the old cache file %s survived: %v", name, err)
		}
	}
	ents, err := os.ReadDir(l.Files())
	if err != nil {
		t.Fatal(err)
	}
	for _, e := range ents {
		if strings.Contains(e.Name(), ".old-") {
			t.Errorf("a cache backup was left behind: %s", e.Name())
		}
	}
}

// A read for a merge keeps the remote payloads aside: the local files of an
// item stay until the merge has decided which side it keeps.
func TestDetachedReadKeepsTheLocalPayloadsUntilTheMergeDecides(t *testing.T) {
	const id3 = "16fd2706-8baf-433b-82eb-8c7fada847da"
	l := paths.Layout{Root: t.TempDir()}
	dir := filepath.Join(l.Files(), strings.ReplaceAll(id3, "-", ""))
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	localPath := filepath.Join(dir, "0000_local.txt")
	if err := os.WriteFile(localPath, []byte("local"), 0o644); err != nil {
		t.Fatal(err)
	}
	snap := newSnapshot(t, func(exec func(string, ...any)) {
		insertItem(exec, 0, id3, 2, `["C:\\src\\remote.txt"]`)
		exec(`INSERT INTO item_files VALUES (?,0,'remote.txt',NULL,6,?)`, id3, []byte("remote"))
	})
	root := filepath.Join(l.Root, ".merge-test")
	remote, err := ReadDetached(ctx, l, root, snap, nil)
	if err != nil {
		t.Fatal(err)
	}
	if b, _ := os.ReadFile(localPath); string(b) != "local" {
		t.Errorf("the local cache changed during the read: %q", b)
	}
	if _, err := os.Stat(filepath.Join(dir, "0000_remote.txt")); !os.IsNotExist(err) {
		t.Error("the remote file was written into the cache before the merge")
	}

	// The merge keeps the local item: its files are still the local ones.
	kept, moves, err := ApplyPayloads(l, root, []model.Item{{
		ID: id3, Kind: model.KindFileList, FilePaths: []string{localPath},
	}})
	if err != nil {
		t.Fatal(err)
	}
	moves.Commit()
	if len(kept[0].FilePaths) != 1 || kept[0].FilePaths[0] != localPath {
		t.Errorf("kept paths %v", kept[0].FilePaths)
	}
	if b, _ := os.ReadFile(localPath); string(b) != "local" {
		t.Errorf("the local file was replaced: %q", b)
	}

	// The merge keeps the remote item: its files replace the cache, and the
	// old folder is gone.
	applied, moves, err := ApplyPayloads(l, root, remote.Items)
	if err != nil {
		t.Fatal(err)
	}
	moves.Commit()
	got := applied[0].FilePaths
	if len(got) != 1 || filepath.Dir(got[0]) != dir || filepath.Base(got[0]) != "0000_remote.txt" {
		t.Fatalf("applied paths %v", got)
	}
	if b, _ := os.ReadFile(got[0]); string(b) != "remote" {
		t.Errorf("imported file %q", b)
	}
	if _, err := os.Stat(localPath); !os.IsNotExist(err) {
		t.Error("the local file of the replaced cache survived")
	}
	ents, err := os.ReadDir(l.Files())
	if err != nil {
		t.Fatal(err)
	}
	for _, e := range ents {
		if strings.Contains(e.Name(), ".old-") {
			t.Errorf("a cache backup was left behind: %s", e.Name())
		}
	}
}

// A cached blob whose bytes do not match its hash is corrupt: it must not be
// imported, and ensure must be able to repair it.
func TestReadRepairsACorruptCachedBlob(t *testing.T) {
	data := []byte("the right bytes")
	sum := sha256.Sum256(data)
	sha := hex.EncodeToString(sum[:])
	const id3 = "16fd2706-8baf-433b-82eb-8c7fada847da"

	l := paths.Layout{Root: t.TempDir()}
	local := l.BlobPath(sha)
	if err := os.MkdirAll(filepath.Dir(local), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(local, []byte("truncated"), 0o644); err != nil {
		t.Fatal(err)
	}
	snap := newSnapshot(t, func(exec func(string, ...any)) {
		insertItem(exec, 0, id3, 2, `["C:\\src\\a.bin"]`)
		exec(`INSERT INTO item_files VALUES (?,0,'a.bin',?,?,NULL)`, id3, sha, len(data))
	})
	ensure := func(got string) (string, error) {
		if got != sha {
			t.Errorf("ensure asked for %q", got)
		}
		return local, os.WriteFile(local, data, 0o600)
	}
	res, err := Read(ctx, l, snap, ensure)
	if err != nil {
		t.Fatal(err)
	}
	if b, _ := os.ReadFile(res.Items[0].FilePaths[0]); !bytes.Equal(b, data) {
		t.Errorf("imported %q", b)
	}
	if b, _ := os.ReadFile(local); !bytes.Equal(b, data) {
		t.Errorf("the cache was not repaired: %q", b)
	}
}

// A replacement that was not committed by saving the history is rolled back
// from its journal, before normalization can drop the item.
func TestRecoverPayloadsRollsBackAnUncommittedReplacement(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	target := filepath.Join(l.Files(), "16fd27068baf433b82eb8c7fada847da")
	backup := target + ".old-deadbeef"
	if err := os.MkdirAll(target, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(target, "0000_new.txt"), []byte("new"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(backup, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(backup, "0000_old.txt"), []byte("old"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := writePayloadJournal(payloadJournalPath(l), []payloadJournalEntry{{Target: target, Backup: backup}}, false); err != nil {
		t.Fatal(err)
	}
	// The history still names a file that only the backup has.
	oldItems := []model.Item{{
		ID: "16fd2706-8baf-433b-82eb-8c7fada847da", Kind: model.KindFileList,
		FilePaths: []string{filepath.Join(target, "0000_old.txt")}, CopyCount: 1,
	}}

	RecoverPayloads(l, oldItems)

	if b, err := os.ReadFile(filepath.Join(target, "0000_old.txt")); err != nil || string(b) != "old" {
		t.Errorf("the old payload was not restored: %q %v", b, err)
	}
	if _, err := os.Stat(backup); !os.IsNotExist(err) {
		t.Error("the backup was left behind")
	}
	if _, err := os.Stat(payloadJournalPath(l)); !os.IsNotExist(err) {
		t.Error("the journal was left behind")
	}
}

// An empty file is content: it has a SHA-256 and must export and import.
func TestEmptyFileRoundTrip(t *testing.T) {
	const id = "16fd2706-8baf-433b-82eb-8c7fada847da"
	src := paths.Layout{Root: t.TempDir()}
	dir := filepath.Join(src.Files(), strings.ReplaceAll(id, "-", ""))
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	empty := filepath.Join(dir, "0000_empty.txt")
	if err := os.WriteFile(empty, nil, 0o644); err != nil {
		t.Fatal(err)
	}
	item := model.Item{
		ID: id, Kind: model.KindFileList, CapturedAt: model.Time{Time: time.Now()},
		FilePaths: []string{empty}, CopyCount: 1,
	}
	snap := filepath.Join(t.TempDir(), "snapshot.sqlite")
	if _, err := Export(ctx, src, snap, []model.Item{item}, model.DefaultSettings()); err != nil {
		t.Fatal(err)
	}
	dst := paths.Layout{Root: t.TempDir()}
	res, err := Read(ctx, dst, snap, func(string) (string, error) { return empty, nil })
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Items) != 1 || len(res.Items[0].FilePaths) != 1 {
		t.Fatalf("%+v", res.Items)
	}
	st, err := os.Stat(res.Items[0].FilePaths[0])
	if err != nil || st.Size() != 0 {
		t.Errorf("empty file: %v %v", st, err)
	}
}

// A schema 1 snapshot carries the file bytes inline, including an empty BLOB.
func TestReadSchema1EmptyInlineFile(t *testing.T) {
	const id = "16fd2706-8baf-433b-82eb-8c7fada847da"
	path := filepath.Join(t.TempDir(), "snapshot.sqlite")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	exec := func(q string, args ...any) {
		if _, err := db.Exec(q, args...); err != nil {
			t.Fatalf("%s: %v", q, err)
		}
	}
	exec(`CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)`)
	exec(`CREATE TABLE settings (json TEXT NOT NULL)`)
	exec(`CREATE TABLE items (position INTEGER NOT NULL, id TEXT PRIMARY KEY, kind INTEGER NOT NULL, capturedAt TEXT NOT NULL, approxBytes INTEGER NOT NULL, text TEXT, filePathsJson TEXT, pinned INTEGER NOT NULL, copyCount INTEGER NOT NULL, firstCapturedAt TEXT, note TEXT, contentHash TEXT, sourceAppName TEXT, sourceAppPath TEXT)`)
	exec(`CREATE TABLE item_files (itemId TEXT NOT NULL, idx INTEGER NOT NULL, fileName TEXT NOT NULL, bytes BLOB NOT NULL, PRIMARY KEY(itemId, idx))`)
	exec(`INSERT INTO meta VALUES ('schema','1')`)
	exec(`INSERT INTO items VALUES (0,?,2,'2026-05-30T12:00:00.0000000+00:00',0,NULL,NULL,0,1,NULL,NULL,NULL,NULL,NULL)`, id)
	exec(`INSERT INTO item_files VALUES (?,0,'empty.txt',?)`, id, []byte{})
	l := paths.Layout{Root: t.TempDir()}
	res, err := Read(ctx, l, path, nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Items) != 1 || len(res.Items[0].FilePaths) != 1 {
		t.Fatalf("%+v", res.Items)
	}
	st, err := os.Stat(res.Items[0].FilePaths[0])
	if err != nil || st.Size() != 0 {
		t.Errorf("empty inline file: %v %v", st, err)
	}
}

// When the history that the journal describes is already saved, recovery
// keeps the new cache and only deletes the backup.
func TestRecoverPayloadsKeepsACommittedReplacement(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	target := filepath.Join(l.Files(), "16fd27068baf433b82eb8c7fada847da")
	backup := target + ".old-deadbeef"
	if err := os.MkdirAll(target, 0o755); err != nil {
		t.Fatal(err)
	}
	newFile := filepath.Join(target, "0000_new.txt")
	if err := os.WriteFile(newFile, []byte("new"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(backup, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(backup, "0000_old.txt"), []byte("old"), 0o644); err != nil {
		t.Fatal(err)
	}
	items := []model.Item{{
		ID: "16fd2706-8baf-433b-82eb-8c7fada847da", Kind: model.KindFileList,
		FilePaths: []string{newFile}, CopyCount: 1,
	}}
	sum, err := fileSHA256(newFile)
	if err != nil {
		t.Fatal(err)
	}
	entry := payloadJournalEntry{Target: target, Backup: backup, Files: map[string]string{"0000_new.txt": sum}}
	if err := writePayloadJournal(payloadJournalPath(l), []payloadJournalEntry{entry}, false); err != nil {
		t.Fatal(err)
	}
	RecoverPayloads(l, items)

	if b, err := os.ReadFile(newFile); err != nil || string(b) != "new" {
		t.Errorf("the saved replacement was rolled back: %q %v", b, err)
	}
	if _, err := os.Stat(backup); !os.IsNotExist(err) {
		t.Error("the backup was left behind")
	}
	if _, err := os.Stat(payloadJournalPath(l)); !os.IsNotExist(err) {
		t.Error("the journal was left behind")
	}
}

// The journal written by ApplyPayloads must describe the items it returns,
// including the final (not staging) file paths.
func TestJournalFromApplyPayloadsMatchesTheResult(t *testing.T) {
	const id = "16fd2706-8baf-433b-82eb-8c7fada847da"
	idn := strings.ReplaceAll(id, "-", "")
	l := paths.Layout{Root: t.TempDir()}
	root := filepath.Join(l.Root, ".import-test")
	staged := filepath.Join(root, "files", idn)
	if err := os.MkdirAll(staged, 0o755); err != nil {
		t.Fatal(err)
	}
	stagedFile := filepath.Join(staged, "0000_new.txt")
	if err := os.WriteFile(stagedFile, []byte("new"), 0o644); err != nil {
		t.Fatal(err)
	}
	dir := filepath.Join(l.Files(), idn)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "0000_old.txt"), []byte("old"), 0o644); err != nil {
		t.Fatal(err)
	}
	items := []model.Item{{
		ID: id, Kind: model.KindFileList, FilePaths: []string{stagedFile}, CopyCount: 1,
	}}
	out, applied, err := ApplyPayloads(l, root, items)
	if err != nil {
		t.Fatal(err)
	}
	// A capture made after ApplyPayloads changes the history but not these
	// payloads: recovery must still see the replacement as committed.
	withCapture := append(out, model.Item{ID: "7c9e6679-7425-40de-944b-e07fc1f90ae7", Kind: model.KindText, Text: model.Ptr("captured"), CopyCount: 1})
	RecoverPayloads(l, withCapture)
	applied.Commit()

	if b, err := os.ReadFile(filepath.Join(dir, "0000_new.txt")); err != nil || string(b) != "new" {
		t.Errorf("the saved replacement was rolled back: %q %v", b, err)
	}
	ents, err := os.ReadDir(l.Files())
	if err != nil {
		t.Fatal(err)
	}
	for _, e := range ents {
		if strings.Contains(e.Name(), ".old-") {
			t.Errorf("a backup was left behind: %s", e.Name())
		}
	}
}

// A migrated item whose FilePaths are the original documents still needs the
// installed cache files for the replacement to count as committed.
func TestRecoverPayloadsRequiresTheInstalledFiles(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	target := filepath.Join(l.Files(), "16fd27068baf433b82eb8c7fada847da")
	backup := target + ".old-deadbeef"
	if err := os.MkdirAll(target, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(backup, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(backup, "0000_old.txt"), []byte("old"), 0o644); err != nil {
		t.Fatal(err)
	}
	// The history only names the user's document, not the cache.
	items := []model.Item{{
		ID: "16fd2706-8baf-433b-82eb-8c7fada847da", Kind: model.KindFileList,
		FilePaths: []string{filepath.Join(t.TempDir(), "document.pdf")}, CopyCount: 1,
	}}
	entry := payloadJournalEntry{
		Target: target, Backup: backup,
		Files: map[string]string{"0000_new.txt": strings.Repeat("a", 64)},
	}
	if err := writePayloadJournal(payloadJournalPath(l), []payloadJournalEntry{entry}, false); err != nil {
		t.Fatal(err)
	}
	RecoverPayloads(l, items)

	if b, err := os.ReadFile(filepath.Join(target, "0000_old.txt")); err != nil || string(b) != "old" {
		t.Errorf("the backup was not restored: %q %v", b, err)
	}
}

// A committed journal keeps the installed replacement even if the old history
// is what is on disk (the crash was after MarkCommitted).
func TestRecoverPayloadsHonorsTheCommitMarker(t *testing.T) {
	const id = "16fd2706-8baf-433b-82eb-8c7fada847da"
	idn := strings.ReplaceAll(id, "-", "")
	l := paths.Layout{Root: t.TempDir()}
	staged := filepath.Join(l.Root, ".import-test", "files", idn)
	if err := os.MkdirAll(staged, 0o755); err != nil {
		t.Fatal(err)
	}
	stagedFile := filepath.Join(staged, "0000_same.txt")
	if err := os.WriteFile(stagedFile, []byte("new"), 0o644); err != nil {
		t.Fatal(err)
	}
	dir := filepath.Join(l.Files(), idn)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "0000_same.txt"), []byte("old"), 0o644); err != nil {
		t.Fatal(err)
	}
	items := []model.Item{{ID: id, Kind: model.KindFileList, FilePaths: []string{stagedFile}, CopyCount: 1}}
	out, moves, err := ApplyPayloads(l, filepath.Join(l.Root, ".import-test"), items)
	if err != nil {
		t.Fatal(err)
	}
	moves.MarkCommitted()
	// Crash after the marker: the old history is still the one read back.
	oldItems := []model.Item{{ID: id, Kind: model.KindFileList, FilePaths: []string{filepath.Join(dir, "0000_same.txt")}, CopyCount: 1}}
	RecoverPayloads(l, oldItems)
	if b, err := os.ReadFile(filepath.Join(dir, "0000_same.txt")); err != nil || string(b) != "new" {
		t.Errorf("the committed replacement was rolled back: %q %v", b, err)
	}
	_ = out
}
