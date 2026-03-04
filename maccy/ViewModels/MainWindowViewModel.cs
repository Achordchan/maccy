using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using maccy.Models;
using maccy.Services;
using System;
using System.Reflection;

namespace maccy.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ClipboardApplyService? _apply;
    private readonly ClipboardHistoryService? _history;
    private readonly SyncService? _sync;
    private readonly AppSettingsService? _settings;
    private readonly Task? _historyLoadTask;

    private CancellationTokenSource? _autoSyncDebounceCts;
    private bool _initialSyncCompleted;
    private bool _suppressAutoSync;
    private int _syncGate;

    private int _toastToken;

    public event System.Action? RequestHide;

    public event System.Action? RequestFocusSearch;

    public event System.Action? RequestOpenPreferences;

    public event System.Action? RequestCheckUpdates;

    public event System.Action<ClipboardItem>? RequestEditNote;

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public IRelayCommand OpenPreferencesCommand { get; }

    public IRelayCommand CheckUpdatesCommand { get; }

    public IRelayCommand AboutCommand { get; }

    public IRelayCommand FocusSearchCommand { get; }

    public IRelayCommand ClearSearchCommand { get; }

    public IRelayCommand SyncNowCommand { get; }

    public ObservableCollection<ClipboardItem> Items { get; }

    public ObservableCollection<ClipboardItem> FilteredItems { get; }

    public ObservableCollection<ClipboardItem> PinnedFilteredItems { get; }

    public int TotalCount => Items.Count;

    public int PinnedCount => Items.Count(x => x.Pinned);

    public string VersionText
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v is null)
                return "v0.0.0";

            return $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private ClipboardItem? _selectedItem;

    [ObservableProperty]
    private string? _toastMessage;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private bool _isSyncProgressVisible;

    [ObservableProperty]
    private SyncResultKind _syncResult;

    [ObservableProperty]
    private bool _showSyncSuccessIcon;

    [ObservableProperty]
    private bool _showSyncErrorIcon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncIconToolTip))]
    private DateTimeOffset? _lastSyncAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncIconToolTip))]
    private string? _lastSyncStatus;

    [ObservableProperty]
    private int _syncProgressPercent;

    [ObservableProperty]
    private string? _syncStatusText;

    [ObservableProperty]
    private bool _showCommonRoot = true;

    [ObservableProperty]
    private bool _showCommonFolder;

    [ObservableProperty]
    private bool _showFavoritesFolder;

    public bool ShowHistory => !ShowCommonRoot && !ShowFavoritesFolder;

    public string SyncProgressText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SyncStatusText))
                return string.Empty;
            return $"{SyncStatusText} {SyncProgressPercent}%";
        }
    }

    public string SyncIconToolTip
    {
        get
        {
            if (!IsCloudSyncEnabled)
                return "未启用云同步";

            if (LastSyncAt is null)
                return "尚未同步";

            var ts = LastSyncAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var status = string.IsNullOrWhiteSpace(LastSyncStatus) ? "同步" : LastSyncStatus!.Trim();
            return status + "\n最近同步：" + ts;
        }
    }

    [ObservableProperty]
    private bool _isCloudSyncEnabled;

    public MainWindowViewModel()
    {
        Items = new ObservableCollection<ClipboardItem>();
        FilteredItems = new ObservableCollection<ClipboardItem>();
        PinnedFilteredItems = new ObservableCollection<ClipboardItem>();
        _apply = null;
        _history = null;
        _sync = null;
        _settings = null;
        _historyLoadTask = null;

        OpenPreferencesCommand = new RelayCommand(() => RequestOpenPreferences?.Invoke());
        CheckUpdatesCommand = new RelayCommand(() => RequestCheckUpdates?.Invoke());
        AboutCommand = new RelayCommand(() => ShowToast("maccy (Windows)"));

        FocusSearchCommand = new RelayCommand(() => RequestFocusSearch?.Invoke());

        ClearSearchCommand = new RelayCommand(() =>
        {
            SearchText = string.Empty;
            RequestFocusSearch?.Invoke();
        });

        SyncNowCommand = new RelayCommand(() => _ = RunAutoSyncAsync(manual: true, showProgress: true));


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
            _ = DeleteItemAsync(item);
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

        UpdateCloudSyncEnabled();
        UpdateSyncIcons();
    }

    public void ReorderItem(Guid movedId, Guid? beforeId)
    {
        if (_history is null)
            return;

        _history.MoveBefore(movedId, beforeId);
        RefreshFiltered();
    }

    public MainWindowViewModel(ClipboardHistoryService history, ClipboardApplyService apply, SyncService? sync, AppSettingsService? settings, Task? historyLoadTask = null)
    {
        Items = history.Items;
        FilteredItems = new ObservableCollection<ClipboardItem>();
        PinnedFilteredItems = new ObservableCollection<ClipboardItem>();
        _apply = apply;
        _history = history;
        _sync = sync;
        _settings = settings;
        _historyLoadTask = historyLoadTask;

        _history.Changed += RefreshFiltered;
        _history.Changed += OnHistoryChangedForAutoSync;

        if (_settings is not null)
        {
            _settings.Changed += () => Dispatcher.UIThread.Post(UpdateCloudSyncEnabled);
        }

        OpenPreferencesCommand = new RelayCommand(() => RequestOpenPreferences?.Invoke());
        CheckUpdatesCommand = new RelayCommand(() => RequestCheckUpdates?.Invoke());
        AboutCommand = new RelayCommand(() => ShowToast("maccy (Windows)"));

        FocusSearchCommand = new RelayCommand(() => RequestFocusSearch?.Invoke());

        ClearSearchCommand = new RelayCommand(() =>
        {
            SearchText = string.Empty;
            RequestFocusSearch?.Invoke();
        });

        SyncNowCommand = new RelayCommand(() => _ = RunAutoSyncAsync(manual: true, showProgress: true));

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
            _ = DeleteItemAsync(item);
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

        UpdateCloudSyncEnabled();
        UpdateSyncIcons();
    }

    private void UpdateCloudSyncEnabled()
    {
        IsCloudSyncEnabled = CanAutoSync();
    }

    partial void OnSyncResultChanged(SyncResultKind value)
    {
        UpdateSyncIcons();
    }

    partial void OnIsSyncProgressVisibleChanged(bool value)
    {
        UpdateSyncIcons();
    }

    private void UpdateSyncIcons()
    {
        var showSuccess = !IsSyncProgressVisible && SyncResult == SyncResultKind.Success;
        var showError = !IsSyncProgressVisible && SyncResult == SyncResultKind.Error;

        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowSyncSuccessIcon = showSuccess;
            ShowSyncErrorIcon = showError;
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            ShowSyncSuccessIcon = showSuccess;
            ShowSyncErrorIcon = showError;
        });
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

    public void StartAutoSync()
    {
        _ = Task.Run(async () => await RunAutoSyncAsync(manual: false, showProgress: true));
    }

    private void OnHistoryChangedForAutoSync()
    {
        if (_sync is null || _settings is null)
            return;
        if (!_initialSyncCompleted)
            return;
        if (_suppressAutoSync)
            return;
        if (_syncGate != 0)
            return;
        if (!CanAutoSync())
            return;

        ScheduleAutoSync();
    }

    private void ScheduleAutoSync()
    {
        _autoSyncDebounceCts?.Cancel();
        _autoSyncDebounceCts?.Dispose();
        _autoSyncDebounceCts = new CancellationTokenSource();
        var ct = _autoSyncDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, ct);
                await RunAutoSyncAsync(manual: false, showProgress: false);
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);
    }

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

    public async Task ClearAllHistoryWithCloudConfirmAsync()
    {
        if (_history is null)
            return;

        if (IsCloudSyncEnabled)
        {
            if (ConfirmAsync is null)
                return;

            var ok = await ConfirmAsync(
                "清除全部记录",
                "此操作将同时删除云端备份，且不可撤销。\n\n是否继续？");
            if (!ok)
                return;
        }

        _suppressAutoSync = true;
        try
        {
            _history.ClearAll();
            SelectedItem = null;
            RefreshFiltered();
        }
        finally
        {
            _suppressAutoSync = false;
        }

        await UploadCloudAfterLocalMutationAsync();
    }

    partial void OnSelectedItemChanged(ClipboardItem? value)
    {
        // Maccy-like behavior: selecting does not immediately apply.
    }

    partial void OnSearchTextChanged(string? value)
    {
        RefreshFiltered();
    }

    partial void OnSyncProgressPercentChanged(int value)
    {
        OnPropertyChanged(nameof(SyncProgressText));
    }

    partial void OnSyncStatusTextChanged(string? value)
    {
        OnPropertyChanged(nameof(SyncProgressText));
    }

    private async Task ApplySelectedAsync()
    {
        if (_apply is null)
            return;

        if (_history is null)
            return;

        var item = SelectedItem;
        if (item is null)
            return;

        await _apply.ApplyAsync(item);
        _history.Touch(item.Id);
        ShowToast("已写回剪贴板");
        RequestHide?.Invoke();
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

    private void DeleteSelected()
    {
        _ = DeleteSelectedAsync();
    }

    private async Task DeleteSelectedAsync()
    {
        if (_history is null)
            return;

        var item = SelectedItem;
        if (item is null)
            return;

        if (IsCloudSyncEnabled)
        {
            if (ConfirmAsync is null)
                return;
            var ok = await ConfirmAsync(
                "删除记录",
                "此操作将同时删除云端备份，且不可撤销。\n\n是否继续？");
            if (!ok)
                return;
        }

        DeleteItemCore(item);
        _ = UploadCloudAfterLocalMutationAsync();
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

    private async Task DeleteItemAsync(ClipboardItem item)
    {
        if (_history is null)
            return;

        if (IsCloudSyncEnabled)
        {
            if (ConfirmAsync is null)
                return;
            var ok = await ConfirmAsync(
                "删除记录",
                "此操作将同时删除云端备份，且不可撤销。\n\n是否继续？");
            if (!ok)
                return;
        }

        DeleteItemCore(item);
        await UploadCloudAfterLocalMutationAsync();
    }

    private void DeleteItemCore(ClipboardItem item)
    {
        if (_history is null)
            return;

        _suppressAutoSync = true;
        try
        {
            _history.Remove(item.Id);
            if (SelectedItem?.Id == item.Id)
                SelectedItem = null;
            RefreshFiltered();
        }
        finally
        {
            _suppressAutoSync = false;
        }
    }

    private Task UploadCloudAfterLocalMutationAsync()
    {
        if (_sync is null)
            return Task.CompletedTask;
        if (!IsCloudSyncEnabled)
            return Task.CompletedTask;
        if (Interlocked.Exchange(ref _syncGate, 1) == 1)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { IsSyncing = true; });
                await WaitHistoryLoadedAsync();
                await _sync.UploadAsync(CancellationToken.None);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SyncResult = SyncResultKind.Success;
                    LastSyncAt = DateTimeOffset.Now;
                    LastSyncStatus = "同步成功";
                });
            }
            catch
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SyncResult = SyncResultKind.Error;
                    LastSyncAt = DateTimeOffset.Now;
                    LastSyncStatus = "同步失败";
                });
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => { IsSyncing = false; });
                Interlocked.Exchange(ref _syncGate, 0);
            }
        });
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

    private async Task RunAutoSyncAsync(bool manual, bool showProgress)
    {
        if (_sync is null || _settings is null)
            return;
        if (Interlocked.Exchange(ref _syncGate, 1) == 1)
            return;

        if (!CanAutoSync())
        {
            if (manual)
                ShowToast("请先登录并填写 NAS 地址");
            Interlocked.Exchange(ref _syncGate, 0);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsSyncing = true;
            if (showProgress)
                IsSyncProgressVisible = true;
        });

        if (showProgress)
            UpdateSyncStatus("准备同步", 5);

        var success = false;
        try
        {
            await WaitHistoryLoadedAsync();

            if (showProgress)
                UpdateSyncStatus("检查远端", 15);
            var manifestJson = await _sync.GetManifestJsonAsync(CancellationToken.None);

            var localLatest = GetLocalLatestCapturedAt();
            var remoteUpdatedAt = TryReadRemoteUpdatedAt(manifestJson);
            var direction = DecideDirection(localLatest, remoteUpdatedAt);

            if (direction == SyncDirection.None)
            {
                if (showProgress)
                {
                    UpdateSyncStatus("已是最新", 100);
                    await Task.Delay(400);
                }
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SyncResult = SyncResultKind.Success;
                    LastSyncAt = DateTimeOffset.Now;
                    LastSyncStatus = "已是最新";
                });
                success = true;
                return;
            }

            if (direction == SyncDirection.Download)
            {
                if (showProgress)
                    UpdateSyncStatus("同步中", 40);

                _suppressAutoSync = true;
                try
                {
                    await _sync.DownloadAndApplyAsync(CancellationToken.None);
                }
                finally
                {
                    _suppressAutoSync = false;
                }
            }
            else
            {
                if (showProgress)
                    UpdateSyncStatus("上传中", 40);
                await _sync.UploadAsync(CancellationToken.None);
            }

            if (showProgress)
            {
                UpdateSyncStatus("完成", 100);
                await Task.Delay(300);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SyncResult = SyncResultKind.Success;
                LastSyncAt = DateTimeOffset.Now;
                LastSyncStatus = "同步成功";
            });
            success = true;
        }
        catch (Exception ex)
        {
            if (showProgress)
                UpdateSyncStatus("同步失败", 100);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SyncResult = SyncResultKind.Error;
                LastSyncAt = DateTimeOffset.Now;
                LastSyncStatus = "同步失败";
            });
            if (manual)
                ShowToast(ex.Message);
            else if (!_initialSyncCompleted)
                ShowToast("首次同步失败：" + (ex.Message ?? "未知错误"));
            if (showProgress)
                await Task.Delay(450);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsSyncing = false;
                if (showProgress)
                {
                    SyncStatusText = null;
                    SyncProgressPercent = 0;
                    IsSyncProgressVisible = false;
                }
            });

            if (success && !_initialSyncCompleted && showProgress && !manual)
                _initialSyncCompleted = true;

            Interlocked.Exchange(ref _syncGate, 0);
        }
    }

    private bool CanAutoSync()
    {
        if (_settings is null)
            return false;
        var s = _settings.Current;
        if (string.IsNullOrWhiteSpace(s.NasAgentBaseUrl))
            return false;
        if (string.IsNullOrWhiteSpace(s.AuthAccessToken))
            return false;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (s.AuthExpiresAtUnixMs <= nowMs + 60_000)
            return false;

        return true;
    }

    private async Task WaitHistoryLoadedAsync()
    {
        if (_historyLoadTask is null)
            return;
        try
        {
            await _historyLoadTask;
        }
        catch
        {
        }
    }

    private DateTimeOffset? GetLocalLatestCapturedAt()
    {
        if (_history is null)
            return null;
        try
        {
            if (_history.Items.Count == 0)
                return null;
            return _history.Items.Max(x => x.CapturedAt);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadRemoteUpdatedAt(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (!doc.RootElement.TryGetProperty("latest", out var latest))
                return null;
            if (!latest.TryGetProperty("updatedAt", out var updatedAt))
                return null;
            if (updatedAt.ValueKind != JsonValueKind.String)
                return null;
            if (DateTimeOffset.TryParse(updatedAt.GetString(), out var parsed))
                return parsed;
        }
        catch
        {
        }

        return null;
    }

    private static SyncDirection DecideDirection(DateTimeOffset? localLatest, DateTimeOffset? remoteUpdatedAt)
    {
        if (remoteUpdatedAt is null)
            return localLatest is null ? SyncDirection.None : SyncDirection.Upload;
        if (localLatest is null)
            return SyncDirection.Download;

        var localUtc = localLatest.Value.UtcDateTime;
        var remoteUtc = remoteUpdatedAt.Value.UtcDateTime;
        var diff = localUtc - remoteUtc;
        if (Math.Abs(diff.TotalSeconds) <= 2)
            return SyncDirection.None;
        return diff.TotalSeconds > 0 ? SyncDirection.Upload : SyncDirection.Download;
    }

    private void UpdateSyncStatus(string status, int percent)
    {
        var capped = Math.Clamp(percent, 0, 100);
        Dispatcher.UIThread.Post(() =>
        {
            SyncStatusText = status;
            SyncProgressPercent = capped;
        });
    }

    private enum SyncDirection
    {
        None,
        Upload,
        Download,
    }

    public enum SyncResultKind
    {
        None,
        Success,
        Error,
    }
}
