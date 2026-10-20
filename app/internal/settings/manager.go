package settings

import (
	"sync"

	"klip/internal/model"
)

// Manager is AppSettingsService: the settings in memory, shared by every part
// of the program, written to settings.json after each update.
type Manager struct {
	path string

	mu       sync.Mutex
	cur      model.Settings
	handlers []func(model.Settings)
}

// Open reads the settings. The Manager is usable also when err says the file
// could not be read: it holds the defaults then, as the C# client does.
func Open(path string) (*Manager, error) {
	s, err := Load(path)
	return &Manager{path: path, cur: s}, err
}

// Get returns a copy of the settings.
func (m *Manager) Get() model.Settings {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.cur
}

// Update changes the settings and saves them. Functions registered with
// OnChange then run, outside the lock, with the new settings. A failure to
// save is returned, the change stays in memory.
func (m *Manager) Update(f func(*model.Settings)) error {
	m.mu.Lock()
	f(&m.cur)
	hs := make([]func(model.Settings), len(m.handlers))
	copy(hs, m.handlers)
	err := Save(m.path, m.cur)
	m.mu.Unlock()
	// Two updates on two goroutines can reach this point in either order, so
	// a handler is given the settings as they are now, not the ones this
	// update made: the last word is always the newest, whoever delivers it.
	for _, h := range hs {
		h(m.Get())
	}
	return err
}

// OnChange registers a function to run after each Update.
func (m *Manager) OnChange(f func(model.Settings)) {
	m.mu.Lock()
	m.handlers = append(m.handlers, f)
	m.mu.Unlock()
}
