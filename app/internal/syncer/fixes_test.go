package syncer

import (
	"context"
	"encoding/base64"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"klip/internal/model"
	"klip/internal/paths"
	"klip/internal/snapshot"
	"klip/internal/syncclient"
)

// unitHistory is the History of a Syncer that does not touch a disk.
type unitHistory struct {
	items    []model.Item
	replaced int
}

func (h *unitHistory) Items() []model.Item { return append([]model.Item(nil), h.items...) }

func (h *unitHistory) ReplaceAll(_ context.Context, items []model.Item) error {
	h.items = append([]model.Item(nil), items...)
	h.replaced++
	return nil
}

func (h *unitHistory) RemoveFiles(paths []string) error {
	for _, p := range paths {
		_ = os.Remove(p)
	}
	return nil
}

func (h *unitHistory) Mutate(_ context.Context, f func([]model.Item) ([]model.Item, error)) error {
	next, err := f(append([]model.Item(nil), h.items...))
	if err != nil {
		return err
	}
	h.items = append([]model.Item(nil), next...)
	h.replaced++
	return nil
}

func (h *unitHistory) ApplyLimits(model.Settings) {}

// changingHistory changes after the first read, as a capture does while an
// upload is running.
type changingHistory struct {
	first []model.Item
	later []model.Item
	reads int
}

func (h *changingHistory) Items() []model.Item {
	h.reads++
	if h.reads == 1 {
		return append([]model.Item(nil), h.first...)
	}
	return append([]model.Item(nil), h.later...)
}

func (h *changingHistory) Mutate(ctx context.Context, f func([]model.Item) ([]model.Item, error)) error {
	next, err := f(h.Items())
	if err != nil {
		return err
	}
	return h.ReplaceAll(ctx, next)
}

func (h *changingHistory) RemoveFiles([]string) error                     { return nil }
func (h *changingHistory) ReplaceAll(context.Context, []model.Item) error { return nil }

func (h *changingHistory) ApplyLimits(model.Settings) {}

func signedIn(s model.Settings) *model.Settings {
	s.AuthAccessToken = model.Ptr("token")
	s.AuthRefreshToken = model.Ptr("refresh")
	s.AuthExpiresAtUnixMs = time.Now().Add(time.Hour).UnixMilli()
	return &s
}

func unitSyncer(t *testing.T, srv *httptest.Server, s *model.Settings, h History) *Syncer {
	t.Helper()
	return New(Config{
		Layout: paths.Layout{Root: t.TempDir()},
		Settings: syncclient.Settings{
			Get:    func() model.Settings { return *s },
			Update: func(f func(*model.Settings)) { f(s) },
		},
		History: h,
		Client:  syncclient.New(srv.URL),
		BaseURL: srv.URL,
		Now:     time.Now,
	})
}

func textItemAt(id, text string) model.Item {
	return model.Item{
		ID: id, Kind: model.KindText, CapturedAt: model.Time{Time: time.Now()},
		ApproxBytes: int64(len(text)) * 2, Text: model.Ptr(text), CopyCount: 1,
	}
}

// The manifest is only the state after an upload when it still describes
// that upload. Another device may have uploaded in between.
func TestPersistAfterUploadKeepsTheUploadedVersion(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, `{"schema":1,"latest":{"version":"other-device","updatedAt":"2026-05-30T13:00:00+00:00","snapshot":{"sha256":"their-sha","size":11}}}`)
	}))
	defer srv.Close()

	s := signedIn(model.DefaultSettings())
	syn := unitSyncer(t, srv, s, &unitHistory{items: []model.Item{textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "hello")}})
	up := &syncclient.UploadResult{Version: "mine", SHA256: "my-sha"}
	if err := syn.persistAfterUpload(context.Background(), up, &ManifestInfo{Version: "basis"}, "fp-sent", ""); err != nil {
		t.Fatal(err)
	}

	if got := model.Str(s.SyncLastRemoteVersion); got != "mine" {
		t.Errorf("version %q: the manifest of another device was recorded", got)
	}
	if got := strings.ToLower(model.Str(s.SyncLastRemoteSha256)); got != "my-sha" {
		t.Errorf("sha %q", got)
	}
	if got := model.Str(s.SyncLastLocalFingerprint); got != "fp-sent" {
		t.Errorf("fingerprint %q", got)
	}
	if got := model.Str(s.SyncLastRemoteUpdatedAt); got == "2026-05-30T13:00:00.0000000+00:00" {
		t.Error("the updatedAt of another device was recorded")
	}
}

func TestPersistAfterUploadUsesTheManifestOfItsOwnUpload(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, `{"schema":1,"latest":{"version":"mine","updatedAt":"2026-05-30T13:00:00+00:00","snapshot":{"sha256":"my-sha","size":11}}}`)
	}))
	defer srv.Close()

	s := signedIn(model.DefaultSettings())
	syn := unitSyncer(t, srv, s, &unitHistory{})
	if err := syn.persistAfterUpload(context.Background(), &syncclient.UploadResult{Version: "mine", SHA256: "my-sha"}, nil, "fp", ""); err != nil {
		t.Fatal(err)
	}

	if got := model.Str(s.SyncLastRemoteVersion); got != "mine" {
		t.Errorf("version %q", got)
	}
	at, err := model.ParseTime(model.Str(s.SyncLastRemoteUpdatedAt))
	if err != nil || at.UTC().Format(time.RFC3339) != "2026-05-30T13:00:00Z" {
		t.Errorf("updatedAt %q %v", model.Str(s.SyncLastRemoteUpdatedAt), err)
	}
}

