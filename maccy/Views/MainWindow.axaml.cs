using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using maccy.ViewModels;
using maccy.Models;

namespace maccy.Views;

public partial class MainWindow : Window
{
    private const string DragItemIdFormat = "maccy/clipboard-item-id";

    private TextBox? _searchBox;
    private ListBox? _historyList;
    private ListBox? _pinnedList;
    private Control? _authorEntry;

    private AuthorInfoWindow? _authorWindow;

    private ClipboardItem? _dragItem;
    private Point _dragStartPoint;
    private bool _dragging;

    private Control? _dragRow;
    private TextBlock? _dragGhostText;
    private Border? _dragGhostRoot;
    private ListBoxItem? _dragOverItem;

    private ImagePreviewWindow? _previewWindow;
    private Bitmap? _previewBitmap;

    private Guid? _previewItemId;

    private ClipboardItem? _previewItem;
    private string? _previewText;
    private bool _pointerOverList;
    private bool _pointerOverPreview;
    private bool _pointerOverWindow;
    private bool _pointerOverAnchor;
    private int _hideToken;
    private int _previewDelayToken;
    private int _anchorLeaveToken;

    private DateTime _lastPreviewInteractionUtc;
    private static readonly TimeSpan PreviewSuppressGrace = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan PreviewShowDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PreviewAnchorGrace = TimeSpan.FromMilliseconds(220);

    public bool SuppressAutoHide =>
        _previewWindow is not null &&
        _previewWindow.IsVisible &&
        (_pointerOverPreview || _pointerOverList || _pointerOverWindow || (DateTime.UtcNow - _lastPreviewInteractionUtc) < PreviewSuppressGrace)
        || (_authorWindow is not null && _authorWindow.IsVisible);

    public MainWindow()
    {
        InitializeComponent();

        _searchBox = this.FindControl<TextBox>("SearchBox");
        _historyList = this.FindControl<ListBox>("HistoryList");
        _pinnedList = this.FindControl<ListBox>("PinnedList");
        _authorEntry = this.FindControl<Control>("AuthorEntry");

        _dragGhostText = this.FindControl<TextBlock>("DragGhostText");
        _dragGhostRoot = this.FindControl<Border>("DragGhostRoot");

        if (_searchBox is not null)
            _searchBox.KeyDown += SearchBoxOnKeyDown;

        if (_historyList is not null)
        {
            _historyList.PointerReleased += HistoryListOnPointerReleased;
            _historyList.PointerEntered += HistoryListOnPointerEntered;
            _historyList.PointerExited += HistoryListOnPointerExited;

            DragDrop.SetAllowDrop(_historyList, true);
            _historyList.AddHandler(DragDrop.DragOverEvent, OnListDragOver);
            _historyList.AddHandler(DragDrop.DropEvent, OnListDrop);
        }

        if (_pinnedList is not null)
        {
            DragDrop.SetAllowDrop(_pinnedList, true);
            _pinnedList.AddHandler(DragDrop.DragOverEvent, OnListDragOver);
            _pinnedList.AddHandler(DragDrop.DropEvent, OnListDrop);
        }

        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel);

        PointerEntered += (_, _) =>
        {
            _pointerOverWindow = true;
            ++_hideToken;
            _lastPreviewInteractionUtc = DateTime.UtcNow;
        };

