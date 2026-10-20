package panel

import (
	"fmt"

	"github.com/egoist/mygo/ui"

	"klip/internal/model"
)

// action is what the user asked of an item; rows only note it, and it runs
// once the rows are built (the list must not change while it is built).
type action struct {
	kind string // apply, pin, note, delete
	item model.Item
}

// View is the window's content.
func (m *Model) View(c *ui.Context) {
	m.refresh()
	t := c.Theme()
	c.Root().Background(t.Background)

	var act *action
	if m.editID == "" {
		if m.query != "" && c.Shortcut(0, ui.KeyEscape) {
			m.query = ""
		} else if c.Shortcut(0, ui.KeyEscape) {
			m.Host.Hide()
		}
		// Enter and the arrows belong to the search box while it has the
		// focus (see OnTextKey below); they act on the list when it has it.
		if c.Shortcut(0, ui.KeyDown) {
			m.choose(m.chosen + 1)
		}
		if c.Shortcut(0, ui.KeyUp) {
			m.choose(m.chosen - 1)
		}
		if c.Shortcut(0, ui.KeyEnter) {
			if it, ok := m.current(); ok {
				act = &action{"apply", it}
			}
		}
		if c.Shortcut(ui.Cmd, ui.KeyP) {
			if it, ok := m.current(); ok {
				act = &action{"pin", it}
			}
		}
		if c.Shortcut(ui.Cmd, ui.KeyE) {
			if it, ok := m.current(); ok {
				act = &action{"note", it}
			}
		}
		if c.Shortcut(ui.Cmd, ui.KeyD) {
			if it, ok := m.current(); ok {
				act = &action{"delete", it}
			}
		}
	}
	c.OnShortcut(ui.Cmd, ui.KeyQ, m.Host.Quit)
	c.OnShortcut(ui.Cmd, ui.KeyComma, m.Host.ShowPrefs)

	ui.Column(c).Fill().Children(func() {
		ui.Row(c).Padding(12, 14).Gap(8).AlignItems(ui.Center).Children(func() {
			ui.TextInputBase(c, &m.query).Bind(&m.input).Grow(1).FontSize(18).
				Placeholder("搜索剪贴板历史").Label("搜索").OnTextKey(m.searchKey)
		})
		if m.editID != "" {
			m.noteBar(c)
		}
		ui.Divider(c)

		// The choice is the model's own (m.chosen), drawn by the rows: the list
		// would move it with the item it remembers when the order changes.
		m.list.Key = func(i int) any { return m.rows[i].ID }
		m.list.Label = func(i int) string { return Preview(m.rows[i]) }
		ui.List(c, &m.list, len(m.rows), func(i int) {
			if a := m.row(c, i); a != nil {
				act = a
			}
		}).Grow(1).Padding(6).Children(func() {
			if len(m.rows) == 0 {
				msg := "复制点什么，它会出现在这里"
				if m.query != "" {
					msg = "没有匹配的记录"
				}
				ui.Text(c, msg).TextColor(t.TextMuted).Padding(16)
			}
		})

		ui.Divider(c)
		ui.Row(c).Padding(6, 14).Gap(8).AlignItems(ui.Center).Children(func() {
			status := fmt.Sprintf("%d 条", m.History.Len())
			if m.Status != nil {
				if s := m.Status(); s != "" {
					status += " · " + s
				}
			}
			ui.Text(c, status).FontSize(11).TextColor(t.TextMuted).SingleLine().Grow(1)
			if v := m.Host.UpdateAvailable(); v != "" {
				if ui.Button(c, "新版本 "+v).Label("有新版本").Clicked() {
					m.Host.ShowUpdate()
				}
			}
			if ui.Button(c, "同步").Label("立即同步").Clicked() {
				m.Host.SyncNow()
			}
			if ui.Button(c, "设置").Label("偏好设置").Clicked() {
				m.Host.ShowPrefs()
			}
		})
	})

	if act != nil {
		m.run(*act)
	}
}