func TestUploadIsInManifest(t *testing.T) {
	cases := []struct {
		name string
		up   *syncclient.UploadResult
		m    ManifestInfo
		want bool
	}{
		{"version", &syncclient.UploadResult{Version: "v2"}, ManifestInfo{Version: "v2"}, true},
		{"another version", &syncclient.UploadResult{Version: "v2"}, ManifestInfo{Version: "v3"}, false},
		{"sha", &syncclient.UploadResult{SHA256: "ABC"}, ManifestInfo{SnapshotSHA256: "abc"}, true},
		{"another sha", &syncclient.UploadResult{SHA256: "abc"}, ManifestInfo{SnapshotSHA256: "def"}, false},
		{"nothing to compare", &syncclient.UploadResult{}, ManifestInfo{Version: "v2"}, false},
		{"no upload", nil, ManifestInfo{Version: "v2"}, false},
	}
	for _, c := range cases {
		if got := uploadIsInManifest(c.up, c.m); got != c.want {
			t.Errorf("%s: %v", c.name, got)
		}
	}
}

// The state after an upload is the fingerprint of the items that went into
// the snapshot, not of a history that changed while the upload ran.
func TestUploadRemembersTheItemsItSent(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.Method == http.MethodPut && r.URL.Path == "/sync/snapshot":
			io.WriteString(w, `{"version":"v1","sha256":"sha-1","size":10}`)
		case r.URL.Path == "/sync/manifest":
			io.WriteString(w, `{"schema":1,"latest":{"version":"v1","updatedAt":"2026-05-30T12:00:00+00:00","snapshot":{"sha256":"sha-1","size":10}}}`)
		case r.URL.Path == "/sync/blobs/check":
			io.WriteString(w, `{"missing":[]}`)
		}
	}))
	defer srv.Close()

	sent := textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "sent")
	captured := textItemAt("7c9e6679-7425-40de-944b-e07fc1f90ae7", "captured while uploading")
	h := &changingHistory{first: []model.Item{sent}, later: []model.Item{captured, sent}}
	s := signedIn(model.DefaultSettings())
	syn := unitSyncer(t, srv, s, h)

	if _, err := syn.UploadAfterLocalMutation(context.Background()); err != nil {
		t.Fatal(err)
	}
	got := model.Str(s.SyncLastLocalFingerprint)
	if got != Fingerprint([]model.Item{sent}) {
		t.Errorf("fingerprint is not the one of the exported items: %s", got)
	}
	if got == Fingerprint([]model.Item{captured, sent}) {
		t.Error("the change made during the upload was remembered as uploaded")
	}
}

// A backup that refers to a blob which could not be stored is not a backup:
// the merge stops before it replaces the history.
func TestMergeStopsWhenTheBackupBlobCannotBeStored(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	if err := os.WriteFile(l.Blobs(), []byte("not a directory"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(l.Images(), 0o755); err != nil {
		t.Fatal(err)
	}
	img := filepath.Join(l.Images(), "1780000000000_aaaa.png")
	if err := os.WriteFile(img, []byte("image bytes"), 0o644); err != nil {
		t.Fatal(err)
	}
	h := &unitHistory{items: []model.Item{{
		ID: "7c9e6679-7425-40de-944b-e07fc1f90ae7", Kind: model.KindImage,
		CapturedAt: model.Time{Time: time.Now()}, ApproxBytes: 11, ImageFilePath: &img, CopyCount: 1,
	}}}
	s := signedIn(model.DefaultSettings())
	syn := New(Config{
		Layout: l,
		Settings: syncclient.Settings{
			Get:    func() model.Settings { return *s },
			Update: func(f func(*model.Settings)) { f(s) },
		},
		History: h,
		Client:  syncclient.New("http://127.0.0.1:1"),
		BaseURL: "http://127.0.0.1:1",
		Now:     time.Now,
	})

	err := syn.MergeAndApply(context.Background(), ManifestInfo{}, "", nil, "test")
	if err == nil || !strings.Contains(err.Error(), "pre-merge backup") {
		t.Fatalf("a backup that cannot be read back: %v", err)
	}
	if h.replaced != 0 {
		t.Error("the merge replaced the history without a usable backup")
	}
}

// Two backups of the same second, or millisecond, are two files: Go's layout
// needs ".000" for the milliseconds, not "-000".
func TestPreMergeBackupNamesAreUnique(t *testing.T) {
	now := time.Date(2026, 5, 30, 12, 0, 0, 500_000_000, time.UTC)
	syn := New(Config{Layout: paths.Layout{Root: t.TempDir()}, Now: func() time.Time { return now }})

	first, err := syn.preMergeBackupPath()
	if err != nil {
		t.Fatal(err)
	}
	second, err := syn.preMergeBackupPath()
	if err != nil {
		t.Fatal(err)
	}
	if first == second {
		t.Fatalf("two backups of one moment: %s", first)
	}
	want := regexp.MustCompile(`^pre-merge-20260530-120000\.500-[0-9a-f]{8}\.sqlite$`)
	for _, p := range []string{first, second} {
		if !want.MatchString(filepath.Base(p)) {
			t.Errorf("name %q", filepath.Base(p))
		}
	}
}

// snapshotFile exports a snapshot of items, as a server would have it.
func snapshotFile(t *testing.T, items []model.Item) []byte {
	t.Helper()
	l := paths.Layout{Root: t.TempDir()}
	path := filepath.Join(l.Root, "snapshot.sqlite")
	if _, err := snapshot.Export(context.Background(), l, path, items, model.DefaultSettings()); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return data
}

func serveSnapshot(t *testing.T, data []byte) *httptest.Server {
	t.Helper()
	return httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/sync/snapshot" {
			w.WriteHeader(http.StatusNotFound)
			return
		}
		w.Write(data)
	}))
}

func serveManifest(t *testing.T, version, sha, updatedAt string) *httptest.Server {
	t.Helper()
	return httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, `{"schema":1,"latest":{"version":"`+version+`","updatedAt":"`+updatedAt+
			`","snapshot":{"sha256":"`+sha+`","size":1}}}`)
	}))
}

// mergeHistory has a capture arrive after the local history was read for the
// backup, but before the merge runs.
type mergeHistory struct {
	local    []model.Item
	captured model.Item
	stored   []model.Item
}

