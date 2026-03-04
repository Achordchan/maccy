using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using maccy.Services;

namespace maccy.ViewModels;

public partial class PreferencesWindowViewModel : ViewModelBase
{
    public sealed record ThemeOption(string Value, string Display);

    public sealed record ShelfTriggerModifierOption(string Value, string Display);

    private const string OldDefaultCaptureFileExtensions = ".pdf,.ppt,.pptx,.doc,.docx,.xls,.xlsx,.txt";
    private const string DefaultCaptureFileExtensions = ".pdf,.ppt,.pptx,.doc,.docx,.xls,.xlsx,.txt,.md,.csv,.zip,.rar,.7z,.png,.jpg,.jpeg,.gif,.bmp,.webp";

    private readonly AppSettingsService _settings;
    private readonly WindowsAutoStartService _autoStart;
    private readonly Action? _checkUpdates;
    private readonly Action? _openStorage;

    private readonly AuthingOidcService _authing;

    private readonly SyncService? _sync;

    private long _dangerConfirmUntilUnixMs;

    private bool _normalizingCaptureFileMax;

    private int _settingsToastToken;

    private bool _suppressSettingsSideEffects;

    public event Action? RequestClose;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string _authStatusText = "未登录";

    [ObservableProperty]
    private bool _authBusy;

    [ObservableProperty]
    private bool _nasBusy;

    [ObservableProperty]
    private string _nasAgentBaseUrlText = string.Empty;

    [ObservableProperty]
    private string _nasAgentBaseUrlDraft = string.Empty;

    [ObservableProperty]
    private string _redeemCodeText = string.Empty;

    [ObservableProperty]
    private string? _subscriptionStatusText;

    [ObservableProperty]
    private bool _redeemBusy;

    public bool IsNasUrlDirty
    {
        get
        {
            var draft = (NasAgentBaseUrlDraft ?? string.Empty).Trim();
            var current = (_settings.Current.NasAgentBaseUrl ?? string.Empty).Trim();
            return !string.Equals(draft, current, StringComparison.Ordinal);
        }
    }

    public ObservableCollection<ThemeOption> ThemeOptions { get; } =
    [
        new ThemeOption("System", "跟随系统"),
        new ThemeOption("Dark", "深色"),
        new ThemeOption("Light", "浅色"),
    ];

    [ObservableProperty]
    private ThemeOption? _selectedTheme;

    [ObservableProperty]
    private bool _captureImages;

    [ObservableProperty]
    private bool _captureText;

    [ObservableProperty]
    private bool _captureFiles;

    [ObservableProperty]
    private string _captureFileExtensionsText = DefaultCaptureFileExtensions;

    [ObservableProperty]
    private string _captureFileMaxMegabytesText = "20";

    [ObservableProperty]
    private bool _mergeDuplicates;

    [ObservableProperty]
    private bool _excludePinnedFromLimits;

    [ObservableProperty]
    private string _maxItemsText = "200";

    [ObservableProperty]
    private string _maxMegabytesText = "300";

    [ObservableProperty]
    private bool _shelfEnabled;

    public ObservableCollection<ShelfTriggerModifierOption> ShelfTriggerModifierOptions { get; } =
    [
        new ShelfTriggerModifierOption("Ctrl", "Ctrl（推荐）"),
        new ShelfTriggerModifierOption("Alt", "Alt"),
        new ShelfTriggerModifierOption("Shift", "Shift"),
    ];

    private ShelfTriggerModifierOption? _selectedShelfTriggerModifier;

    public ShelfTriggerModifierOption? SelectedShelfTriggerModifier
    {
        get => _selectedShelfTriggerModifier;
        set
        {
            if (!SetProperty(ref _selectedShelfTriggerModifier, value))
                return;

            if (value is null)
                return;

            if (_suppressSettingsSideEffects)
                return;

            var current = _settings.Current.ShelfTriggerModifier ?? string.Empty;
            if (string.Equals(current, value.Value, StringComparison.OrdinalIgnoreCase))
                return;

            _settings.Update(s => s.ShelfTriggerModifier = value.Value);
            ToastService.Instance.Show("设置已生效");
        }
    }