// noteBar is the box that edits the note of m.editID.
func (m *Model) noteBar(c *ui.Context) {
	ui.Row(c).Padding(0, 14, 10).Gap(8).AlignItems(ui.Center).Children(func() {
		if m.focusNew {
			m.focusNew = false
			m.noteHand.Focus()
		}
		ui.TextInputBase(c, &m.noteBuf).Bind(&m.noteHand).Grow(1).Placeholder("备注（留空则清除）").Label("备注")
		save := ui.PrimaryButton(c, "保存").Clicked() || c.Shortcut(0, ui.KeyEnter)
		cancel := ui.Button(c, "取消").Clicked() || c.Shortcut(0, ui.KeyEscape)
		switch {
		case save:
			m.History.UpdateNote(m.editID, m.noteBuf)
			m.editID = ""
			m.input.Focus()
		case cancel:
			m.editID = ""
			m.input.Focus()
		}
	})
}

// run performs an action on an item.
func (m *Model) run(a action) {
	switch a.kind {
	case "apply":
		m.Host.Apply(a.item)
		m.Host.Hide()
	case "pin":
		m.History.TogglePinned(a.item.ID)
	case "delete":
		m.History.Remove(a.item.ID)
	case "note":
		m.editID, m.noteBuf, m.focusNew = a.item.ID, model.Str(a.item.Note), true
	}
}

// row builds one row and returns what the user did to it.
func (m *Model) row(c *ui.Context, i int) *action {
	it := m.rows[i]
	t := c.Theme()
	bg := ui.Transparent
	text, muted := t.Text, t.TextMuted
	if i == m.chosen {
		bg = t.Accent.Alpha(0.85)
		text, muted = t.AccentText, t.AccentText.Alpha(0.75)
	}
	var act *action
	btn := ui.ButtonBase(c).Padding(6, 10).Gap(10).AlignItems(ui.Center).Radius(8).Background(bg)
	btn = btn.Children(func() {
		if it.Kind == model.KindImage {
			if bm := m.thumbs.get(model.Str(it.ImageFilePath), m.Redraw); bm != nil {
				ui.Image(c, bm).Size(36, 36)
			}
		}
		ui.Column(c).Grow(1).Gap(1).Children(func() {
			ui.Text(c, Preview(it)).TextColor(text).SingleLine()
			if n := model.Str(it.Note); n != "" {
				ui.Text(c, "备注："+n).FontSize(12).TextColor(muted).SingleLine()
			}
			ui.Text(c, Detail(it, m.Now())).FontSize(11).TextColor(muted).SingleLine()
		})
		if it.Pinned {
			ui.Badge(c, "置顶")
		}
	})
	btn.ContextMenu(func(menu *ui.Menu) {
		if menu.Item("粘贴").Chosen() {
			act = &action{"apply", it}
		}
		if menu.Item("置顶").Checked(it.Pinned).Shortcut(ui.Cmd, ui.KeyP).Chosen() {
			act = &action{"pin", it}
		}
		if menu.Item("备注…").Shortcut(ui.Cmd, ui.KeyE).Chosen() {
			act = &action{"note", it}
		}
		menu.Separator()
		if menu.Item("删除").Shortcut(ui.Cmd, ui.KeyD).Chosen() {
			act = &action{"delete", it}
		}
	})
	if btn.Clicked() {
		m.chosen = i
		act = &action{"apply", it}
	}
	return act
}

// searchKey takes the keys that move through the list from the search box:
// the box would otherwise keep Enter and the arrows for itself. It runs on
// the UI thread, between frames.
func (m *Model) searchKey(k ui.TextKey) bool {
	if k.Mods != 0 {
		return false
	}
	switch k.Key {
	case ui.KeyDown:
		m.choose(m.chosen + 1)
	case ui.KeyUp:
		m.choose(m.chosen - 1)
	case ui.KeyEnter:
		if it, ok := m.current(); ok {
			m.run(action{"apply", it})
		}
	default:
		return false
	}
	if m.Redraw != nil {
		m.Redraw()
	}
	return true
}