func (h *mergeHistory) Items() []model.Item { return append([]model.Item(nil), h.local...) }

func (h *mergeHistory) Mutate(_ context.Context, f func([]model.Item) ([]model.Item, error)) error {
	next, err := f(append([]model.Item{h.captured}, h.local...))
	if err != nil {
		return err
	}
	h.stored = next
	return nil
}

func (h *mergeHistory) RemoveFiles([]string) error                     { return nil }
func (h *mergeHistory) ReplaceAll(context.Context, []model.Item) error { return nil }

func (h *mergeHistory) ApplyLimits(model.Settings) {}

// A capture made while the remote snapshot is downloading is part of the
// merge: the history is read again, under its lock, when the merge applies.
func TestMergeAppliesTheHistoryAsItIs(t *testing.T) {
	remote := textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "remote")
	local := textItemAt("7c9e6679-7425-40de-944b-e07fc1f90ae7", "local")
	captured := textItemAt("16fd2706-8baf-433b-82eb-8c7fada847da", "captured during the download")
	srv := serveSnapshot(t, snapshotFile(t, []model.Item{remote}))
	defer srv.Close()
	h := &mergeHistory{local: []model.Item{local}, captured: captured}
	s := signedIn(model.DefaultSettings())
	syn := unitSyncer(t, srv, s, h)

	if err := syn.MergeAndApply(context.Background(), ManifestInfo{}, "", nil, "test"); err != nil {
		t.Fatal(err)
	}
	var got []string
	for _, it := range h.stored {
		got = append(got, model.Str(it.Text))
	}
	if len(got) != 3 {
		t.Fatalf("merged %v: the capture made during the merge was lost", got)
	}
	found := false
	for _, text := range got {
		if text == "captured during the download" {
			found = true
		}
	}
	if !found {
		t.Errorf("merged %v", got)
	}
}

// captureAfterMutationHistory takes a capture just after the download
// replaced the history, before the sync remembers its state.
type captureAfterMutationHistory struct {
	unitHistory
	extra model.Item
}

func (h *captureAfterMutationHistory) Mutate(ctx context.Context, f func([]model.Item) ([]model.Item, error)) error {
	if err := h.unitHistory.Mutate(ctx, f); err != nil {
		return err
	}
	h.items = append([]model.Item{h.extra}, h.items...)
	return nil
}

// The fingerprint after a download is of what the download applied, not of a
// capture that arrived while it was saving.
func TestDownloadRemembersTheHistoryItApplied(t *testing.T) {
	remote := []model.Item{textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "from the server")}
	data := snapshotFile(t, remote)
	srv := serveSnapshot(t, data)
	defer srv.Close()
	h := &captureAfterMutationHistory{extra: textItemAt("7c9e6679-7425-40de-944b-e07fc1f90ae7", "captured after saving")}
	s := signedIn(model.DefaultSettings())
	syn := unitSyncer(t, srv, s, h)

	dr, err := syn.DownloadAndApply(context.Background(), nil, "test", "")
	if err != nil {
		t.Fatal(err)
	}
	if want := Fingerprint(remote); dr.Fingerprint != want {
		t.Errorf("fingerprint %s, want %s: a capture made during the save was recorded as downloaded", dr.Fingerprint, want)
	}
	if dr.Fingerprint == Fingerprint(h.Items()) {
		t.Error("the fingerprint is of the live history")
	}
	tmp := filepath.Join(t.TempDir(), "downloaded.sqlite")
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		t.Fatal(err)
	}
	wantSHA, err := snapshot.FileSHA256(tmp)
	if err != nil || dr.SnapshotSHA256 != wantSHA {
		t.Errorf("sha %q, want %q (%v)", dr.SnapshotSHA256, wantSHA, err)
	}
}

// A manual restore remembers the manifest only when it still describes the
// snapshot that was restored; otherwise only the hash, so the next sync looks
// again.
func TestRestoreStateIsBoundToTheDownloadedSnapshot(t *testing.T) {
	other := serveManifest(t, "v2", "bbb", "2026-05-30T13:00:00+00:00")
	defer other.Close()
	s := signedIn(model.DefaultSettings())
	syn := unitSyncer(t, other, s, &unitHistory{})
	if err := syn.PersistAfterDownload(context.Background(), DownloadResult{Fingerprint: "fp", SnapshotSHA256: "aaa"}); err != nil {
		t.Fatal(err)
	}

	if got := model.Str(s.SyncLastRemoteVersion); got != "" {
		t.Errorf("version %q: another device's snapshot was recorded", got)
	}
	if got := model.Str(s.SyncLastRemoteSha256); got != "aaa" {
		t.Errorf("sha %q", got)
	}
	if got := model.Str(s.SyncLastLocalFingerprint); got != "fp" {
		t.Errorf("fingerprint %q", got)
	}

	same := serveManifest(t, "v2", "AAA", "2026-05-30T13:00:00+00:00")
	defer same.Close()
	s2 := signedIn(model.DefaultSettings())
	syn2 := unitSyncer(t, same, s2, &unitHistory{})
	if err := syn2.PersistAfterDownload(context.Background(), DownloadResult{Fingerprint: "fp", SnapshotSHA256: "aaa"}); err != nil {
		t.Fatal(err)
	}
	if got := model.Str(s2.SyncLastRemoteVersion); got != "v2" {
		t.Errorf("version %q: the manifest of the restored snapshot was not used", got)
	}
}

