using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using maccy.Services;
using maccy.ViewModels;

namespace maccy.Views;

public partial class ShelfWindow : Window
{
    private const string ShelfInternalDragFormat = "maccy/shelf-internal-drag";

    private const double CompactSize = 260;
    private const double ExpandedSize = 460;

    private readonly Action<ShelfWindow>? _onPinned;

    private Control? _stackHandle;
    private Point _dragStart;
    private bool _dragging;

    private ItemsControl? _expandedItems;
    private ItemsControl? _expandedGrid;
    private ShelfFileItemViewModel? _itemDrag;
    private Point _itemDragStart;
    private bool _itemDragging;

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

        _stackHandle = this.FindControl<Control>("StackHandle");
        if (_stackHandle is not null)
        {
            _stackHandle.PointerPressed += OnStackPointerPressed;
            _stackHandle.PointerMoved += OnStackPointerMoved;
            _stackHandle.PointerReleased += OnStackPointerReleased;
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

        if (_dragging || _itemDragging)
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

        if (DataContext is not ShelfWindowViewModel vm)
            return;

        if (!vm.Pinned || vm.Items.Count == 0)
            return;

        _dragStart = e.GetPosition(this);
        _dragging = false;
    }

    private async void OnStackPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (DataContext is not ShelfWindowViewModel vm)
            return;

        if (vm.IsExpanded)
            return;

        if (!vm.Pinned || vm.Items.Count == 0)
            return;

        if (_dragging)
            return;

        var p = e.GetPosition(this);
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;
        if ((dx * dx + dy * dy) < (6 * 6))
            return;

        _dragging = true;

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

            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }
        catch
        {
        }
        finally
        {
            _dragging = false;
        }
    }

    private void OnStackPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is ShelfWindowViewModel vm && !_dragging && vm.HasMultipleItems)
        {
            var p = e.GetPosition(this);
            var dx = p.X - _dragStart.X;
            var dy = p.Y - _dragStart.Y;
            if ((dx * dx + dy * dy) < (6 * 6))
            {
                vm.IsExpanded = !vm.IsExpanded;
                UpdateSize(vm);
            }
        }

        _dragging = false;
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
            if (string.IsNullOrWhiteSpace(_itemDrag.FilePath))
                return;

            var data = new DataObject();
            data.Set(ShelfInternalDragFormat, true);
            data.Set(DataFormats.FileNames, new[] { _itemDrag.FilePath });
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }
        catch
        {
        }
        finally
        {
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
            return;
        }

        if (!DragFileHelper.HasFileData(e.Data))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(ShelfInternalDragFormat))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (!DragFileHelper.HasFileData(e.Data))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        // no-op
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(ShelfInternalDragFormat))
        {
            e.Handled = true;
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
    }
}
