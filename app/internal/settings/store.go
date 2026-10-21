// Package settings reads and writes settings.json.
package settings

import (
	"errors"
	"io/fs"
	"os"
	"path/filepath"

	"klip/internal/model"
)

// Load reads the file. A missing or unreadable file gives the defaults, as
// in the C# client; the error says why, for a log. The legacy sync address
// is migrated, and written back when it changed.
func Load(path string) (model.Settings, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return model.DefaultSettings(), ignoreMissing(err)
	}
	s, err := model.ParseSettings(data)
	if err != nil {
		return model.DefaultSettings(), err
	}
	if s.MigrateLegacySyncBaseUrl() {
		_ = Save(path, s)
	}
	return s, nil
}

// Save writes the file through a temporary one, so that a crash does not
// leave half a file.
func Save(path string, s model.Settings) error {
	s.MigrateLegacySyncBaseUrl()
	data, err := s.Marshal()
	if err != nil {
		return err
	}
	return writeAtomic(path, data)
}

func ignoreMissing(err error) error {
	if errors.Is(err, fs.ErrNotExist) {
		return nil
	}
	return err
}

func writeAtomic(path string, data []byte) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return err
	}
	if err := os.Rename(tmp, path); err != nil {
		return err
	}
	// The file holds the tokens of the account: owner only, also for a file
	// that an earlier version made world-readable.
	_ = os.Chmod(path, 0o600)
	return nil
}
