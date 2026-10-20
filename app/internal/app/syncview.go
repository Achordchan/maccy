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
	a.update(func(v *SyncView) {
		v.Syncing = true
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
// 100% for a moment, and longer when the sync failed.
func (a *App) finishView(reason string, manual bool, err error) {
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
			time.Sleep(300 * time.Millisecond)
		}
		a.update(func(v *SyncView) {
			v.Result, v.LastAt = SyncOK, time.Now()
			v.LastStatus = map[bool]string{true: "同步成功", false: "后台同步成功"}[manual]
		})
		a.mu.Lock()
		a.status = "已同步 " + time.Now().Format("15:04")
		a.mu.Unlock()
	} else {
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
			time.Sleep(450 * time.Millisecond)
		}
	}
	a.update(func(v *SyncView) {
		v.Syncing = false
		if v.Progress {
			v.Progress, v.Text, v.Percent = false, "", 0
		}
	})
}

// afterMutation uploads at once what the user changed in the history
// (UploadCloudAfterLocalMutationAsync), when there is an account: a sync
// that looked at the server first could bring back what was just deleted.
func (a *App) afterMutation(mutate func()) {
	a.Auto.Suppress(true)
	mutate()
	a.Auto.Suppress(false)
	if !a.Syncer.CanAutoSync() {
		return
	}
	go func() {
		if _, err := a.Syncer.UploadAfterLocalMutation(context.Background()); err != nil {
			a.cfg.Logf("upload after change: %v", err)
		}
	}()
}

// Delete removes an item; with an account the cloud copy follows.
func (a *App) Delete(id string) { a.afterMutation(func() { a.History.Remove(id) }) }

// ClearAll empties the history; with an account the cloud copy follows.
func (a *App) ClearAll() { a.afterMutation(a.History.ClearAll) }

// Reorder moves an item before another one (or to the end).
func (a *App) Reorder(id string, before *string) { a.History.MoveBefore(id, before) }
