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
	return cur != "" && !strings.EqualFold(cur, account)
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
	token = s.bearer()
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
	fp := preMergeFingerprint
	if strings.TrimSpace(fp) == "" {
		fp = "pre-merge"
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
	up, _, err := s.uploadWithFingerprint(ctx, progress, reason, s.currentAccount())
	return up, err
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
			return &syncclient.UploadResult{Version: model.Str(st.SyncLastRemoteVersion), SHA256: exp.SHA256, Size: &size}, fp, nil
		}
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
		token = s.bearer()
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
	err := s.uploadMissingBlobs(ctx, token, exp, started, reason)
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
	token = s.bearer()
	return token, s.uploadMissingBlobs(ctx, token, exp, started, reason+";blob_refresh")
}

func (s *Syncer) uploadMissingBlobs(ctx context.Context, token string, exp snapshot.ExportResult, started time.Time, reason string) error {
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
}

// DownloadAndApply replaces the history and the portable settings with the
// server's snapshot, unless the history changed since expectFingerprint, in
// which case the new captures are merged instead of being lost.
func (s *Syncer) DownloadAndApply(ctx context.Context, progress func(Progress), reason, expectFingerprint string) (DownloadResult, error) {
	s.workflow.Lock()
	defer s.workflow.Unlock()
	return s.downloadAndApply(ctx, progress, reason, expectFingerprint, s.currentAccount())
}

func (s *Syncer) downloadAndApply(ctx context.Context, progress func(Progress), reason, expectFingerprint, account string) (DownloadResult, error) {
	s.gate.Lock()
	defer s.gate.Unlock()
	started := s.cfg.Now()
	report(progress, "prepare_local", "准备本地数据", 30)
	token, err := s.connection(ctx)
	if err != nil {
		return DownloadResult{}, err
	}
	if strings.TrimSpace(account) == "" {
		account = syncclient.AccountID(token)
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

	s.suppress(true)
	defer s.suppress(false)

	var applied []model.Item
	var moves *snapshot.AppliedPayloads
	merged := false
	err = s.cfg.History.Mutate(ctx, func(current []model.Item) ([]model.Item, error) {
		next := data.Items
		if expectFingerprint != "" && Fingerprint(current) != expectFingerprint {
			// A capture arrived while the snapshot was downloading: keep it
			// and let the upload that follows publish both sides.
			next = MergeItems(data.Items, current)
			merged = true
		}
		out, ap, err := snapshot.ApplyPayloads(s.cfg.Layout, importRoot, next)
		if err != nil {
			return nil, err
		}
		moves, applied = ap, out
		return out, nil
	})
	if err != nil {
		if moves != nil {
			moves.Rollback()
		}
		return DownloadResult{}, err
	}
	if moves != nil {
		moves.Commit()
	}
	// The settings change only after the history was replaced: a failure
	// above leaves both as they were.
	if data.Settings != nil {
		s.cfg.Settings.Update(func(m *model.Settings) { m.CopyPortable(*data.Settings) })
		s.cfg.History.ApplyLimits(s.settings())
	}
	res := DownloadResult{Fingerprint: Fingerprint(applied), SnapshotSHA256: sum, Merged: merged}
	s.timing("download", "import_snapshot", started, reason)

	report(progress, "persist_local", "保存本地状态", 90)
	if err := s.cfg.Flush(ctx); err != nil {
		return DownloadResult{}, err
	}
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
			*token = s.bearer()
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
	return s.mergeAndApply(ctx, basis, preMergeFingerprint, progress, reason, s.currentAccount())
}

func (s *Syncer) mergeAndApply(ctx context.Context, basis ManifestInfo, preMergeFingerprint string, progress func(Progress), reason, account string) error {
	s.gate.Lock()
	defer s.gate.Unlock()
	started := s.cfg.Now()
	report(progress, "prepare_local", "prepare local", 18)
	token, err := s.connection(ctx)
	if err != nil {
		return err
	}
	if strings.TrimSpace(account) == "" {
		account = syncclient.AccountID(token)
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
	var merged []model.Item
	var moves *snapshot.AppliedPayloads
	s.suppress(true)
	defer s.suppress(false)
	// The merge runs on the history as it is now, and the payloads it keeps
	// are moved into place under the same lock: a capture made while the
	// download ran is merged, not overwritten.
	err = s.cfg.History.Mutate(ctx, func(current []model.Item) ([]model.Item, error) {
		merged = MergeItems(remote.Items, current)
		applied, ap, err := snapshot.ApplyPayloads(s.cfg.Layout, importRoot, merged)
		if err != nil {
			return nil, err
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
	if moves != nil {
		moves.Commit()
	}
	mergedSettings := localSettings.Portable()
	if remote.Settings != nil {
		mergedSettings = remote.Settings.Portable()
	}
	EnsureSettingsCanHoldItems(&mergedSettings, merged)

	s.cfg.Settings.Update(func(m *model.Settings) { m.CopyPortable(mergedSettings) })
	s.cfg.History.ApplyLimits(s.settings())
	s.timing("merge", "merge_snapshot", started, fmt.Sprintf("%s;localItems=%d;remoteItems=%d;mergedItems=%d", reason, len(local), len(remote.Items), len(merged)))

	report(progress, "persist_local", "persist local", 90)
	if err := s.cfg.Flush(ctx); err != nil {
		return err
	}
	s.persistPendingMerge(preMergeFingerprint, basis)
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
		if target == "" || !isFile(b.SourcePath) {
			continue
		}
		if a, _ := filepath.Abs(b.SourcePath); strings.EqualFold(a, mustAbs(target)) {
			continue
		}
		if isFile(target) {
			// A blob that is there but does not hash to its name is not a
			// backup: keep the one the source still gives.
			if sum, err := snapshot.FileSHA256(target); err == nil && strings.EqualFold(sum, b.SHA256) {
				continue
			}
			if err := os.Remove(target); err != nil {
				return fmt.Errorf("remove the corrupt blob %s: %w", b.SHA256, err)
			}
		}
		if err := copyNew(b.SourcePath, target); err != nil {
			return fmt.Errorf("blob %s: %w", b.SHA256, err)
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

func report(f func(Progress), code, msg string, pct int) {
	if f != nil {
		f(Progress{code, msg, pct})
	}
}

func (s *Syncer) tempSnapshotPath() (string, error) {
	dir := filepath.Join(s.cfg.Layout.Root, "sync", "tmp")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	return filepath.Join(dir, "snapshot-"+guidN()+".sqlite"), nil
}

func (s *Syncer) preMergeBackupPath() (string, error) {
	dir := filepath.Join(s.cfg.Layout.Root, "sync", "backups")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return "", err
	}
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