// A successful but garbled manifest answer must not look like "the server has
// no snapshot": that would upload local history over the remote one.
func TestParseManifestStrictRejectsAGarbledAnswer(t *testing.T) {
	for _, in := range []string{
		"",
		"<html>proxy login</html>",
		`{}`,
		`{"latest":{}}`,
		`{"latest":{"updatedAt":"not a time"}}`,
		`{"latest":{"updatedAt":"2026-05-30T12:00:00+00:00","snapshot":[]}}`,
		`{"latest":`,
	} {
		if _, err := ParseManifestStrict(in); err == nil {
			t.Errorf("%q was accepted as a manifest", in)
		}
	}
	m, err := ParseManifestStrict(`{"schema":1,"latest":{"version":"v1","updatedAt":"2026-05-30T12:00:00+00:00","snapshot":{"sha256":"abc","size":12}}}`)
	if err != nil || m.Version != "v1" || m.SnapshotSHA256 != "abc" || m.UpdatedAt == nil || m.SnapshotSize == nil || *m.SnapshotSize != 12 {
		t.Errorf("%+v %v", m, err)
	}
}

// downloadArrivalHistory has the history change between the decision and the
// download: current is what the history holds when the download applies.
type downloadArrivalHistory struct {
	initial []model.Item
	current []model.Item
	items   []model.Item
}

func (h *downloadArrivalHistory) Items() []model.Item {
	if h.items != nil {
		return append([]model.Item(nil), h.items...)
	}
	return append([]model.Item(nil), h.initial...)
}

func (h *downloadArrivalHistory) Mutate(_ context.Context, f func([]model.Item) ([]model.Item, error)) error {
	next, err := f(append([]model.Item(nil), h.current...))
	if err != nil {
		return err
	}
	h.items = next
	return nil
}

func (h *downloadArrivalHistory) RemoveFiles([]string) error                     { return nil }
func (h *downloadArrivalHistory) ReplaceAll(context.Context, []model.Item) error { return nil }

func (h *downloadArrivalHistory) ApplyLimits(model.Settings) {}

// A capture made after Run chose Download but before the snapshot was applied
// is merged, not replaced: the fingerprint at the decision no longer matches.
func TestDownloadKeepsACaptureMadeWhileItRuns(t *testing.T) {
	remote := textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "from the server")
	srv := serveSnapshot(t, snapshotFile(t, []model.Item{remote}))
	defer srv.Close()
	local := textItemAt("7c9e6679-7425-40de-944b-e07fc1f90ae7", "local at the decision")
	captured := textItemAt("16fd2706-8baf-433b-82eb-8c7fada847da", "captured while downloading")
	h := &downloadArrivalHistory{initial: []model.Item{local}, current: []model.Item{captured, local}}
	s := signedIn(model.DefaultSettings())
	syn := unitSyncer(t, srv, s, h)

	dr, err := syn.DownloadAndApply(context.Background(), nil, "test", Fingerprint([]model.Item{local}))
	if err != nil {
		t.Fatal(err)
	}
	var texts []string
	for _, it := range h.items {
		texts = append(texts, model.Str(it.Text))
	}
	if len(texts) != 3 {
		t.Fatalf("history %v: the capture made during the download was lost", texts)
	}
	if got := dr.Fingerprint; got != Fingerprint(h.items) {
		t.Errorf("fingerprint %q is not of the applied history", got)
	}
}

// A pre-merge backup must not accept a cached blob that does not hash to its
// name: the source is still there to repair it.
func TestMergeBackupRepairsACorruptBlob(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	if err := os.MkdirAll(l.Images(), 0o755); err != nil {
		t.Fatal(err)
	}
	src := filepath.Join(l.Images(), "good.png")
	if err := os.WriteFile(src, []byte("good bytes"), 0o644); err != nil {
		t.Fatal(err)
	}
	sum, err := snapshot.FileSHA256(src)
	if err != nil {
		t.Fatal(err)
	}
	target := l.BlobPath(sum)
	if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(target, []byte("corrupt"), 0o644); err != nil {
		t.Fatal(err)
	}
	s := New(Config{Layout: l})
	if err := s.ensureLocalBackupBlobs(context.Background(), snapshot.ExportResult{
		Blobs: []snapshot.Blob{{SHA256: sum, SourcePath: src, Size: 10}},
	}); err != nil {
		t.Fatal(err)
	}
	if b, _ := os.ReadFile(target); string(b) != "good bytes" {
		t.Errorf("the corrupt blob was kept: %q", b)
	}
}

// Two runs must not interleave: each looks at the manifest, decides, applies
// and remembers, and a second decision on the same manifest would apply the
// same remote items twice.
func TestRunIsSerialized(t *testing.T) {
	var active, peak atomic.Int32
	enter := func() {
		n := active.Add(1)
		for {
			p := peak.Load()
			if n <= p || peak.CompareAndSwap(p, n) {
				return
			}
		}
	}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		enter()
		defer active.Add(-1)
		time.Sleep(20 * time.Millisecond)
		switch r.URL.Path {
		case "/sync/manifest":
			w.WriteHeader(http.StatusNotFound)
		case "/sync/snapshot":
			io.WriteString(w, `{"version":"v1","sha256":"sha-1","size":10}`)
		case "/sync/blobs/check":
			io.WriteString(w, `{"missing":[]}`)
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	defer srv.Close()

	s := signedIn(model.DefaultSettings())
	syn := unitSyncer(t, srv, s, &unitHistory{items: []model.Item{textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "one")}})
	var wg sync.WaitGroup
	errs := make([]error, 2)
	for i := range errs {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			_, errs[i] = syn.Run(context.Background(), nil, "test")
		}(i)
	}
	wg.Wait()
	for _, err := range errs {
		if err != nil {
			t.Fatal(err)
		}
	}
	if n := peak.Load(); n > 1 {
		t.Errorf("%d requests were in flight at once: the runs overlapped", n)
	}
}

