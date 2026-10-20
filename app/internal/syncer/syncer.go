package syncer

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"klip/internal/model"
	"klip/internal/snapshot"
	"klip/internal/syncclient"
)

// Syncer runs the sync operations. One operation runs at a time, as with the
// static gate of SyncService.
type Syncer struct {
	cfg     Config
	session *syncclient.Session
	// gate serializes the operations that talk to the server or change the
	// data folder; workflow is held for a whole Run, so that two runs cannot
	// decide on the same manifest and apply it twice.
	gate     sync.Mutex
	workflow sync.Mutex
}

// New returns a Syncer. cfg.Client, Settings, History, Layout and BaseURL are
// required.
func New(cfg Config) *Syncer {
	if cfg.Now == nil {
		cfg.Now = time.Now
	}
	if cfg.Flush == nil {
		cfg.Flush = func(context.Context) error { return nil }
	}
	return &Syncer{
		cfg: cfg,
		session: &syncclient.Session{
			Client: cfg.Client, Settings: cfg.Settings, Now: cfg.Now, BaseURL: cfg.BaseURL,
		},
	}
}

func (s *Syncer) settings() model.Settings { return s.cfg.Settings.Get() }

// ---- connection and tokens

// connection returns the access token to use, after the checks of
// GetConnectionAsync: the address is the official one, a token that expires
// within a minute is renewed, and one that cannot be used is "not logged in".
func (s *Syncer) connection(ctx context.Context) (string, error) {
	cur := s.settings()
	base := strings.TrimRight(s.cfg.BaseURL, "/")
	if !strings.EqualFold(strings.TrimRight(strings.TrimSpace(cur.NasAgentBaseUrl), "/"), base) {
		s.cfg.Settings.Update(func(m *model.Settings) { m.NasAgentBaseUrl = s.cfg.BaseURL })
		cur = s.settings()
	}
	nowMs := s.cfg.Now().UnixMilli()
	if cur.AuthExpiresAtUnixMs <= nowMs+60_000 {
		// A temporary failure must reach the caller as it is: the tokens are
		// still there, so "not logged in" would be wrong.
		if _, err := s.session.Refresh(ctx); err != nil {
			return "", err
		}
	}
	cur = s.settings()
	token := strings.TrimSpace(model.Str(cur.AuthAccessToken))
	if token == "" || cur.AuthExpiresAtUnixMs <= nowMs+30_000 {
		return "", ErrNotLoggedIn
	}
	return token, nil
}

func (s *Syncer) bearer() string {
	return strings.TrimSpace(model.Str(s.settings().AuthAccessToken))
}

// currentAccount is the account of the stored access token, "" when the
// token does not say (an opaque one).
func (s *Syncer) currentAccount() string { return syncclient.AccountID(s.bearer()) }

// accountChanged says a known account is no longer the signed-in one: the
// user signed out or signed into another account during the workflow.
func (s *Syncer) accountChanged(account string) bool {
	if strings.TrimSpace(account) == "" {
		return false
	}
	cur := s.currentAccount()
	if cur == "" {
		// Signed out (or the token is gone): the session the workflow started
		// in is not there any more.
		return true
	}
	return !strings.EqualFold(cur, account)
}

// checkToken says the access token belongs to account. The token itself is
// checked, not the settings: a sign-in between reading the settings and
// sending the request cannot swap the credentials of a workflow.
func (s *Syncer) checkToken(token, account string) error {
	if strings.TrimSpace(account) == "" {
		return nil
	}
	if id := syncclient.AccountID(token); id != "" && !strings.EqualFold(id, account) {
		return ErrSessionChanged
	}
	return nil
}

// ensureAccount refuses when the workflow's account changed and the token
// about to be used belongs to another one.
func (s *Syncer) ensureAccount(token, account string) error {
	if s.accountChanged(account) {
		return ErrSessionChanged
	}
	return s.checkToken(token, account)
}

// tokenFor is checkToken for the stored token: the token the caller gets is
// the one it will send, so it cannot be replaced by another account's.
func (s *Syncer) tokenFor(account string) (string, error) {
	token := s.bearer()
	if err := s.checkToken(token, account); err != nil {
		return "", err
	}
	return token, nil
}

// refreshBound renews the token only for the account that started the
// workflow. If the settings now hold another account, its credentials are
// not used for the old account's data (a retry would publish a snapshot into
// the wrong account).
func (s *Syncer) refreshBound(ctx context.Context, account string) (bool, error) {
	if s.accountChanged(account) {
		return false, ErrSessionChanged
	}
	ok, err := s.session.Refresh(ctx)
	if err != nil || !ok {
		return ok, err
	}
	if s.accountChanged(account) {
		return false, ErrSessionChanged
	}
	return true, nil
}

func isAuthError(err error) bool { return syncclient.IsStatus(err, 401) }

// isSubscriptionExpired is IsSubscriptionExpired: a 403 whose body says so.
func isSubscriptionExpired(err error) bool {
	var ae *syncclient.APIError
	if !errors.As(err, &ae) || ae.StatusCode != 403 {
		return false
	}
	body := strings.ToLower(ae.Body)
	return strings.Contains(body, "订阅已过期") || (strings.Contains(body, "subscription") && strings.Contains(body, "expired"))
}

// refreshAndRetry is the pattern of the C# calls: when the server refuses the
// token, renew it once and try again. A subscription that has expired is its
// own error.
func (s *Syncer) refreshAndRetry(ctx context.Context, token, account string, call func(token string) error) (string, error) {
	if strings.TrimSpace(account) == "" {
		account = syncclient.AccountID(token)
	}
	if err := s.checkToken(token, account); err != nil {
		return token, err
	}
	if s.accountChanged(account) {
		return token, ErrSessionChanged
	}
	err := call(token)
	if err == nil {
		return token, nil
	}
	if isSubscriptionExpired(err) {
		return token, ErrSubscriptionExpired
	}
	if !isAuthError(err) {
		return token, err
	}
	ok, rerr := s.refreshBound(ctx, account)
	if rerr != nil {
		return token, rerr
	}
	if !ok {
		return token, err
	}
	if token, err = s.tokenFor(account); err != nil {
		return token, err
	}
	return token, call(token)
}

// ---- manifest

// ManifestJSON fetches the server's manifest text; "" and a 404 error mean
// that the server has no snapshot yet.
func (s *Syncer) ManifestJSON(ctx context.Context) (string, error) {
	s.gate.Lock()
	defer s.gate.Unlock()
	return s.manifestJSON(ctx)
}

