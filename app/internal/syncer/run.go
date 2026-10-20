package syncer

import (
	"context"
	"strings"
	"time"

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
	report(progress, "check_remote", "检查远端", 10)
	manifest, err := s.remoteManifest(ctx)
	if err != nil {
		return RunResult{}, err
	}
	if s.accountChanged(account) {
		return RunResult{}, ErrSessionChanged
	}
	d := s.Decide(manifest)
	res := RunResult{Decision: d}

	switch d.Direction {
	case None:
		if s.accountChanged(account) {
			return res, ErrSessionChanged
		}
		s.PersistSyncState(d.LocalFingerprint, d.Manifest)

	case Download:
		dr, err := s.downloadAndApply(ctx, progress, reason, d.LocalFingerprint, account)
		if err != nil {
			return res, err
		}
		if dr.Merged {
			// The history changed while the snapshot was downloading: the
			// merge has unpublished items, so upload it as a merge does.
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
		s.PersistSyncState(dr.Fingerprint, snapshotState(dr, d.Manifest, s.cfg.Now().UTC()))

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
	return res, nil
}

// UploadAfterLocalMutation uploads right after the user changed the history
// (UploadCloudAfterLocalMutationAsync), without looking at the server first.
func (s *Syncer) UploadAfterLocalMutation(ctx context.Context) (*syncclient.UploadResult, error) {
	s.workflow.Lock()
	defer s.workflow.Unlock()
	account := s.currentAccount()
	up, fp, err := s.uploadWithFingerprint(ctx, nil, "local_mutation", account)
	if err != nil {
		return nil, err
	}
	if err := s.persistAfterUpload(ctx, up, nil, fp, account); err != nil {
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
		s.PersistSyncState(fp, m)
		return nil
	}
	now := s.cfg.Now().UTC()
	switch {
	case up != nil:
		m := ManifestInfo{Version: up.Version, UpdatedAt: &now, SnapshotSHA256: up.SHA256, SnapshotSize: up.Size}
		s.PersistSyncState(fp, m)
	case fallback != nil:
		s.PersistSyncState(fp, *fallback)
	default:
		s.PersistSyncState(fp, ManifestInfo{UpdatedAt: &now})
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
		return v == strings.TrimSpace(m.Version)
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
	if s.accountChanged(dr.Account) {
		// The restore belongs to another session: the current one must not
		// record its snapshot as its own.
		return ErrSessionChanged
	}
	m, err := s.ManifestInfo(ctx)
	if err != nil {
		m = ManifestInfo{}
	}
	s.PersistSyncState(dr.Fingerprint, snapshotState(dr, m, s.cfg.Now().UTC()))
	return nil
}

// snapshotState is the server state to remember after a download: the
// manifest, when it still describes the snapshot that was applied; otherwise
// only the hash of what was applied, so that the next sync looks at the newer
// snapshot again.
func snapshotState(dr DownloadResult, basis ManifestInfo, now time.Time) ManifestInfo {
	if sha := strings.TrimSpace(basis.SnapshotSHA256); sha != "" && dr.SnapshotSHA256 != "" &&
		strings.EqualFold(sha, dr.SnapshotSHA256) {
		return basis
	}
	return ManifestInfo{UpdatedAt: &now, SnapshotSHA256: dr.SnapshotSHA256}
}
