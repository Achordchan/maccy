package history

import (
	"klip/internal/model"
	"os"
	"path/filepath"
)

// replaceCapturedFiles preserves the retained item ID while replacing its
// cached bytes. Failure leaves the new capture separate instead of discarding
// it and presenting old bytes as the latest capture. Caller holds the lock.
func (s *Service) replaceCapturedFiles(old, next model.Item) bool {
	if old.ID == next.ID {
		return true
	}
	from := filepath.Join(s.layout.Files(), next.IDN())
	to := filepath.Join(s.layout.Files(), old.IDN())
	if _, err := os.Stat(from); err != nil {
		return false
	}
	backup, err := os.MkdirTemp(s.layout.Files(), ".capture-backup-")
	if err != nil {
		return false
	}
	if err = os.Remove(backup); err != nil {
		return false
	}
	hadOld := false
	if err = os.Rename(to, backup); err == nil {
		hadOld = true
	} else if !os.IsNotExist(err) {
		return false
	}
	if err = os.Rename(from, to); err != nil {
		if hadOld {
			_ = os.Rename(backup, to)
		}
		return false
	}
	if hadOld {
		_ = os.RemoveAll(backup)
	}
	return true
}
