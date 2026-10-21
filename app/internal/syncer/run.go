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
		if err := s.DownloadAndApply(ctx, progress, reason); err != nil {
			return res, err
		}
		s.PersistSyncState(s.Fingerprint(), d.Manifest)

	case Merge:
		if err := s.MergeAndApply(ctx, d.Manifest, d.LocalFingerprint, progress, reason); err != nil {
			return res, err
		}
		up, err := s.Upload(ctx, progress, reason+"_merge_upload")
		if err != nil {
			return res, err
		}
		res.Uploaded = up
		s.persistAfterUpload(ctx, up, &d.Manifest)

	default: // Upload
		up, err := s.Upload(ctx, progress, reason)
		if err != nil {
			return res, err
		}
		res.Uploaded = up
		s.persistAfterUpload(ctx, up, &d.Manifest)
	}
	return res, nil
}

// UploadAfterLocalMutation uploads right after the user changed the history
// (UploadCloudAfterLocalMutationAsync). The C# client sends the history as it
// is; here the server is looked at first, and when another device has
// uploaded since the last sync, its snapshot is merged in before: what it
// added would otherwise be dropped from the server. The merge leaves out what
// the user deleted (Config.Deleted).
func (s *Syncer) UploadAfterLocalMutation(ctx context.Context) (*syncclient.UploadResult, error) {
	manifest, err := s.remoteManifest(ctx)
	if err != nil {
		return nil, err
	}
	if remoteExists(manifest) && !s.remoteUnchanged(manifest) {
		if err := s.MergeAndApply(ctx, manifest, s.Fingerprint(), nil, "local_mutation"); err != nil {
			return nil, err
		}
	}
	up, err := s.Upload(ctx, nil, "local_mutation")
	if err != nil {
		return nil, err
	}
	s.persistAfterUpload(ctx, up, &manifest)
	return up, nil
}

// remoteExists says that the server has a snapshot: the manifest names one,
// by its time, its version or its hash. Decide goes by the time alone, as the
// C# client does; a snapshot whose time cannot be read is still one that an
// upload would replace.
func remoteExists(m ManifestInfo) bool {
	return m.UpdatedAt != nil || strings.TrimSpace(m.Version) != "" || strings.TrimSpace(m.SnapshotSHA256) != ""
}

// remoteUnchanged says that the server's snapshot is the one the last sync
// left there.
func (s *Syncer) remoteUnchanged(m ManifestInfo) bool {
	st := s.settings()
	last := strings.TrimSpace(model.Str(st.SyncLastRemoteVersion))
	return last != "" && sameRemoteSnapshot(last, strings.TrimSpace(m.Version), strings.TrimSpace(model.Str(st.SyncLastRemoteSha256)), strings.TrimSpace(m.SnapshotSHA256))
}

// persistAfterUpload is PersistSyncStateAfterUploadAsync: the state to
// remember is the server's manifest as it is now; when it cannot be fetched,
// what the upload answered, or the manifest the decision was made on.
func (s *Syncer) persistAfterUpload(ctx context.Context, up *syncclient.UploadResult, fallback *ManifestInfo) {
	// The history as it was uploaded, not as it is: an item copied since is
	// not on the server, and must still count as a change to send.
	fp, _ := s.uploadFP.Load().(string)
	if fp == "" {
		fp = s.Fingerprint()
	}
	if m, err := s.ManifestInfo(ctx); err == nil && (m.UpdatedAt != nil || m.Version != "") {
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

// PersistAfterDownload remembers the state after a manual restore.
func (s *Syncer) PersistAfterDownload(ctx context.Context) {
	if m, err := s.ManifestInfo(ctx); err == nil {
		s.PersistSyncState(s.Fingerprint(), m)
	}
}
