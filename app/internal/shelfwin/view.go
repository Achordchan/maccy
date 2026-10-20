package shelfwin

import (
	"github.com/egoist/mygo/transfer"
	"github.com/egoist/mygo/ui"
)

var dropOptions = transfer.DropOptions{
	Formats:    []transfer.Format{transfer.FileList, transfer.URIList},
	Operations: transfer.Copy,
}

// View is the window's content.
func (m *Model) View(c *ui.Context) {
	t := c.Theme()
	c.Root().Background(ui.Transparent)

	root := ui.Column(c).Fill().Padding(14).Gap(8).Radius(18).Background(t.Background).Border(1, t.Border).DragWindow()
	_, over := ui.DataDragOver(root, dropOptions)
	m.dragOver = over && !m.draggingOut
	if m.dragOver {
		root.Border(2, t.Accent)
	}
	if drop, ok := ui.DropData(root, dropOptions); ok && !m.draggingOut {
		if files, err := drop.Data.Files(); err == nil {
			m.AddFiles(files)
		}
	}

	root.Children(func() {
		if m.dragOver {
			ui.Column(c).Grow(1).AlignItems(ui.Center).Justify(ui.Center).Children(func() {
				ui.Text(c, "松开以放入").Bold().FontSize(16).TextColor(t.Accent)
			})
			return
		}
		if m.Expanded {
			m.expanded(c)
		} else {
			m.compact(c)
		}
	})
}

// closeButton is the × in the corner.
func (m *Model) closeButton(c *ui.Context) {
	if ui.Button(c, "×").Label("关闭").Padding(0, 8).Clicked() {
		m.Close()
	}
}

func (m *Model) compact(c *ui.Context) {
	t := c.Theme()
	ui.Row(c).Justify(ui.End).Children(func() { m.closeButton(c) })

	handle := ui.Column(c).Grow(1).AlignItems(ui.Center).Justify(ui.Center).Gap(8).Radius(14).Background(t.Surface).Padding(12)
	if m.Pinned && len(m.Items) > 0 {
		// The whole shelf can be dragged out from here.
		handle.DragDataFrom(func() transfer.Data { return m.dragData(m.AllPaths()) }, m.dragOptions())
	}
	handle.Children(func() {
		switch len(m.Items) {
		case 0:
			ui.Text(c, m.Title()).TextColor(t.TextMuted).Wrap().TextAlign(ui.Center)
		case 1:
			m.icon(c, m.Items[0], 56)
			ui.Text(c, m.Items[0].Name).SingleLine().Ellipsis("…").MaxWidth(190)
		default:
			// A stack: the newest file and how many there are.
			last := m.Items[len(m.Items)-1]
			m.icon(c, last, 56)
			ui.Text(c, m.TotalSize()).Bold()
			if ui.Button(c, m.CountBadge()).Label("展开").Clicked() {
				m.FoldStack()
			}
		}
	})
}

func (m *Model) expanded(c *ui.Context) {
	t := c.Theme()
	ui.Row(c).Gap(10).AlignItems(ui.Center).Children(func() {
		if len(m.Items) <= 1 {
			if ui.Button(c, "收起").Label("收起").Clicked() {
				m.ToggleExpanded()
			}
		} else if ui.Button(c, "收起").Label("折叠").Clicked() {
			m.FoldStack()
		}
		ui.Column(c).Grow(1).AlignItems(ui.Center).Children(func() {
			ui.Text(c, m.TotalSize()).Bold().FontSize(12)
			ui.Text(c, m.Title()).TextColor(t.TextMuted).FontSize(11).SingleLine().Ellipsis("…").MaxWidth(240)
		})
		if len(m.Items) <= 1 {
			label := "列表"
			if !m.Grid {
				label = "网格"
			}
			if ui.Button(c, label).Label("切换视图").Clicked() {
				m.ToggleView()
			}
		}
		m.closeButton(c)
	})

	ui.Scroll(c).Grow(1).Children(func() {
		if m.Grid && len(m.Items) <= 1 {
			ui.Row(c).Wrap().Gap(12).Children(func() {
				for i := range m.Items {
					m.item(c, i, 210, 96)
				}
			})
			return
		}
		ui.Column(c).Gap(8).Children(func() {
			for i := range m.Items {
				m.item(c, i, 0, 0)
			}
		})
	})
}

// item is one file of the expanded shelf: a press chooses it (Ctrl adds), a
// drag takes it, or all the chosen ones, out.
func (m *Model) item(c *ui.Context, i, w, h int) {
	t := c.Theme()
	it := m.Items[i]
	bg := t.Surface
	if it.Selected {
		bg = t.Accent.Alpha(0.25)
	}
	btn := ui.ButtonBase(c).Padding(10).Gap(10).AlignItems(ui.Center).Radius(12).Background(bg).Label(it.Name)
	if w > 0 {
		btn.Width(float32(w)).Height(float32(h))
	}
	btn.DragDataFrom(func() transfer.Data {
		if !m.Items[i].Selected {
			m.Select(i, false)
		}
		return m.dragData(m.PathsOf(i))
	}, m.dragOptions())
	btn.Children(func() {
		m.icon(c, it, 40)
		ui.Column(c).Grow(1).Gap(2).Children(func() {
			ui.Text(c, it.Name).SingleLine().Ellipsis("…")
			if meta := it.Meta(); meta != "" {
				ui.Text(c, meta).FontSize(11).TextColor(t.TextMuted).SingleLine().Ellipsis("…")
			}
		})
	})
	if btn.Clicked() {
		m.Select(i, btn.ClickModifiers()&ui.Ctrl != 0)
	}
}

// icon is the icon of the system when there is one, else a badge with the
// extension in the color of its type.
func (m *Model) icon(c *ui.Context, it Item, size float32) {
	if it.Icon != nil {
		ui.Image(c, it.Icon).Size(size, size)
		return
	}
	r, g, b := it.TypeColor()
	ui.Column(c).Size(size, size).Radius(10).Background(ui.RGB(r, g, b)).AlignItems(ui.Center).Justify(ui.Center).Children(func() {
		ui.Text(c, it.Badge()).Bold().FontSize(size / 4).TextColor(ui.RGB(255, 255, 255))
	})
}

// dragData makes the files a drag takes along, and notes that the drag is
// ours, so that the shelf does not take them in again.
func (m *Model) dragData(paths []string) transfer.Data {
	m.draggingOut = true
	d, err := transfer.FileData(paths...)
	if err != nil {
		m.draggingOut = false
		return transfer.Data{}
	}
	return d
}

func (m *Model) dragOptions() transfer.DragOptions {
	return transfer.DragOptions{Operations: transfer.Copy, Done: func(transfer.Result) { m.draggingOut = false }}
}