func (s *Syncer) manifestJSON(ctx context.Context) (string, error) {
	started := s.cfg.Now()
	token, err := s.connection(ctx)
	if err != nil {
		return "", err
	}
	var out string
	_, err = s.refreshAndRetry(ctx, token, syncclient.AccountID(token), func(t string) error {
		var e error
		out, e = s.cfg.Client.GetManifestJSON(ctx, t)
		return e
	})
	if err == nil {
		s.timing("manifest", "fetch", started, "ok")
	}
	return out, err
}

// ManifestInfo fetches and parses the manifest. A server without a snapshot
// answers 404, which is returned as the *syncclient.APIError it is, as the C#
// client does ("云端还没有快照").
func (s *Syncer) ManifestInfo(ctx context.Context) (ManifestInfo, error) {
	s.gate.Lock()
	defer s.gate.Unlock()
	return s.manifestInfoLocked(ctx)
}

// manifestInfoLocked is ManifestInfo for a caller that already holds the
// gate (upload checks whether the remote snapshot is still there).
func (s *Syncer) manifestInfoLocked(ctx context.Context) (ManifestInfo, error) {
	text, err := s.manifestJSON(ctx)
	if err != nil {
		return ManifestInfo{}, err
	}
	return ParseManifestStrict(text)
}

// remoteManifest is ManifestInfo for a sync that has to decide: a server
// without a snapshot is an empty manifest, which the decision turns into an
// upload ("remote_missing"). The C# client fails here with a 404 instead, so
// a new account's first sync never uploads by itself; that is the one
// difference of Run from RunAutoSyncAsync.
func (s *Syncer) remoteManifest(ctx context.Context) (ManifestInfo, error) {
	m, err := s.ManifestInfo(ctx)
	if syncclient.IsStatus(err, 404) {
		return ManifestInfo{}, nil
	}
	return m, err
}

// ---- state

// Fingerprint is the fingerprint of the history now.
func (s *Syncer) Fingerprint() string { return Fingerprint(s.cfg.History.Items()) }

// Decide says what a sync has to do given the server's manifest.
func (s *Syncer) Decide(manifest ManifestInfo) Decision {
	items := s.cfg.History.Items()
	var latest *time.Time
	for _, it := range items {
		if latest == nil || it.CapturedAt.After(*latest) {
			t := it.CapturedAt.Time
			latest = &t
		}
	}
	return Decide(manifest, Fingerprint(items), len(items), latest, s.settings())
}

// PersistSyncState remembers that this history and that snapshot were the
// same at this moment.
func (s *Syncer) PersistSyncState(localFingerprint string, m ManifestInfo) {
	s.cfg.Settings.Update(func(st *model.Settings) {
		st.SyncLastLocalFingerprint = nonEmpty(localFingerprint)
		st.SyncLastRemoteVersion = nonEmpty(m.Version)
		st.SyncLastRemoteUpdatedAt = nil
		if m.UpdatedAt != nil {
			v := model.FormatO(*m.UpdatedAt)
			st.SyncLastRemoteUpdatedAt = &v
		}
		st.SyncLastRemoteSha256 = nonEmpty(m.SnapshotSHA256)
	})
}

// persistPendingMerge records the state before a merge, so that the upload
// that follows is seen as a local change.
func (s *Syncer) persistPendingMerge(preMergeFingerprint string, basis ManifestInfo) {
	fp := strings.TrimSpace(preMergeFingerprint)
	// The pending fingerprint must differ from the history that is about to
	// be uploaded: an empty remote snapshot plus a capture can make the two
	// equal, and then a failed upload would look synchronized.
	if fp == "" || fp == s.Fingerprint() {
		fp = "pending-upload"
	}
	s.PersistSyncState(fp, basis)
}

func nonEmpty(v string) *string {
	if strings.TrimSpace(v) == "" {
		return nil
	}
	return &v
}

// ---- upload

// Upload exports the history and sends it, with the blobs the server lacks.
// It sends nothing when the snapshot is the one the server already has.
func (s *Syncer) Upload(ctx context.Context, progress func(Progress), reason string) (*syncclient.UploadResult, error) {
	s.workflow.Lock()
	defer s.workflow.Unlock()
	account := s.currentAccount()
	if err := s.checkBound(account); err != nil {
		return nil, err
	}
	if err := s.bindFor(account); err != nil {
		return nil, err
	}
	up, _, err := s.uploadWithFingerprint(ctx, progress, reason, account)
	if err != nil {
		return nil, err
	}
	_ = s.bindAccount(account)
	return up, nil
}

// uploadWithFingerprint is Upload, and also says which history went into the
// snapshot: a change made while the upload ran must not be remembered as
// uploaded.
func (s *Syncer) uploadWithFingerprint(ctx context.Context, progress func(Progress), reason, account string) (*syncclient.UploadResult, string, error) {
	s.gate.Lock()
	defer s.gate.Unlock()
	return s.upload(ctx, progress, reason, account)
}

func (s *Syncer) upload(ctx context.Context, progress func(Progress), reason, account string) (res *syncclient.UploadResult, fp string, err error) {
	started := s.cfg.Now()
	report(progress, "prepare_local", "准备本地数据", 30)
	token, err := s.connection(ctx)
	if err != nil {
		return nil, "", err
	}
	if strings.TrimSpace(account) == "" {
		account = syncclient.AccountID(token)
	}
	if err := s.checkBound(account); err != nil {
		return nil, "", err
	}
	if err := s.bindFor(account); err != nil {
		return nil, "", err
	}
	if err := s.checkToken(token, account); err != nil {
		return nil, "", err
	}
	if s.accountChanged(account) {
		return nil, "", ErrSessionChanged
	}

	path, err := s.tempSnapshotPath()
	if err != nil {
		return nil, "", err
	}
	defer tryDelete(path)

	items := s.cfg.History.Items()
	fp = Fingerprint(items)
	// The history is read under the account that started the upload: a sign-in
	// while it is being read must not ship the new account's items with this
	// one's token.
	if err := s.ensureAccount(token, account); err != nil {
		return nil, fp, err
	}
	st := s.settings()
	s.timing("upload", "snapshot_source_ready", started, fmt.Sprintf("%s;items=%d", reason, len(items)))

	report(progress, "export_snapshot", "导出快照", 52)
	exp, err := snapshot.Export(ctx, s.cfg.Layout, path, items, st)
	if err != nil {
		return nil, fp, err
	}
	s.timing("upload", "export_snapshot", started, fmt.Sprintf("%s;size=%d;sha256=%s;blobs=%d", reason, exp.Size, exp.SHA256, len(exp.Blobs)))

	if last := strings.TrimSpace(model.Str(st.SyncLastRemoteSha256)); last != "" && strings.EqualFold(last, exp.SHA256) {
		// The shortcut is safe only while the server still has that snapshot:
		// after a storage reset or a deletion the same export has to be
		// uploaded again.
		if m, err := s.manifestInfoLocked(ctx); err == nil &&
			strings.EqualFold(strings.TrimSpace(m.SnapshotSHA256), exp.SHA256) {
			s.timing("upload", "skip_upload_same_sha", started, reason)
			size := exp.Size
			return &syncclient.UploadResult{Version: strings.TrimSpace(m.Version), SHA256: exp.SHA256, Size: &size}, fp, nil
		}
	}

	if err := s.ensureAccount(token, account); err != nil {
		return nil, fp, err
	}
	if len(exp.Blobs) > 0 {
		report(progress, "upload_blobs", "上传二进制对象", 68)
		if token, err = s.uploadMissingBlobsWithRefresh(ctx, token, exp, started, reason, account); err != nil {
			return nil, fp, err
		}
	}

	report(progress, "upload_snapshot", "上传快照", 84)
	call := func(t string) error {
		var e error
		res, e = s.cfg.Client.UploadSnapshot(ctx, t, path)
		return e
	}
	if err := s.ensureAccount(token, account); err != nil {
		return nil, fp, err
	}
	err = call(token)
	if err != nil {
		if isSubscriptionExpired(err) {
			return nil, fp, ErrSubscriptionExpired
		}
		if !isAuthError(err) {
			return nil, fp, err
		}
		ok, rerr := s.refreshBound(ctx, account)
		if rerr != nil {
			return nil, fp, rerr
		}
		if !ok {
			return nil, fp, err
		}
		if token, err = s.tokenFor(account); err != nil {
			return nil, fp, err
		}
		if len(exp.Blobs) > 0 {
			if token, err = s.uploadMissingBlobsWithRefresh(ctx, token, exp, started, reason+";after_refresh", account); err != nil {
				return nil, fp, err
			}
		}
		if err = call(token); err != nil {
			return nil, fp, err
		}
		s.timing("upload", "upload_snapshot_after_refresh", started, reason)
		return res, fp, nil
	}
	s.timing("upload", "upload_snapshot", started, reason)
	return res, fp, nil
}

