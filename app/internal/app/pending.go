package app

import (
	"encoding/json"
	"errors"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
)

// pendingDeletes are the items the user deleted (or cleared) whose removal has
// not reached the server: the upload failed, or the program was closed before
// it was made. A sync merges what the server has with the local history, which
// would bring such an item back; so the items here are left out of what a sync
// brings in, and removed again once it has merged. The list is kept in a file,
// to survive a restart.
//
// The list is owed by one account (its address, as the checkpoints are), and
// is read and forgotten only for that one: it is kept through a sign-out and a
// sign-in to the same account, and is replaced when another account writes
// down a deletion of its own. The work of an account that is gone (an older
// generation, App.acctGen) cannot replace it: a late failure of the old
// account's upload does not take the list from the account signed in now.
type pendingDeletes struct {
	path  string
	logf  func(string, ...any)
	mu    sync.Mutex
	owner string
	ids   map[string]bool
	read  bool
	gen   int64
}

// pendingFile is what the file holds.
type pendingFile struct {
	Account string   `json:"account"`
	IDs     []string `json:"ids"`
}

func sameOwner(a, b string) bool {
	return strings.EqualFold(strings.TrimSpace(a), strings.TrimSpace(b))
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
	var f pendingFile
	if err := json.Unmarshal(b, &f); err != nil {
		p.log("pending deletions: %v", err)
		return
	}
	p.owner = strings.TrimSpace(f.Account)
	for _, id := range f.IDs {
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
	b, err := json.Marshal(pendingFile{Account: p.owner, IDs: list})
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
	if err := os.Rename(tmp, p.path); err != nil {
		_ = os.Remove(tmp)
		return err
	}
	return nil
}

// Add notes items whose deletion is owed to the server by the account owner,
// for work of the account generation gen. A list of another account is
// replaced, unless gen is not the latest: then the work is that of an
// account that is gone, and the items are not noted. When the file cannot be
// written the items are still noted for as long as the program runs, and the
// error says so.
func (p *pendingDeletes) Add(gen int64, owner string, ids ...string) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	if len(ids) == 0 {
		return nil
	}
	p.load()
	if !sameOwner(owner, p.owner) {
		if gen != p.gen {
			return nil
		}
		p.owner, p.ids = strings.TrimSpace(owner), map[string]bool{}
	}
	for _, id := range ids {
		p.ids[id] = true
	}
	return p.save()
}

// IDs are the items that are owed by the account owner.
func (p *pendingDeletes) IDs(owner string) []string {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.load()
	if !sameOwner(owner, p.owner) {
		return nil
	}
	out := make([]string, 0, len(p.ids))
	for id := range p.ids {
		out = append(out, id)
	}
	return out
}

// Forget drops items whose deletion the server of the account owner now has.
func (p *pendingDeletes) Forget(owner string, ids ...string) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.load()
	if !sameOwner(owner, p.owner) || len(ids) == 0 {
		return nil
	}
	for _, id := range ids {
		delete(p.ids, id)
	}
	return p.save()
}

// NewGeneration is for a change of login: from now on only the work of
// generation gen may give the list to another account. The list itself stays.
func (p *pendingDeletes) NewGeneration(gen int64) {
	p.mu.Lock()
	p.gen = gen
	p.mu.Unlock()
}
