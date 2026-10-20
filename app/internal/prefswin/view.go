package prefswin

import (
	"fmt"
	"time"

	"github.com/egoist/mygo/ui"

	"klip/internal/prefs"
)

const labelWidth = 130

// View is the window's content.
func (m *Model) View(c *ui.Context) {
	t := c.Theme()
	c.Root().Background(t.Background)
	for _, msg := range m.takeToasts() {
		c.Toast(msg)
	}
	if !m.later.IsZero() {
		if left := m.later.Sub(c.Now()); left <= 0 {
			m.later = time.Time{}
			c.Toast("设置已生效")
		} else {
			c.After(left)
		}
	}
	c.OnShortcut(0, ui.KeyEscape, func() {
		if m.forgot || m.change {
			m.closeDialogs()
		}
	})

	ui.Column(c).Fill().Children(func() {
		ui.Tabs(c, &m.tab, "通用", "内容", "货架", "云同步").Padding(8, 12, 0)
		ui.Divider(c)
		ui.Scroll(c).Grow(1).Children(func() {
			ui.Column(c).Padding(16, 20).Gap(14).Children(func() {
				switch m.tab {
				case 0:
					m.general(c)
				case 1:
					m.content(c)
				case 2:
					m.shelf(c)
				default:
					m.cloud(c)
				}
			})
		})
		ui.Divider(c)
		ui.Row(c).Padding(8, 16).Justify(ui.End).Children(func() {
			if ui.Button(c, "关闭").Clicked() {
				m.Host.Close()
			}
		})
	})

	m.passwordDialog(c)
}

// effect turns what a setter did into toasts and box texts. Numbers typed
// toast after a pause, the rest at once.
func (m *Model) effect(c *ui.Context, e prefs.Effect, typed bool, box *string) {
	if e.SetText && box != nil {
		*box = e.Text
	}
	if e.Err != nil {
		c.Toast(e.Err.Error())
		return
	}
	if !e.Changed {
		return
	}
	if typed {
		m.later = c.Now().Add(700 * time.Millisecond)
		c.After(700 * time.Millisecond)
		return
	}
	c.Toast("设置已生效")
}

func (m *Model) check(c *ui.Context, label string, on *bool, set func(bool) prefs.Effect) {
	if ui.Checkbox(c, on, label).Changed() {
		e := set(*on)
		if e.Err != nil {
			*on = !*on // the system refused: show what is true
		}
		m.effect(c, e, false, nil)
	}
}

func row(c *ui.Context, label string, body func()) {
	ui.Row(c).Gap(12).AlignItems(ui.Center).Children(func() {
		ui.Text(c, label).Width(labelWidth).TextColor(c.Theme().TextMuted)
		body()
	})
}

func heading(c *ui.Context, s string) { ui.Text(c, s).Bold().FontSize(15) }

func optionNames(list []prefs.Option) []string {
	out := make([]string, len(list))
	for i, o := range list {
		out[i] = o.Display
	}
	return out
}

func byDisplay(list []prefs.Option, display string) prefs.Option {
	for _, o := range list {
		if o.Display == display {
			return o
		}
	}
	return list[0]
}

func (m *Model) general(c *ui.Context) {
	m.check(c, "开机启动", &m.v.StartWithWindows, m.Prefs.SetStartWithWindows)
	row(c, "主题", func() {
		sel := m.v.Theme.Display
		if ui.Select(c, &sel, optionNames(prefs.Themes)).Label("主题选择").Width(180).Changed() {
			m.v.Theme = byDisplay(prefs.Themes, sel)
			m.effect(c, m.Prefs.SetTheme(m.v.Theme.Value), false, nil)
		}
	})
	if check := m.Host.CheckUpdates(); check != nil {
		row(c, "检查更新", func() {
			if ui.Button(c, "检查").Clicked() {
				check()
			}
		})
	}
}