func (s *Syncer) uploadMissingBlobsWithRefresh(ctx context.Context, token string, exp snapshot.ExportResult, started time.Time, reason, account string) (string, error) {
	if err := s.ensureAccount(token, account); err != nil {
		return token, err
	}
	err := s.uploadMissingBlobs(ctx, token, exp, started, reason, account)
	if err == nil || !isAuthError(err) {
		return token, err
	}
	ok, rerr := s.refreshBound(ctx, account)
	if rerr != nil {
		return token, rerr
	}
	if !ok {
		return token, err
	}
	if token, err = s.tokenFor(account); err != nil {
		return token, err
	}
	return token, s.uploadMissingBlobs(ctx, token, exp, started, reason+";blob_refresh", account)
}

func (s *Syncer) uploadMissingBlobs(ctx context.Context, token string, exp snapshot.ExportResult, started time.Time, reason, account string) error {
	seen := map[string]bool{}
	var unique []snapshot.Blob
	for _, b := range exp.Blobs {
		k := strings.ToLower(b.SHA256)
		if !seen[k] {
			seen[k] = true
			unique = append(unique, b)
		}
	}
	if len(unique) == 0 {
		return nil
	}
	hashes := make([]string, len(unique))
	for i, b := range unique {
		hashes[i] = b.SHA256
	}
	missingList, err := s.cfg.Client.CheckMissingBlobs(ctx, token, hashes)
	if err != nil {
		return err
	}
	missing := map[string]bool{}
	for _, h := range missingList {
		missing[strings.ToLower(h)] = true
	}
	s.timing("upload", "check_missing_blobs", started, fmt.Sprintf("%s;total=%d;missing=%d", reason, len(unique), len(missing)))
	for _, b := range unique {
		if err := ctx.Err(); err != nil {
			return err
		}
		if !missing[strings.ToLower(b.SHA256)] {
			continue
		}
		if err := s.ensureAccount(token, account); err != nil {
			return err
		}
		if err := s.cfg.Client.UploadBlob(ctx, token, b.SHA256, b.SourcePath); err != nil {
			return err
		}
	}
	s.timing("upload", "upload_blobs", started, fmt.Sprintf("%s;uploaded=%d", reason, len(missing)))
	return nil
}

// ---- download

// DownloadResult says which snapshot a download applied, and with which
// history: the caller remembers that, not the state of the history later,
// which may already hold a capture that was never uploaded.
type DownloadResult struct {
	// Fingerprint is of the items the download applied.
	Fingerprint string
	// SnapshotSHA256 is of the snapshot file, as the server hashes it.
	SnapshotSHA256 string
	// Merged says the history changed while the snapshot was downloaded and
	// the new items were merged in: the result has to be uploaded.
	Merged bool
	// Account is the account the download started in, so that a later step
	// (PersistAfterDownload) can tell whether the session changed.
	Account string
	// PreMergeFingerprint is the history before a download that had to merge
	// a concurrent capture: it is the baseline of the pending upload.
	PreMergeFingerprint string
}

// DownloadAndApply replaces the history and the portable settings with the
// server's snapshot, unless the history changed since expectFingerprint, in
// which case the new captures are merged instead of being lost.
func (s *Syncer) DownloadAndApply(ctx context.Context, progress func(Progress), reason, expectFingerprint string) (DownloadResult, error) {
	s.workflow.Lock()
	defer s.workflow.Unlock()
	account := s.currentAccount()
	if err := s.checkBound(account); err != nil {
		return DownloadResult{}, err
	}
	if err := s.bindFor(account); err != nil {
		return DownloadResult{}, err
	}
	res, err := s.downloadAndApply(ctx, progress, reason, expectFingerprint, account)
	if err != nil {
		return DownloadResult{}, err
	}
	_ = s.bindAccount(account)
	return res, nil
}

