package app

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sort"
	"sync"
)

// pendingDeletes are the items the user deleted (or cleared) whose removal has
// not reached the server: the upload failed, or the program was closed before
// it was made. A sync merges what the server has with the local history, which
// would bring such an item back; so once it has merged, the items here are
// removed again and the result is uploaded. The list is kept in a file, to
// survive a restart, and belongs to the account that is signed in.
type pendingDeletes struct {
	path string
	mu   sync.Mutex
	ids  map[string]bool
	read bool
}

func (p *pendingDeletes) load() {
	if p.read {
		return
	}
	p.read = true
	p.ids = map[string]bool{}
	b, err := os.ReadFile(p.path)
	if err != nil {
		return
	}
	var list []string
	if json.Unmarshal(b, &list) == nil {
		for _, id := range list {
			p.ids[id] = true
		}
	}
}

func (p *pendingDeletes) save() {
	if p.path == "" {
		return
	}
	if len(p.ids) == 0 {
		_ = os.Remove(p.path)
		return
	}
	list := make([]string, 0, len(p.ids))
	for id := range p.ids {
		list = append(list, id)
	}
	sort.Strings(list)
	b, _ := json.Marshal(list)
	_ = os.MkdirAll(filepath.Dir(p.path), 0o700)
	_ = os.WriteFile(p.path, b, 0o600)
}

// Add notes items whose deletion is owed to the server.
func (p *pendingDeletes) Add(ids ...string) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.load()
	for _, id := range ids {
		p.ids[id] = true
	}
	p.save()
}

// IDs are the items that are owed.
func (p *pendingDeletes) IDs() []string {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.load()
	out := make([]string, 0, len(p.ids))
	for id := range p.ids {
		out = append(out, id)
	}
	return out
}

// Forget drops items whose deletion the server now has.
func (p *pendingDeletes) Forget(ids ...string) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.load()
	for _, id := range ids {
		delete(p.ids, id)
	}
	p.save()
}

// Reset forgets everything: the account the list was for is gone.
func (p *pendingDeletes) Reset() {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.ids, p.read = map[string]bool{}, true
	p.save()
}
