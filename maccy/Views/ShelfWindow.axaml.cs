using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Markup.Xaml;
using maccy.Services;
using maccy.ViewModels;

namespace maccy.Views;

public partial class ShelfWindow : Window
{
    private const string ShelfInternalDragFormat = "maccy/shelf-internal-drag";

    private const double CompactSize = 220;
    private const double ExpandedSize = 420;

    private readonly Action<ShelfWindow>? _onPinned;

    private Control? _stackHandle;
    private Control? _compactIcon;
    private PointerPressedEventArgs? _stackMoveStartArgs;
    private Point _stackDragStart;
    private bool _stackMoving;

    private Point _iconDragStart;
    private bool _iconDragging;

    private ItemsControl? _expandedItems;
    private ItemsControl? _expandedGrid;
    private ShelfFileItemViewModel? _itemDrag;
    private Point _itemDragStart;
    private bool _itemDragging;

    private Border? _dragOverlay;
    private Border? _dragOutOverlay;
    private Border? _dragOutPill;

    private ShelfWindowViewModel? _vm;

    public ShelfWindow()
        : this(null)
    {
    }

    public ShelfWindow(Action<ShelfWindow>? onPinned)
    {
        _onPinned = onPinned;
        InitializeComponent();

        DataContextChanged += (_, _) => AttachViewModel();
        AttachViewModel();

        _dragOverlay = this.FindControl<Border>("DragOverlay");
        _dragOutOverlay = this.FindControl<Border>("DragOutOverlay");
        _dragOutPill = this.FindControl<Border>("DragOutPill");

        _stackHandle = this.FindControl<Control>("StackHandle");
        if (_stackHandle is not null)
        {
            _stackHandle.PointerPressed += OnStackPointerPressed;
            _stackHandle.PointerMoved += OnStackPointerMoved;
            _stackHandle.PointerReleased += OnStackPointerReleased;
        }

        _compactIcon = this.FindControl<Control>("CompactIcon");
        if (_compactIcon is not null)
        {
            _compactIcon.PointerPressed += OnCompactIconPointerPressed;
            _compactIcon.PointerMoved += OnCompactIconPointerMoved;
            _compactIcon.PointerReleased += OnCompactIconPointerReleased;
        }

        _expandedItems = this.FindControl<ItemsControl>("ExpandedItems");
        if (_expandedItems is not null)
        {
            _expandedItems.AddHandler(PointerPressedEvent, OnExpandedItemPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            _expandedItems.AddHandler(PointerMovedEvent, OnExpandedItemPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            _expandedItems.AddHandler(PointerReleasedEvent, OnExpandedItemPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        _expandedGrid = this.FindControl<ItemsControl>("ExpandedGrid");
        if (_expandedGrid is not null)
        {
            _expandedGrid.AddHandler(PointerPressedEvent, OnExpandedItemPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            _expandedGrid.AddHandler(PointerMovedEvent, OnExpandedItemPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            _expandedGrid.AddHandler(PointerReleasedEvent, OnExpandedItemPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (_iconDragging || _itemDragging)
            return;

        if (e.Source is not Control control)
            return;

        var current = control;
        while (current is not null)
        {
            if (ReferenceEquals(current, _stackHandle)
                || ReferenceEquals(current, _expandedItems)
                || ReferenceEquals(current, _expandedGrid))
                return;

            if (current is Button or ScrollViewer)
                return;

            current = current.Parent as Control;
        }

        try
        {
            BeginMoveDrag(e);
        }
        catch
        {
        }
    }

    private void AttachViewModel()
    {
        if (_vm is INotifyPropertyChanged oldInpc)
            oldInpc.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = DataContext as ShelfWindowViewModel;
        if (_vm is INotifyPropertyChanged inpc)
            inpc.PropertyChanged += OnViewModelPropertyChanged;

        if (_vm is not null)
            UpdateSize(_vm);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ShelfWindowViewModel vm && e.PropertyName == nameof(ShelfWindowViewModel.IsExpanded))
            UpdateSize(vm);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnStackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (IsWithinCompactIcon(e.Source as Control))
            return;

        _stackMoveStartArgs = e;
        _stackDragStart = e.GetPosition(this);
        _stackMoving = false;
    }

    private void OnStackPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (_stackMoving)
            return;

        if (IsWithinCompactIcon(e.Source as Control))
            return;

        var p = e.GetPosition(this);
        var dx = p.X - _stackDragStart.X;
        var dy = p.Y - _stackDragStart.Y;
        if ((dx * dx + dy * dy) < (6 * 6))
            return;

        _stackMoving = true;
        try
        {
            if (_stackMoveStartArgs is not null)
                BeginMoveDrag(_stackMoveStartArgs);
        }
        catch
        {
        }
    }

    private void OnStackPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is ShelfWindowViewModel vm && !_stackMoving && vm.HasMultipleItems)
        {
            vm.IsExpanded = !vm.IsExpanded;
            UpdateSize(vm);
        }

        _stackMoving = false;
        _stackMoveStartArgs = null;
    }

    private void OnCompactIconPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (DataContext is not ShelfWindowViewModel vm)
            return;

        if (vm.IsExpanded)
            return;

        if (!vm.Pinned || vm.Items.Count == 0)
            return;

        _iconDragStart = e.GetPosition(this);
        _iconDragging = false;
    }

    private async void OnCompactIconPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (DataContext is not ShelfWindowViewModel vm)
            return;

        if (vm.IsExpanded)
            return;

        if (!vm.Pinned || vm.Items.Count == 0)
            return;

        if (_iconDragging)
            return;

        var p = e.GetPosition(this);
        var dx = p.X - _iconDragStart.X;
        var dy = p.Y - _iconDragStart.Y;
        if ((dx * dx + dy * dy) < (6 * 6))
            return;

        _iconDragging = true;

        try
        {
            var paths = vm.Items
                .Select(x => x.FilePath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (paths.Length == 0)
                return;

            var data = new DataObject();
            data.Set(ShelfInternalDragFormat, true);
            data.Set(DataFormats.FileNames, paths);

            if (_dragOutOverlay is not null)
            {
                _dragOutOverlay.DataContext = vm.PrimaryItem;
                _dragOutOverlay.Opacity = 1;
            }
            if (_dragOutPill?.RenderTransform is ScaleTransform pillScale)
            {
                pillScale.ScaleX = 1;
                pillScale.ScaleY = 1;
            }
            if (_dragOutPill is not null)
                _dragOutPill.Opacity = 1;
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }
        catch
        {
        }
        finally
        {
            if (_dragOutOverlay is not null)
            {
                _dragOutOverlay.Opacity = 0;
                _dragOutOverlay.DataContext = null;
            }
            if (_dragOutPill?.RenderTransform is ScaleTransform pillScale)
            {
                pillScale.ScaleX = 0.96;
                pillScale.ScaleY = 0.96;
            }
            if (_dragOutPill is not null)
                _dragOutPill.Opacity = 0;
            _iconDragging = false;
        }
    }

    private void OnCompactIconPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _iconDragging = false;
    }

    private bool IsWithinCompactIcon(Control? control)
    {
        if (_compactIcon is null || control is null)
            return false;

        var current = control;
        while (current is not null)
        {
            if (ReferenceEquals(current, _compactIcon))
                return true;
            current = current.Parent as Control;
        }

        return false;
    }

    private void UpdateSize(ShelfWindowViewModel vm)
    {
        if (vm.IsExpanded)
        {
            Width = ExpandedSize;
            Height = ExpandedSize;
        }
        else
        {
            Width = CompactSize;
            Height = CompactSize;
        }
    }

    private void OnExpandedItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (DataContext is not ShelfWindowViewModel vm || !vm.IsExpanded)
            return;

        if (e.Source is not Control src)
            return;

        var item = src.DataContext as ShelfFileItemViewModel;
        if (item is null)
            return;

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            item.IsSelected = !item.IsSelected;
        }
        else
        {
            foreach (var it in vm.Items)
            {
                if (!ReferenceEquals(it, item) && it.IsSelected)
                    it.IsSelected = false;
            }
            item.IsSelected = true;
        }

        _itemDrag = item;
        _itemDragStart = e.GetPosition(this);
        _itemDragging = false;
    }

