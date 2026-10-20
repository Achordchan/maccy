package syncer

import (
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
	"time"

	"klip/internal/model"
	"klip/internal/paths"
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
	syn.persistAfterUpload(context.Background(), up, &ManifestInfo{Version: "basis"}, "fp-sent")

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
	syn.persistAfterUpload(context.Background(), &syncclient.UploadResult{Version: "mine", SHA256: "my-sha"}, nil, "fp")

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
