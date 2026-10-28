package history

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"klip/internal/model"
	"os"
	"path/filepath"
	"strings"
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
	for _, it := range s.items {
		if it.ID == old.ID {
			continue
		}
		if it.ImageFilePath != nil && withinDir(to, *it.ImageFilePath) {
			return false
		}
		for _, p := range it.FilePaths {
			if withinDir(to, p) {
				return false
			}
		}
	}
	if _, err := os.Stat(from); err != nil {
		return false
	}
	for i, source := range next.FilePaths {
		cached := filepath.Join(from, CaptureCacheName(i, filepath.Base(source)))
		if !isFile(cached) {
			return false
		}
	}
	backup := to + ".capture-backup"
	if _, err := os.Lstat(backup); err == nil {
		return false
	} else if !os.IsNotExist(err) {
		return false
	}
	if s.deferCleanup {
		b, _ := json.Marshal(next.LocalCaptureMutation)
		if err := os.WriteFile(backup+".capture-time", b, 0600); err != nil {
			return false
		}
	}
	var err error
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
	if hadOld && !s.deferCleanup {
		_ = os.RemoveAll(backup)
	}
	return true
}

// recoverCapturedFiles restores the retained ID before history normalization.
// Backup names encode only an existing directory component, never a path.
func recoverCapturedFiles(root string, histories ...[]model.Item) {
	recoverCaptureTransactions(root, false, histories...)
}
func retireCapturedFiles(root string, items []model.Item) {
	recoverCaptureTransactions(root, true, items)
}
func recoverCaptureTransactions(root string, retireOnly bool, histories ...[]model.Item) {
	entries, err := os.ReadDir(root)
	if err != nil {
		return
	}
	for _, entry := range entries {
		name := entry.Name()
		if !entry.IsDir() || !strings.HasSuffix(name, ".capture-backup") {
			continue
		}
		original := strings.TrimSuffix(name, ".capture-backup")
		if original == "" || original == "." || original == ".." {
			continue
		}
		backup, to := filepath.Join(root, name), filepath.Join(root, original)
		if data, err := os.ReadFile(backup + ".capture-time"); err == nil {
			var marker string
			if json.Unmarshal(data, &marker) == nil {
				committed := false
				present := false
				unknown := false
				for _, items := range histories {
					for _, it := range items {
						if it.IDN() == original {
							present = true
						}
						if it.IDN() == original && it.LocalCaptureMutation == "" {
							unknown = true
						}
						if it.IDN() == original && it.LocalCaptureMutation == marker && marker != "" {
							committed = true
						}
					}
				}
				if retireOnly && !present {
					if err := os.RemoveAll(backup); err == nil {
						_ = os.Remove(backup + ".capture-time")
					}
					continue
				}
				if unknown && !retireOnly {
					if _, err := os.Lstat(to); os.IsNotExist(err) {
						if err := os.Rename(backup, to); err == nil {
							_ = os.Remove(backup + ".capture-time")
						}
						continue
					}
				}
				if unknown && !retireOnly {
					preserved := backup + ".legacy-preserved-" + captureMutationID()
					if err := os.Rename(backup, preserved); err == nil {
						_ = os.Remove(backup + ".capture-time")
					}
					continue
				}
				if !committed && (retireOnly || unknown) {
					continue
				}
				if committed {
					if err := os.RemoveAll(backup); err != nil {
						continue
					}
				} else {
					if err := os.RemoveAll(to); err != nil {
						continue
					}
					if err := os.Rename(backup, to); err != nil {
						continue
					}
				}
				_ = os.Remove(backup + ".capture-time")
				continue
			}
		}
		if _, err := os.Lstat(to); os.IsNotExist(err) {
			_ = os.Rename(backup, to)
		} else if err == nil {
			_ = os.RemoveAll(backup)
		}
	}
}

func captureMutationID() string {
	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b)
}