    [ObservableProperty]
    private string? _toastMessage;

    public IRelayCommand CloseCommand { get; }

    public IRelayCommand CheckUpdatesCommand { get; }

    public IRelayCommand OpenStorageCommand { get; }

    public IAsyncRelayCommand LoginCommand { get; }

    public IRelayCommand LogoutCommand { get; }

    public IAsyncRelayCommand TestNasConnectionCommand { get; }

    public IAsyncRelayCommand UploadSnapshotCommand { get; }

    public IAsyncRelayCommand DownloadAndApplySnapshotCommand { get; }

    public IRelayCommand SaveNasUrlCommand { get; }

    public IAsyncRelayCommand RedeemCardCommand { get; }

    public IAsyncRelayCommand RefreshSubscriptionCommand { get; }

    public PreferencesWindowViewModel(AppSettingsService settings, WindowsAutoStartService autoStart)
        : this(settings, autoStart, null, null, null)
    {
    }

    public PreferencesWindowViewModel(AppSettingsService settings, WindowsAutoStartService autoStart, Action? checkUpdates)
        : this(settings, autoStart, checkUpdates, null, null)
    {
    }

    public PreferencesWindowViewModel(AppSettingsService settings, WindowsAutoStartService autoStart, Action? checkUpdates, Action? openStorage)
        : this(settings, autoStart, checkUpdates, openStorage, null)
    {
    }

    public PreferencesWindowViewModel(AppSettingsService settings, WindowsAutoStartService autoStart, Action? checkUpdates, Action? openStorage, SyncService? sync)
    {
        _settings = settings;
        _autoStart = autoStart;
        _checkUpdates = checkUpdates;
        _openStorage = openStorage;

        _sync = sync;

        _authing = new AuthingOidcService();

        try
        {
            ToastService.Instance.ToastChanged += msg => ToastMessage = msg;
        }
        catch
        {
        }

        ReloadFromSettings();

        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
        CheckUpdatesCommand = new RelayCommand(() => _checkUpdates?.Invoke());
        OpenStorageCommand = new RelayCommand(() => _openStorage?.Invoke());

        LoginCommand = new AsyncRelayCommand(LoginAsync, () => !AuthBusy);
        LogoutCommand = new RelayCommand(Logout);
        TestNasConnectionCommand = new AsyncRelayCommand(TestNasConnectionAsync, () => !NasBusy);

        UploadSnapshotCommand = new AsyncRelayCommand(UploadSnapshotAsync, () => !NasBusy);
        DownloadAndApplySnapshotCommand = new AsyncRelayCommand(DownloadAndApplySnapshotAsync, () => !NasBusy);
        SaveNasUrlCommand = new RelayCommand(SaveNasUrl);

        RedeemCardCommand = new AsyncRelayCommand(RedeemCardAsync, CanRedeem);
        RefreshSubscriptionCommand = new AsyncRelayCommand(RefreshSubscriptionAsync, CanRefreshSubscription);
    }

    private bool CanRedeem() => !RedeemBusy && IsLoggedIn;

    private bool CanRefreshSubscription() => IsLoggedIn;