// The same-sha shortcut is only valid while the remote snapshot is still
// there: after a storage reset the export has to be uploaded again.
func TestUploadOnlySkipsWhenTheRemoteHasThatSnapshot(t *testing.T) {
	var puts atomic.Int32
	var mu sync.Mutex
	remoteSHA := ""
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/sync/manifest":
			mu.Lock()
			sha := remoteSHA
			mu.Unlock()
			if sha == "" {
				w.WriteHeader(http.StatusNotFound)
				return
			}
			io.WriteString(w, `{"latest":{"version":"v-old","updatedAt":"2026-05-30T12:00:00+00:00","snapshot":{"sha256":"`+sha+`","size":1}}}`)
		case "/sync/snapshot":
			puts.Add(1)
			io.WriteString(w, `{"version":"v-new","sha256":"x","size":1}`)
		case "/sync/blobs/check":
			io.WriteString(w, `{"missing":[]}`)
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	defer srv.Close()

	h := &unitHistory{items: []model.Item{textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "one")}}
	s := signedIn(model.DefaultSettings())
	syn := unitSyncer(t, srv, s, h)
	tmp := filepath.Join(t.TempDir(), "export.sqlite")
	exp, err := snapshot.Export(context.Background(), syn.cfg.Layout, tmp, h.items, *s)
	if err != nil {
		t.Fatal(err)
	}
	s.SyncLastRemoteSha256, s.SyncLastRemoteVersion = &exp.SHA256, model.Ptr("v-old")

	// The remote lost its snapshot: upload it again even though the export
	// has the hash the device remembers.
	if _, err := syn.Upload(context.Background(), nil, "again"); err != nil {
		t.Fatal(err)
	}
	if n := puts.Load(); n != 1 {
		t.Fatalf("uploads %d: a snapshot that is gone was not uploaded", n)
	}

	// The remote still has it: the shortcut applies.
	mu.Lock()
	remoteSHA = exp.SHA256
	mu.Unlock()
	up, err := syn.Upload(context.Background(), nil, "again")
	if err != nil {
		t.Fatal(err)
	}
	if n := puts.Load(); n != 1 {
		t.Errorf("uploads %d: the same snapshot was uploaded again", n)
	}
	if up.Version != "v-old" {
		t.Errorf("version %q: the manifest that confirmed the snapshot was not returned", up.Version)
	}
}

// When a capture turns a download into a merge, the merged history is
// uploaded; otherwise it would be remembered as synchronized while the
// server still has the older snapshot.
func TestRunUploadsAfterADownloadMergedACapture(t *testing.T) {
	remote := textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "remote")
	data := snapshotFile(t, []model.Item{remote})
	tmp := filepath.Join(t.TempDir(), "remote.sqlite")
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		t.Fatal(err)
	}
	sha, err := snapshot.FileSHA256(tmp)
	if err != nil {
		t.Fatal(err)
	}
	var puts atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/sync/manifest":
			io.WriteString(w, `{"schema":1,"latest":{"version":"v1","updatedAt":"2026-05-30T12:00:00+00:00","snapshot":{"sha256":"`+sha+`","size":1}}}`)
		case r.URL.Path == "/sync/snapshot" && r.Method == http.MethodGet:
			w.Write(data)
		case r.URL.Path == "/sync/snapshot" && r.Method == http.MethodPut:
			puts.Add(1)
			io.WriteString(w, `{"version":"v2","sha256":"uploaded","size":1}`)
		case r.URL.Path == "/sync/blobs/check":
			io.WriteString(w, `{"missing":[]}`)
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	defer srv.Close()

	captured := textItemAt("7c9e6679-7425-40de-944b-e07fc1f90ae7", "captured during the download")
	h := &downloadArrivalHistory{current: []model.Item{captured}}
	s := signedIn(model.DefaultSettings())
	syn := unitSyncer(t, srv, s, h)
	res, err := syn.Run(context.Background(), nil, "test")
	if err != nil {
		t.Fatal(err)
	}
	if res.Decision.Direction != Download {
		t.Fatalf("decision %+v", res.Decision)
	}
	if n := puts.Load(); n != 1 {
		t.Errorf("uploads %d: the merged history was not published", n)
	}
	if len(h.items) != 2 {
		t.Errorf("history %d items: remote and the capture should both be there", len(h.items))
	}
	if got := model.Str(s.SyncLastRemoteVersion); got != "v2" {
		t.Errorf("version %q", got)
	}
}

func testJWT(sub string) string {
	payload := base64.RawURLEncoding.EncodeToString([]byte(`{"sub":"` + sub + `"}`))
	return "header." + payload + ".signature"
}

// An upload that started in one account must not retry with another account's
// token: that would publish the first account's snapshot into the second.
func TestUploadStopsWhenTheSessionChanges(t *testing.T) {
	entered := make(chan struct{})
	release := make(chan struct{})
	var once sync.Once
	var refreshes atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.Method == http.MethodPut && r.URL.Path == "/sync/snapshot":
			once.Do(func() { close(entered) })
			<-release
			w.WriteHeader(http.StatusUnauthorized)
		case r.URL.Path == "/auth/refresh":
			refreshes.Add(1)
			io.WriteString(w, `{"accessToken":"`+testJWT("account-b")+`","refreshToken":"rb","accessTokenExpiresAt":"2030-01-01T00:00:00+00:00"}`)
		case r.URL.Path == "/sync/blobs/check":
			io.WriteString(w, `{"missing":[]}`)
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	defer srv.Close()

	var mu sync.Mutex
	s := model.DefaultSettings()
	s.AuthAccessToken = model.Ptr(testJWT("account-a"))
	s.AuthRefreshToken = model.Ptr("ra")
	s.AuthExpiresAtUnixMs = time.Now().Add(time.Hour).UnixMilli()
	syn := New(Config{
		Layout: paths.Layout{Root: t.TempDir()},
		Settings: syncclient.Settings{
			Get:    func() model.Settings { mu.Lock(); defer mu.Unlock(); return s },
			Update: func(f func(*model.Settings)) { mu.Lock(); defer mu.Unlock(); f(&s) },
		},
		History: &unitHistory{items: []model.Item{textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "private")}},
		Client:  syncclient.New(srv.URL),
		BaseURL: srv.URL,
		Now:     time.Now,
	})

	done := make(chan error, 1)
	go func() {
		_, err := syn.Upload(context.Background(), nil, "test")
		done <- err
	}()
	<-entered
	// The user signs into another account while the upload is in flight.
	mu.Lock()
	s.AuthAccessToken = model.Ptr(testJWT("account-b"))
	s.AuthRefreshToken = model.Ptr("rb")
	mu.Unlock()
	close(release)

	err := <-done
	if !errors.Is(err, ErrSessionChanged) {
		t.Fatalf("a session change during the upload: %v", err)
	}
	if n := refreshes.Load(); n != 0 {
		t.Errorf("the new account's token was refreshed %d times for the old upload", n)
	}
}

