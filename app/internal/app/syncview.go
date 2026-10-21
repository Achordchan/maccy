package app

import (
	"context"
	"errors"
	"strings"
	"time"

	"klip/internal/syncer"
)

// SyncResult is how the last sync ended (MainWindowViewModel.SyncResultKind).
type SyncResult int

const (
	SyncNone SyncResult = iota
	SyncOK
	SyncFailed
)

// SyncView is what the main window shows of the sync: the cloud icon, the
// progress bar of a sync the user asked for, and the text of the icon's tip.
type SyncView struct {
	// Cloud says that the history is synchronized with an account.
	Cloud bool
	// Syncing is true while a sync runs, of either kind.
	Syncing bool
	// Progress is shown for a sync the user asked for; Text and Percent say
	// where it is.
	Progress bool
	Text     string
	Percent  int
	Result   SyncResult
	LastAt   time.Time
	// LastStatus is the words of the last sync or of the stage a background
	// one is at.
	LastStatus string
}

// Icon is the cloud that is drawn: idle, working, success or error.
func (v SyncView) Icon() string {
	working := v.Cloud && v.Syncing
	switch {
	case working:
		return "working"
	case v.Cloud && !v.Progress && v.Result == SyncOK:
		return "success"
	case v.Cloud && !v.Progress && v.Result == SyncFailed:
		return "error"
	}
	return "idle"
}

// ProgressText is the words beside the bar: the stage and the percentage.
func (v SyncView) ProgressText() string {
	if strings.TrimSpace(v.Text) == "" {
		return ""
	}
	return v.Text + " " + itoa(v.Percent) + "%"
}

// Tip is the tool tip of the cloud icon.
func (v SyncView) Tip() string {
	if v.Syncing {
		working := strings.TrimSpace(v.LastStatus)
		if working == "" {
			working = "同步中"
		}
		if p := v.ProgressText(); p != "" {
			return working + "\n" + p
		}
		return working
	}
	if !v.Cloud {
		return "未启用云同步"
	}
	if v.LastAt.IsZero() {
		return "已启用云同步\n等待首次同步"
	}
	status := strings.TrimSpace(v.LastStatus)
	if status == "" {
		status = "同步"
	}
	return status + "\n最近同步：" + v.LastAt.Format("2006-01-02 15:04:05")
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	neg := n < 0
	if neg {
		n = -n
	}
	var b [20]byte
	i := len(b)
	for n > 0 {
		i--
		b[i] = byte('0' + n%10)
		n /= 10
	}
	if neg {
		i--
		b[i] = '-'
	}
	return string(b[i:])
}

// Sync returns the state of the sync for the window.
func (a *App) Sync() SyncView {
	v := a.copyView()
	v.Cloud = a.Syncer.CanAutoSync()
	return v
}

func (a *App) copyView() SyncView {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.view
}

func (a *App) update(f func(v *SyncView)) {
	a.mu.Lock()
	f(&a.view)
	a.mu.Unlock()
	a.changed()
}

func (a *App) toast(msg string) {
	if a.cfg.Toast != nil && msg != "" {
		a.cfg.Toast(msg)
	}
}

// stageNames are the words for the steps of a sync.
var stageNames = map[string]string{
	"prepare_local":     "准备本地",
	"backup_snapshot":   "备份本地快照",
	"export_snapshot":   "导出本地快照",
	"upload_blobs":      "上传二进制对象",
	"read_snapshot":     "读取云端快照",
	"merge_snapshot":    "合并历史",
	"import_snapshot":   "导入本地快照",
	"upload_snapshot":   "上传快照",
	"download_snapshot": "下载快照",
	"persist_local":     "写入本地",
	"check_remote":      "检查远端",
}

