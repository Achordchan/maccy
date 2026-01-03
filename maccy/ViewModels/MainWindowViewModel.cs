using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using maccy.Models;
using maccy.Services;
using System;

namespace maccy.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ClipboardApplyService? _apply;
    private readonly ClipboardHistoryService? _history;

    private int _toastToken;

    public event System.Action? RequestHide;

    public event System.Action? RequestFocusSearch;

    public event System.Action? RequestOpenPreferences;

    public event System.Action? RequestCheckUpdates;

    public event System.Action<ClipboardItem>? RequestEditNote;

    public IRelayCommand OpenPreferencesCommand { get; }

    public IRelayCommand CheckUpdatesCommand { get; }

    public IRelayCommand AboutCommand { get; }

    public IRelayCommand FocusSearchCommand { get; }

    public IRelayCommand ClearSearchCommand { get; }

    public ObservableCollection<ClipboardItem> Items { get; }

    public ObservableCollection<ClipboardItem> FilteredItems { get; }

    public ObservableCollection<ClipboardItem> PinnedFilteredItems { get; }

    public int TotalCount => Items.Count;

    public int PinnedCount => Items.Count(x => x.Pinned);

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private ClipboardItem? _selectedItem;

    [ObservableProperty]
    private string? _toastMessage;

    [ObservableProperty]
    private bool _showCommonRoot = true;

    [ObservableProperty]
    private bool _showCommonFolder;

    [ObservableProperty]
    private bool _showFavoritesFolder;

    public bool ShowHistory => !ShowCommonRoot && !ShowFavoritesFolder;

    public MainWindowViewModel()
    {
        Items = new ObservableCollection<ClipboardItem>();
        FilteredItems = new ObservableCollection<ClipboardItem>();
        PinnedFilteredItems = new ObservableCollection<ClipboardItem>();
        _apply = null;
        _history = null;

        OpenPreferencesCommand = new RelayCommand(() => RequestOpenPreferences?.Invoke());
        CheckUpdatesCommand = new RelayCommand(() => RequestCheckUpdates?.Invoke());
        AboutCommand = new RelayCommand(() => ShowToast("maccy (Windows)"));

        FocusSearchCommand = new RelayCommand(() => RequestFocusSearch?.Invoke());

        ClearSearchCommand = new RelayCommand(() =>
        {
            SearchText = string.Empty;
            RequestFocusSearch?.Invoke();
        });

        ApplySelectedCommand = new RelayCommand(() => _ = ApplySelectedAsync());
        HideCommand = new RelayCommand(() => RequestHide?.Invoke());
        DeleteSelectedCommand = new RelayCommand(() => DeleteSelected());
        TogglePinCommand = new RelayCommand(() => TogglePinSelected());

        TogglePinItemCommand = new RelayCommand<ClipboardItem?>(item =>
        {
            if (item is null)
                return;
            _history?.TogglePinned(item.Id);
            RefreshFiltered();
        });

        DeleteItemCommand = new RelayCommand<ClipboardItem?>(item =>
        {
            if (item is null)
                return;
            _history?.Remove(item.Id);
            if (SelectedItem?.Id == item.Id)
                SelectedItem = null;
            RefreshFiltered();
        });

        EditNoteCommand = new RelayCommand<ClipboardItem?>(item =>
        {
            if (item is null)
                return;
            RequestEditNote?.Invoke(item);
        });

        EnterCommonClipsCommand = new RelayCommand(() =>
        {
            ShowCommonRoot = false;
            ShowCommonFolder = false;
            ShowFavoritesFolder = true;
            OnPropertyChanged(nameof(ShowHistory));
        });

        EnterFavoritesCommand = new RelayCommand(() =>
        {
            ShowCommonRoot = false;
            ShowCommonFolder = false;
            ShowFavoritesFolder = true;
            OnPropertyChanged(nameof(ShowHistory));
        });

        BackCommonSectionCommand = new RelayCommand(() =>
        {
            ShowCommonRoot = true;
            ShowCommonFolder = false;
            ShowFavoritesFolder = false;
            OnPropertyChanged(nameof(ShowHistory));
        });
    }

    public void ReorderItem(Guid movedId, Guid? beforeId)
    {
        if (_history is null)
            return;

        _history.MoveBefore(movedId, beforeId);
        RefreshFiltered();
    }

    public MainWindowViewModel(ClipboardHistoryService history, ClipboardApplyService apply)
    {
        Items = history.Items;
        FilteredItems = new ObservableCollection<ClipboardItem>();
        PinnedFilteredItems = new ObservableCollection<ClipboardItem>();
        _apply = apply;
        _history = history;

        _history.Changed += RefreshFiltered;

        OpenPreferencesCommand = new RelayCommand(() => RequestOpenPreferences?.Invoke());
        CheckUpdatesCommand = new RelayCommand(() => RequestCheckUpdates?.Invoke());
        AboutCommand = new RelayCommand(() => ShowToast("maccy (Windows)"));

        FocusSearchCommand = new RelayCommand(() => RequestFocusSearch?.Invoke());

        ClearSearchCommand = new RelayCommand(() =>
        {
            SearchText = string.Empty;
            RequestFocusSearch?.Invoke();
        });

        ApplySelectedCommand = new RelayCommand(() => _ = ApplySelectedAsync());
        HideCommand = new RelayCommand(() => RequestHide?.Invoke());
        DeleteSelectedCommand = new RelayCommand(() => DeleteSelected());
        TogglePinCommand = new RelayCommand(() => TogglePinSelected());

        TogglePinItemCommand = new RelayCommand<ClipboardItem?>(item =>
        {
            if (item is null)
                return;
            _history.TogglePinned(item.Id);
            RefreshFiltered();
        });

        DeleteItemCommand = new RelayCommand<ClipboardItem?>(item =>
        {
            if (item is null)
                return;
            _history.Remove(item.Id);
            if (SelectedItem?.Id == item.Id)
                SelectedItem = null;
            RefreshFiltered();
        });

        EditNoteCommand = new RelayCommand<ClipboardItem?>(item =>
        {
            if (item is null)
                return;
            RequestEditNote?.Invoke(item);
        });

        EnterCommonClipsCommand = new RelayCommand(() =>
        {
            ShowCommonRoot = false;
            ShowCommonFolder = false;
            ShowFavoritesFolder = true;
        });

        EnterFavoritesCommand = new RelayCommand(() =>
        {
            ShowCommonRoot = false;
            ShowCommonFolder = false;
            ShowFavoritesFolder = true;
        });

        BackCommonSectionCommand = new RelayCommand(() =>
        {
            ShowCommonRoot = true;
            ShowCommonFolder = false;
            ShowFavoritesFolder = false;
        });

        RefreshFiltered();
    }

    public IRelayCommand ApplySelectedCommand { get; }

    public IRelayCommand HideCommand { get; }

    public IRelayCommand DeleteSelectedCommand { get; }

    public IRelayCommand TogglePinCommand { get; }

    public IRelayCommand TogglePinItemCommand { get; }

    public IRelayCommand DeleteItemCommand { get; }

    public IRelayCommand EditNoteCommand { get; }

    public IRelayCommand EnterCommonClipsCommand { get; }

    public IRelayCommand EnterFavoritesCommand { get; }

    public IRelayCommand BackCommonSectionCommand { get; }

    public void UpdateNote(ClipboardItem item, string? note)
    {
        if (_history is null)
            return;
        _history.UpdateNote(item.Id, note);
        RefreshFiltered();
    }

    public void ClearAllHistory()
    {
        if (_history is null)
            return;

        _history.ClearAll();
        SelectedItem = null;
        RefreshFiltered();
    }

    partial void OnSelectedItemChanged(ClipboardItem? value)
    {
        // Maccy-like behavior: selecting does not immediately apply.
    }

    partial void OnSearchTextChanged(string? value)
    {
        RefreshFiltered();
    }

    private async Task ApplySelectedAsync()
    {
        if (_apply is null)
            return;

        var item = SelectedItem;
        if (item is null)
            return;

        await _apply.ApplyAsync(item);
        ShowToast("已写回剪贴板");
        RequestHide?.Invoke();
    }

    private void DeleteSelected()
    {
        if (_history is null)
            return;

        var item = SelectedItem;
        if (item is null)
            return;

        _history.Remove(item.Id);
        SelectedItem = null;
        RefreshFiltered();
    }

    private void TogglePinSelected()
    {
        if (_history is null)
            return;

        var item = SelectedItem;
        if (item is null)
            return;

        _history.TogglePinned(item.Id);
        var updated = Items.FirstOrDefault(x => x.Id == item.Id);
        if (updated is not null)
            SelectedItem = updated;
        RefreshFiltered();
    }

    private void RefreshFiltered()
    {
        var query = SearchText;

        var prevSelectedId = SelectedItem?.Id;

        var source = Items.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            source = source.Where(i => Matches(i, q));
        }

        var pinnedOrdered = source
            .Where(x => x.Pinned)
            .ToList();

        var ordered = source
            .ToList();

        PinnedFilteredItems.Clear();
        foreach (var it in pinnedOrdered)
            PinnedFilteredItems.Add(it);

        FilteredItems.Clear();
        foreach (var it in ordered)
            FilteredItems.Add(it);

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(PinnedCount));

        var allOrdered = ordered;

        if (prevSelectedId is not null)
        {
            var stillThere = allOrdered.FirstOrDefault(x => x.Id == prevSelectedId.Value);
            if (stillThere is not null)
                SelectedItem = stillThere;
        }

        if (SelectedItem is null)
        {
            if (ShowFavoritesFolder && PinnedFilteredItems.Count > 0)
                SelectedItem = PinnedFilteredItems[0];
            else if (FilteredItems.Count > 0)
                SelectedItem = FilteredItems[0];
            else if (PinnedFilteredItems.Count > 0)
                SelectedItem = PinnedFilteredItems[0];
        }
    }

    private static bool Matches(ClipboardItem item, string query)
    {
        if (item.Text is not null && item.Text.Contains(query, System.StringComparison.OrdinalIgnoreCase))
            return true;

        if (item.Note is not null && item.Note.Contains(query, System.StringComparison.OrdinalIgnoreCase))
            return true;

        if (item.ImageFilePath is not null && item.ImageFilePath.Contains(query, System.StringComparison.OrdinalIgnoreCase))
            return true;

        if (item.FilePaths is not null && item.FilePaths.Any(p => p.Contains(query, System.StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private void ShowToast(string message)
    {
        ToastMessage = message;
        var token = Interlocked.Increment(ref _toastToken);
        _ = ClearToastLaterAsync(token);
    }

    private async Task ClearToastLaterAsync(int token)
    {
        await Task.Delay(900);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token == _toastToken)
                ToastMessage = null;
        });
    }
}