func (m *Model) content(c *ui.Context) {
	heading(c, "历史")
	m.check(c, "合并重复内容", &m.v.MergeDuplicates, m.Prefs.SetMergeDuplicates)
	m.check(c, "收藏项不计入上限", &m.v.ExcludePinnedFromLimits, m.Prefs.SetExcludePinnedFromLimits)
	row(c, "最大条目", func() {
		if ui.TextInput(c, &m.v.MaxItems).Label("最大条目数").Width(120).Changed() {
			m.effect(c, m.Prefs.SetMaxItems(m.v.MaxItems), true, nil)
		}
	})
	row(c, "最大容量（MB）", func() {
		if ui.TextInput(c, &m.v.MaxMegabytes).Label("最大容量 MB").Width(120).Changed() {
			m.effect(c, m.Prefs.SetMaxMegabytes(m.v.MaxMegabytes), true, &m.v.CaptureFileMaxMegabytes)
		}
	})
	row(c, "存储位置", func() {
		if ui.Button(c, "打开").Clicked() {
			m.Host.OpenStorage()
		}
	})
	ui.Divider(c)
	heading(c, "捕获")
	m.check(c, "捕获文本", &m.v.CaptureText, m.Prefs.SetCaptureText)
	m.check(c, "捕获图片", &m.v.CaptureImages, m.Prefs.SetCaptureImages)
	m.check(c, "捕获文件", &m.v.CaptureFiles, m.Prefs.SetCaptureFiles)
	row(c, "文件后缀", func() {
		if ui.TextInput(c, &m.v.CaptureFileExtensions).Label("文件后缀列表").Grow(1).Changed() {
			m.effect(c, m.Prefs.SetCaptureFileExtensions(m.v.CaptureFileExtensions), true, nil)
		}
	})
	row(c, "单文件上限（MB）", func() {
		if ui.TextInput(c, &m.v.CaptureFileMaxMegabytes).Label("单文件上限 MB").Width(120).Changed() {
			m.effect(c, m.Prefs.SetCaptureFileMax(m.v.CaptureFileMaxMegabytes), true, &m.v.CaptureFileMaxMegabytes)
		}
	})
}

func (m *Model) shelf(c *ui.Context) {
	m.check(c, "启用货架", &m.v.ShelfEnabled, m.Prefs.SetShelfEnabled)
	row(c, "触发键", func() {
		sel := m.v.ShelfModifier.Display
		if ui.Select(c, &sel, optionNames(prefs.ShelfModifiers)).Label("触发键选择").Width(180).Changed() {
			m.v.ShelfModifier = byDisplay(prefs.ShelfModifiers, sel)
			m.effect(c, m.Prefs.SetShelfModifier(m.v.ShelfModifier.Value), false, nil)
		}
	})
	ui.Text(c, "按住触发键，在任意位置按住鼠标左键并快速左右摇晃，即可唤出货架窗口。").
		TextColor(c.Theme().TextMuted).Wrap()
	ui.Text(c, "货架目前只在 Windows 上可用，其他系统会保存设置但不会生效。").
		TextColor(c.Theme().TextMuted).FontSize(12).Wrap()
}

