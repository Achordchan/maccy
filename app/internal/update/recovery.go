package update

import (
	"encoding/json"
	"klip/internal/durable"
	"os"
	"path/filepath"
)

type recoveryEntry struct {
	Path, Backup   string
	Existed, Moved bool
}

func recoveryPath(root string) string { return filepath.Join(root, ".klip-update-recovery.json") }
func saveRecovery(root string, items []rollbackItem) error {
	entries := make([]recoveryEntry, len(items))
	for i, it := range items {
		entries[i] = recoveryEntry{it.path, it.backup, it.existed, it.moved}
	}
	b, err := json.Marshal(entries)
	if err != nil {
		return err
	}
	f, err := os.CreateTemp(root, ".recovery-*")
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
	return durable.Rename(f.Name(), recoveryPath(root))
}

// RecoverInterruptedUpdate restores a recorded transaction before normal startup.
func RecoverInterruptedUpdate(root string) error {
	b, err := os.ReadFile(recoveryPath(root))
	if os.IsNotExist(err) {
		return nil
	}
	if err != nil {
		return err
	}
	var entries []recoveryEntry
	if err = json.Unmarshal(b, &entries); err != nil {
		return err
	}
	for i := len(entries) - 1; i >= 0; i-- {
		e := entries[i]
		rel, err := filepath.Rel(root, e.Path)
		if err != nil {
			return err
		}
		if _, err = insideRoot(root, rel); err != nil {
			return err
		}
		if e.Moved {
			if _, err := os.Stat(e.Backup); os.IsNotExist(err) {
				continue
			}
			if err = os.RemoveAll(e.Path); err != nil {
				return err
			}
			if err = os.Rename(e.Backup, e.Path); err != nil {
				return err
			}
		} else if e.Existed {
			if err = copyFile(e.Backup, e.Path); err != nil {
				return err
			}
		} else {
			if err = os.Remove(e.Path); err != nil && !os.IsNotExist(err) {
				return err
			}
		}
	}
	return os.Remove(recoveryPath(root))
}
