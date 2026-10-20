package syncer

import (
	"sort"
	"strings"

	"klip/internal/model"
)

const emptyGUID = "00000000-0000-0000-0000-000000000000"

// MergeItems is SyncService.MergeItems: the items of both sides, with the
// ones that are the same item (same content hash and kind, else same id)
// made one, newest first.
func MergeItems(remote, local []model.Item) []model.Item {
	merged := map[string]model.Item{}
	var order []string // keys in insertion order: the dictionary's
	var passthrough []model.Item

	// byID reconciles items that are the same item by id but get different
	// keys because only one side has a content hash (an older client wrote
	// it). The snapshot export keeps one row per id, so the merged history
	// must too.
	byID := map[string]string{}
	add := func(it model.Item, isLocal bool) {
		key := mergeKey(it)
		if strings.TrimSpace(key) == "" {
			passthrough = append(passthrough, it)
			return
		}
		key = strings.ToLower(key) // the C# dictionary ignores case
		id := strings.ToLower(strings.TrimSpace(it.ID))
		if id != "" && id != emptyGUID {
			if existingKey, ok := byID[id]; ok && existingKey != key && merged[existingKey].Kind == it.Kind {
				merged[existingKey] = mergeDuplicate(merged[existingKey], it, isLocal)
				return
			}
		}
		if existing, ok := merged[key]; ok {
			merged[key] = mergeDuplicate(existing, it, isLocal)
			return
		}
		merged[key] = it
		order = append(order, key)
		if id != "" && id != emptyGUID {
			byID[id] = key
		}
	}
	for _, it := range remote {
		add(it, false)
	}
	for _, it := range local {
		add(it, true)
	}

	out := make([]model.Item, 0, len(merged)+len(passthrough))
	for _, k := range order {
		out = append(out, merged[k])
	}
	out = append(out, passthrough...)
	// Newest first, then by id: the order is stable and the GUID order is that
	// of its text (a, b, c as fixed-width hex, then the bytes).
	sort.SliceStable(out, func(i, j int) bool {
		a, b := out[i], out[j]
		if !a.CapturedAt.Equal(b.CapturedAt.Time) {
			return a.CapturedAt.After(b.CapturedAt.Time)
		}
		return strings.ToLower(a.ID) < strings.ToLower(b.ID)
	})
	return out
}

func mergeKey(it model.Item) string {
	if h := strings.TrimSpace(model.Str(it.ContentHash)); h != "" {
		return "hash:" + itoa(int(it.Kind)) + ":" + h
	}
	if strings.ToLower(it.ID) == emptyGUID || it.ID == "" {
		return ""
	}
	return "id:" + strings.ToLower(it.ID)
}

func itoa(n int) string { return string(rune('0' + n)) } // kinds are 0..2

func mergeDuplicate(existing, incoming model.Item, incomingIsLocal bool) model.Item {
	latest := existing
	if !incoming.CapturedAt.Before(existing.CapturedAt.Time) {
		latest = incoming
	}
	firstOf := func(it model.Item) model.Time {
		if it.FirstCapturedAt != nil {
			return *it.FirstCapturedAt
		}
		return it.CapturedAt
	}
	first := firstOf(existing)
	if f := firstOf(incoming); f.Before(first.Time) {
		first = f
	}

	hash := latest.ContentHash
	if strings.TrimSpace(model.Str(hash)) == "" {
		if strings.TrimSpace(model.Str(existing.ContentHash)) == "" {
			hash = incoming.ContentHash
		} else {
			hash = existing.ContentHash
		}
	}

	out := latest
	out.ApproxBytes = max(existing.ApproxBytes, incoming.ApproxBytes)
	out.Pinned = existing.Pinned || incoming.Pinned
	out.CopyCount = max(0, existing.CopyCount) + max(0, incoming.CopyCount)
	out.FirstCapturedAt = &first
	out.Note = mergedNote(existing.Note, incoming.Note, incomingIsLocal)
	out.ContentHash = hash
	return out
}

// mergedNote keeps the local note when there is one, else the one that was
// there first.
func mergedNote(existing, incoming *string, incomingIsLocal bool) *string {
	blank := func(p *string) bool { return strings.TrimSpace(model.Str(p)) == "" }
	switch {
	case incomingIsLocal && !blank(incoming):
		return incoming
	case !blank(existing):
		return existing
	case blank(incoming):
		return nil
	}
	return incoming
}

// EnsureSettingsCanHoldItems raises the history limits of s to what the
// merged items need, so that the merge does not lose items to the limits.
func EnsureSettingsCanHoldItems(s *model.Settings, items []model.Item) {
	if int(s.MaxItems) < len(items) {
		s.MaxItems = int32(len(items))
	}
	var total int64
	for _, it := range items {
		if it.ApproxBytes <= 0 {
			continue
		}
		if int64(^uint64(0)>>1)-total < it.ApproxBytes {
			total = int64(^uint64(0) >> 1)
			break
		}
		total += it.ApproxBytes
	}
	needed := int64(1)
	if total > 0 {
		const mb = 1024 * 1024
		needed = max(1, (total+mb-1)/mb)
		needed = min(needed, int64(^uint32(0)>>1))
	}
	if int64(s.MaxMegabytes) < needed {
		s.MaxMegabytes = int32(needed)
	}
}