func (s *Syncer) downloadAndApply(ctx context.Context, progress func(Progress), reason, expectFingerprint, account string) (DownloadResult, error) {
	s.gate.Lock()
	defer s.gate.Unlock()
	started := s.cfg.Now()
	report(progress, "prepare_local", "准备本地数据", 30)
	s.recoverPayloadDirs()
	token, err := s.connection(ctx)
	if err != nil {
		return DownloadResult{}, err
	}
	if strings.TrimSpace(account) == "" {
		account = syncclient.AccountID(token)
	}
	if err := s.checkBound(account); err != nil {
		return DownloadResult{}, err
	}
	if err := s.bindFor(account); err != nil {
		return DownloadResult{}, err
	}
	if err := s.checkToken(token, account); err != nil {
		return DownloadResult{}, err
	}
	if s.accountChanged(account) {
		return DownloadResult{}, ErrSessionChanged
	}
	path, err := s.tempSnapshotPath()
	if err != nil {
		return DownloadResult{}, err
	}
	defer tryDelete(path)

	report(progress, "download_snapshot", "下载快照", 52)
	if token, err = s.downloadSnapshot(ctx, token, path, account); err != nil {
		return DownloadResult{}, err
	}
	s.timing("download", "download_snapshot", started, fmt.Sprintf("%s;size=%d", reason, fileSize(path)))

	sum, err := snapshot.FileSHA256(path)
	if err != nil {
		return DownloadResult{}, err
	}

	report(progress, "import_snapshot", "导入快照", 76)
	// The payloads are kept apart from the data folders until the history is
	// replaced: a failed replacement must leave the old history's files.
	importRoot := filepath.Join(s.cfg.Layout.Root, ".download-"+guidN())
	if err := os.MkdirAll(importRoot, 0o700); err != nil {
		return DownloadResult{}, err
	}
	defer os.RemoveAll(importRoot)
	data, err := snapshot.ReadDetached(ctx, s.cfg.Layout, importRoot, path, s.ensureBlob(ctx, &token, account))
	if err != nil {
		return DownloadResult{}, err
	}

	if s.accountChanged(account) {
		return DownloadResult{}, ErrSessionChanged
	}
	oldSettings := s.settings()
	var before, applied []model.Item
	var moves *snapshot.AppliedPayloads
	merged := false
	preMerge, appliedFP := "", ""
	err = s.mutateHistory(ctx, func(current []model.Item) ([]model.Item, error) {
		if s.accountChanged(account) {
			return nil, ErrSessionChanged
		}
		before = append([]model.Item(nil), current...)
		next := data.Items
		if expectFingerprint != "" && Fingerprint(current) != expectFingerprint {
			// A capture arrived while the snapshot was downloading: keep it
			// and let the upload that follows publish both sides.
			preMerge = Fingerprint(current)
			next = MergeForDownload(data.Items, current)
			merged = true
		}
		out, ap, err := snapshot.ApplyPayloads(s.cfg.Layout, importRoot, next, before)
		if err != nil {
			return nil, err
		}
		if s.accountChanged(account) {
			// The session changed while the payloads were moving: undo them
			// before the history commit, so the new session cannot get this
			// account's items.
			ap.Rollback()
			return nil, ErrSessionChanged
		}
		// The fingerprint of what this mutation applied, taken while the
		// history is locked: a later deletion is a local change, not part of
		// the downloaded baseline.
		appliedFP = Fingerprint(out)
		moves, applied = ap, out
		return out, nil
	})
	if err != nil {
		if moves != nil {
			moves.Rollback()
		}
		return DownloadResult{}, err
	}
	if s.accountChanged(account) {
		if err := s.discardMutation(ctx, applied, moves, oldSettings); err != nil {
			return DownloadResult{}, fmt.Errorf("%w (session cleanup failed: %v)", ErrSessionChanged, err)
		}
		return DownloadResult{}, ErrSessionChanged
	}
	// The settings change only after the history was replaced: a failure
	// above leaves both as they were.
	if data.Settings != nil {
		settings := data.Settings.Portable()
		if merged {
			// The merge added items to a history that may be at its limits:
			// do not let ApplyLimits prune them before the upload.
			EnsureSettingsCanHoldItems(&settings, applied)
		}
		s.cfg.Settings.Update(func(m *model.Settings) { m.CopyPortable(settings) })
		s.cfg.History.ApplyLimits(s.settings())
	}
	limitPruned := prunedItems(applied, s.cfg.History.Items())
	res := DownloadResult{
		Fingerprint:         appliedFP,
		SnapshotSHA256:      sum,
		Merged:              merged,
		Account:             account,
		PreMergeFingerprint: preMerge,
	}
	// What the limits pruned is unreferenced too.
	removed := s.unreferencedFiles(before, s.cfg.History.Items())
	s.timing("download", "import_snapshot", started, reason)

	report(progress, "persist_local", "保存本地状态", 90)
	if err := s.cfg.Flush(ctx); err != nil {
		// The history was not saved: put the old one, its settings and its
		// files back. Captures made meanwhile are kept.
		if s.accountChanged(account) {
			if derr := s.discardMutation(ctx, applied, moves, oldSettings); derr != nil {
				return DownloadResult{}, fmt.Errorf("%w (session cleanup failed: %v)", err, derr)
			}
			return DownloadResult{}, ErrSessionChanged
		}
		rctx, rcancel := cleanupContext()
		rerr := s.rollbackMutation(rctx, before, applied, oldSettings, limitPruned)
		rcancel()
		if rerr == nil && moves != nil {
			moves.Rollback()
		}
		if rerr != nil {
			return DownloadResult{}, fmt.Errorf("%w (history rollback failed: %v)", err, rerr)
		}
		return DownloadResult{}, err
	}
	if moves != nil {
		moves.MarkCommitted()
	}
	if s.accountChanged(account) {
		// The session changed while the new history was being saved: take
		// this account's items out again and save that, so the new session
		// cannot publish them later.
		if err := s.discardMutation(ctx, applied, moves, oldSettings); err != nil {
			return DownloadResult{}, fmt.Errorf("%w (session cleanup failed: %v)", ErrSessionChanged, err)
		}
		if err := s.persistCleanup(); err != nil {
			return DownloadResult{}, fmt.Errorf("%w (session cleanup not saved: %v)", ErrSessionChanged, err)
		}
		return DownloadResult{}, ErrSessionChanged
	}
	if moves != nil {
		moves.Commit()
	}
	_ = s.cfg.History.RemoveFiles(removed)
	s.timing("download", "persist_local", started, reason)
	return res, nil
}

func (s *Syncer) downloadSnapshot(ctx context.Context, token, path, account string) (string, error) {
	return s.refreshAndRetry(ctx, token, account, func(t string) error {
		return s.cfg.Client.DownloadSnapshot(ctx, t, path)
	})
}

// ensureBlob returns the function snapshot.Read calls for a blob that is not
// stored locally. It reads *token each time, as the C# closure does, so a
// renewed token is used. A blob that cannot be fetched is "not there": the
// import then fails with "blob missing".
func (s *Syncer) ensureBlob(ctx context.Context, token *string, account string) snapshot.EnsureBlob {
	if strings.TrimSpace(account) == "" {
		account = syncclient.AccountID(*token)
	}
	return func(sha string) (string, error) {
		local := s.cfg.Layout.BlobPath(sha)
		if local == "" {
			return "", nil
		}
		if isFile(local) {
			return local, nil
		}
		err := s.cfg.Client.DownloadBlob(ctx, *token, sha, local)
		if err != nil && isAuthError(err) {
			ok, rerr := s.refreshBound(ctx, account)
			if rerr != nil {
				return "", rerr
			}
			if !ok {
				return "", nil
			}
			tok, terr := s.tokenFor(account)
			if terr != nil {
				return "", terr
			}
			*token = tok
			err = s.cfg.Client.DownloadBlob(ctx, *token, sha, local)
		}
		if err != nil || !isFile(local) {
			return "", nil
		}
		return local, nil
	}
}