// A download that started in one account must not apply its snapshot after
// the user signed into another: the new session's local history would hold
// the old account's clipboard contents, which the next sync would upload.
func TestDownloadStopsWhenTheSessionChanges(t *testing.T) {
	data := snapshotFile(t, []model.Item{textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "from account a")})
	entered := make(chan struct{})
	release := make(chan struct{})
	var once sync.Once
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/sync/snapshot" {
			w.WriteHeader(http.StatusNotFound)
			return
		}
		once.Do(func() { close(entered) })
		<-release
		w.Write(data)
	}))
	defer srv.Close()

	var mu sync.Mutex
	s := model.DefaultSettings()
	s.AuthAccessToken = model.Ptr(testJWT("account-a"))
	s.AuthRefreshToken = model.Ptr("ra")
	s.AuthExpiresAtUnixMs = time.Now().Add(time.Hour).UnixMilli()
	h := &unitHistory{}
	syn := New(Config{
		Layout: paths.Layout{Root: t.TempDir()},
		Settings: syncclient.Settings{
			Get:    func() model.Settings { mu.Lock(); defer mu.Unlock(); return s },
			Update: func(f func(*model.Settings)) { mu.Lock(); defer mu.Unlock(); f(&s) },
		},
		History: h,
		Client:  syncclient.New(srv.URL),
		BaseURL: srv.URL,
		Now:     time.Now,
	})

	done := make(chan error, 1)
	go func() {
		_, err := syn.DownloadAndApply(context.Background(), nil, "test", "")
		done <- err
	}()
	<-entered
	mu.Lock()
	s.AuthAccessToken = model.Ptr(testJWT("account-b"))
	s.AuthRefreshToken = model.Ptr("rb")
	mu.Unlock()
	close(release)

	if err := <-done; !errors.Is(err, ErrSessionChanged) {
		t.Fatalf("a session change during the download: %v", err)
	}
	if h.replaced != 0 {
		t.Error("the old account's snapshot was applied to the new session")
	}
}

// The token used for a retry is checked itself, not the settings it was read
// from: another account's token is never sent for this workflow's data.
func TestTokenForRejectsAnotherAccountsToken(t *testing.T) {
	s := model.DefaultSettings()
	s.AuthAccessToken = model.Ptr(testJWT("account-b"))
	syn := New(Config{
		Settings: syncclient.Settings{
			Get:    func() model.Settings { return s },
			Update: func(f func(*model.Settings)) { f(&s) },
		},
	})
	if _, err := syn.tokenFor("account-a"); !errors.Is(err, ErrSessionChanged) {
		t.Errorf("another account's token: %v", err)
	}
	if tok, err := syn.tokenFor("account-b"); err != nil || tok == "" {
		t.Errorf("the same account's token: %q %v", tok, err)
	}
	// An opaque token cannot be attributed, so it is not rejected.
	s.AuthAccessToken = model.Ptr("opaque")
	if tok, err := syn.tokenFor("account-a"); err != nil || tok != "opaque" {
		t.Errorf("opaque token: %q %v", tok, err)
	}
}

// Signing out is a session change too: an in-flight download must not put the
// old account's history into the signed-out session.
func TestDownloadStopsWhenSignedOut(t *testing.T) {
	data := snapshotFile(t, []model.Item{textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "from the account")})
	entered := make(chan struct{})
	release := make(chan struct{})
	var once sync.Once
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/sync/snapshot" {
			w.WriteHeader(http.StatusNotFound)
			return
		}
		once.Do(func() { close(entered) })
		<-release
		w.Write(data)
	}))
	defer srv.Close()

	var mu sync.Mutex
	s := model.DefaultSettings()
	s.AuthAccessToken = model.Ptr(testJWT("account-a"))
	s.AuthRefreshToken = model.Ptr("ra")
	s.AuthExpiresAtUnixMs = time.Now().Add(time.Hour).UnixMilli()
	h := &unitHistory{}
	syn := New(Config{
		Layout: paths.Layout{Root: t.TempDir()},
		Settings: syncclient.Settings{
			Get:    func() model.Settings { mu.Lock(); defer mu.Unlock(); return s },
			Update: func(f func(*model.Settings)) { mu.Lock(); defer mu.Unlock(); f(&s) },
		},
		History: h,
		Client:  syncclient.New(srv.URL),
		BaseURL: srv.URL,
		Now:     time.Now,
	})

	done := make(chan error, 1)
	go func() {
		_, err := syn.DownloadAndApply(context.Background(), nil, "test", "")
		done <- err
	}()
	<-entered
	mu.Lock()
	s.AuthAccessToken, s.AuthRefreshToken = nil, nil
	s.AuthExpiresAtUnixMs = 0
	mu.Unlock()
	close(release)

	if err := <-done; !errors.Is(err, ErrSessionChanged) {
		t.Fatalf("a sign-out during the download: %v", err)
	}
	if h.replaced != 0 {
		t.Error("the old account's snapshot was applied after the sign-out")
	}
}