    private async void OnExpandedItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (_itemDrag is null)
            return;

        if (_itemDragging)
            return;

        if (DataContext is not ShelfWindowViewModel vm || !vm.IsExpanded)
            return;

        var p = e.GetPosition(this);
        var dx = p.X - _itemDragStart.X;
        var dy = p.Y - _itemDragStart.Y;
        if ((dx * dx + dy * dy) < (6 * 6))
            return;

        _itemDragging = true;
        try
        {
            var selected = vm.Items.Where(x => x.IsSelected).ToList();
            if (selected.Count == 0)
                selected.Add(_itemDrag);

            var paths = selected
                .Select(x => x.FilePath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (paths.Length == 0)
                return;

            var data = new DataObject();
            data.Set(ShelfInternalDragFormat, true);
            data.Set(DataFormats.FileNames, paths);
            if (_dragOutOverlay is not null)
            {
                _dragOutOverlay.DataContext = _itemDrag;
                _dragOutOverlay.Opacity = 1;
            }
            if (_dragOutPill?.RenderTransform is ScaleTransform pillScale)
            {
                pillScale.ScaleX = 1;
                pillScale.ScaleY = 1;
            }
            if (_dragOutPill is not null)
                _dragOutPill.Opacity = 1;
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }
        catch
        {
        }
        finally
        {
            if (_dragOutOverlay is not null)
            {
                _dragOutOverlay.Opacity = 0;
                _dragOutOverlay.DataContext = null;
            }
            if (_dragOutPill?.RenderTransform is ScaleTransform pillScale)
            {
                pillScale.ScaleX = 0.96;
                pillScale.ScaleY = 0.96;
            }
            if (_dragOutPill is not null)
                _dragOutPill.Opacity = 0;
            _itemDragging = false;
            _itemDrag = null;
        }
    }

