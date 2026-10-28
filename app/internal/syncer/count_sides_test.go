package syncer

import (
	"klip/internal/model"
	"testing"
)

func TestDownloadMergePreservesCountsWithinLocalSide(t *testing.T) {
	a := textItemAt("a", "same")
	a.CopyCount = 2
	a.ContentHash = model.Ptr("same")
	b := a
	b.ID = "b"
	b.CopyCount = 3
	got := MergeForDownload(nil, []model.Item{a, b}, nil)
	if len(got) != 1 || got[0].CopyCount != 5 {
		t.Fatalf("counts lost: %+v", got)
	}
}