// ---- merge

// MergeAndApply merges the server's snapshot into the local history: it
// backs the local history up first, then keeps the items of both sides.
// basis is the manifest the decision was made on and preMergeFingerprint the
// fingerprint of the history before the merge; they are remembered so that
// the upload that follows counts as a local change.
func (s *Syncer) MergeAndApply(ctx context.Context, basis ManifestInfo, preMergeFingerprint string, progress func(Progress), reason string) error {
	s.workflow.Lock()
	defer s.workflow.Unlock()
	account := s.currentAccount()
	if err := s.checkBound(account); err != nil {
		return err
	}
	if err := s.bindFor(account); err != nil {
		return err
	}
	if err := s.mergeAndApply(ctx, basis, preMergeFingerprint, progress, reason, account); err != nil {
		return err
	}
	_ = s.bindAccount(account)
	return nil
}

func (s *Syncer) mergeAndApply(ctx context.Context, basis ManifestInfo, preMergeFingerprint string, progress func(Progress), reason, account string) error {
	s.gate.Lock()
	defer s.gate.Unlock()
	started := s.cfg.Now()
	report(progress, "prepare_local", "prepare local", 18)
	s.recoverPayloadDirs()
	token, err := s.connection(ctx)
	if err != nil {
		return err
	}
	if strings.TrimSpace(account) == "" {
		account = syncclient.AccountID(token)
	}
	if err := s.checkBound(account); err != nil {
		return err
	}
	if err := s.bindFor(account); err != nil {
		return err
	}
	if err := s.checkToken(token, account); err != nil {
		return err
	}
	if s.accountChanged(account) {
		return ErrSessionChanged
	}
	local := s.cfg.History.Items()
	localSettings := s.settings()

	report(progress, "backup_snapshot", "backup local snapshot", 28)
	backup, err := s.preMergeBackupPath()
	if err != nil {
		return err
	}
	bexp, err := snapshot.Export(ctx, s.cfg.Layout, backup, local, localSettings)
	if err != nil {
		return err
	}
	if err := s.ensureLocalBackupBlobs(ctx, bexp); err != nil {
		// The local history is about to be replaced: a backup that cannot be
		// read back is not a backup, so stop before anything is changed.
		return fmt.Errorf("pre-merge backup: %w", err)
	}
	s.timing("merge", "backup_snapshot", started, fmt.Sprintf("%s;path=%s;items=%d;size=%d", reason, backup, len(local), bexp.Size))

	path, err := s.tempSnapshotPath()
	if err != nil {
		return err
	}
	defer tryDelete(path)

	report(progress, "download_snapshot", "download remote snapshot", 44)
	if token, err = s.downloadSnapshot(ctx, token, path, account); err != nil {
		return err
	}
	if sum, err := snapshot.FileSHA256(path); err == nil {
		// The snapshot may be newer than the manifest the decision was made
		// on: the pending state must name the file that was actually merged.
		basis = snapshotState(DownloadResult{SnapshotSHA256: sum}, basis)
		if basis.Version == "" {
			basis.Version = sum
		}
	}
	s.timing("merge", "download_snapshot", started, fmt.Sprintf("%s;size=%d", reason, fileSize(path)))

	report(progress, "read_snapshot", "read remote snapshot", 62)
	// The remote payloads go to a folder of their own: the merge may keep the
	// local item of a file-list item, whose files must stay where they are
	// until that is decided.
	importRoot := filepath.Join(s.cfg.Layout.Root, ".merge-"+guidN())
	if err := os.MkdirAll(importRoot, 0o700); err != nil {
		return err
	}
	defer os.RemoveAll(importRoot)
	remote, err := snapshot.ReadDetached(ctx, s.cfg.Layout, importRoot, path, s.ensureBlob(ctx, &token, account))
	if err != nil {
		return err
	}
	s.timing("merge", "read_snapshot", started, fmt.Sprintf("%s;remoteItems=%d;images=%d;files=%d", reason, len(remote.Items), remote.ImageCount, remote.FileCount))

	report(progress, "merge_snapshot", "merge snapshots", 78)
	if s.accountChanged(account) {
		return ErrSessionChanged
	}
	var before, merged []model.Item
	var moves *snapshot.AppliedPayloads
	// The merge runs on the history as it is now, and the payloads it keeps
	// are moved into place under the same lock: a capture made while the
	// download ran is merged, not overwritten.
	err = s.mutateHistory(ctx, func(current []model.Item) ([]model.Item, error) {
		if s.accountChanged(account) {
			return nil, ErrSessionChanged
		}
		before = append([]model.Item(nil), current...)
		merged = MergeItems(remote.Items, current)
		applied, ap, err := snapshot.ApplyPayloads(s.cfg.Layout, importRoot, merged, before)
		if err != nil {
			return nil, err
		}
		if s.accountChanged(account) {
			// The session changed while the payloads were moving: undo them
			// before the history commit.
			ap.Rollback()
			return nil, ErrSessionChanged
		}
		moves, merged = ap, applied
		return merged, nil
	})
	if err != nil {
		if moves != nil {
			moves.Rollback()
		}
		return err
	}
	if s.accountChanged(account) {
		if err := s.discardMutation(ctx, merged, moves, localSettings); err != nil {
			return fmt.Errorf("%w (session cleanup failed: %v)", ErrSessionChanged, err)
		}
		return ErrSessionChanged
	}
	mergedSettings := localSettings.Portable()
	if remote.Settings != nil {
		mergedSettings = remote.Settings.Portable()
	}
	EnsureSettingsCanHoldItems(&mergedSettings, merged)

	s.cfg.Settings.Update(func(m *model.Settings) { m.CopyPortable(mergedSettings) })
	s.cfg.History.ApplyLimits(s.settings())
	limitPruned := prunedItems(merged, s.cfg.History.Items())
	s.timing("merge", "merge_snapshot", started, fmt.Sprintf("%s;localItems=%d;remoteItems=%d;mergedItems=%d", reason, len(local), len(remote.Items), len(merged)))

	removed := s.unreferencedFiles(before, s.cfg.History.Items())
	report(progress, "persist_local", "persist local", 90)
	if err := s.cfg.Flush(ctx); err != nil {
		// The history was not saved: put the old one, its settings and its
		// files back. Captures made meanwhile are kept.
		if s.accountChanged(account) {
			if derr := s.discardMutation(ctx, merged, moves, localSettings); derr != nil {
				return fmt.Errorf("%w (session cleanup failed: %v)", err, derr)
			}
			return ErrSessionChanged
		}
		rctx, rcancel := cleanupContext()
		rerr := s.rollbackMutation(rctx, before, merged, localSettings, limitPruned)
		rcancel()
		if rerr == nil && moves != nil {
			moves.Rollback()
		}
		if rerr != nil {
			return fmt.Errorf("%w (history rollback failed: %v)", err, rerr)
		}
		return err
	}
	if moves != nil {
		moves.MarkCommitted()
	}
	if s.accountChanged(account) {
		// The session changed while the merged history was being saved: take
		// this account's items out again and save that.
		if err := s.discardMutation(ctx, merged, moves, localSettings); err != nil {
			return fmt.Errorf("%w (session cleanup failed: %v)", ErrSessionChanged, err)
		}
		if err := s.persistCleanup(); err != nil {
			return fmt.Errorf("%w (session cleanup not saved: %v)", ErrSessionChanged, err)
		}
		return ErrSessionChanged
	}
	// The merged history is on disk: record the pending upload before the
	// payload cleanup and the upload that follows.
	s.persistPendingMerge(preMergeFingerprint, basis)
	if moves != nil {
		moves.Commit()
	}
	_ = s.cfg.History.RemoveFiles(removed)
	s.timing("merge", "persist_local", started, reason)
	return nil
}

