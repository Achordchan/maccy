using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.IO;
using System.Net.Http;
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
    private bool _showSyncIdleIcon;

    [ObservableProperty]
    private bool _showSyncWorkingIcon;

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
            if (IsSyncing)
            {
                var working = string.IsNullOrWhiteSpace(LastSyncStatus) ? "同步中" : LastSyncStatus!.Trim();
                return string.IsNullOrWhiteSpace(SyncStatusText)
                    ? working
                    : $"{working}\n{SyncProgressText}";
            }

            if (!IsCloudSyncEnabled)
                return "未启用云同步";

            if (LastSyncAt is null)
                return "已启用云同步\n等待首次同步";

            var ts = LastSyncAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var status = string.IsNullOrWhiteSpace(LastSyncStatus) ? "同步" : LastSyncStatus!.Trim();
            return status + "\n最近同步：" + ts;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncIconToolTip))]
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

        SyncNowCommand = new RelayCommand(() => _ = RunAutoSyncAsync(manual: true, showProgress: true, reason: "manual"));


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

        SyncNowCommand = new RelayCommand(() => _ = RunAutoSyncAsync(manual: true, showProgress: true, reason: "manual"));

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

    partial void OnIsSyncingChanged(bool value)
    {
        OnPropertyChanged(nameof(SyncIconToolTip));
        UpdateSyncIcons();
    }

    partial void OnIsSyncProgressVisibleChanged(bool value)
    {
        UpdateSyncIcons();
    }

    private void UpdateSyncIcons()
    {
        var showWorking = IsCloudSyncEnabled && IsSyncing;
        var showSuccess = IsCloudSyncEnabled && !showWorking && !IsSyncProgressVisible && SyncResult == SyncResultKind.Success;
        var showError = IsCloudSyncEnabled && !showWorking && !IsSyncProgressVisible && SyncResult == SyncResultKind.Error;
        var showIdle = !showWorking && !showSuccess && !showError;

        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowSyncIdleIcon = showIdle;
            ShowSyncWorkingIcon = showWorking;
            ShowSyncSuccessIcon = showSuccess;
            ShowSyncErrorIcon = showError;
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            ShowSyncIdleIcon = showIdle;
            ShowSyncWorkingIcon = showWorking;
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
        _ = Task.Run(async () => await RunAutoSyncAsync(manual: false, showProgress: false, reason: "startup"));
    }

    public void TriggerBackgroundSync(string reason = "manual_background")
    {
        _ = Task.Run(async () => await RunAutoSyncAsync(manual: false, showProgress: false, reason: reason));
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
                await RunAutoSyncAsync(manual: false, showProgress: false, reason: "auto");
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
                var uploadResult = await _sync.UploadAsync(CancellationToken.None, reason: "local_mutation");
                await PersistSyncStateAfterUploadAsync(uploadResult, null, CancellationToken.None);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SyncResult = SyncResultKind.Success;
                    LastSyncAt = DateTimeOffset.Now;
                    LastSyncStatus = "同步成功";
                });
            }
            catch (Exception ex)
            {
                WriteSyncErrorLog(ex);
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

    private async Task RunAutoSyncAsync(bool manual, bool showProgress, string reason = "auto")
    {
        if (_sync is null || _settings is null)
            return;
        if (Interlocked.Exchange(ref _syncGate, 1) == 1)
            return;

        if (!CanAutoSync())
        {
            if (manual)
                ShowToast("请先登录");
            Interlocked.Exchange(ref _syncGate, 0);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsSyncing = true;
            if (showProgress)
                IsSyncProgressVisible = true;
            else
                IsSyncProgressVisible = false;
        });

        if (showProgress)
            UpdateSyncStatus("检查远端", 10);
        else
            UpdateBackgroundSyncStatus("后台同步：检查远端");

        var success = false;
        try
        {
            await WaitHistoryLoadedAsync();
            var manifest = await _sync.GetManifestInfoAsync(CancellationToken.None);
            var decision = _sync.DecideDirectionWithState(manifest);

            if (decision.Direction == SyncService.SyncDirection.None)
            {
                _sync.PersistSyncState(decision.LocalFingerprint, decision.Manifest);
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

            if (decision.Direction == SyncService.SyncDirection.Download)
            {
                if (showProgress)
                    UpdateSyncStatus("准备本地", 30);
                else
                    UpdateBackgroundSyncStatus("后台同步：准备下载");

                _suppressAutoSync = true;
                try
                {
                    await _sync.DownloadAndApplyAsync(
                        CancellationToken.None,
                        p => ReportSyncProgress(p, showProgress),
                        reason);
                }
                finally
                {
                    _suppressAutoSync = false;
                }

                _sync.PersistSyncState(_sync.ComputeLocalFingerprint(), decision.Manifest);
            }
            else
            {
                if (showProgress)
                    UpdateSyncStatus("准备本地", 30);
                else
                    UpdateBackgroundSyncStatus("后台同步：准备上传");

                var uploadResult = await _sync.UploadAsync(
                    CancellationToken.None,
                    p => ReportSyncProgress(p, showProgress),
                    reason);
                await PersistSyncStateAfterUploadAsync(uploadResult, decision.Manifest, CancellationToken.None);
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
                LastSyncStatus = showProgress ? "同步成功" : "后台同步成功";
            });
            success = true;
        }
        catch (Exception ex)
        {
            WriteSyncErrorLog(ex);
            if (showProgress)
                UpdateSyncStatus("同步失败", 100);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SyncResult = SyncResultKind.Error;
                LastSyncAt = DateTimeOffset.Now;
                LastSyncStatus = showProgress ? "同步失败" : "后台同步失败";
            });
            var explain = ExplainSyncError(ex);
            if (manual)
                ShowToast(explain);
            else if (!_initialSyncCompleted)
                ShowToast("首次同步失败：" + explain);
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

            if (success && !_initialSyncCompleted && !manual)
                _initialSyncCompleted = true;

            Interlocked.Exchange(ref _syncGate, 0);
        }
    }

    private bool CanAutoSync()
    {
        if (_settings is null)
            return false;
        var s = _settings.Current;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hasValidAccess = !string.IsNullOrWhiteSpace(s.AuthAccessToken)
            && s.AuthExpiresAtUnixMs > nowMs + 60_000;
        if (hasValidAccess)
            return true;

        return !string.IsNullOrWhiteSpace(s.AuthRefreshToken);
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

    private static string ExplainSyncError(Exception ex)
    {
        if (ex is InvalidOperationException ioe)
        {
            var msg = (ioe.Message ?? string.Empty).Trim();
            if (string.Equals(msg, "订阅已过期", StringComparison.Ordinal))
                return "订阅已过期，请续费后重试";
            if (string.Equals(msg, "subscription expired", StringComparison.OrdinalIgnoreCase))
                return "订阅已过期，请续费后重试";
            if (string.Equals(msg, "not logged in", StringComparison.Ordinal))
                return "登录已失效，请重新登录";
            if (string.Equals(msg, "missing NAS base url", StringComparison.Ordinal))
                return "云同步服务地址缺失，请重启应用后重试";
            if (!string.IsNullOrWhiteSpace(msg))
                return msg;
        }

        if (ex is NasAgentApiException api)
        {
            if (api.StatusCode == System.Net.HttpStatusCode.Forbidden && IsOverLimit(api))
                return "云端存储已超限，请清理旧快照或升级套餐";
            if (api.StatusCode == System.Net.HttpStatusCode.Forbidden && IsTierLimited(api))
                return "当前套餐不支持该同步操作，请升级套餐后重试";
            if (api.StatusCode == System.Net.HttpStatusCode.Forbidden && IsExpiredText(api.ResponseBody))
                return "订阅已过期，请续费后重试";
            if (api.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return "登录已失效，请重新登录";
            if (api.StatusCode == System.Net.HttpStatusCode.NotFound)
                return "云端还没有快照";
            if ((int)api.StatusCode == 413)
                return "同步数据过大，请清理后重试";
            return $"同步失败({(int)api.StatusCode})";
        }

        if (ex is TimeoutException)
            return "网络请求超时，请检查域名解析、HTTPS 和反向代理";

        if (ex is HttpRequestException hre)
        {
            var msg = (hre.Message ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(msg))
                return "同步服务不可达：" + msg;
            return "同步服务不可达，请稍后重试";
        }

        if (ex is OperationCanceledException)
            return "请求超时或已取消，请稍后重试";

        var fallback = (ex.Message ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(fallback) ? "未知错误" : fallback;
    }

    private static bool IsExpiredText(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        return body.Contains("订阅已过期", StringComparison.OrdinalIgnoreCase)
            || (body.Contains("subscription", StringComparison.OrdinalIgnoreCase)
                && body.Contains("expired", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOverLimit(NasAgentApiException api)
    {
        var code = (api.ErrorCode ?? string.Empty).Trim();
        if (code.Contains("over_limit", StringComparison.OrdinalIgnoreCase)
            || code.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || code.Contains("storage", StringComparison.OrdinalIgnoreCase))
            return true;

        var body = api.ResponseBody ?? string.Empty;
        return body.Contains("over limit", StringComparison.OrdinalIgnoreCase)
            || body.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || body.Contains("存储已超限", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTierLimited(NasAgentApiException api)
    {
        var code = (api.ErrorCode ?? string.Empty).Trim();
        if (code.Contains("tier", StringComparison.OrdinalIgnoreCase)
            || code.Contains("plan", StringComparison.OrdinalIgnoreCase)
            || code.Contains("subscription_required", StringComparison.OrdinalIgnoreCase))
            return true;

        var body = api.ResponseBody ?? string.Empty;
        return body.Contains("tier", StringComparison.OrdinalIgnoreCase)
            || body.Contains("plan", StringComparison.OrdinalIgnoreCase)
            || body.Contains("套餐", StringComparison.OrdinalIgnoreCase);
    }

    private void ReportSyncProgress(SyncService.SyncProgress progress, bool showProgress)
    {
        var stage = progress.Code switch
        {
            "prepare_local" => "准备本地",
            "export_snapshot" => "导出本地快照",
            "upload_blobs" => "上传二进制对象",
            "import_snapshot" => "导入本地快照",
            "upload_snapshot" => "上传快照",
            "download_snapshot" => "下载快照",
            "persist_local" => "写入本地",
            _ => string.IsNullOrWhiteSpace(progress.Message) ? "同步中" : progress.Message,
        };

        if (showProgress)
            UpdateSyncStatus(stage, progress.Percent);
        else
            UpdateBackgroundSyncStatus("后台同步：" + stage);
    }

    private void UpdateBackgroundSyncStatus(string status)
    {
        Dispatcher.UIThread.Post(() => { LastSyncStatus = status; });
    }

    private async Task PersistSyncStateAfterUploadAsync(
        UploadSnapshotResult? uploadResult,
        SyncService.SyncManifestInfo? fallbackManifest,
        CancellationToken ct)
    {
        if (_sync is null)
            return;

        var localFingerprint = _sync.ComputeLocalFingerprint();
        try
        {
            var latestManifest = await _sync.GetManifestInfoAsync(ct);
            _sync.PersistSyncState(localFingerprint, latestManifest);
            return;
        }
        catch
        {
        }

        var fallback = fallbackManifest ?? new SyncService.SyncManifestInfo(
            uploadResult?.Version,
            DateTimeOffset.UtcNow,
            uploadResult?.Sha256,
            uploadResult?.Size,
            null);
        _sync.PersistSyncState(localFingerprint, fallback);
    }

    private static void WriteSyncErrorLog(Exception ex)
    {
        try
        {
            var root = AppPaths.AppDataRoot;
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "sync_last_error.txt");
            var content =
                "time=" + DateTimeOffset.Now.ToString("O") + Environment.NewLine +
                "appDataRoot=" + root + Environment.NewLine +
                ex.ToString();
            File.WriteAllText(path, content);
        }
        catch
        {
        }
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

    public enum SyncResultKind
    {
        None,
        Success,
        Error,
    }
}