    public void ReloadFromSettings()
    {
        _suppressSettingsSideEffects = true;
        try
        {
            var s = _settings.Current;

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var hasToken = !string.IsNullOrWhiteSpace(s.AuthAccessToken);
            var valid = hasToken && s.AuthExpiresAtUnixMs > nowMs + 30_000;
            IsLoggedIn = valid;
            AuthStatusText = valid ? "已登录" : "未登录";

            NasAgentBaseUrlText = s.NasAgentBaseUrl ?? string.Empty;
            NasAgentBaseUrlDraft = s.NasAgentBaseUrl ?? string.Empty;

            var rawShelfModifier = s.ShelfTriggerModifier ?? string.Empty;
            var shelfEnabled = s.ShelfEnabled;
            if (string.Equals(rawShelfModifier, "Disabled", StringComparison.OrdinalIgnoreCase))
                shelfEnabled = false;
            SetProperty(ref _shelfEnabled, shelfEnabled, nameof(ShelfEnabled));

            var captureExt = s.CaptureFileExtensions ?? string.Empty;

            var normalized = NormalizeExtensionList(captureExt);
            var normalizedOldDefault = NormalizeExtensionList(OldDefaultCaptureFileExtensions);

            if (string.IsNullOrWhiteSpace(normalized)
                || string.Equals(normalized, normalizedOldDefault, StringComparison.OrdinalIgnoreCase))
            {
                captureExt = DefaultCaptureFileExtensions;
                try
                {
                    _settings.Update(x => x.CaptureFileExtensions = captureExt);
                    s = _settings.Current;
                }
                catch
                {
                }
            }

            var enabled = _autoStart.IsEnabled();
            SetProperty(ref _startWithWindows, enabled, nameof(StartWithWindows));
            if (s.StartWithWindows != enabled)
                _settings.Update(x => x.StartWithWindows = enabled);

            SetProperty(
                ref _selectedTheme,
                ThemeOptions.FirstOrDefault(x => string.Equals(x.Value, s.Theme, StringComparison.OrdinalIgnoreCase))
                ?? ThemeOptions[0],
                nameof(SelectedTheme));

            SetProperty(ref _captureText, s.CaptureText, nameof(CaptureText));
            SetProperty(ref _captureImages, s.CaptureImages, nameof(CaptureImages));
            SetProperty(ref _captureFiles, s.CaptureFiles, nameof(CaptureFiles));
            SetProperty(ref _captureFileExtensionsText, captureExt, nameof(CaptureFileExtensionsText));
            SetProperty(ref _captureFileMaxMegabytesText, s.CaptureFileMaxMegabytes.ToString(), nameof(CaptureFileMaxMegabytesText));
            SetProperty(ref _mergeDuplicates, s.MergeDuplicates, nameof(MergeDuplicates));
            SetProperty(ref _excludePinnedFromLimits, s.ExcludePinnedFromLimits, nameof(ExcludePinnedFromLimits));
            SetProperty(ref _maxItemsText, s.MaxItems.ToString(), nameof(MaxItemsText));
            SetProperty(ref _maxMegabytesText, s.MaxMegabytes.ToString(), nameof(MaxMegabytesText));

            SetProperty(
                ref _selectedShelfTriggerModifier,
                ShelfTriggerModifierOptions.FirstOrDefault(x => string.Equals(x.Value, rawShelfModifier, StringComparison.OrdinalIgnoreCase))
                ?? ShelfTriggerModifierOptions[0],
                nameof(SelectedShelfTriggerModifier));
        }
        finally
        {
            _suppressSettingsSideEffects = false;
        }

        // 如果已登录，自动刷新订阅状态
        if (IsLoggedIn)
            _ = RefreshSubscriptionAsync(CancellationToken.None);
    }

    partial void OnShelfEnabledChanged(bool value)
    {
        if (_suppressSettingsSideEffects)
            return;
        if (_settings.Current.ShelfEnabled == value)
            return;
        _settings.Update(s => s.ShelfEnabled = value);
        ToastService.Instance.Show("设置已生效");
    }

