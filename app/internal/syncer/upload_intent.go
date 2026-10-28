package syncer

import (
	"encoding/json"
	"klip/internal/durable"
	"os"
	"path/filepath"
	"strings"
)

type uploadIntent struct{ Account, Fingerprint, SHA string }

func (s *Syncer) uploadIntentPath() string {
	return filepath.Join(s.cfg.Layout.Root, "sync", "upload-intent.json")
}
func (s *Syncer) writeUploadIntent(v uploadIntent) error {
	path := s.uploadIntentPath()
	if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		return err
	}
	b, err := json.Marshal(v)
	if err != nil {
		return err
	}
	f, err := os.CreateTemp(filepath.Dir(path), "upload-intent-*")
	if err != nil {
		return err
	}
	defer os.Remove(f.Name())
	if _, err = f.Write(b); err != nil {
		f.Close()
		return err
	}
	if err = f.Sync(); err != nil {
		f.Close()
		return err
	}
	if err = f.Close(); err != nil {
		return err
	}
	return durable.Rename(f.Name(), path)
}
func (s *Syncer) recoverUploadIntent(m ManifestInfo, account string) error {
	b, err := os.ReadFile(s.uploadIntentPath())
	if os.IsNotExist(err) {
		return nil
	}
	if err != nil {
		return err
	}
	var v uploadIntent
	if err = json.Unmarshal(b, &v); err != nil {
		return err
	}
	if !strings.EqualFold(v.Account, account) {
		return nil
	}
	if strings.EqualFold(v.SHA, m.SnapshotSHA256) && v.SHA != "" {
		if err = s.PersistSyncState(v.Fingerprint, m, account); err != nil {
			return err
		}
	}
	return os.Remove(s.uploadIntentPath())
}