// ensureLocalBackupBlobs copies the files the backup refers to into the blob
// store, so that the backup can be read back later. It fails when a copy
// fails: the merge must not go on with a backup that refers to nothing.
func (s *Syncer) ensureLocalBackupBlobs(ctx context.Context, exp snapshot.ExportResult) error {
	for _, b := range exp.Blobs {
		if err := ctx.Err(); err != nil {
			return err
		}
		target := s.cfg.Layout.BlobPath(b.SHA256)
		if target == "" {
			return fmt.Errorf("blob %s: no local path", b.SHA256)
		}
		if isFile(target) {
			// A blob that is there but does not hash to its name is not a
			// backup: repair it from the source, which Export hashed.
			if sum, err := snapshot.FileSHA256(target); err == nil && strings.EqualFold(sum, b.SHA256) {
				continue
			}
		}
		if a, _ := filepath.Abs(b.SourcePath); strings.EqualFold(a, mustAbs(target)) {
			return fmt.Errorf("blob %s: the only copy is corrupt", b.SHA256)
		}
		if !isFile(b.SourcePath) {
			return fmt.Errorf("blob %s: the payload is gone", b.SHA256)
		}
		if isFile(target) {
			if err := os.Remove(target); err != nil {
				return fmt.Errorf("remove the corrupt blob %s: %w", b.SHA256, err)
			}
		}
		if err := copyNew(b.SourcePath, target); err != nil {
			return fmt.Errorf("blob %s: %w", b.SHA256, err)
		}
		if sum, err := snapshot.FileSHA256(target); err != nil || !strings.EqualFold(sum, b.SHA256) {
			_ = os.Remove(target)
			return fmt.Errorf("blob %s: the copied bytes do not match", b.SHA256)
		}
	}
	return nil
}

// copyNew copies src to dst, which must not exist yet. A file that cannot be
// written or closed is removed, so that a later backup cannot mistake a
// partial file for a complete blob.
func copyNew(src, dst string) (err error) {
	if err := os.MkdirAll(filepath.Dir(dst), 0o700); err != nil {
		return err
	}
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.OpenFile(dst, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return err
	}
	defer func() {
		if cerr := out.Close(); err == nil {
			err = cerr
		}
		if err != nil {
			_ = os.Remove(dst)
		}
	}()
	_, err = io.Copy(out, in)
	return err
}

// ---- small helpers

func (s *Syncer) suppress(on bool) {
	if s.cfg.SuppressAutoSync != nil {
		s.cfg.SuppressAutoSync(on)
	}
}

// mutateHistory applies f to the history with the sync's own change
// suppressed. Suppression covers only the mutation: a capture made while the
// result is being saved is the user's, not the sync's, and schedules a sync.
func (s *Syncer) mutateHistory(ctx context.Context, f func([]model.Item) ([]model.Item, error)) error {
	s.suppress(true)
	defer s.suppress(false)
	return s.cfg.History.Mutate(ctx, f)
}

// recoverPayloadDirs finishes or undoes a cache-folder replacement that a
// crash interrupted. history.json is the source of truth: a folder whose
// files the history still names is current (its backup is deleted); a folder
// whose history names files that are missing is restored from its backup.
func (s *Syncer) recoverPayloadDirs() {
	snapshot.RecoverPayloads(s.cfg.Layout, s.cfg.History.Items())
	dir := s.cfg.Layout.Files()
	ents, err := os.ReadDir(dir)
	if err != nil {
		return
	}
	backups := map[string]string{}
	for _, e := range ents {
		name := e.Name()
		if i := strings.Index(name, ".old-"); i > 0 {
			backups[name[:i]] = filepath.Join(dir, name)
		}
	}
	if len(backups) == 0 {
		return
	}
	items := s.cfg.History.Items()
	for target, backup := range backups {
		targetPath := filepath.Join(dir, target)
		used, broken := false, false
		for _, it := range items {
			for _, p := range it.FilePaths {
				if filepath.Dir(filepath.Clean(p)) != targetPath {
					continue
				}
				used = true
				if !isFile(p) {
					broken = true
				}
			}
		}
		if used && !broken {
			_ = os.RemoveAll(backup)
			continue
		}
		_ = os.RemoveAll(targetPath)
		_ = os.Rename(backup, targetPath)
	}
}

// payloadFiles is where an item keeps its image or file contents.
func payloadFiles(it model.Item) []string {
	switch it.Kind {
	case model.KindImage:
		if p := strings.TrimSpace(model.Str(it.ImageFilePath)); p != "" {
			return []string{p}
		}
	case model.KindFileList:
		var out []string
		for _, p := range it.FilePaths {
			if strings.TrimSpace(p) != "" {
				out = append(out, p)
			}
		}
		return out
	}
	return nil
}

// unreferencedFiles is the files of old that next does not refer to: the
// payloads a history mutation made orphans, to be removed only after the new
// history is on disk. Only files in Klip's own folders are listed: the
// FilePaths of an item migrated from C# point at the user's documents, and
// those are never ours to delete.
func (s *Syncer) unreferencedFiles(old, next []model.Item) []string {
	keep := map[string]bool{}
	for _, it := range next {
		for _, p := range append(payloadFiles(it), s.fileCacheFiles(it)...) {
			keep[filepath.Clean(p)] = true
		}
	}
	seen := map[string]bool{}
	var out []string
	for _, it := range old {
		for _, p := range append(payloadFiles(it), s.fileCacheFiles(it)...) {
			c := filepath.Clean(p)
			if keep[c] || seen[c] || !s.ownPayload(c) {
				continue
			}
			seen[c] = true
			out = append(out, p)
		}
	}
	return out
}

