package syncer

import (
	"context"
	"strings"

	"klip/internal/model"
	"klip/internal/syncclient"
)

// RunResult says what a Run did.
type RunResult struct {
	Decision Decision
	// Uploaded is the server's answer to the upload, when there was one.
	Uploaded *syncclient.UploadResult
}

// CanAutoSync says whether a sync can be tried without asking the user to
// sign in: there is a valid token, or a refresh token to get one.
func (s *Syncer) CanAutoSync() bool {
	cur := s.settings()
	if strings.TrimSpace(model.Str(cur.AuthAccessToken)) != "" && cur.AuthExpiresAtUnixMs > s.cfg.Now().UnixMilli()+60_000 {
		return true
	}
	return strings.TrimSpace(model.Str(cur.AuthRefreshToken)) != ""
}

// Run is a whole sync: look at the server, decide, do it, and remember the
// state. It is RunAutoSyncAsync without the window, and also what "sync
// now" does.
func (s *Syncer) Run(ctx context.Context, progress func(Progress), reason string) (RunResult, error) {
	s.workflow.Lock()
	defer s.workflow.Unlock()
	return s.run(ctx, progress, reason)
}

func (s *Syncer) run(ctx context.Context, progress func(Progress), reason string) (RunResult, error) {
	account := s.currentAccount()
	if err := s.checkBound(account); err != nil {
		return RunResult{}, err
	}
	if err := s.bindFor(account); err != nil {
		return RunResult{}, err
	}
	report(progress, "check_remote", "检查远端", 10)
	manifest, err := s.remoteManifest(ctx)
	if err != nil {
		return RunResult{}, err
	}
	if strings.TrimSpace(account) == "" {
		account = s.currentAccount()
	}
	if err := s.checkBound(account); err != nil {
		return RunResult{}, err
	}
	if err := s.bindFor(account); err != nil {
		return RunResult{}, err
	}
	if s.accountChanged(account) {
		return RunResult{}, ErrSessionChanged
	}
	localItems := s.cfg.History.Items()
	d := s.DecideItems(manifest, localItems)
	res := RunResult{Decision: d}

	switch d.Direction {
	case None:
		if s.accountChanged(account) {
			return res, ErrSessionChanged
		}
		// timestamp_fallback is a no-op on two histories that differ: do not
		// record them as synchronized, or the next run believes they match.
		if d.Reason != "timestamp_fallback" {
			s.PersistSyncState(d.LocalFingerprint, d.Manifest, account)
		}

	case Download:
		dr, err := s.downloadAndApply(ctx, progress, reason, d.LocalFingerprint, localItems, account)
		if err != nil {
			return res, err
		}
		if dr.Merged {
			// The history changed while the snapshot was downloading: the
			// merge has unpublished items, so upload it as a merge does. The
			// pending state is recorded first, so a failed upload is retried
			// as a local change and not mistaken for the downloaded snapshot.
			state := s.downloadState(ctx, dr, d.Manifest)
			if s.accountChanged(account) {
				return res, ErrSessionChanged
			}
			s.persistPendingMerge(dr.PreMergeFingerprint, state, dr.Account)
			up, fp, err := s.uploadWithFingerprint(ctx, progress, reason+"_download_merge_upload", account)
			if err != nil {
				return res, err
			}
			res.Uploaded = up
			if err := s.persistAfterUpload(ctx, up, &d.Manifest, fp, account); err != nil {
				return res, err
			}
			break
		}
		if s.accountChanged(account) {
			return res, ErrSessionChanged
		}
		state := s.downloadState(ctx, dr, d.Manifest)
		if s.accountChanged(account) {
			return res, ErrSessionChanged
		}
		s.PersistSyncState(dr.Fingerprint, state, dr.Account)

	case Merge:
		if err := s.mergeAndApply(ctx, d.Manifest, d.LocalFingerprint, progress, reason, account); err != nil {
			return res, err
		}
		up, fp, err := s.uploadWithFingerprint(ctx, progress, reason+"_merge_upload", account)
		if err != nil {
			return res, err
		}
		res.Uploaded = up
		if err := s.persistAfterUpload(ctx, up, &d.Manifest, fp, account); err != nil {
			return res, err
		}

	default: // Upload
		up, fp, err := s.uploadWithFingerprint(ctx, progress, reason, account)
		if err != nil {
			return res, err
		}
		res.Uploaded = up
		if err := s.persistAfterUpload(ctx, up, &d.Manifest, fp, account); err != nil {
			return res, err
		}
	}
	if s.accountChanged(account) {
		return res, ErrSessionChanged
	}
	if err := s.bindAccount(account); err != nil {
		return res, err
	}
	return res, nil
}

// UploadAfterLocalMutation uploads right after the user changed the history
// (UploadCloudAfterLocalMutationAsync), without looking at the server first.
func (s *Syncer) UploadAfterLocalMutation(ctx context.Context) (*syncclient.UploadResult, error) {
	s.workflow.Lock()
	defer s.workflow.Unlock()
	account := s.currentAccount()
	if account == "" {
		// A refresh-only session learns its account here, before the upload,
		// so the state that follows is checked against the same account.
		if token, err := s.connection(ctx); err == nil {
			account = syncclient.AccountID(token)
		}
	}
	if err := s.checkBound(account); err != nil {
		return nil, err
	}
	if err := s.bindFor(account); err != nil {
		return nil, err
	}
	up, fp, err := s.uploadWithFingerprint(ctx, nil, "local_mutation", account)
	if err != nil {
		return nil, err
	}
	if err := s.persistAfterUpload(ctx, up, nil, fp, account); err != nil {
		return nil, err
	}
	if err := s.bindAccount(account); err != nil {
		return nil, err
	}
	return up, nil
}

