package syncclient_test

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"klip/internal/model"
	"klip/internal/paths"
	"klip/internal/snapshot"
	"klip/internal/syncclient"
)

// This test runs the real nas-agent-py and drives the client against it:
// accounts, a card, the tier, a snapshot with its blobs from one device to
// another, and the event stream. It needs a Python with the server's
// requirements; point KLIP_NAS_PYTHON at it, for example:
//
//	python3 -m venv /tmp/nasvenv && /tmp/nasvenv/bin/pip install -r nas-agent-py/requirements.txt
//	KLIP_NAS_PYTHON=/tmp/nasvenv/bin/python go test ./internal/syncclient/
//
// Without it the test is skipped.

const adminKey = "test-admin-key"

type server struct {
	url string
	t   *testing.T
}

func startServer(t *testing.T) *server {
	t.Helper()
	py := os.Getenv("KLIP_NAS_PYTHON")
	if py == "" {
		t.Skip("KLIP_NAS_PYTHON is not set: the real server is not run")
	}
	dir, err := filepath.Abs("../../../nas-agent-py")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(dir, "app.py")); err != nil {
		t.Skipf("server source not available: %v", err)
	}
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	port := ln.Addr().(*net.TCPAddr).Port
	ln.Close()

	cmd := exec.Command(py, "-m", "uvicorn", "main:app", "--host", "127.0.0.1", "--port", fmt.Sprint(port), "--log-level", "warning")
	cmd.Dir = dir
	cmd.Env = append(os.Environ(),
		"STORAGE_ROOT="+t.TempDir(),
		"ADMIN_KEY="+adminKey,
		"AUTH_SIGNING_KEY=test-signing-key-that-is-long-enough-for-hs256",
		"SMTP_HOST=", // email is not configured: password codes give 503
	)
	var out bytes.Buffer
	cmd.Stdout, cmd.Stderr = &out, &out
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		cmd.Process.Kill()
		cmd.Wait()
		if t.Failed() {
			t.Logf("server output:\n%s", out.String())
		}
	})

	s := &server{url: fmt.Sprintf("http://127.0.0.1:%d", port), t: t}
	c := syncclient.New(s.url)
	deadline := time.Now().Add(30 * time.Second)
	for !c.Health(context.Background()) {
		if time.Now().After(deadline) {
			t.Fatalf("the server did not start:\n%s", out.String())
		}
		time.Sleep(100 * time.Millisecond)
	}
	return s
}

// admin posts to an /admin route with the legacy key and returns the JSON.
func (s *server) admin(path string, payload any) map[string]any {
	s.t.Helper()
	b, _ := json.Marshal(payload)
	req, _ := http.NewRequest(http.MethodPost, s.url+path, bytes.NewReader(b))
	req.Header.Set("Authorization", "Bearer "+adminKey)
	req.Header.Set("Content-Type", "application/json")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		s.t.Fatal(err)
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != 200 {
		s.t.Fatalf("%s: %d %s", path, resp.StatusCode, body)
	}
	var m map[string]any
	json.Unmarshal(body, &m)
	return m
}

func sha(b []byte) string { h := sha256.Sum256(b); return hex.EncodeToString(h[:]) }

func statusOf(err error) int {
	var ae *syncclient.APIError
	if errors.As(err, &ae) {
		return ae.StatusCode
	}
	return 0
}