func (a *App) syncBegin(reason string, manual bool) {
	a.mu.Lock()
	a.viewSeq++ // a pause of the one before ends nothing of this
	a.mu.Unlock()
	a.update(func(v *SyncView) {
		v.Syncing = true
		// Whatever the one before left on the bar (it stays up a moment after a
		// manual sync) is not this sync's.
		v.Progress, v.Text, v.Percent = false, "", 0
		if manual {
			v.Progress = true
			v.Text, v.Percent = "准备中", 5
		}
	})
	a.mu.Lock()
	a.status = "同步中…"
	a.mu.Unlock()
}

func (a *App) syncProgress(p syncer.Progress, reason string, manual bool) {
	stage := stageNames[p.Code]
	if stage == "" {
		stage = strings.TrimSpace(p.Message)
	}
	if stage == "" {
		stage = "同步中"
	}
	pct := max(0, min(p.Percent, 100))
	a.update(func(v *SyncView) {
		if manual {
			v.Text, v.Percent = stage, pct
		} else {
			v.LastStatus = "后台同步：" + stage
		}
	})
}

// finishView shows how a sync ended (syncDone has dealt with the accounting
// and what is owed). The pauses are those of the C# client: the bar stays at
// 100% for a moment, and longer when the sync failed. The pause is a timer, not
// a sleep: this runs before the runner is released, and the next sync (the one
// that a sign-in is owed) must not be turned away for it.
func (a *App) finishView(reason string, manual bool, err error) {
	a.mu.Lock()
	seq := a.viewSeq
	a.mu.Unlock()
	// closeBar ends the display of the sync, unless another has begun since.
	closeBar := func(after time.Duration) {
		end := func() {
			// The check and the change are one step: a sync that began in
			// between must not find its bar taken down.
			a.mu.Lock()
			same := a.viewSeq == seq
			if same {
				a.view.Syncing = false
				if a.view.Progress {
					a.view.Progress, a.view.Text, a.view.Percent = false, "", 0
				}
			}
			a.mu.Unlock()
			if same {
				a.changed()
			}
		}
		if after <= 0 {
			end()
			return
		}
		time.AfterFunc(after, end)
	}
	if manual && errors.Is(err, syncer.ErrNotLoggedIn) {
		a.mu.Lock()
		a.status = "请先登录"
		a.view.Syncing, a.view.Progress, a.view.Text, a.view.Percent = false, false, "", 0
		a.mu.Unlock()
		a.changed()
		a.toast("请先登录")
		return
	}
	if err == nil {
		if manual {
			a.update(func(v *SyncView) { v.Text, v.Percent = "完成", 100 })
		}
		a.update(func(v *SyncView) {
			v.Result, v.LastAt = SyncOK, time.Now()
			v.LastStatus = map[bool]string{true: "同步成功", false: "后台同步成功"}[manual]
		})
		a.mu.Lock()
		a.status = "已同步 " + time.Now().Format("15:04")
		a.mu.Unlock()
		if manual {
			closeBar(300 * time.Millisecond)
		} else {
			closeBar(0)
		}
		return
	}
	a.cfg.Logf("sync (%s): %v", reason, err)
	if manual {
		a.update(func(v *SyncView) { v.Text, v.Percent = "同步失败", 100 })
	}
	a.update(func(v *SyncView) {
		v.Result, v.LastAt = SyncFailed, time.Now()
		v.LastStatus = map[bool]string{true: "同步失败", false: "后台同步失败"}[manual]
	})
	explain := syncer.Explain(err, syncer.InMainWindow)
	a.mu.Lock()
	a.status = "同步失败"
	if manual {
		a.status = explain
	}
	a.mu.Unlock()
	switch {
	case manual:
		a.toast(explain)
	case !a.initialOK.Load():
		a.toast("首次同步失败：" + explain)
	}
	if manual {
		closeBar(450 * time.Millisecond)
	} else {
		closeBar(0)
	}
}

