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
	// The file holds the access and the refresh token: tighten what an older
	// version may have left world-readable, even when nothing is saved now.
	_ = os.Chmod(filepath.Dir(path), 0o700)
	_ = os.Chmod(path, 0o600)
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
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	// The file holds the access and the refresh token: the folder and the
	// file are for this user only, also when an older version created them
	// world-readable.
	_ = os.Chmod(dir, 0o700)
	tmp := path + ".tmp"
	_ = os.Remove(tmp)
	f, err := os.OpenFile(tmp, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return err
	}
	if _, err := f.Write(data); err != nil {
		f.Close()
		os.Remove(tmp)
		return err
	}
	if err := f.Close(); err != nil {
		os.Remove(tmp)
		return err
	}
	if err := os.Rename(tmp, path); err != nil {
		os.Remove(tmp)
		return err
	}
	// The rename gives the file the mode of the temporary one, but make sure
	// again: the path may be on a file system that keeps the old mode.
	_ = os.Chmod(path, 0o600)
	return nil
}