// fileCacheFiles is the copy Klip keeps of a file-list item. A history
// migrated from C# names the user's documents in FilePaths, so the cache is
// found from the item id instead.
func (s *Syncer) fileCacheFiles(it model.Item) []string {
	if it.Kind != model.KindFileList || strings.TrimSpace(it.ID) == "" {
		return nil
	}
	// The active cache is the one FilePaths names, when it is one of ours;
	// the canonical folder is only for migrated items whose paths are the
	// user's documents. Returning every folder with the prefix would keep
	// the obsolete copies "referenced" forever.
	dir := ""
	if len(it.FilePaths) > 0 {
		if d := filepath.Dir(filepath.Clean(it.FilePaths[0])); s.ownPayload(d) {
			dir = d
		}
	}
	if dir == "" {
		dir = filepath.Join(s.cfg.Layout.Files(), it.IDN())
	}
	ents, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	var out []string
	for _, e := range ents {
		if !e.IsDir() {
			out = append(out, filepath.Join(dir, e.Name()))
		}
	}
	return out
}

// ownPayload says the file is inside the folders Klip keeps for images and
// files. A path from a migrated history that points at the user's documents
// is not one.
func (s *Syncer) ownPayload(p string) bool {
	for _, base := range []string{s.cfg.Layout.Images(), s.cfg.Layout.Files()} {
		rel, err := filepath.Rel(base, p)
		if err != nil || filepath.IsAbs(rel) || rel == ".." {
			continue
		}
		if !strings.HasPrefix(rel, ".."+string(filepath.Separator)) {
			return true
		}
	}
	return false
}

// sameMergeIdentity says two items became one in MergeItems: same kind and
// the same id, or the same content hash (an older item may have no hash).
func sameMergeIdentity(a, b model.Item) bool {
	if a.Kind != b.Kind {
		return false
	}
	ha := strings.TrimSpace(model.Str(a.ContentHash))
	hb := strings.TrimSpace(model.Str(b.ContentHash))
	if ha != "" && hb != "" && strings.EqualFold(ha, hb) {
		return true
	}
	ia := strings.ToLower(strings.TrimSpace(a.ID))
	ib := strings.ToLower(strings.TrimSpace(b.ID))
	return ia != "" && ia != emptyGUID && ia == ib
}

// MergeForDownload merges a downloaded history with the local one when a
// capture arrived during the download. Unlike a first merge, the two sides
// share a synchronized baseline, so the copy count of an item both sides have
// is the larger one, not the sum.
func MergeForDownload(remote, local []model.Item) []model.Item {
	merged := MergeItems(remote, local)
	for i := range merged {
		best, found := int32(0), false
		for _, it := range remote {
			if sameMergeIdentity(merged[i], it) {
				best, found = max(best, it.CopyCount), true
			}
		}
		for _, it := range local {
			if sameMergeIdentity(merged[i], it) {
				best, found = max(best, it.CopyCount), true
			}
		}
		if found {
			merged[i].CopyCount = best
		}
	}
	return merged
}

// prunedItems is the applied items that the limits removed: they are not
// user deletions, so a failed replacement restores them.
func prunedItems(applied, current []model.Item) map[string]bool {
	have := make(map[string]bool, len(current))
	for _, it := range current {
		have[strings.ToLower(it.ID)] = true
	}
	pruned := map[string]bool{}
	for _, it := range applied {
		id := strings.ToLower(it.ID)
		if !have[id] {
			pruned[id] = true
		}
	}
	return pruned
}

// rollbackItems is the history after a failed mutation: the items before it,
// with the note, pin and copy count edits made meanwhile, plus the captures
// that were added. Items that only the failed mutation added are dropped.
func rollbackItems(before, applied, current []model.Item, limitPruned map[string]bool) []model.Item {
	currentByID := make(map[string]model.Item, len(current))
	for _, it := range current {
		currentByID[strings.ToLower(it.ID)] = it
	}
	appliedIDs := make(map[string]bool, len(applied))
	for _, it := range applied {
		appliedIDs[strings.ToLower(it.ID)] = true
	}
	appliedByID := make(map[string]model.Item, len(applied))
	for _, it := range applied {
		appliedByID[strings.ToLower(it.ID)] = it
	}
	beforeIDs := make(map[string]bool, len(before))
	out := make([]model.Item, 0, len(before)+len(current))
	for _, it := range before {
		id := strings.ToLower(it.ID)
		beforeIDs[id] = true
		_, inApplied := appliedByID[id]
		cur, inCurrent := currentByID[id]
		if inApplied && !inCurrent && !limitPruned[id] {
			// The user deleted this item while the failed operation was
			// being flushed: keep the deletion. A limit pruning is not a
			// user deletion and must be undone with the replacement.
			continue
		}
		if inCurrent {
			// Only a field changed after the mutation was applied is the
			// user's edit; the rest goes back to what it was before.
			if ap, applied := appliedByID[id]; applied {
				if !sameNote(cur.Note, ap.Note) {
					it.Note = cur.Note
				}
				if cur.Pinned != ap.Pinned {
					it.Pinned = cur.Pinned
				}
				if cur.CopyCount != ap.CopyCount {
					it.CopyCount = cur.CopyCount
				}
			}
		}
		out = append(out, it)
	}
	for _, it := range current {
		id := strings.ToLower(it.ID)
		if !appliedIDs[id] && !beforeIDs[id] {
			out = append(out, it)
		}
	}
	// Sort only: MergeItems would fold duplicate items of a history that does
	// not merge them.
	sortItems(out)
	return out
}

func sameNote(a, b *string) bool {
	if a == nil || b == nil {
		return a == nil && b == nil
	}
	return *a == *b
}

// sortItems is the order MergeItems gives: newest first, then by id.
func sortItems(items []model.Item) {
	sort.SliceStable(items, func(i, j int) bool {
		a, b := items[i], items[j]
		if !a.CapturedAt.Equal(b.CapturedAt.Time) {
			return a.CapturedAt.After(b.CapturedAt.Time)
		}
		return strings.ToLower(a.ID) < strings.ToLower(b.ID)
	})
}

// cleanupContext bounds the rollback of a failed operation; it is not the
// cancelled context that may have failed the save.
func cleanupContext() (context.Context, context.CancelFunc) {
	return context.WithTimeout(context.Background(), 30*time.Second)
}