// snapshotFileWithSettings is snapshotFile with the portable settings the
// snapshot carries.
func snapshotFileWithSettings(t *testing.T, items []model.Item, settings model.Settings) []byte {
	t.Helper()
	l := paths.Layout{Root: t.TempDir()}
	path := filepath.Join(l.Root, "snapshot.sqlite")
	if _, err := snapshot.Export(context.Background(), l, path, items, settings); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return data
}

// pruningHistory applies the item limit as the real history does.
type pruningHistory struct {
	downloadArrivalHistory
}

func (h *pruningHistory) ApplyLimits(s model.Settings) {
	if n := int(s.MaxItems); n > 0 && len(h.items) > n {
		h.items = h.items[:n]
	}
}

// A download that turns into a merge must raise the item limit: otherwise
// ApplyLimits prunes the merged history before Run can upload it.
func TestDownloadMergeExpandsLimits(t *testing.T) {
	remote := []model.Item{
		textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "remote one"),
		textItemAt("7c9e6679-7425-40de-944b-e07fc1f90ae7", "remote two"),
	}
	settings := model.DefaultSettings()
	settings.MaxItems = 2
	srv := serveSnapshot(t, snapshotFileWithSettings(t, remote, settings))
	defer srv.Close()

	local := textItemAt("16fd2706-8baf-433b-82eb-8c7fada847da", "local")
	captured := textItemAt("11111111-2222-3333-4444-555555555555", "captured while downloading")
	h := &pruningHistory{downloadArrivalHistory{initial: []model.Item{local}, current: []model.Item{captured, local}}}
	s := signedIn(model.DefaultSettings())
	s.MaxItems = 2
	syn := unitSyncer(t, srv, s, h)

	if _, err := syn.DownloadAndApply(context.Background(), nil, "test", Fingerprint([]model.Item{local})); err != nil {
		t.Fatal(err)
	}
	if len(h.items) != 4 {
		t.Errorf("history %d items: the merged items were pruned", len(h.items))
	}
	if s.MaxItems < 4 {
		t.Errorf("MaxItems %d: the limit was not expanded for the merge", s.MaxItems)
	}
}

// The old files and history stay until the new history is on disk: a failed
// flush restores both.
func TestDownloadRollsBackWhenFlushFails(t *testing.T) {
	remote := []model.Item{textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "from the server")}
	srv := serveSnapshot(t, snapshotFile(t, remote))
	defer srv.Close()

	local := textItemAt("7c9e6679-7425-40de-944b-e07fc1f90ae7", "local")
	h := &downloadArrivalHistory{initial: []model.Item{local}, current: []model.Item{local}}
	s := signedIn(model.DefaultSettings())
	syn := unitSyncer(t, srv, s, h)
	syn.cfg.Flush = func(context.Context) error { return errors.New("disk full") }

	if _, err := syn.DownloadAndApply(context.Background(), nil, "test", ""); err == nil {
		t.Fatal("a failed flush stopped nothing")
	}
	if len(h.items) != 1 || h.items[0].ID != local.ID {
		t.Errorf("the history was not put back: %+v", h.items)
	}
}

func TestMergeBackupFailsWhenTheSourceIsGone(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	s := New(Config{Layout: l})
	err := s.ensureLocalBackupBlobs(context.Background(), snapshot.ExportResult{
		Blobs: []snapshot.Blob{{
			SHA256:     strings.Repeat("a", 64),
			SourcePath: filepath.Join(t.TempDir(), "gone.png"),
			Size:       10,
		}},
	})
	if err == nil || !strings.Contains(err.Error(), "payload is gone") {
		t.Errorf("a backup without its payload: %v", err)
	}
}

// cleaningHistory removes payloads in RemoveFiles, as the real history does.
type cleaningHistory struct {
	unitHistory
	removed []string
}

func (h *cleaningHistory) RemoveFiles(paths []string) error {
	h.removed = append(h.removed, paths...)
	return h.unitHistory.RemoveFiles(paths)
}

// A payload that the downloaded history no longer refers to stays until the
// new history is saved; only then is it removed.
func TestDownloadKeepsPayloadsUntilFlushed(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	if err := os.MkdirAll(l.Images(), 0o755); err != nil {
		t.Fatal(err)
	}
	img := filepath.Join(l.Images(), "1780000000000_local.png")
	if err := os.WriteFile(img, []byte("image"), 0o644); err != nil {
		t.Fatal(err)
	}
	local := model.Item{
		ID: "7c9e6679-7425-40de-944b-e07fc1f90ae7", Kind: model.KindImage,
		CapturedAt: model.Time{Time: time.Now()}, ApproxBytes: 5, ImageFilePath: &img, CopyCount: 1,
	}
	remote := textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "from the server")
	srv := serveSnapshot(t, snapshotFile(t, []model.Item{remote}))
	defer srv.Close()

	h := &cleaningHistory{unitHistory: unitHistory{items: []model.Item{local}}}
	s := signedIn(model.DefaultSettings())
	syn := New(Config{
		Layout:   l,
		Settings: syncclient.Settings{Get: func() model.Settings { return *s }, Update: func(f func(*model.Settings)) { f(s) }},
		History:  h,
		Client:   syncclient.New(srv.URL),
		BaseURL:  srv.URL,
		Now:      time.Now,
	})
	syn.cfg.Flush = func(context.Context) error { return errors.New("disk full") }
	if _, err := syn.DownloadAndApply(context.Background(), nil, "test", ""); err == nil {
		t.Fatal("a failed flush stopped nothing")
	}
	if _, err := os.Stat(img); err != nil {
		t.Fatal("the old payload was removed before the new history was saved")
	}
	if len(h.items) != 1 || h.items[0].ID != local.ID {
		t.Errorf("the old history was not restored: %+v", h.items)
	}

	// The next attempt saves the history, and only then removes the payload.
	syn.cfg.Flush = func(context.Context) error { return nil }
	if _, err := syn.DownloadAndApply(context.Background(), nil, "test", ""); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(img); !os.IsNotExist(err) {
		t.Errorf("the unreferenced payload is still there: %v", err)
	}
}

