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
	"time"

	"klip/internal/model"
	"klip/internal/syncer"
)

// keepDeleted is how long a deletion the server has had is still enforced
// here.
const keepDeleted = 30 * 24 * time.Hour

// pendingDeletes are the items the user deleted (or cleared) on this device,
// kept in a file, one list for each account (by its address, as the
// checkpoints are): another account's work never touches them, and a
// sign-out or a sign-in to the same account keeps them.
//
// A sync merges what the server has with the local history, which would bring
// such an item back; so a merge or a download leaves them out (see
// syncer.Tombstone for which items that is), and when it has left one out the
// result is uploaded. A deletion that has not reached the server (the upload
// failed, or the program stopped first) stays until it has. One that has is
// still enforced for keepDeleted: a device that was offline with the item, and
// uploads its history merged with the server's, brings it back; this device
// takes it out again at its next sync.
//
// That is the limit of it: the marks are on this device only. The snapshot,
// which other devices and the C# client read and write as it is, has no place
// for them, so when this device does not sync again after another has
// brought the item back, the item stays.
type pendingDeletes struct {
	path  string
	logf  func(string, ...any)
	now   func() time.Time
	mu    sync.Mutex
	lists map[string][]deletion // by account (lower case)
	alias map[string]string     // an account known by another address since (Adopt)
	read  bool
}

// deletion is one item the user deleted.
type deletion struct {
	ID    string    `json:"id"`
	Key   string    `json:"key,omitempty"` // syncer.MergeKey
	At    time.Time `json:"at"`
	Acked bool      `json:"acked,omitempty"` // the server has had it
}

// pendingFile is what the file holds.
type pendingFile struct {
	Accounts map[string][]deletion `json:"accounts"`
}

func accountKey(owner string) string { return strings.ToLower(strings.TrimSpace(owner)) }

func (p *pendingDeletes) account(owner string) string {
	k := accountKey(owner)
	if to, ok := p.alias[k]; ok {
		return to
	}
	return k
}

func (p *pendingDeletes) clock() time.Time {
	if p.now != nil {
		return p.now()
	}
	return time.Now()
}

func (p *pendingDeletes) load() {
	if p.read {
		return
	}
	p.read = true
	p.lists = map[string][]deletion{}
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
	for k, l := range f.Accounts {
		p.lists[accountKey(k)] = append(p.lists[accountKey(k)], l...)
	}
}

func (p *pendingDeletes) log(format string, args ...any) {
	if p.logf != nil {
		p.logf(format, args...)
	}
}

// save writes the lists to a file beside it and puts that in its place, so
// that a crash or a full disk leaves the old lists, not a broken file.
func (p *pendingDeletes) save() error {
	if p.path == "" {
		return nil
	}
	for k, l := range p.lists {
		if len(l) == 0 {
			delete(p.lists, k)
		}
	}
	if len(p.lists) == 0 {
		if err := os.Remove(p.path); err != nil && !errors.Is(err, fs.ErrNotExist) {
			return err
		}
		return nil
	}
	for _, l := range p.lists {
		sort.SliceStable(l, func(i, j int) bool { return l[i].At.Before(l[j].At) })
	}
	b, err := json.Marshal(pendingFile{Accounts: p.lists})
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

// Add notes that the user of the account owner deleted items, now. When the
// file cannot be written the items are still noted for as long as the program
// runs, and the error says so.
func (p *pendingDeletes) Add(owner string, items ...model.Item) error {
	if len(items) == 0 {
		return nil
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	p.load()
	k, at := p.account(owner), p.clock()
	l := p.lists[k]
	for _, it := range items {
		l = append(l, deletion{ID: it.ID, Key: syncer.MergeKey(it), At: at})
	}
	p.lists[k] = l
	return p.save()
}

// Tombstones are the deletions of the account owner that are still enforced:
// those the server has not had, and those it has had for less than
// keepDeleted.
func (p *pendingDeletes) Tombstones(owner string) []syncer.Tombstone {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.load()
	cut := p.clock().Add(-keepDeleted)
	var out []syncer.Tombstone
	for _, d := range p.lists[p.account(owner)] {
		if d.Acked && d.At.Before(cut) {
			continue // kept long enough; Ack drops it
		}
		out = append(out, syncer.Tombstone{ID: d.ID, Key: d.Key, At: d.At})
	}
	return out
}

// Owed says whether the account owner has deletions the server has not had.
func (p *pendingDeletes) Owed(owner string) bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.load()
	for _, d := range p.lists[p.account(owner)] {
		if !d.Acked {
			return true
		}
	}
	return false
}

// Ack notes that the server of the account owner has had all its deletions
// (they are made, and uploaded, one piece of work at a time), and drops those
// that have been enforced long enough.
func (p *pendingDeletes) Ack(owner string) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.load()
	k := p.account(owner)
	l := p.lists[k]
	cut := p.clock().Add(-keepDeleted)
	kept := make([]deletion, 0, len(l))
	changed := false
	for _, d := range l {
		if !d.Acked {
			d.Acked, changed = true, true
		}
		if d.At.Before(cut) {
			changed = true
			continue
		}
		kept = append(kept, d)
	}
	if !changed {
		return nil
	}
	p.lists[k] = kept
	return p.save()
}

// Adopt is for a login that learned its address: what it noted under the
// address it had (from) is the account to's. Work of that login that still
// names from counts as to's until the login changes (ForgetAliases).
func (p *pendingDeletes) Adopt(from, to string) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.load()
	f, t := accountKey(from), accountKey(to)
	if f == t {
		return nil
	}
	if p.alias == nil {
		p.alias = map[string]string{}
	}
	p.alias[f] = t
	if len(p.lists[f]) == 0 {
		return nil
	}
	p.lists[t] = append(p.lists[t], p.lists[f]...)
	delete(p.lists, f)
	return p.save()
}

// ForgetAliases is for a change of login: the address a login had before it
// learned its own is nobody's in particular any more.
func (p *pendingDeletes) ForgetAliases() {
	p.mu.Lock()
	p.alias = nil
	p.mu.Unlock()
}