func TestAgainstTheRealServer(t *testing.T) {
	srv := startServer(t)
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	c := syncclient.New(srv.url)

	// ---- accounts
	if _, err := c.Register(ctx, "not-an-email", "password1"); err == nil || err.Error() != "invalid email" {
		t.Errorf("bad email: %v", err)
	}
	if _, err := c.Register(ctx, "a@example.com", "short"); err == nil || err.Error() != "password must be at least 8 characters" {
		t.Errorf("short password: %v", err)
	}
	tok, err := c.Register(ctx, "  Alice@Example.com ", "password1")
	if err != nil {
		t.Fatal(err)
	}
	if tok.AccessToken == "" || tok.RefreshToken == "" || tok.Email != "alice@example.com" {
		t.Errorf("tokens: %+v", tok)
	}
	if d := time.Until(tok.ExpiresAt); d < 119*time.Minute || d > 121*time.Minute {
		t.Errorf("the token lives %v, the server says 2 hours", d)
	}
	if _, err := c.Register(ctx, "alice@example.com", "password1"); err == nil || err.Error() != "email already registered" {
		t.Errorf("duplicate: %v", err)
	}
	if _, err := c.Login(ctx, "alice@example.com", "wrong-password"); err == nil || err.Error() != "invalid email or password" {
		t.Errorf("wrong password: %v", err)
	}
	tok, err = c.Login(ctx, "alice@example.com", "password1")
	if err != nil {
		t.Fatal(err)
	}
	if e, err := c.CurrentEmail(ctx, tok.AccessToken); err != nil || e != "alice@example.com" {
		t.Errorf("me: %q %v", e, err)
	}
	if e, _ := c.CurrentEmail(ctx, "garbage"); e != "" {
		t.Errorf("me with a bad token: %q", e)
	}

	// Refresh gives new tokens and revokes the old refresh token.
	next, err := c.Refresh(ctx, tok.RefreshToken)
	if err != nil || next.AccessToken == "" || next.RefreshToken == tok.RefreshToken {
		t.Fatalf("refresh: %+v %v", next, err)
	}
	if _, err := c.Refresh(ctx, tok.RefreshToken); statusOf(err) != 401 {
		t.Errorf("a used refresh token: %v", err)
	}
	access := next.AccessToken

	// Email is not configured on this server: the code call says so.
	if _, err := c.SendPasswordCode(ctx, "reset", "alice@example.com", ""); err == nil || err.Error() != "email service not configured" || statusOf(err) != 503 {
		t.Errorf("code: %v", err)
	}
	// An unknown address looks the same as a known one (no account leak).
	if n, err := c.SendPasswordCode(ctx, "reset", "nobody@example.com", ""); err != nil || n != 60 {
		t.Errorf("code for nobody: %d %v", n, err)
	}
	if err := c.ResetPassword(ctx, "alice@example.com", "000000", "newpassword1"); err == nil {
		t.Error("reset without a code")
	}

	// ---- before a subscription
	st, err := c.SubscriptionStatus(ctx, access)
	if err != nil || st.Subscribed || st.Tier != nil || st.StorageBytes == nil || *st.StorageBytes != 0 {
		t.Errorf("status before: %+v %v", st, err)
	}
	if st, err := c.SubscriptionStatus(ctx, "garbage"); err != nil || st.Subscribed {
		t.Errorf("status with a bad token: %+v %v", st, err)
	}
	_, err = c.GetManifestJSON(ctx, access)
	var ae *syncclient.APIError
	if !errors.As(err, &ae) || ae.StatusCode != 403 || ae.Code != "subscription_expired" {
		t.Errorf("manifest without a subscription: %#v", err)
	}

	// ---- a card and a tier
	cards := srv.admin("/admin/card/generate", map[string]any{"count": 1, "durationDays": 30})
	code := cards["codes"].([]any)[0].(string)
	if r, err := c.RedeemCard(ctx, access, "0000-0000-0000-0000"); err != nil || r.Success || !strings.Contains(r.Error, "card not found or used") {
		t.Errorf("unknown card: %+v %v", r, err)
	}
	r, err := c.RedeemCard(ctx, access, strings.ToLower(code)) // the server upper-cases it
	if err != nil || !r.Success || r.ExpiresAt == nil {
		t.Fatalf("redeem: %+v %v", r, err)
	}
	if r, _ := c.RedeemCard(ctx, access, code); r.Success {
		t.Error("a card works once")
	}
	// Subscribed, but with no tier yet: sync is still refused.
	if _, err := c.GetManifestJSON(ctx, access); !syncclientIs(err, 403, "tier_not_assigned") {
		t.Errorf("no tier: %v", err)
	}
	srv.admin("/admin/user/tier", map[string]any{"emailOrUserId": "alice@example.com", "tier": "mini"})
	st, err = c.SubscriptionStatus(ctx, access)
	if err != nil || !st.Subscribed || st.Tier == nil || *st.Tier != "Mini" || *st.StorageLimitBytes != 100*1024*1024 || *st.RetentionDays != 30 || *st.OverLimit {
		t.Errorf("status after: %+v %v", st, err)
	}

	// ---- no snapshot yet
	if _, err := c.GetManifestJSON(ctx, access); statusOf(err) != 404 {
		t.Errorf("manifest before the first upload: %v", err)
	}
	if err := c.DownloadSnapshot(ctx, access, filepath.Join(t.TempDir(), "x")); statusOf(err) != 404 {
		t.Errorf("snapshot before the first upload: %v", err)
	}

	// ---- device A: a history with the three kinds, exported and uploaded
	a := paths.Layout{Root: t.TempDir()}
	os.MkdirAll(a.Images(), 0o755)
	imgPath := filepath.Join(a.Images(), "1780000000000_aaaa.png")
	imgBytes := bytes.Repeat([]byte("\x89PNG-image-"), 4000) // compressible
	os.WriteFile(imgPath, imgBytes, 0o644)
	fileID := "16fd2706-8baf-433b-82eb-8c7fada847da"
	cache := filepath.Join(a.Files(), strings.ReplaceAll(fileID, "-", ""))
	os.MkdirAll(cache, 0o755)
	os.WriteFile(filepath.Join(cache, "0000_report.pdf"), []byte("pdf-bytes"), 0o644)
	now := model.Time{Time: time.Now().UTC().Truncate(time.Millisecond)}
	items := []model.Item{
		{ID: "0f8fad5b-d9cb-469f-a165-70867728950e", Kind: model.KindText, CapturedAt: now, ApproxBytes: 16, Text: model.Ptr("你好, Klip"), Pinned: true, CopyCount: 2},
		{ID: "7c9e6679-7425-40de-944b-e07fc1f90ae7", Kind: model.KindImage, CapturedAt: now, ApproxBytes: int64(len(imgBytes)), ImageFilePath: &imgPath, CopyCount: 1},
		{ID: fileID, Kind: model.KindFileList, CapturedAt: now, ApproxBytes: 9, FilePaths: []string{`C:\src\report.pdf`}, CopyCount: 1},
	}
	snapPath := filepath.Join(t.TempDir(), "snapshot.sqlite")
	exp, err := snapshot.Export(ctx, a, snapPath, items, model.DefaultSettings())
	if err != nil {
		t.Fatal(err)
	}

	up, err := c.UploadSnapshot(ctx, access, snapPath)
	if err != nil {
		t.Fatal(err)
	}
	if up.SHA256 != exp.SHA256 || up.Size == nil || *up.Size != exp.Size || up.Version == "" {
		t.Errorf("the server's view of the upload: %+v, local %s %d", up, exp.SHA256, exp.Size)
	}
	mj, err := c.GetManifestJSON(ctx, access)
	if err != nil {
		t.Fatal(err)
	}
	m, err := syncclient.ParseManifest(mj)
	if err != nil || m.Schema != 1 || m.Latest.Version != up.Version || m.Latest.Snapshot.SHA256 != exp.SHA256 ||
		m.Latest.Snapshot.Size != exp.Size || m.Latest.Snapshot.FileName != "snapshot.sqlite" || m.Latest.Snapshot.Type != "sqlite" {
		t.Errorf("manifest: %s %v", mj, err)
	}

	// ---- blobs: only what the server lacks is sent
	var hashes []string
	for _, b := range exp.Blobs {
		hashes = append(hashes, b.SHA256)
	}
	missing, err := c.CheckMissingBlobs(ctx, access, append(hashes, "not-a-hash"))
	if err != nil || len(missing) != len(hashes) {
		t.Fatalf("missing %v %v (the server ignores what is not a hash)", missing, err)
	}
	if err := c.UploadBlob(ctx, access, hashes[0], filepath.Join(t.TempDir(), "none")); err == nil {
		t.Error("a blob that is not on disk")
	}
	wrong := filepath.Join(t.TempDir(), "wrong")
	os.WriteFile(wrong, []byte("other bytes"), 0o644)
	if err := c.UploadBlob(ctx, access, hashes[0], wrong); statusOf(err) != 400 || !strings.Contains(err.Error(), "sha256 mismatch") {
		t.Errorf("a blob under the wrong hash: %v", err)
	}
	for _, b := range exp.Blobs {
		if err := c.UploadBlob(ctx, access, b.SHA256, b.SourcePath); err != nil {
			t.Fatal(err)
		}
	}
	if err := c.UploadBlob(ctx, access, exp.Blobs[0].SHA256, exp.Blobs[0].SourcePath); err != nil {
		t.Errorf("uploading a blob twice is fine: %v", err)
	}
	if missing, _ := c.CheckMissingBlobs(ctx, access, hashes); len(missing) != 0 {
		t.Errorf("still missing %v", missing)
	}
	if st, _ := c.SubscriptionStatus(ctx, access); st.StorageBytes == nil || *st.StorageBytes <= exp.Size {
		t.Errorf("the account's usage must count the blobs: %+v", st)
	}

	// ---- device B downloads and imports
	b := paths.Layout{Root: t.TempDir()}
	downloaded := filepath.Join(b.Root, "sync", "snapshot.sqlite")
	if err := c.DownloadSnapshot(ctx, access, downloaded); err != nil {
		t.Fatal(err)
	}
	if got, _ := os.ReadFile(downloaded); sha(got) != exp.SHA256 {
		t.Error("the downloaded snapshot is not the uploaded one")
	}
	ensure := func(h string) (string, error) {
		dest := b.BlobPath(h)
		return dest, c.DownloadBlob(ctx, access, h, dest)
	}
	res, err := snapshot.Read(ctx, b, downloaded, ensure)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Items) != 3 || res.ImageCount != 1 || res.FileCount != 1 {
		t.Fatalf("imported %+v", res)
	}
	if got, _ := os.ReadFile(model.Str(res.Items[1].ImageFilePath)); !bytes.Equal(got, imgBytes) {
		t.Error("the image differs after the trip")
	}
	if got, _ := os.ReadFile(res.Items[2].FilePaths[0]); string(got) != "pdf-bytes" {
		t.Errorf("file: %q", got)
	}
	if model.Str(res.Items[0].Text) != "你好, Klip" || !res.Items[0].Pinned {
		t.Errorf("text item %+v", res.Items[0])
	}

	// ---- the event stream: B hears that A uploads
	events := make(chan syncclient.Event, 8)
	lctx, lcancel := context.WithCancel(ctx)
	listenDone := make(chan error, 1)
	go func() { listenDone <- c.Listen(lctx, access, func(e syncclient.Event) { events <- e }) }()
	expectEvent := func(name string) syncclient.Event {
		t.Helper()
		for {
			select {
			case e := <-events:
				if e.Name == name {
					return e
				}
			case <-time.After(15 * time.Second):
				t.Fatalf("no %q event", name)
			}
		}
	}
	conn := expectEvent("connected")
	if !strings.Contains(conn.Data, "serverTime") {
		t.Errorf("connected: %q", conn.Data)
	}
	up2, err := c.UploadSnapshot(ctx, access, snapPath)
	if err != nil {
		t.Fatal(err)
	}
	ev := expectEvent("sync-updated")
	var payload struct {
		Version, UpdatedAt, SHA256 string
		Size                       int64
	}
	if err := json.Unmarshal([]byte(ev.Data), &payload); err != nil || payload.Version != up2.Version || payload.SHA256 != exp.SHA256 {
		t.Errorf("sync-updated %q %v", ev.Data, err)
	}
	lcancel()
	<-listenDone

	// A refused token on the stream is ErrUnauthorized, for Watch to refresh.
	if err := c.Listen(ctx, "garbage", func(syncclient.Event) {}); !errors.Is(err, syncclient.ErrUnauthorized) {
		t.Errorf("stream with a bad token: %v", err)
	}

	// ---- Watch with a Session, over the real server: the tokens expire, the
	// session renews them, and the update arrives.
	var s = model.DefaultSettings()
	s.AuthAccessToken, s.AuthRefreshToken = model.Ptr("expired-garbage"), &next.RefreshToken
	s.AuthExpiresAtUnixMs = time.Now().UnixMilli() + 3_600_000 // looks valid, is not
	sess := &syncclient.Session{Client: c, Settings: syncclient.Settings{
		Get: func() model.Settings { return s }, Update: func(f func(*model.Settings)) { f(&s) }}}
	updated := make(chan struct{}, 4)
	wctx, wcancel := context.WithCancel(ctx)
	watchDone := make(chan struct{})
	go func() {
		c.Watch(wctx, sess, syncclient.WatchOptions{InitialDelay: 50 * time.Millisecond, Debounce: 20 * time.Millisecond},
			func() { updated <- struct{}{} })
		close(watchDone)
	}()
	// Wait until the watcher has renewed its token (the stored one changes).
	deadline := time.Now().Add(15 * time.Second)
	for model.Str(s.AuthAccessToken) == "expired-garbage" && time.Now().Before(deadline) {
		time.Sleep(20 * time.Millisecond)
	}
	if model.Str(s.AuthAccessToken) == "expired-garbage" {
		t.Fatal("the session did not renew the token after the server refused it")
	}
	time.Sleep(300 * time.Millisecond) // let the stream connect
	if _, err := c.UploadSnapshot(ctx, model.Str(s.AuthAccessToken), snapPath); err != nil {
		t.Fatal(err)
	}
	select {
	case <-updated:
	case <-time.After(15 * time.Second):
		t.Error("Watch did not report the upload")
	}
	wcancel()
	<-watchDone
}

func syncclientIs(err error, status int, code string) bool {
	var ae *syncclient.APIError
	return errors.As(err, &ae) && ae.StatusCode == status && ae.Code == code
}
