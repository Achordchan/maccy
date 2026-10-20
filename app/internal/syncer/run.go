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
	report(progress, "check_remote", "检查远端", 10)
	manifest, err := s.remoteManifest(ctx)
	if err != nil {
		return RunResult{}, err
	}
	d := s.Decide(manifest)
	res := RunResult{Decision: d}

	switch d.Direction {
	case None:
		s.PersistSyncState(d.LocalFingerprint, d.Manifest)

	case Download:
		dr, err := s.DownloadAndApply(ctx, progress, reason)
		if err != nil {
			return res, err
		}
		s.PersistSyncState(dr.Fingerprint, snapshotState(dr, d.Manifest, s.cfg.Now().UTC()))

	case Merge:
		if err := s.MergeAndApply(ctx, d.Manifest, d.LocalFingerprint, progress, reason); err != nil {
			return res, err
		}
		up, fp, err := s.uploadWithFingerprint(ctx, progress, reason+"_merge_upload")
		if err != nil {
			return res, err
		}
		res.Uploaded = up
		s.persistAfterUpload(ctx, up, &d.Manifest, fp)

	default: // Upload
		up, fp, err := s.uploadWithFingerprint(ctx, progress, reason)
		if err != nil {
			return res, err
		}
		res.Uploaded = up
		s.persistAfterUpload(ctx, up, &d.Manifest, fp)
	}
	return res, nil
}

// UploadAfterLocalMutation uploads right after the user changed the history
// (UploadCloudAfterLocalMutationAsync), without looking at the server first.
func (s *Syncer) UploadAfterLocalMutation(ctx context.Context) (*syncclient.UploadResult, error) {
	up, fp, err := s.uploadWithFingerprint(ctx, nil, "local_mutation")
	if err != nil {
		return nil, err
	}
	s.persistAfterUpload(ctx, up, nil, fp)
	return up, nil
}

// persistAfterUpload is PersistSyncStateAfterUploadAsync: the state to
// remember is the snapshot that was uploaded, with the fingerprint of the
// items that went into it. The manifest is used only when it still describes
// that upload: another device may have uploaded in between, and remembering
// its version next to this history would lose both changes.
func (s *Syncer) persistAfterUpload(ctx context.Context, up *syncclient.UploadResult, fallback *ManifestInfo, fp string) {
	if m, err := s.ManifestInfo(ctx); err == nil && uploadIsInManifest(up, m) {
		s.PersistSyncState(fp, m)
		return
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
func (s *Syncer) PersistAfterDownload(ctx context.Context, dr DownloadResult) {
	m, err := s.ManifestInfo(ctx)
	if err != nil {
		m = ManifestInfo{}
	}
	s.PersistSyncState(dr.Fingerprint, snapshotState(dr, m, s.cfg.Now().UTC()))
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