// persistCleanup writes a session-change cleanup on a context of its own, so
// a cancelled sync still saves the removal of the old account's items. It
// retries, because the window it closes is the history surviving a restart.
func (s *Syncer) persistCleanup() error {
	var err error
	for i := 0; i < 3; i++ {
		ctx, cancel := cleanupContext()
		err = s.cfg.Flush(ctx)
		cancel()
		if err == nil {
			return nil
		}
		time.Sleep(200 * time.Millisecond)
	}
	return err
}

// itemsNotIn is the items whose id is not in exclude.
func itemsNotIn(exclude, items []model.Item) []model.Item {
	have := make(map[string]bool, len(exclude))
	for _, it := range exclude {
		have[strings.ToLower(it.ID)] = true
	}
	var out []model.Item
	for _, it := range items {
		if !have[strings.ToLower(it.ID)] {
			out = append(out, it)
		}
	}
	return out
}

// rollbackMutation puts before back after a failed save, but keeps the items
// captured while the operation was in flight: they were neither downloaded nor
// part of the mutation, so losing them would be data loss. The previous
// portable settings are restored too.
func (s *Syncer) rollbackMutation(ctx context.Context, before, applied []model.Item, oldSettings model.Settings, limitPruned map[string]bool) error {
	if err := s.cfg.History.Mutate(ctx, func(current []model.Item) ([]model.Item, error) {
		return rollbackItems(before, applied, current, limitPruned), nil
	}); err != nil {
		return err
	}
	s.cfg.Settings.Update(func(m *model.Settings) { m.CopyPortable(oldSettings.Portable()) })
	s.cfg.History.ApplyLimits(s.settings())
	return nil
}

// discardMutation removes the items an operation had applied when the
// session changed during its commit: the new session must not inherit them.
// Unlike rollbackMutation it does not restore before, so a history the new
// session already replaced (or cleared) is left alone.
func (s *Syncer) discardMutation(ctx context.Context, applied []model.Item, moves *snapshot.AppliedPayloads, oldSettings model.Settings) error {
	rctx, cancel := cleanupContext()
	defer cancel()
	if err := s.cfg.History.Mutate(rctx, func(current []model.Item) ([]model.Item, error) {
		return itemsNotIn(applied, current), nil
	}); err != nil {
		// The history still refers to the moved payloads: keep them.
		return err
	}
	s.cfg.Settings.Update(func(m *model.Settings) { m.CopyPortable(oldSettings.Portable()) })
	if moves != nil {
		moves.Rollback()
	}
	return nil
}

// retainedItems is the applied items that the history still holds: the limits
// may have pruned some, and a capture made later is not part of what this
// sync applied.
func retainedItems(applied, current []model.Item) []model.Item {
	have := make(map[string]bool, len(current))
	for _, it := range current {
		have[strings.ToLower(it.ID)] = true
	}
	out := make([]model.Item, 0, len(applied))
	for _, it := range applied {
		if have[strings.ToLower(it.ID)] {
			out = append(out, it)
		}
	}
	return out
}

func report(f func(Progress), code, msg string, pct int) {
	if f != nil {
		f(Progress{code, msg, pct})
	}
}

func (s *Syncer) tempSnapshotPath() (string, error) {
	dir := filepath.Join(s.cfg.Layout.Root, "sync", "tmp")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return "", err
	}
	_ = os.Chmod(dir, 0o700)
	return filepath.Join(dir, "snapshot-"+guidN()+".sqlite"), nil
}

func (s *Syncer) preMergeBackupPath() (string, error) {
	dir := filepath.Join(s.cfg.Layout.Root, "sync", "backups")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return "", err
	}
	_ = os.Chmod(dir, 0o700)
	// ".000" is the milliseconds of the layout, not literal text. The guid
	// keeps two backups of the same millisecond apart; Export deletes a file
	// that is already there.
	name := "pre-merge-" + s.cfg.Now().Format("20060102-150405.000") + "-" + guidN()[:8] + ".sqlite"
	return filepath.Join(dir, name), nil
}

func guidN() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

func tryDelete(p string) {
	if p == "" {
		return
	}
	for i := 0; i < 6; i++ {
		if err := os.Remove(p); err == nil || errors.Is(err, os.ErrNotExist) {
			return
		}
		time.Sleep(120 * time.Millisecond)
	}
}

func fileSize(p string) int64 {
	st, err := os.Stat(p)
	if err != nil {
		return 0
	}
	return st.Size()
}

func isFile(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}

func mustAbs(p string) string {
	a, _ := filepath.Abs(p)
	return a
}

// timing appends a line to sync_timing.log, as WriteTimingLog does.
func (s *Syncer) timing(op, stage string, started time.Time, details string) {
	line := fmt.Sprintf("%s | %s | %s | elapsedMs=%d", s.cfg.Now().Format(model.OLayout), op, stage, s.cfg.Now().Sub(started).Milliseconds())
	if strings.TrimSpace(details) != "" {
		line += " | " + strings.TrimSpace(details)
	}
	f, err := os.OpenFile(filepath.Join(s.cfg.Layout.Root, "sync_timing.log"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return
	}
	defer f.Close()
	fmt.Fprintln(f, line)
}

// ---- account binding

// accountFile is where the sync state's account is kept, next to the backups.
// It is not part of settings.json: the C# client has no such property, and a
// Klip 1.0.10 device must keep reading the file.
func (s *Syncer) accountFile() string {
	return filepath.Join(s.cfg.Layout.Root, "sync", "account")
}

// boundAccount is the account the local sync state belongs to, "" when no
// sync has recorded one yet.
func (s *Syncer) boundAccount() string {
	b, err := os.ReadFile(s.accountFile())
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(b))
}

// bindAccount records the account of the state that was just saved.
func (s *Syncer) bindAccount(account string) error {
	if strings.TrimSpace(account) == "" {
		return nil
	}
	dir := filepath.Join(s.cfg.Layout.Root, "sync")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	return os.WriteFile(s.accountFile(), []byte(account), 0o600)
}

// bindFor records the account when no state has been bound yet: the local
// history is about to be used, so from now on it belongs to this account even
// if the operation does not finish.
func (s *Syncer) bindFor(account string) error {
	if strings.TrimSpace(account) == "" || s.boundAccount() != "" {
		return nil
	}
	return s.bindAccount(account)
}

// checkBound refuses to sync a state that belongs to another account. After a
// sign-out or an account change the app has to clear the sync state (and this
// file) before the new account syncs; until then nothing is uploaded with the
// new account's credentials.
func (s *Syncer) checkBound(account string) error {
	if strings.TrimSpace(account) == "" {
		return nil
	}
	if bound := s.boundAccount(); bound != "" && !strings.EqualFold(bound, account) {
		return ErrSessionChanged
	}
	return nil
}
