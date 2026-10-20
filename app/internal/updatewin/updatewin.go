// Package updatewin is the update window: what UpdatePromptWindow,
// UpdateCheckWindow and UpdateProgressWindow of the C# client show, in one
// window that follows the state of the update.Coordinator.
package updatewin

import (
	"context"
	"fmt"
	"strings"

	"github.com/egoist/mygo/ui"

	"klip/internal/update"
)

// Host is what the window asks of the program around it.
type Host interface {
	// Close hides the window.
	Close()
	// Quit ends the program (a mandatory update that is declined).
	Quit()
}

// Model is the state of the window.
type Model struct {
	Coordinator *update.Coordinator
	Host        Host
	// Go runs f in the background (go f()); tests run it at once.
	Go func(f func())
}

// New returns a Model.
func New(c *update.Coordinator, h Host) *Model {
	return &Model{Coordinator: c, Host: h, Go: func(f func()) { go f() }}
}

// View is the window's content.
func (m *Model) View(c *ui.Context) {
	t := c.Theme()
	st := m.Coordinator.State()
	c.Root().Background(t.Background)

	ui.Column(c).Fill().Padding(20).Gap(12).Children(func() {
		switch st.Phase {
		case update.Checking:
			ui.Text(c, "检查更新").Bold().FontSize(16)
			ui.Progress(c, -1).Label("检查更新")
			ui.Text(c, st.Message).TextColor(t.TextMuted).Wrap()

		case update.UpToDateNow:
			ui.Text(c, "检查更新").Bold().FontSize(16)
			ui.Text(c, st.Message).Wrap()
			m.spacer(c)
			m.buttons(c, func() {
				if ui.PrimaryButton(c, "好").Clicked() {
					m.dismiss()
				}
			})

		case update.Available:
			m.available(c, st)

		case update.Downloading:
			ui.Text(c, "正在下载更新").Bold().FontSize(16)
			if st.Info != nil {
				ui.Text(c, "版本 "+st.Info.Version).TextColor(t.TextMuted)
			}
			value := -1.0
			label := "正在连接…"
			if st.Total > 0 {
				value = float64(st.Read) / float64(st.Total)
				label = fmt.Sprintf("%s / %s（%d%%）", size(st.Read), size(st.Total), int(value*100))
			} else if st.Read > 0 {
				label = size(st.Read)
			}
			ui.Progress(c, value).Label("下载进度")
			ui.Text(c, label).TextColor(t.TextMuted).FontSize(12)
			if st.Message != "" {
				ui.Text(c, st.Message).Wrap().TextColor(t.Danger)
			}

		case update.Applying:
			ui.Text(c, "正在更新").Bold().FontSize(16)
			ui.Progress(c, -1).Label("正在更新")
			ui.Text(c, st.Message).TextColor(t.TextMuted).Wrap()

		case update.Problem:
			ui.Text(c, "更新失败").Bold().FontSize(16)
			ui.Text(c, st.Message).Wrap()
			m.spacer(c)
			m.buttons(c, func() {
				if st.Info != nil && ui.Button(c, "重试").Clicked() {
					m.apply()
				}
				if ui.PrimaryButton(c, "关闭").Clicked() {
					m.dismiss()
				}
			})

		default:
			ui.Text(c, "已经是最新版本。").Wrap()
			m.spacer(c)
			m.buttons(c, func() {
				if ui.PrimaryButton(c, "关闭").Clicked() {
					m.Host.Close()
				}
			})
		}
	})
}

func (m *Model) available(c *ui.Context, st update.State) {
	t := c.Theme()
	info := st.Info
	ui.Text(c, "发现新版本").Bold().FontSize(16)
	ui.Text(c, "版本："+info.Version).TextColor(t.TextMuted)
	ui.Text(c, "更新内容：").TextColor(t.TextMuted).FontSize(12)
	ui.Scroll(c).Height(170).Border(1, t.Border).Radius(8).Children(func() {
		notes := strings.TrimSpace(info.Notes)
		if notes == "" {
			notes = "（没有更新说明）"
		}
		ui.Text(c, notes).Wrap().Padding(10).Selectable()
	})
	if info.Mandatory {
		ui.Text(c, "这是必须安装的更新。").TextColor(t.Danger).FontSize(12)
	}
	m.spacer(c)
	m.buttons(c, func() {
		if info.Mandatory {
			if ui.Button(c, "退出").Clicked() {
				m.Host.Quit()
			}
		} else if ui.Button(c, "稍后").Clicked() {
			m.dismiss()
		}
		if ui.PrimaryButton(c, "立即更新").Clicked() {
			m.apply()
		}
	})
}

func (m *Model) spacer(c *ui.Context) { ui.Row(c).Grow(1) }

func (m *Model) buttons(c *ui.Context, f func()) {
	ui.Divider(c)
	ui.Row(c).Gap(10).Justify(ui.End).Children(f)
}

// dismiss closes a message and hides the window.
func (m *Model) dismiss() {
	m.Coordinator.Dismiss()
	m.Host.Close()
}

func (m *Model) apply() {
	m.Go(func() { m.Coordinator.Apply(context.Background()) })
}

// CheckNow is the user asking for a check: the window should be shown by
// the caller; the check runs in the background.
func (m *Model) CheckNow() {
	m.Go(func() { m.Coordinator.Check(context.Background()) })
}

func size(n int64) string {
	switch {
	case n >= 1<<20:
		return fmt.Sprintf("%.1f MB", float64(n)/(1<<20))
	case n >= 1<<10:
		return fmt.Sprintf("%.0f KB", float64(n)/(1<<10))
	}
	return fmt.Sprintf("%d B", n)
}