func (m *Model) cloud(c *ui.Context) {
	t := c.Theme()
	heading(c, "账户")
	row(c, "登录状态", func() { ui.Text(c, m.statusText) })
	if !m.signedIn {
		row(c, "邮箱", func() {
			ui.TextInput(c, &m.email).Label("邮箱地址").Placeholder("name@example.com").Grow(1).MaxWidth(320)
		})
		row(c, "密码", func() {
			in := ui.TextInput(c, &m.password).Label("密码输入").Password().Grow(1).MaxWidth(320)
			if in.Submitted() {
				m.signIn(false)
			}
		})
		row(c, "", func() {
			if ui.PrimaryButton(c, "登录").Disabled(m.authBusy).Clicked() {
				m.signIn(false)
			}
			if ui.Button(c, "注册").Disabled(m.authBusy).Clicked() {
				m.signIn(true)
			}
			if ui.Button(c, "忘记密码").Clicked() {
				m.openForgot()
			}
		})
		ui.Text(c, "登录后即可启用云同步，剪贴板历史将自动同步到云端。").TextColor(t.TextMuted).FontSize(12).Wrap()
	} else {
		row(c, "", func() {
			if ui.Button(c, "退出登录").Clicked() {
				m.signOut()
			}
			if ui.Button(c, "修改密码").Clicked() {
				m.openChange()
			}
		})
	}

	ui.Divider(c)
	heading(c, "同步")
	row(c, "手动操作", func() {
		if ui.Button(c, "立即同步").Clicked() {
			m.Host.SyncNow()
		}
		if m.SyncStatus != nil {
			ui.Text(c, m.SyncStatus()).TextColor(t.TextMuted).FontSize(12)
		}
	})

	ui.Divider(c)
	heading(c, "订阅")
	row(c, "订阅状态", func() {
		ui.Text(c, m.sub.Status)
		if ui.Button(c, "刷新").Disabled(!m.signedIn).Clicked() {
			m.loadSubscription()
		}
	})
	row(c, "套餐", func() { ui.Text(c, m.sub.Tier) })
	row(c, "存储占用", func() { ui.Text(c, m.sub.Storage) })
	row(c, "保留天数", func() { ui.Text(c, m.sub.Retention) })
	row(c, "超限状态", func() {
		txt := ui.Text(c, m.sub.Limit)
		if m.sub.OverLimit {
			txt.TextColor(t.Danger)
		}
	})
	row(c, "兑换卡密", func() {
		in := ui.TextInput(c, &m.redeem).Label("卡密输入").Placeholder("XXXX-XXXX-XXXX-XXXX").Grow(1).MaxWidth(260)
		if ui.Button(c, "兑换").Disabled(m.redeemBusy).Clicked() || in.Submitted() {
			m.redeemCard()
		}
	})
}

func (m *Model) passwordDialog(c *ui.Context) {
	open := m.forgot || m.change
	if !open {
		return
	}
	if ui.Modal(c, &open, func() {
		t := c.Theme()
		title := "找回密码"
		if m.change {
			title = "修改密码"
		}
		ui.Column(c).Gap(10).Width(380).Children(func() {
			ui.Text(c, title).Bold().FontSize(16)
			ui.Text(c, "验证码会发送到你的邮箱，修改成功后需要重新登录。").TextColor(t.TextMuted).FontSize(12).Wrap()
			if m.forgot {
				ui.Text(c, "邮箱").TextColor(t.TextMuted).FontSize(12)
				ui.TextInput(c, &m.email).Label("找回邮箱").Placeholder("name@example.com")
			} else {
				ui.Text(c, "当前账号").TextColor(t.TextMuted).FontSize(12)
				ui.Text(c, m.Account.Email())
			}
			ui.Text(c, "验证码").TextColor(t.TextMuted).FontSize(12)
			ui.Row(c).Gap(8).Children(func() {
				ui.TextInput(c, &m.code).Label("验证码输入").Grow(1)
				label := "发送验证码"
				if m.cooldown > 0 {
					label = fmt.Sprintf("%ds 后重发", m.cooldown)
				}
				if ui.Button(c, label).Disabled(m.codeBusy || m.cooldown > 0).Clicked() {
					m.sendCode()
				}
			})
			ui.Text(c, "新密码").TextColor(t.TextMuted).FontSize(12)
			ui.TextInput(c, &m.pw1).Label("新密码输入").Password()
			ui.Text(c, "确认新密码").TextColor(t.TextMuted).FontSize(12)
			ui.TextInput(c, &m.pw2).Label("确认新密码输入").Password()
			ui.Row(c).Gap(8).Justify(ui.End).Children(func() {
				if ui.Button(c, "取消").Clicked() {
					m.closeDialogs()
				}
				ok := "确认修改"
				if m.forgot {
					ok = "重置密码"
				}
				if ui.PrimaryButton(c, ok).Disabled(m.codeBusy).Clicked() {
					m.submitPassword()
				}
			})
		})
	}).Dismissed() {
		m.closeDialogs()
	}
}