    private static string NormalizeExtensionList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var parts = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLowerInvariant());

        return string.Join(",", parts);
    }

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (value is null)
            return;
        if (_suppressSettingsSideEffects)
            return;
        if (string.Equals(_settings.Current.Theme, value.Value, StringComparison.OrdinalIgnoreCase))
            return;
        _settings.Update(s => s.Theme = value.Value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_suppressSettingsSideEffects)
            return;
        if (_settings.Current.StartWithWindows == value)
            return;
        _autoStart.SetEnabled(value);
        _settings.Update(s => s.StartWithWindows = value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnCaptureImagesChanged(bool value)
    {
        if (_suppressSettingsSideEffects)
            return;
        if (_settings.Current.CaptureImages == value)
            return;
        _settings.Update(s => s.CaptureImages = value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnCaptureTextChanged(bool value)
    {
        if (_suppressSettingsSideEffects)
            return;
        if (_settings.Current.CaptureText == value)
            return;
        _settings.Update(s => s.CaptureText = value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnCaptureFilesChanged(bool value)
    {
        if (_suppressSettingsSideEffects)
            return;
        if (_settings.Current.CaptureFiles == value)
            return;
        _settings.Update(s => s.CaptureFiles = value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnCaptureFileExtensionsTextChanged(string value)
    {
        if (_suppressSettingsSideEffects)
            return;

        var v = value ?? string.Empty;
        var current = _settings.Current.CaptureFileExtensions ?? string.Empty;
        if (string.Equals(current, v, StringComparison.Ordinal))
            return;

        _settings.Update(s => s.CaptureFileExtensions = v);
        ShowSettingsToastDebounced();
    }

    partial void OnCaptureFileMaxMegabytesTextChanged(string value)
    {
        if (_normalizingCaptureFileMax)
            return;

        if (_suppressSettingsSideEffects)
            return;

        if (!int.TryParse(value, out var n))
            return;

        n = Math.Max(1, n);

        var maxHistory = _settings.Current.MaxMegabytes;
        if (n > maxHistory)
            n = maxHistory;

        var normalized = n.ToString();
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            try
            {
                _normalizingCaptureFileMax = true;
                CaptureFileMaxMegabytesText = normalized;
            }
            finally
            {
                _normalizingCaptureFileMax = false;
            }

            if (_settings.Current.CaptureFileMaxMegabytes != n)
                _settings.Update(s => s.CaptureFileMaxMegabytes = n);
            return;
        }

        if (_settings.Current.CaptureFileMaxMegabytes == n)
            return;

        _settings.Update(s => s.CaptureFileMaxMegabytes = n);
        ShowSettingsToastDebounced();
    }

    partial void OnMergeDuplicatesChanged(bool value)
    {
        if (_suppressSettingsSideEffects)
            return;
        if (_settings.Current.MergeDuplicates == value)
            return;
        _settings.Update(s => s.MergeDuplicates = value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnExcludePinnedFromLimitsChanged(bool value)
    {
        if (_suppressSettingsSideEffects)
            return;
        if (_settings.Current.ExcludePinnedFromLimits == value)
            return;
        _settings.Update(s => s.ExcludePinnedFromLimits = value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnMaxItemsTextChanged(string value)
    {
        if (_suppressSettingsSideEffects)
            return;

        if (int.TryParse(value, out var n) && n > 0)
        {
            if (_settings.Current.MaxItems == n)
                return;
            _settings.Update(s => s.MaxItems = n);
            ShowSettingsToastDebounced();
        }
    }

    partial void OnMaxMegabytesTextChanged(string value)
    {
        if (_suppressSettingsSideEffects)
            return;

        if (int.TryParse(value, out var n) && n > 0)
        {
            if (_settings.Current.MaxMegabytes != n)
                _settings.Update(s => s.MaxMegabytes = n);
        }

        try
        {
            var maxHistory = _settings.Current.MaxMegabytes;
            var current = _settings.Current.CaptureFileMaxMegabytes;
            if (current > maxHistory)
            {
                _settings.Update(s => s.CaptureFileMaxMegabytes = maxHistory);
                try
                {
                    _normalizingCaptureFileMax = true;
                    CaptureFileMaxMegabytesText = maxHistory.ToString();
                }
                finally
                {
                    _normalizingCaptureFileMax = false;
                }
            }
        }
        catch
        {
        }

        ShowSettingsToastDebounced();
    }

    private void ShowSettingsToastDebounced()
    {
        if (_suppressSettingsSideEffects)
            return;

        var token = Interlocked.Increment(ref _settingsToastToken);
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            if (token != _settingsToastToken)
                return;
            ToastService.Instance.Show("设置已生效");
        });
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        if (AuthBusy)
            return;

        try
        {
            AuthBusy = true;
            LoginCommand.NotifyCanExecuteChanged();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(3));

            var token = await _authing.LoginAsync(cts.Token);

            _settings.Update(s =>
            {
                s.AuthAccessToken = token.AccessToken;
                s.AuthRefreshToken = token.RefreshToken;
                s.AuthIdToken = token.IdToken;
                s.AuthExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
                // 自动填充官方服务器地址
                if (string.IsNullOrWhiteSpace(s.NasAgentBaseUrl))
                    s.NasAgentBaseUrl = AuthingOidcService.OfficialSyncBaseUrl;
            });

            ReloadFromSettings();
            ToastService.Instance.Show("登录成功");

            // 自动刷新订阅状态
            _ = RefreshSubscriptionAsync(ct);
        }
        catch (OperationCanceledException)
        {
            ToastService.Instance.Show("登录已取消");
        }
        catch
        {
            ToastService.Instance.Show("登录失败");
        }
        finally
        {
            AuthBusy = false;
            LoginCommand.NotifyCanExecuteChanged();
        }
    }

    private void Logout()
    {
        try
        {
            _settings.Update(s =>
            {
                s.AuthAccessToken = null;
                s.AuthRefreshToken = null;
                s.AuthIdToken = null;
                s.AuthExpiresAtUnixMs = 0;
            });
        }
        catch
        {
        }

        ReloadFromSettings();

        try
        {
            var url = _authing.BuildLogoutUrl();
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }

        ToastService.Instance.Show("已退出登录");
    }

    private void SaveNasUrl()
    {
        var v = (NasAgentBaseUrlDraft ?? string.Empty).Trim();
        var current = (_settings.Current.NasAgentBaseUrl ?? string.Empty).Trim();
        if (string.Equals(current, v, StringComparison.Ordinal))
            return;

        _settings.Update(s => s.NasAgentBaseUrl = v);
        NasAgentBaseUrlText = v;
        OnPropertyChanged(nameof(IsNasUrlDirty));
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnNasAgentBaseUrlDraftChanged(string value)
    {
        OnPropertyChanged(nameof(IsNasUrlDirty));
    }

    partial void OnNasAgentBaseUrlTextChanged(string value)
    {
        // 不再自动保存，仅用于同步已保存值
    }

    private async Task TestNasConnectionAsync(CancellationToken ct)
    {
        if (NasBusy)
            return;

        var baseUrl = (_settings.Current.NasAgentBaseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ToastService.Instance.Show("请先填写 NAS 地址");
            return;
        }

        try
        {
            NasBusy = true;
            TestNasConnectionCommand.NotifyCanExecuteChanged();

            var client = new NasAgentClient(baseUrl);
            var ok = await client.CheckHealthAsync(ct);
            ToastService.Instance.Show(ok ? "NAS Agent 正常" : "NAS Agent 不可达");
        }
        catch (OperationCanceledException)
        {
            ToastService.Instance.Show("已取消");
        }
        catch
        {
            ToastService.Instance.Show("NAS Agent 不可达");
        }
        finally
        {
            NasBusy = false;
            TestNasConnectionCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task UploadSnapshotAsync(CancellationToken ct)
    {
        if (NasBusy)
            return;
        if (_sync is null)
        {
            ToastService.Instance.Show("同步服务未就绪");
            return;
        }

        try
        {
            NasBusy = true;
            UploadSnapshotCommand.NotifyCanExecuteChanged();

            await _sync.UploadAsync(ct);
            ToastService.Instance.Show("已上传到 NAS");
        }
        catch (OperationCanceledException)
        {
            ToastService.Instance.Show("已取消");
        }
        catch (InvalidOperationException ex)
        {
            try
            {
                var path = Path.Combine(AppPaths.AppDataRoot, "sync_last_error.txt");
                File.WriteAllText(path, ex.ToString());
            }
            catch
            {
            }

            if (ex.Message == "not logged in")
                ToastService.Instance.Show("请先登录");
            else if (ex.Message == "missing NAS base url")
                ToastService.Instance.Show("请先填写 NAS 地址");
            else
                ToastService.Instance.Show("上传失败：" + (ex.Message ?? ex.GetType().Name) + "（详情见 sync_last_error.txt）");
        }
        catch (NasAgentApiException ex)
        {
            try
            {
                var path = Path.Combine(AppPaths.AppDataRoot, "sync_last_error.txt");
                File.WriteAllText(path, "NAS API error\nstatus=" + (int)ex.StatusCode + "\n" + (ex.ResponseBody ?? string.Empty));
            }
            catch
            {
            }

            if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized || ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var hasRefresh = !string.IsNullOrWhiteSpace(_settings.Current.AuthRefreshToken);
                ToastService.Instance.Show(hasRefresh
                    ? "未授权：已尝试自动刷新，请重试（详情见 sync_last_error.txt）"
                    : "未授权：缺少 refresh_token，需要重新登录一次以启用自动续期");
            }
            else if ((int)ex.StatusCode == 413)
                ToastService.Instance.Show("文件太大：NAS 端拒绝(413)");
            else
                ToastService.Instance.Show("上传失败：" + (int)ex.StatusCode);
        }
        catch (Exception ex)
        {
            var msg = ex.Message ?? string.Empty;
            msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
            if (msg.Length > 200)
                msg = msg[..200] + "...";

            try
            {
                var path = Path.Combine(AppPaths.AppDataRoot, "sync_last_error.txt");
                File.WriteAllText(path, ex.ToString());
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(msg))
                ToastService.Instance.Show("上传失败：" + ex.GetType().Name + "（详情见存储目录 sync_last_error.txt）");
            else
                ToastService.Instance.Show("上传失败：" + ex.GetType().Name + " " + msg + "（详情见存储目录 sync_last_error.txt）");
        }
        finally
        {
            NasBusy = false;
            UploadSnapshotCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task DownloadAndApplySnapshotAsync(CancellationToken ct)
    {
        if (NasBusy)
            return;
        if (_sync is null)
        {
            ToastService.Instance.Show("同步服务未就绪");
            return;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_dangerConfirmUntilUnixMs < nowMs)
        {
            _dangerConfirmUntilUnixMs = nowMs + 6_000;
            ToastService.Instance.Show("将覆盖本地历史，再点一次确认");
            return;
        }

        _dangerConfirmUntilUnixMs = 0;

        try
        {
            NasBusy = true;
            DownloadAndApplySnapshotCommand.NotifyCanExecuteChanged();

            await _sync.DownloadAndApplyAsync(ct);
            ReloadFromSettings();
            ToastService.Instance.Show("已从 NAS 恢复");
        }
        catch (OperationCanceledException)
        {
            ToastService.Instance.Show("已取消");
        }
        catch (InvalidOperationException ex)
        {
            try
            {
                var path = Path.Combine(AppPaths.AppDataRoot, "sync_last_error.txt");
                File.WriteAllText(path, ex.ToString());
            }
            catch
            {
            }

            if (ex.Message == "not logged in")
                ToastService.Instance.Show("请先登录");
            else if (ex.Message == "missing NAS base url")
                ToastService.Instance.Show("请先填写 NAS 地址");
            else
                ToastService.Instance.Show("下载失败：" + (ex.Message ?? ex.GetType().Name) + "（详情见 sync_last_error.txt）");
        }
        catch (NasAgentApiException ex)
        {
            try
            {
                var path = Path.Combine(AppPaths.AppDataRoot, "sync_last_error.txt");
                File.WriteAllText(path, "NAS API error\nstatus=" + (int)ex.StatusCode + "\n" + (ex.ResponseBody ?? string.Empty));
            }
            catch
            {
            }

            if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized || ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var hasRefresh = !string.IsNullOrWhiteSpace(_settings.Current.AuthRefreshToken);
                ToastService.Instance.Show(hasRefresh
                    ? "未授权：已尝试自动刷新，请重试（详情见 sync_last_error.txt）"
                    : "未授权：缺少 refresh_token，需要重新登录一次以启用自动续期");
            }
            else if ((int)ex.StatusCode == 413)
                ToastService.Instance.Show("文件太大：NAS 端拒绝(413)");
            else if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                ToastService.Instance.Show("NAS 端还没有快照");
            else
                ToastService.Instance.Show("下载失败：" + (int)ex.StatusCode);
        }
        catch (Exception ex)
        {
            var msg = ex.Message ?? string.Empty;
            msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
            if (msg.Length > 200)
                msg = msg[..200] + "...";

            try
            {
                var path = Path.Combine(AppPaths.AppDataRoot, "sync_last_error.txt");
                File.WriteAllText(path, ex.ToString());
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(msg))
                ToastService.Instance.Show("下载失败：" + ex.GetType().Name + "（详情见存储目录 sync_last_error.txt）");
            else
                ToastService.Instance.Show("下载失败：" + ex.GetType().Name + " " + msg + "（详情见存储目录 sync_last_error.txt）");
        }
        finally
        {
            NasBusy = false;
            DownloadAndApplySnapshotCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RefreshSubscriptionAsync(CancellationToken ct)
    {
        var baseUrl = _settings.Current.NasAgentBaseUrl;
        var token = _settings.Current.AuthAccessToken;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            SubscriptionStatusText = "未登录";
            return;
        }

        try
        {
            var client = new NasAgentClient(baseUrl);
            var status = await client.GetSubscriptionStatusAsync(token, ct);
            if (status is null)
            {
                SubscriptionStatusText = "查询失败";
                return;
            }

            if (!status.Subscribed)
            {
                SubscriptionStatusText = "未订阅";
                return;
            }

            if (status.ExpiresAt is not null && DateTimeOffset.TryParse(status.ExpiresAt, out var expires))
                SubscriptionStatusText = $"订阅至 {expires:yyyy-MM-dd}";
            else
                SubscriptionStatusText = "已订阅";
        }
        catch
        {
            SubscriptionStatusText = "查询失败";
        }
    }

    private async Task RedeemCardAsync(CancellationToken ct)
    {
        if (RedeemBusy)
            return;

        var code = (RedeemCodeText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            ToastService.Instance.Show("请输入卡密");
            return;
        }

        var baseUrl = _settings.Current.NasAgentBaseUrl;
        var token = _settings.Current.AuthAccessToken;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            ToastService.Instance.Show("请先登录");
            return;
        }

        RedeemBusy = true;
        RedeemCardCommand.NotifyCanExecuteChanged();

        try
        {
            var client = new NasAgentClient(baseUrl);
            var result = await client.RedeemCardAsync(token, code, ct);
            if (result is null)
            {
                ToastService.Instance.Show("兑换失败：网络错误");
                return;
            }

            if (!result.Success)
            {
                var error = result.Error ?? "未知错误";
                if (error.StartsWith("{") || error.StartsWith("["))
                    error = "兑换失败";
                ToastService.Instance.Show(error);
                return;
            }

            RedeemCodeText = string.Empty;
            ToastService.Instance.Show("兑换成功");

            await RefreshSubscriptionAsync(ct);
        }
        catch (Exception ex)
        {
            ToastService.Instance.Show("兑换失败：" + (ex.Message ?? "未知错误"));
        }
        finally
        {
            RedeemBusy = false;
            RedeemCardCommand.NotifyCanExecuteChanged();
        }
    }
}