    private void OnExpandedItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _itemDragging = false;
        _itemDrag = null;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(ShelfInternalDragFormat))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            if (_dragOverlay is not null)
                _dragOverlay.Opacity = 0;
            return;
        }

        if (!DragFileHelper.HasFileData(e.Data))
        {
            e.DragEffects = DragDropEffects.None;
            if (_dragOverlay is not null)
                _dragOverlay.Opacity = 0;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        if (_dragOverlay is not null)
            _dragOverlay.Opacity = 1;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(ShelfInternalDragFormat))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            if (_dragOverlay is not null)
                _dragOverlay.Opacity = 0;
            return;
        }

        if (!DragFileHelper.HasFileData(e.Data))
        {
            e.DragEffects = DragDropEffects.None;
            if (_dragOverlay is not null)
                _dragOverlay.Opacity = 0;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        if (_dragOverlay is not null)
            _dragOverlay.Opacity = 1;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (_dragOverlay is not null)
            _dragOverlay.Opacity = 0;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(ShelfInternalDragFormat))
        {
            e.Handled = true;
            if (_dragOverlay is not null)
                _dragOverlay.Opacity = 0;
            return;
        }

        var paths = DragFileHelper.GetFilePaths(e.Data);
        if (paths.Count == 0)
            return;

        if (DataContext is not ShelfWindowViewModel vm)
            return;

        vm.AddFiles(paths);
        _onPinned?.Invoke(this);
        e.Handled = true;
        if (_dragOverlay is not null)
            _dragOverlay.Opacity = 0;
    }
}