        PointerExited += (_, _) =>
        {
            _pointerOverWindow = false;
            RequestHidePreviewLater();
        };

        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible == false)
            {
                HidePreview();
                CloseAuthorWindow();
            }
        };
    }

    private T? GetAppResource<T>(string key) where T : class
    {
        if (Application.Current is null)
            return null;

        try
        {
            if (Application.Current.Resources.TryGetResource(key, Application.Current.ActualThemeVariant, out var value))
                return value as T;
        }
        catch
        {
        }

        return null;
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_authorWindow is null)
            return;

        if (_authorEntry is null)
        {
            CloseAuthorWindow();
            return;
        }

        var src = e.Source as Control;
        while (src is not null)
        {
            if (ReferenceEquals(src, _authorEntry))
                return;
            src = src.Parent as Control;
        }

        CloseAuthorWindow();
    }

    private void CloseAuthorWindow()
    {
        if (_authorWindow is null)
            return;

        try
        {
            _authorWindow.Close();
        }
        catch
        {
        }

        _authorWindow = null;
    }

    private void OnAuthorPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (_authorWindow is not null)
        {
            CloseAuthorWindow();
            e.Handled = true;
            return;
        }

        var w = new AuthorInfoWindow();
        _authorWindow = w;
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_authorWindow, w))
                _authorWindow = null;
        };

        PositionAuthorWindow(w);
        w.Show(this);

        e.Handled = true;
    }

    private void PositionAuthorWindow(Window w)
    {
        PixelPoint desired;

        if (_authorEntry is not null)
        {
            var pt = _authorEntry.PointToScreen(new Point(_authorEntry.Bounds.Width / 2, _authorEntry.Bounds.Height));
            desired = new PixelPoint((int)Math.Round((double)pt.X), (int)Math.Round((double)pt.Y) + 10);
        }
        else
        {
            desired = new PixelPoint(Position.X + 20, Position.Y + 60);
        }

        var screen = Screens.ScreenFromPoint(desired) ?? Screens.Primary;
        if (screen is not null)
        {
            var wa = screen.WorkingArea;
            var ww = (int)Math.Round(w.Width);
            var wh = (int)Math.Round(w.Height);
            var x = desired.X - ww / 2;
            var y = desired.Y;
            x = Math.Clamp(x, wa.X, Math.Max(wa.X, wa.Right - ww));
            y = Math.Clamp(y, wa.Y, Math.Max(wa.Y, wa.Bottom - wh));
            desired = new PixelPoint(x, y);
        }

        w.Position = desired;
    }

    private void OnCommonMenuPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (DataContext is not MainWindowViewModel vm)
            return;

        if (vm.EnterCommonClipsCommand.CanExecute(null))
            vm.EnterCommonClipsCommand.Execute(null);

        e.Handled = true;
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var src = e.Source as Control;
        while (src is not null)
        {
            if (src is TextBox)
                return;
            if (src is Button)
                return;
            if (src is ListBoxItem)
                return;
            src = src.Parent as Control;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    public void FocusSearch()
    {
        _searchBox?.Focus();
        if (_historyList is not null && _historyList.SelectedIndex < 0 && _historyList.ItemCount > 0)
            _historyList.SelectedIndex = 0;
    }

    public void PrepareForOpen()
    {
        if (_historyList is null)
            return;

        if (_historyList.ItemCount <= 0)
            return;

        _historyList.SelectedIndex = 0;
        if (_historyList.SelectedItem is not null)
            _historyList.ScrollIntoView(_historyList.SelectedItem);
    }

    private void EnsurePreviewWindow()
    {
        if (_previewWindow is not null)
            return;

        _previewWindow = new ImagePreviewWindow();
        _previewWindow.HoverChanged += isOver =>
        {
            _pointerOverPreview = isOver;
            _lastPreviewInteractionUtc = DateTime.UtcNow;
            if (!_pointerOverPreview)
            {
                // tooltip behavior: leaving preview closes it immediately unless we're still on the anchor row.
                if (!_pointerOverAnchor)
                    HidePreview();
                return;
            }

            // entering/moving within preview cancels pending hides
            ++_hideToken;
        };

        _previewWindow.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
        {
            _lastPreviewInteractionUtc = DateTime.UtcNow;
            _pointerOverPreview = true;
            ++_hideToken;
        }, RoutingStrategies.Tunnel);

        _previewWindow.PointerMoved += (_, _) =>
        {
            _pointerOverPreview = true;
            _lastPreviewInteractionUtc = DateTime.UtcNow;
            ++_hideToken;
        };

        _previewWindow.PointerWheelChanged += (_, _) =>
        {
            _pointerOverPreview = true;
            _lastPreviewInteractionUtc = DateTime.UtcNow;
            ++_hideToken;
        };
    }

    private void HidePreview()
    {
        if (_previewWindow is not null)
            _previewWindow.Hide();

        _previewItemId = null;
        _previewItem = null;
        _previewText = null;
        _pointerOverPreview = false;

        if (_previewBitmap is not null)
        {
            _previewBitmap.Dispose();
            _previewBitmap = null;
        }
    }

    private void RequestHidePreviewLater()
    {
        var token = ++_hideToken;
        DispatcherTimer.RunOnce(() =>
        {
            if (token != _hideToken)
                return;

            if (_pointerOverList || _pointerOverPreview || _pointerOverWindow)
                return;

            if ((DateTime.UtcNow - _lastPreviewInteractionUtc) < PreviewSuppressGrace)
                return;

            HidePreview();
        }, TimeSpan.FromMilliseconds(120));
    }

    private void PositionAndShowPreview(int desiredW, int desiredH, Control? anchor = null)
    {
        EnsurePreviewWindow();
        if (_previewWindow is null)
            return;

        var refPoint = anchor is null
            ? new PixelPoint(Position.X + 8, Position.Y + 8)
            : new PixelPoint(
                (int)Math.Round((double)anchor.PointToScreen(new Point(anchor.Bounds.Width / 2, anchor.Bounds.Height / 2)).X),
                (int)Math.Round((double)anchor.PointToScreen(new Point(anchor.Bounds.Width / 2, anchor.Bounds.Height / 2)).Y));

        var refScreen = Screens.ScreenFromPoint(refPoint) ?? Screens.Primary;
        if (refScreen is not null)
        {
            var wa = refScreen.WorkingArea;
            var maxW = Math.Max(200, wa.Width - 24);
            var maxH = Math.Max(200, wa.Height - 24);
            desiredW = Math.Min(desiredW, maxW);
            desiredH = Math.Min(desiredH, maxH);
        }

        _previewWindow.Width = desiredW;
        _previewWindow.Height = desiredH;

        PixelPoint ComputeClampedPosition(PixelPoint desired)
        {
            var screen = Screens.ScreenFromPoint(desired) ?? Screens.Primary;
            if (screen is null)
                return desired;

            var wa = screen.WorkingArea;
            var px = Math.Clamp(desired.X, wa.X, Math.Max(wa.X, wa.Right - desiredW));
            var py = Math.Clamp(desired.Y, wa.Y, Math.Max(wa.Y, wa.Bottom - desiredH));
            return new PixelPoint(px, py);
        }

        PixelPoint pos;

        if (anchor is not null)
        {
            var anchorTopLeft = anchor.PointToScreen(new Point(0, 0));
            var anchorBounds = anchor.Bounds;
            var anchorPx = new PixelPoint(
                (int)Math.Round((double)anchorTopLeft.X),
                (int)Math.Round((double)anchorTopLeft.Y));
            var anchorH = (int)Math.Round((double)anchorBounds.Height);
            var anchorW = (int)Math.Round((double)anchorBounds.Width);

            var anchorMidY = anchorPx.Y + anchorH / 2;

            var gap = 12;
            var preferRightX = anchorPx.X + anchorW + gap;
            var preferLeftX = anchorPx.X - gap - desiredW;

            var screen = Screens.ScreenFromPoint(anchorPx) ?? Screens.Primary;
            bool placeRight = true;
            if (screen is not null)
            {
                var wa = screen.WorkingArea;
                if (preferRightX + desiredW > wa.Right)
                    placeRight = false;
            }

            var targetX = placeRight ? preferRightX : preferLeftX;
            var targetY = anchorMidY - desiredH / 2;

            pos = ComputeClampedPosition(new PixelPoint(targetX, targetY));
        }
        else
        {
            var mainPos = Position;
            var mainW = (int)Math.Max(100, Width);
            var gap = 12;

            var desired = new PixelPoint(mainPos.X + mainW + gap, mainPos.Y + 24);
            pos = ComputeClampedPosition(desired);
        }

        _previewWindow.Position = pos;

        if (!_previewWindow.IsVisible)
            _previewWindow.Show();
    }

    private void ShowPreviewForImagePath(string imagePath, Control? anchor)
    {
        EnsurePreviewWindow();
        if (_previewWindow is null)
            return;

        if (!File.Exists(imagePath))
        {
            HidePreview();
            return;
        }

        if (_previewBitmap is not null)
        {
            _previewBitmap.Dispose();
            _previewBitmap = null;
        }

        using (var fs = File.OpenRead(imagePath))
        {
            _previewBitmap = new Bitmap(fs);
        }

        _previewWindow.SetSource(_previewBitmap);

        const int chrome = 20;
        const int infoHeight = 88;
        const int minWindow = 260;
        const int maxWindow = 980;

        var pw = _previewBitmap.PixelSize.Width;
        var ph = _previewBitmap.PixelSize.Height;

        var maxImage = Math.Max(1, maxWindow - chrome - infoHeight);
        var minShortSide = Math.Max(1, minWindow - chrome - infoHeight);

        // Allow small images to upscale to a minimum short-side size, but never exceed max bounds.
        var maxScale = Math.Min((double)maxImage / Math.Max(1, pw), (double)maxImage / Math.Max(1, ph));
        var minScale = (double)minShortSide / Math.Max(1, Math.Min(pw, ph));
        var scale = Math.Min(maxScale, Math.Max(1.0, minScale));

        var scaledW = (int)Math.Round(pw * scale);
        var scaledH = (int)Math.Round(ph * scale);

        var desiredW = Math.Clamp(scaledW + chrome, minWindow, maxWindow);
        var desiredH = Math.Clamp(scaledH + chrome + infoHeight, minWindow, maxWindow);

        PositionAndShowPreview(desiredW, desiredH, anchor);
    }

    private void ImageThumbOnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c)
            return;

        if (c.DataContext is not ClipboardItem item)
            return;

        if (item.Kind != ClipboardContentKind.Image)
            return;

        if (string.IsNullOrWhiteSpace(item.ImageFilePath))
            return;

        _previewItemId = item.Id;
        _previewItem = item;
        _pointerOverAnchor = true;
        ++_hideToken;

        _previewText = null;
        ShowPreviewWithDelay(item.Id, c, () =>
        {
            ShowPreviewForImagePath(item.ImageFilePath, c);
            if (_previewWindow is not null)
                _previewWindow.SetItem(item, _previewBitmap, null);
        });
        return;
    }

    private void ImageThumbOnPointerExited(object? sender, PointerEventArgs e)
    {
        CancelPendingPreviewShow();
        _pointerOverAnchor = false;

        var token = ++_anchorLeaveToken;
        DispatcherTimer.RunOnce(() =>
        {
            if (token != _anchorLeaveToken)
                return;
            if (!_pointerOverAnchor && !_pointerOverPreview)
                HidePreview();
        }, PreviewAnchorGrace);
    }

    private void HistoryListOnPointerEntered(object? sender, PointerEventArgs e)
    {
        _pointerOverList = true;
        ++_hideToken;
    }

    private void HistoryListOnPointerExited(object? sender, PointerEventArgs e)
    {
        _pointerOverList = false;
        CancelPendingPreviewShow();
        // Do not hide here: moving from the list row to the preview window will exit the list.
    }

    private void HistoryListOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_historyList is null)
            return;

        if (_dragging)
            return;

        if (e.InitialPressMouseButton != MouseButton.Left)
            return;

        var src = e.Source as Control;
        while (src is not null)
        {
            if (src.DataContext is ClipboardItem)
                break;
            src = src.Parent as Control;
        }

        if (src?.DataContext is not ClipboardItem)
            return;

        if (DataContext is MainWindowViewModel vm)
            vm.ApplySelectedCommand.Execute(null);

        e.Handled = true;
    }

    private void HistoryRowOnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c)
            return;

        if (c.DataContext is not ClipboardItem item)
            return;

        _previewItemId = item.Id;

        if (item.Kind == ClipboardContentKind.Image && !string.IsNullOrWhiteSpace(item.ImageFilePath))
        {
            _previewItem = item;
            _pointerOverAnchor = true;
            ++_hideToken;

            _previewText = null;
            ShowPreviewWithDelay(item.Id, c, () =>
            {
                ShowPreviewForImagePath(item.ImageFilePath, c);
                if (_previewWindow is not null)
                    _previewWindow.SetItem(item, _previewBitmap, null);
            });
            return;
        }

        string text;
        if (item.Kind == ClipboardContentKind.FileList)
        {
            text = item.FilePaths is null
                ? string.Empty
                : string.Join(Environment.NewLine, item.FilePaths.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        else
        {
            text = item.Text ?? string.Empty;
        }

        _previewItem = item;
        _pointerOverAnchor = true;
        ++_hideToken;

        ShowPreviewWithDelay(item.Id, c, () =>
        {
            _previewText = text;

            if (_previewBitmap is not null)
            {
                _previewBitmap.Dispose();
                _previewBitmap = null;
            }

            EnsurePreviewWindow();
            if (_previewWindow is null)
                return;

            _previewWindow.SetItem(item, null, text);

            const int minWindow = 260;
            const int maxWindow = 560;

            var lines = (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var maxLineLen = lines.Any() ? lines.Max(l => l.Length) : 0;
            var desiredW = Math.Clamp(80 + maxLineLen * 7, minWindow, maxWindow);
            var desiredH = Math.Clamp(140 + lines.Length * 18, minWindow, maxWindow);

            PositionAndShowPreview(desiredW, desiredH, c);
        });
    }

    private void HistoryRowOnPointerExited(object? sender, PointerEventArgs e)
    {
        CancelPendingPreviewShow();
        _pointerOverAnchor = false;

        // Grace window to allow moving from row -> preview without closing.
        var token = ++_anchorLeaveToken;
        DispatcherTimer.RunOnce(() =>
        {
            if (token != _anchorLeaveToken)
                return;
            if (!_pointerOverAnchor && !_pointerOverPreview)
                HidePreview();
        }, PreviewAnchorGrace);
    }

    private void OnTogglePinMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (sender is not MenuItem mi)
            return;

        var item = mi.DataContext as ClipboardItem;
        if (item is null)
        {
            var cm = mi.Parent as ContextMenu;
            var target = cm?.PlacementTarget as Control;
            item = target?.DataContext as ClipboardItem;
        }

        if (item is null)
            return;

        vm.TogglePinItemCommand.Execute(item);
    }

    private void OnEditNoteMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (sender is not MenuItem mi)
            return;

        var item = mi.DataContext as ClipboardItem;
        if (item is null)
        {
            var cm = mi.Parent as ContextMenu;
            var target = cm?.PlacementTarget as Control;
            item = target?.DataContext as ClipboardItem;
        }

        if (item is null)
            return;

        vm.EditNoteCommand.Execute(item);
    }

    private void OnDeleteMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (sender is not MenuItem mi)
            return;

        var item = mi.DataContext as ClipboardItem;
        if (item is null)
        {
            var cm = mi.Parent as ContextMenu;
            var target = cm?.PlacementTarget as Control;
            item = target?.DataContext as ClipboardItem;
        }

        if (item is null)
            return;

        vm.DeleteItemCommand.Execute(item);
    }

    public async void BeginEditNote(ClipboardItem item)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var appPanelBg = GetAppResource<IBrush>("AppPanelBackground") ?? Brushes.Transparent;
        var appControlBg = GetAppResource<IBrush>("AppControlBackground") ?? Brushes.Transparent;
        var appBorder = GetAppResource<IBrush>("AppBorder") ?? Brushes.Transparent;
        var appFg = GetAppResource<IBrush>("AppForeground") ?? Brushes.Black;
        var appMuted = GetAppResource<IBrush>("AppMutedForeground") ?? appFg;
        var appMuted2 = GetAppResource<IBrush>("AppMuted2Foreground") ?? appMuted;

        var tb = new TextBox
        {
            Text = item.Note ?? string.Empty,
            AcceptsReturn = true,
            MinHeight = 80,
            Background = Avalonia.Media.Brushes.Transparent,
            Foreground = appFg,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
            FontSize = 13,
        };

        var saveBtn = new Button
        {
            Content = "保存",
            MinWidth = 80,
            Padding = new Thickness(14, 8),
            CornerRadius = new CornerRadius(10),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3B82F6")),
            Foreground = Avalonia.Media.Brushes.White,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
        };

        var cancelBtn = new Button
        {
            Content = "取消",
            MinWidth = 80,
            Padding = new Thickness(14, 8),
            CornerRadius = new CornerRadius(10),
            Background = appControlBg,
            Foreground = appFg,
            BorderBrush = appBorder,
            BorderThickness = new Thickness(1),
        };

        var w = new Window
        {
            Title = "备注",
            Width = 360,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.Transparent,
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
        };

        var chrome = new Border
        {
            Background = appPanelBg,
            BorderBrush = appBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
        };

        root.Children.Add(new TextBlock
        {
            Text = "添加备注",
            Foreground = appFg,
            FontSize = 15,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });

        var hint = new TextBlock
        {
            Text = "请输入备注（可用于搜索）：",
            Foreground = appMuted2,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetRow(hint, 1);
        root.Children.Add(hint);

        var editorBorder = new Border
        {
            Background = appControlBg,
            BorderBrush = appBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 10, 0, 0),
            Child = tb,
        };
        Grid.SetRow(editorBorder, 2);
        root.Children.Add(editorBorder);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 12, 0, 0),
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(saveBtn);
        Grid.SetRow(btnRow, 3);
        root.Children.Add(btnRow);

        chrome.Child = root;
        w.Content = chrome;

        cancelBtn.Click += (_, _) => w.Close(false);
        saveBtn.Click += (_, _) => w.Close(true);

        var result = await w.ShowDialog<bool>(this);
        if (!result)
            return;

        vm.UpdateNote(item, tb.Text);

        if (_previewWindow is not null && _previewItemId == item.Id)
        {
            var updated = item with { Note = tb.Text };
            _previewWindow.SetItem(updated, _previewBitmap, _previewText);
        }
    }

    private void SearchBoxOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_historyList is null)
            return;

        if (e.Key == Key.Back && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (DataContext is MainWindowViewModel vm)
                vm.ClearSearchCommand.Execute(null);

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            var next = Math.Min(_historyList.SelectedIndex + 1, _historyList.ItemCount - 1);
            _historyList.SelectedIndex = Math.Max(0, next);
            if (_historyList.SelectedItem is not null)
                _historyList.ScrollIntoView(_historyList.SelectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            var prev = Math.Max(_historyList.SelectedIndex - 1, 0);
            _historyList.SelectedIndex = prev;
            if (_historyList.SelectedItem is not null)
                _historyList.ScrollIntoView(_historyList.SelectedItem);
            e.Handled = true;
            return;
        }
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (sender is not Control c)
            return;

        if (c.DataContext is not ClipboardItem item)
            return;

        _dragItem = item;
        _dragRow = c;
        _dragStartPoint = e.GetPosition(this);
        _dragging = false;
    }

    private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragItem = null;
            _dragging = false;
            return;
        }

        if (_dragging)
            return;

        var p = e.GetPosition(this);
        var dx = p.X - _dragStartPoint.X;
        var dy = p.Y - _dragStartPoint.Y;
        if ((dx * dx + dy * dy) < (6 * 6))
            return;

        _dragging = true;

        if (_dragRow is not null)
            _dragRow.Classes.Add("dragging");

        if (_dragGhostText is not null)
            _dragGhostText.Text = GetDragGhostText(_dragItem);

        SetGhostPosition(p);
        if (_dragGhostRoot is not null)
            _dragGhostRoot.IsVisible = true;
        try
        {
            var data = new DataObject();
            data.Set(DragItemIdFormat, _dragItem.Id.ToString());

            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        catch
        {
        }
        finally
        {
            EndDragVisuals();
            _dragItem = null;
            _dragging = false;
        }
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragRow is not null)
            _dragRow.Classes.Remove("dragging");

        _dragItem = null;
        _dragRow = null;
        _dragging = false;
    }

    private void OnListDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DragItemIdFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;

        SetGhostPosition(e.GetPosition(this));
        UpdateDragOverHighlight(e.Source);

        e.Handled = true;
    }

    private void OnListDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (!e.Data.Contains(DragItemIdFormat))
            return;

        var raw = e.Data.Get(DragItemIdFormat) as string;
        if (!Guid.TryParse(raw, out var movedId))
            return;

        ClipboardItem? target = null;
        if (e.Source is Control c)
        {
            var cur = c;
            while (cur is not null)
            {
                if (cur.DataContext is ClipboardItem it)
                {
                    target = it;
                    break;
                }
                cur = cur.Parent as Control;
            }
        }

        if (target is not null && target.Id == movedId)
            return;

        vm.ReorderItem(movedId, target?.Id);

        var droppedOn = _dragOverItem;
        EndDragVisuals();

        if (droppedOn is not null)
        {
            droppedOn.Classes.Add("drop-flash");
            DispatcherTimer.RunOnce(() => droppedOn.Classes.Remove("drop-flash"), TimeSpan.FromMilliseconds(180));
        }
        e.Handled = true;
    }

    private void EndDragVisuals()
    {
        if (_dragRow is not null)
            _dragRow.Classes.Remove("dragging");

        if (_dragGhostRoot is not null)
            _dragGhostRoot.IsVisible = false;

        if (_dragOverItem is not null)
            _dragOverItem.Classes.Remove("drag-over");

        _dragRow = null;
        _dragOverItem = null;
    }

    private void UpdateDragOverHighlight(object? dragEventSource)
    {
        var next = FindParentListBoxItem(dragEventSource as Control);
        if (ReferenceEquals(next, _dragOverItem))
            return;

        if (_dragOverItem is not null)
            _dragOverItem.Classes.Remove("drag-over");

        _dragOverItem = next;
        if (_dragOverItem is not null)
            _dragOverItem.Classes.Add("drag-over");
    }

    private static ListBoxItem? FindParentListBoxItem(Control? c)
    {
        var cur = c;
        while (cur is not null)
        {
            if (cur is ListBoxItem lbi)
                return lbi;
            cur = cur.Parent as Control;
        }

        return null;
    }

    private void SetGhostPosition(Point p)
    {
        if (_dragGhostRoot is null)
            return;

        var x = p.X + 12;
        var y = p.Y + 14;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w > 0 && h > 0)
        {
            var gw = _dragGhostRoot.Bounds.Width;
            var gh = _dragGhostRoot.Bounds.Height;
            if (gw > 0)
                x = Math.Clamp(x, 0, Math.Max(0, w - gw));
            if (gh > 0)
                y = Math.Clamp(y, 0, Math.Max(0, h - gh));
        }

        Canvas.SetLeft(_dragGhostRoot, x);
        Canvas.SetTop(_dragGhostRoot, y);
    }

    private static string GetDragGhostText(ClipboardItem item)
    {
        if (item.Kind == ClipboardContentKind.FileList)
        {
            var count = item.FilePaths?.Count ?? 0;
            return count > 0 ? $"{count} 个文件" : "文件";
        }

        if (item.Kind == ClipboardContentKind.Image)
            return "图片";

        var t = item.Text ?? string.Empty;
        t = t.Replace("\r\n", " ").Replace("\n", " ");
        if (t.Length > 42)
            t = t.Substring(0, 42) + "…";
        return string.IsNullOrWhiteSpace(t) ? "文本" : t;
    }

    private async void OnClearAllClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var appPanelBg = GetAppResource<IBrush>("AppPanelBackground") ?? Brushes.Transparent;
        var appControlBg = GetAppResource<IBrush>("AppControlBackground") ?? Brushes.Transparent;
        var appBorder = GetAppResource<IBrush>("AppBorder") ?? Brushes.Transparent;
        var appFg = GetAppResource<IBrush>("AppForeground") ?? Brushes.Black;
        var appMuted = GetAppResource<IBrush>("AppMutedForeground") ?? appFg;

        var cancelBtn = new Button
        {
            Content = "取消",
            MinWidth = 80,
            Padding = new Thickness(14, 8),
            CornerRadius = new CornerRadius(10),
            Background = appControlBg,
            Foreground = appFg,
            BorderBrush = appBorder,
            BorderThickness = new Thickness(1),
        };

        var clearBtn = new Button
        {
            Content = "清除",
            MinWidth = 80,
            Padding = new Thickness(14, 8),
            CornerRadius = new CornerRadius(10),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EF4444")),
            Foreground = Avalonia.Media.Brushes.White,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
        };

        var w = new Window
        {
            Title = "清除全部记录",
            Width = 360,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.Transparent,
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
        };

        var chrome = new Border
        {
            Background = appPanelBg,
            BorderBrush = appBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
        };

        root.Children.Add(new TextBlock
        {
            Text = "清除全部记录",
            Foreground = appFg,
            FontSize = 15,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });

        var msg = new Border
        {
            Background = appControlBg,
            BorderBrush = appBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 10, 0, 0),
            Child = new TextBlock
            {
                Text = "确定要清除本工具记录的全部剪贴记录吗？\n此操作不可撤销。",
                Foreground = appMuted,
                FontSize = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            },
        };
        Grid.SetRow(msg, 1);
        root.Children.Add(msg);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 12, 0, 0),
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(clearBtn);
        Grid.SetRow(btnRow, 2);
        root.Children.Add(btnRow);

        chrome.Child = root;
        w.Content = chrome;

        cancelBtn.Click += (_, _) => w.Close(false);
        clearBtn.Click += (_, _) => w.Close(true);

        var result = await w.ShowDialog<bool>(this);
        if (!result)
            return;

        HidePreview();
        vm.ClearAllHistory();
    }

    private void ShowPreviewWithDelay(Guid itemId, Control anchor, Action showAction)
    {
        var token = ++_previewDelayToken;
        _previewItemId = itemId;
        DispatcherTimer.RunOnce(() =>
        {
            if (token != _previewDelayToken)
                return;
            showAction();
        }, PreviewShowDelay);
    }

    private void CancelPendingPreviewShow()
    {
        ++_previewDelayToken;
    }
}