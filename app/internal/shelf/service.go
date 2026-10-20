package shelf

import (
	"errors"
	"sync"
	"time"
)

// Service is the shelf's trigger: it listens to the mouse everywhere and
// calls Spawn when the gesture is made.
type Service struct {
	// Settings is read at each mouse move (the app's settings).
	Settings func() Settings
	// Spawn shows a shelf. It is called on the hook's goroutine; it must
	// hand over to the UI thread itself.
	Spawn func()

	// Hook, Modifier and Now are StartHook, ModifierDown and time.Now unless
	// a test sets them.
	Hook     func(Events) (stop func(), err error)
	Modifier func(string) bool
	Now      func() time.Time

	mu   sync.Mutex
	stop func()
}

// Start installs the hook. It is an error to start twice.
func (s *Service) Start() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.stop != nil {
		return errors.New("shelf: already started")
	}
	hook, mod := s.Hook, s.Modifier
	if hook == nil {
		hook = StartHook
	}
	if mod == nil {
		mod = ModifierDown
	}
	g := &Gesture{Settings: s.Settings, ModifierDown: mod, Now: s.Now, Spawn: func(int, int) {
		if s.Spawn != nil {
			s.Spawn()
		}
	}}
	stop, err := hook(Events{LeftDown: g.LeftDown, LeftUp: g.LeftUp, Move: g.Move})
	if err != nil {
		return err
	}
	s.stop = stop
	return nil
}

// Stop removes the hook.
func (s *Service) Stop() {
	s.mu.Lock()
	stop := s.stop
	s.stop = nil
	s.mu.Unlock()
	if stop != nil {
		stop()
	}
}

// Running says whether the hook is installed.
func (s *Service) Running() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.stop != nil
}

// Follow starts or stops the hook as the settings ask: the hook sees every
// mouse move on the machine, so it is only there while the shelf is on.
// (1.0.10 keeps it always; this is cheaper and means the same.)
func (s *Service) Follow(st Settings) error {
	on := st.Enabled && st.Modifier != "Disabled"
	switch {
	case on && !s.Running():
		return s.Start()
	case !on && s.Running():
		s.Stop()
	}
	return nil
}