// remove deletes items from the history (pick says which, at the moment they
// are removed) and, when there is an account, uploads the result at once
// (UploadCloudAfterLocalMutationAsync). The removal and its upload are one
// piece of work that waits for the sync that may be running and keeps the
// others out, so that no sync can put the deleted items back or share its
// fingerprints with the upload; it belongs to the account that is signed in,
// and Stop waits for it.
//
// The deletion is written down (pendingDeletes) before the items go, and is
// forgotten only once the server has it: a failed or cancelled upload, a crash
// or a quit leaves it written down, and every later sync leaves those items
// out of what it brings in and makes the deletion again.
func (a *App) remove(pick func() []string) {
	if !a.Syncer.CanAutoSync() {
		a.mutateQuietly(func() { a.History.RemoveMany(pick()) })
		return
	}
	ctx, gen := a.acctAndGen()
	a.done.Add(1)
	go func() {
		defer a.done.Done()
		ran := a.Auto.Exclusive(ctx, func() {
			a.runGen.Store(gen) // the account this upload is for
			a.runActive.Store(true)
			defer a.runActive.Store(false)
			ids := pick()
			if err := a.pending.Add(gen, ids...); err != nil {
				// Still owed for as long as the program runs.
				a.cfg.Logf("pending deletions: %v", err)
			}
			a.mutateQuietly(func() { a.History.RemoveMany(ids) })
			if _, err := a.Syncer.UploadAfterLocalMutation(ctx); err != nil {
				if ctx.Err() == nil {
					a.cfg.Logf("upload after change: %v", err)
					a.retryLater()
				}
				return
			}
			if err := a.pending.Forget(gen, ids...); err != nil {
				a.cfg.Logf("pending deletions: %v", err)
			}
		})
		if !ran {
			// The account changed before its turn came: the change is the user's
			// all the same, and the cloud copy is not this account's to touch.
			a.mutateQuietly(func() { a.History.RemoveMany(pick()) })
			return
		}
		if ctx.Err() != nil {
			return
		}
		// What was asked for while the gate was shut (a sign-in's sync, a change
		// of the history) was turned away: ask again.
		if a.loginWanted.Load() {
			a.Auto.Trigger(a.acct(), "login", false)
		}
		a.Auto.OnHistoryChanged(a.acct())
	}()
}

// retryLater asks for a sync in a while, which also makes the deletions that
// are owed.
func (a *App) retryLater() {
	time.AfterFunc(30*time.Second, func() {
		if c := a.acct(); c.Err() == nil {
			a.Auto.Trigger(c, "auto", false)
		}
	})
}

// settleDeletions is for the end of a sync that went through, for the account
// of generation gen: what it brought in has left out the items the user
// deleted and the server never heard of, but one may still be here (the
// program stopped between writing the deletion down and making it). They are
// removed, and the result is uploaded; what other devices added stays.
func (a *App) settleDeletions(ctx context.Context, gen int64) error {
	ids := a.pending.IDs(gen)
	if len(ids) == 0 {
		return nil
	}
	a.mutateQuietly(func() { a.History.RemoveMany(ids) })
	if _, err := a.Syncer.UploadAfterLocalMutation(ctx); err != nil {
		if ctx.Err() == nil {
			a.retryLater()
		}
		return err
	}
	if err := a.pending.Forget(gen, ids...); err != nil {
		a.cfg.Logf("pending deletions: %v", err)
	}
	return nil
}

func (a *App) mutateQuietly(mutate func()) {
	a.Auto.Suppress(true)
	mutate()
	a.Auto.Suppress(false)
}

// Delete removes an item; with an account the cloud copy follows.
func (a *App) Delete(id string) { a.remove(func() []string { return []string{id} }) }

// ClearAll empties the history; with an account the cloud copy follows. What
// is cleared is what is there when the clearing is made, after a sync that
// was running has ended; an item copied after that stays.
func (a *App) ClearAll() {
	a.remove(func() []string {
		var ids []string
		for _, it := range a.History.Items() {
			ids = append(ids, it.ID)
		}
		return ids
	})
}

// Reorder moves an item before another one (or to the end).
func (a *App) Reorder(id string, before *string) { a.History.MoveBefore(id, before) }