// persistAfterUpload is PersistSyncStateAfterUploadAsync: the state to
// remember is the snapshot that was uploaded, with the fingerprint of the
// items that went into it. The manifest is used only when it still describes
// that upload: another device may have uploaded in between, and remembering
// its version next to this history would lose both changes.
func (s *Syncer) persistAfterUpload(ctx context.Context, up *syncclient.UploadResult, fallback *ManifestInfo, fp, account string) error {
	if s.accountChanged(account) {
		// The upload went to the old account; the new session must not record
		// it as its own state.
		return ErrSessionChanged
	}
	if m, err := s.ManifestInfo(ctx); err == nil && uploadIsInManifest(up, m) {
		if s.accountChanged(account) {
			return ErrSessionChanged
		}
		s.PersistSyncState(fp, m, account)
		return nil
	}
	if s.accountChanged(account) {
		return ErrSessionChanged
	}
	switch {
	case up != nil:
		// No server timestamp: the client's clock would look like a rollback.
		m := ManifestInfo{Version: up.Version, SnapshotSHA256: up.SHA256, SnapshotSize: up.Size}
		s.PersistSyncState(fp, m, account)
	case fallback != nil:
		s.PersistSyncState(fp, *fallback, account)
	default:
		s.PersistSyncState(fp, ManifestInfo{}, account)
	}
	return nil
}

// uploadIsInManifest says the server's manifest describes the snapshot this
// upload wrote, not a later one from another device. The upload's version is
// authoritative; the hash is the fallback for a server that answers without
// one.
func uploadIsInManifest(up *syncclient.UploadResult, m ManifestInfo) bool {
	if up == nil {
		return false
	}
	if v := strings.TrimSpace(up.Version); v != "" {
		if v != strings.TrimSpace(m.Version) {
			return false
		}
		// A version can be reused (timestamp-based): the hash must agree too
		// when both sides provide one.
		sha := strings.TrimSpace(up.SHA256)
		remote := strings.TrimSpace(m.SnapshotSHA256)
		if sha != "" && remote != "" {
			return strings.EqualFold(sha, remote)
		}
		return true
	}
	if sha := strings.TrimSpace(up.SHA256); sha != "" {
		return strings.EqualFold(sha, strings.TrimSpace(m.SnapshotSHA256))
	}
	return false
}

// PersistAfterDownload remembers the state after a manual restore. The
// manifest is used only when it still describes the snapshot that was
// applied; another device may have uploaded in between, and its version
// recorded next to the restored history would make the next sync think the
// two are the same.
func (s *Syncer) PersistAfterDownload(ctx context.Context, dr DownloadResult) error {
	s.workflow.Lock()
	defer s.workflow.Unlock()
	if err := s.checkBound(dr.Account); err != nil {
		return err
	}
	if err := s.bindFor(dr.Account); err != nil {
		return err
	}
	if s.accountChanged(dr.Account) {
		// The restore belongs to another session: the current one must not
		// record its snapshot as its own.
		return ErrSessionChanged
	}
	m, err := s.ManifestInfo(ctx)
	if err != nil {
		m = ManifestInfo{}
	}
	if s.accountChanged(dr.Account) {
		return ErrSessionChanged
	}
	state := s.downloadState(ctx, dr, m)
	if s.accountChanged(dr.Account) {
		return ErrSessionChanged
	}
	if dr.Merged {
		// A restore that had to merge a local capture has items that were
		// never uploaded: remember the pending upload, not a synchronized
		// history, so the next run publishes it.
		s.persistPendingMerge(dr.PreMergeFingerprint, state, dr.Account)
	} else {
		s.PersistSyncState(dr.Fingerprint, state, dr.Account)
	}
	_ = s.bindAccount(dr.Account)
	return nil
}

// downloadState is the state to remember after a download. When the manifest
// no longer names the downloaded file, a manifest fetched for that file is
// used; if the server changed again, the hash is recorded as the version, so
// the next run downloads instead of treating the state as a first sync (which
// would merge the same items twice).
func (s *Syncer) downloadState(ctx context.Context, dr DownloadResult, basis ManifestInfo) ManifestInfo {
	state := snapshotState(dr, basis)
	if state.Version != "" || dr.SnapshotSHA256 == "" {
		return state
	}
	if m, err := s.ManifestInfo(ctx); err == nil && strings.EqualFold(strings.TrimSpace(m.SnapshotSHA256), dr.SnapshotSHA256) {
		return m
	}
	state.Version = dr.SnapshotSHA256
	return state
}

// snapshotState is the server state to remember after a download: the
// manifest, when it still describes the snapshot that was applied; otherwise
// only the hash of what was applied, so that the next sync looks at the newer
// snapshot again.
func snapshotState(dr DownloadResult, basis ManifestInfo) ManifestInfo {
	if sha := strings.TrimSpace(basis.SnapshotSHA256); sha != "" && dr.SnapshotSHA256 != "" &&
		strings.EqualFold(sha, dr.SnapshotSHA256) {
		return basis
	}
	// No server timestamp: a client clock would make the next run think the
	// remote snapshot was rolled back.
	return ManifestInfo{SnapshotSHA256: dr.SnapshotSHA256}
}
