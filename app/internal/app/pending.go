package app

import (
	"encoding/json"
	"errors"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"sync"
)

// pendingDeletes are the items the user deleted (or cleared) whose removal has
// not reached the server: the upload failed, or the program was closed before
// it was made. A sync merges what the server has with the local history, which
// would bring such an item back; so the items here are left out of what a sync
// brings in, and removed again once it has merged. The list is kept in a file,
// to survive a restart, and belongs to the account that is signed in.
//
// Each change names the account generation (App.acctGen) it is for, and is
// compared with the list's under the same lock: a change from the work of an
// account that is gone is dropped, even when that work ends after Reset.
type pendingDeletes struct {
	path string
	logf func(string, ...any)
	mu   sync.Mutex
	ids  map[string]bool
	read bool
	gen  int64
}

func (p *pendingDeletes) load() {
	if p.read {
		return
	}
	p.read = true
	p.ids = map[string]bool{}
	b, err := os.ReadFile(p.path)
	if err != nil {
		if !errors.Is(err, fs.ErrNotExist) {
			p.log("pending deletions: %v", err)
		}
		return
	}
	var list []string
	if err := json.Unmarshal(b, &list); err != nil {
		p.log("pending deletions: %v", err)
		return
	}
	for _, id := range list {
		p.ids[id] = true
	}
}

func (p *pendingDeletes) log(format string, args ...any) {
	if p.logf != nil {
		p.logf(format, args...)
	}
}

// save writes the list to a file beside it and puts that in its place, so
// that a crash or a full disk leaves the old list, not a broken one.
func (p *pendingDeletes) save() error {
	if p.path == "" {
		return nil
	}
	if len(p.ids) == 0 {
		if err := os.Remove(p.path); err != nil && !errors.Is(err, fs.ErrNotExist) {
			return err
		}
		return nil
	}
	list := make([]string, 0, len(p.ids))
	for id := range p.ids {
		list = append(list, id)
	}
	sort.Strings(list)
	b, err := json.Marshal(list)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(p.path), 0o700); err != nil {
		return err
	}
	tmp := p.path + ".tmp"
	if err := os.WriteFile(tmp, b, 0o600); err != nil {
		_ = os.Remove(tmp)
		return err
	}
	return os.Rename(tmp, p.path)
}

// Add notes items whose deletion is owed to the server by the account of
// generation gen. When the file cannot be written the items are still noted
// for as long as the program runs, and the error says so.
func (p *pendingDeletes) Add(gen int64, ids ...string) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	if gen != p.gen || len(ids) == 0 {
		return nil
	}
	p.load()
	for _, id := range ids {
		p.ids[id] = true
	}
	return p.save()
}

// IDs are the items that are owed by the account of generation gen.
func (p *pendingDeletes) IDs(gen int64) []string {
	p.mu.Lock()
	defer p.mu.Unlock()
	if gen != p.gen {
		return nil
	}
	p.load()
	out := make([]string, 0, len(p.ids))
	for id := range p.ids {
		out = append(out, id)
	}
	return out
}

// Forget drops items whose deletion the server now has.
func (p *pendingDeletes) Forget(gen int64, ids ...string) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	if gen != p.gen || len(ids) == 0 {
		return nil
	}
	p.load()
	for _, id := range ids {
		delete(p.ids, id)
	}
	return p.save()
}

// Reset forgets everything: the account the list was for is gone, and the
// list is now for the account of generation gen.
func (p *pendingDeletes) Reset(gen int64) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.gen = gen
	p.ids, p.read = map[string]bool{}, true
	return p.save()
}