// A capture made while the failed download is being flushed is not part of
// what is rolled back; the previous portable settings come back too.
func TestDownloadRollbackKeepsANewCaptureAndTheSettings(t *testing.T) {
	remoteSettings := model.DefaultSettings()
	remoteSettings.MaxItems = 1
	remote := textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "from the server")
	srv := serveSnapshot(t, snapshotFileWithSettings(t, []model.Item{remote}, remoteSettings))
	defer srv.Close()

	local := textItemAt("7c9e6679-7425-40de-944b-e07fc1f90ae7", "local")
	captured := textItemAt("16fd2706-8baf-433b-82eb-8c7fada847da", "captured while flushing")
	h := &unitHistory{items: []model.Item{local}}
	s := signedIn(model.DefaultSettings())
	s.MaxItems = 50
	syn := unitSyncer(t, srv, s, h)
	syn.cfg.Flush = func(context.Context) error {
		h.items = append([]model.Item{captured}, h.items...)
		return errors.New("disk full")
	}

	if _, err := syn.DownloadAndApply(context.Background(), nil, "test", ""); err == nil {
		t.Fatal("a failed flush stopped nothing")
	}
	var ids []string
	for _, it := range h.items {
		ids = append(ids, it.ID)
	}
	if len(ids) != 2 {
		t.Fatalf("history %v: the capture made during the flush was lost", ids)
	}
	have := map[string]bool{ids[0]: true, ids[1]: true}
	if !have[captured.ID] || !have[local.ID] {
		t.Errorf("history %v", ids)
	}
	if s.MaxItems != 50 {
		t.Errorf("MaxItems %d: the remote settings were not rolled back", s.MaxItems)
	}
}

// If the upload after a download-time merge fails, the pending state was
// recorded, so the next run uploads the merged history instead of treating it
// as the downloaded snapshot.
func TestDownloadMergeRecordsPendingStateBeforeUpload(t *testing.T) {
	remote := textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "remote")
	data := snapshotFile(t, []model.Item{remote})
	tmp := filepath.Join(t.TempDir(), "remote.sqlite")
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		t.Fatal(err)
	}
	sha, err := snapshot.FileSHA256(tmp)
	if err != nil {
		t.Fatal(err)
	}
	var failUpload atomic.Bool
	failUpload.Store(true)
	var puts atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/sync/manifest":
			io.WriteString(w, `{"schema":1,"latest":{"version":"v1","updatedAt":"2026-05-30T12:00:00+00:00","snapshot":{"sha256":"`+sha+`","size":1}}}`)
		case r.URL.Path == "/sync/snapshot" && r.Method == http.MethodGet:
			w.Write(data)
		case r.URL.Path == "/sync/snapshot" && r.Method == http.MethodPut:
			puts.Add(1)
			if failUpload.Load() {
				w.WriteHeader(http.StatusInternalServerError)
				return
			}
			io.WriteString(w, `{"version":"v2","sha256":"uploaded","size":1}`)
		case r.URL.Path == "/sync/blobs/check":
			io.WriteString(w, `{"missing":[]}`)
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	defer srv.Close()

	captured := textItemAt("7c9e6679-7425-40de-944b-e07fc1f90ae7", "captured during the download")
	h := &downloadArrivalHistory{current: []model.Item{captured}}
	s := signedIn(model.DefaultSettings())
	syn := unitSyncer(t, srv, s, h)
	if _, err := syn.Run(context.Background(), nil, "test"); err == nil {
		t.Fatal("the failed upload stopped nothing")
	}
	if got := model.Str(s.SyncLastRemoteVersion); got != "v1" {
		t.Errorf("version %q: the pending merge state was not recorded", got)
	}
	if got := model.Str(s.SyncLastLocalFingerprint); got == Fingerprint(h.items) {
		t.Error("the unpublished merge was recorded as synchronized")
	}

	failUpload.Store(false)
	res, err := syn.Run(context.Background(), nil, "test")
	if err != nil {
		t.Fatal(err)
	}
	if res.Decision.Reason != "local_changed_only" || puts.Load() != 2 {
		t.Errorf("retry: %+v, uploads %d", res.Decision, puts.Load())
	}
}

// The FilePaths of history migrated from C# can point at the user's
// documents: cleanup must never pass those to RemoveFiles.
func TestRemoveFilesOnlyGetsKlipPayloads(t *testing.T) {
	l := paths.Layout{Root: t.TempDir()}
	doc := filepath.Join(t.TempDir(), "my-document.pdf")
	if err := os.WriteFile(doc, []byte("user data"), 0o644); err != nil {
		t.Fatal(err)
	}
	local := model.Item{
		ID: "16fd2706-8baf-433b-82eb-8c7fada847da", Kind: model.KindFileList,
		CapturedAt: model.Time{Time: time.Now()}, ApproxBytes: 9, FilePaths: []string{doc}, CopyCount: 1,
	}
	srv := serveSnapshot(t, snapshotFile(t, []model.Item{textItemAt("0f8fad5b-d9cb-469f-a165-70867728950e", "remote")}))
	defer srv.Close()
	h := &cleaningHistory{unitHistory: unitHistory{items: []model.Item{local}}}
	s := signedIn(model.DefaultSettings())
	syn := New(Config{
		Layout:   l,
		Settings: syncclient.Settings{Get: func() model.Settings { return *s }, Update: func(f func(*model.Settings)) { f(s) }},
		History:  h,
		Client:   syncclient.New(srv.URL),
		BaseURL:  srv.URL,
		Now:      time.Now,
	})
	if _, err := syn.DownloadAndApply(context.Background(), nil, "test", ""); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(doc); err != nil {
		t.Fatal("a file outside Klip's folders was deleted")
	}
	for _, p := range h.removed {
		if filepath.Clean(p) == filepath.Clean(doc) {
			t.Errorf("RemoveFiles was given the user's document: %s", p)
		}
	}
}
