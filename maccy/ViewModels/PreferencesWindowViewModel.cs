using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using maccy.Services;

namespace maccy.ViewModels;

public partial class PreferencesWindowViewModel : ViewModelBase, IDisposable
{
    public sealed record ThemeOption(string Value, string Display);

    public sealed record ShelfTriggerModifierOption(string Value, string Display);

    private const string OldDefaultCaptureFileExtensions = ".pdf,.ppt,.pptx,.doc,.docx,.xls,.xlsx,.txt";
    private const string DefaultCaptureFileExtensions = ".pdf,.ppt,.pptx,.doc,.docx,.xls,.xlsx,.txt,.md,.csv,.zip,.rar,.7z,.png,.jpg,.jpeg,.gif,.bmp,.webp";

    private readonly AppSettingsService _settings;
    private readonly WindowsAutoStartService _autoStart;
    private readonly Action? _checkUpdates;
    private readonly Action? _openStorage;
    private readonly Action? _triggerBackgroundSync;

    private readonly AuthService _authing;

    private readonly SyncService? _sync;

    private long _dangerConfirmUntilUnixMs;

    private bool _normalizingCaptureFileMax;

    private int _settingsToastToken;

    private bool _suppressSettingsSideEffects;
    private bool _disposed;

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
    private string _authEmailText = string.Empty;

    [ObservableProperty]
    private string _authPasswordText = string.Empty;

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
    private string _subscriptionTierText = "-";

    [ObservableProperty]
    private string _subscriptionStorageText = "-";

    [ObservableProperty]
    private string _subscriptionRetentionText = "-";

    [ObservableProperty]
    private string _subscriptionOverLimitText = "-";

    [ObservableProperty]
    private bool _subscriptionOverLimit;

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

    public IAsyncRelayCommand RegisterCommand { get; }

    public IRelayCommand LogoutCommand { get; }

    public IAsyncRelayCommand TestNasConnectionCommand { get; }

    public IAsyncRelayCommand SyncNowCommand { get; }

    public IAsyncRelayCommand UploadSnapshotCommand { get; }

    public IAsyncRelayCommand DownloadAndApplySnapshotCommand { get; }

    public IRelayCommand SaveNasUrlCommand { get; }

    public IAsyncRelayCommand RedeemCardCommand { get; }

    public IAsyncRelayCommand RefreshSubscriptionCommand { get; }

    public PreferencesWindowViewModel(AppSettingsService settings, WindowsAutoStartService autoStart)
        : this(settings, autoStart, null, null, null, null)
    {
    }

    public PreferencesWindowViewModel(AppSettingsService settings, WindowsAutoStartService autoStart, Action? checkUpdates)
        : this(settings, autoStart, checkUpdates, null, null, null)
    {
    }

    public PreferencesWindowViewModel(AppSettingsService settings, WindowsAutoStartService autoStart, Action? checkUpdates, Action? openStorage)
        : this(settings, autoStart, checkUpdates, openStorage, null, null)
    {
    }

    public PreferencesWindowViewModel(
        AppSettingsService settings,
        WindowsAutoStartService autoStart,
        Action? checkUpdates,
        Action? openStorage,
        SyncService? sync,
        Action? triggerBackgroundSync)
    {
        _settings = settings;
        _autoStart = autoStart;
        _checkUpdates = checkUpdates;
        _openStorage = openStorage;
        _sync = sync;
        _triggerBackgroundSync = triggerBackgroundSync;

        _authing = new AuthService();

        ToastService.Instance.ToastChanged += OnToastChanged;

        _settings.Changed += OnSettingsChanged;

        ReloadFromSettings();

        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
        CheckUpdatesCommand = new RelayCommand(() => _checkUpdates?.Invoke());
        OpenStorageCommand = new RelayCommand(() => _openStorage?.Invoke());

        LoginCommand = new AsyncRelayCommand(LoginAsync, () => !AuthBusy);
        RegisterCommand = new AsyncRelayCommand(RegisterAsync, () => !AuthBusy);
        LogoutCommand = new RelayCommand(Logout);
        TestNasConnectionCommand = new AsyncRelayCommand(TestNasConnectionAsync, () => !NasBusy);
        SyncNowCommand = new AsyncRelayCommand(SyncNowAsync, () => !NasBusy);

        UploadSnapshotCommand = new AsyncRelayCommand(UploadSnapshotAsync, () => !NasBusy);
        DownloadAndApplySnapshotCommand = new AsyncRelayCommand(DownloadAndApplySnapshotAsync, () => !NasBusy);
        SaveNasUrlCommand = new RelayCommand(SaveNasUrl);

        RedeemCardCommand = new AsyncRelayCommand(RedeemCardAsync, CanRedeem);
        RefreshSubscriptionCommand = new AsyncRelayCommand(RefreshSubscriptionAsync, CanRefreshSubscription);
    }

    private bool CanRedeem() => !RedeemBusy && IsLoggedIn;

    private bool CanRefreshSubscription() => IsLoggedIn;

    partial void OnIsLoggedInChanged(bool value)
    {
        RedeemCardCommand?.NotifyCanExecuteChanged();
        RefreshSubscriptionCommand?.NotifyCanExecuteChanged();
    }

    private void OnSettingsChanged()
    {
        if (_disposed)
            return;
        Dispatcher.UIThread.Post(ReloadFromSettings);
    }

    private void OnToastChanged(string? msg)
    {
        if (_disposed)
            return;
        ToastMessage = msg;
    }

    public void ReloadFromSettings()
    {
        _suppressSettingsSideEffects = true;
        try
        {
            var s = _settings.Current;

            var valid = HasSignedInSession(s);
            IsLoggedIn = valid;
            var signedInEmail = valid && !string.IsNullOrWhiteSpace(s.AuthUserEmail) ? s.AuthUserEmail!.Trim() : string.Empty;
            AuthEmailText = !string.IsNullOrWhiteSpace(signedInEmail) ? signedInEmail : AuthEmailText;
            AuthPasswordText = string.Empty;
            AuthStatusText = valid
                ? (!string.IsNullOrWhiteSpace(signedInEmail) ? $"已登录（{signedInEmail}）" : "已登录")
                : "未登录";
            if (!valid)
                ResetSubscriptionFields("未登录");

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
    }

    public void OnWindowShown()
    {
        if (!IsLoggedIn)
            return;

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

        var baseUrl = (_settings.Current.NasAgentBaseUrl ?? string.Empty).Trim();
        var email = (AuthEmailText ?? string.Empty).Trim();
        var password = AuthPasswordText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ToastService.Instance.Show("请先填写 NAS 地址");
            return;
        }
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ToastService.Instance.Show("请输入邮箱和密码");
            return;
        }

        try
        {
            AuthBusy = true;
            LoginCommand.NotifyCanExecuteChanged();
            RegisterCommand.NotifyCanExecuteChanged();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(3));

            var token = await _authing.LoginAsync(baseUrl, email, password, cts.Token);

            _settings.Update(s =>
            {
                s.AuthAccessToken = token.AccessToken;
                s.AuthRefreshToken = token.RefreshToken;
                s.AuthIdToken = token.IdToken;
                s.AuthExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
                s.AuthUserEmail = token.Email ?? email;
                // 自动填充官方服务器地址
                if (string.IsNullOrWhiteSpace(s.NasAgentBaseUrl))
                    s.NasAgentBaseUrl = ServerDefaults.OfficialSyncBaseUrl;
            });

            ReloadFromSettings();
            ToastService.Instance.Show("登录成功");
            await RefreshSubscriptionAsync(ct);
            _triggerBackgroundSync?.Invoke();
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
            RegisterCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        if (AuthBusy)
            return;

        var baseUrl = (_settings.Current.NasAgentBaseUrl ?? string.Empty).Trim();
        var email = (AuthEmailText ?? string.Empty).Trim();
        var password = AuthPasswordText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ToastService.Instance.Show("请先填写 NAS 地址");
            return;
        }
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ToastService.Instance.Show("请输入邮箱和密码");
            return;
        }

        try
        {
            AuthBusy = true;
            LoginCommand.NotifyCanExecuteChanged();
            RegisterCommand.NotifyCanExecuteChanged();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(3));

            var token = await _authing.RegisterAsync(baseUrl, email, password, cts.Token);
            _settings.Update(s =>
            {
                s.AuthAccessToken = token.AccessToken;
                s.AuthRefreshToken = token.RefreshToken;
                s.AuthIdToken = token.IdToken;
                s.AuthExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
                s.AuthUserEmail = token.Email ?? email;
                if (string.IsNullOrWhiteSpace(s.NasAgentBaseUrl))
                    s.NasAgentBaseUrl = ServerDefaults.OfficialSyncBaseUrl;
            });

            ReloadFromSettings();
            ToastService.Instance.Show("注册成功");
            await RefreshSubscriptionAsync(ct);
            _triggerBackgroundSync?.Invoke();
        }
        catch (OperationCanceledException)
        {
            ToastService.Instance.Show("已取消");
        }
        catch
        {
            ToastService.Instance.Show("注册失败");
        }
        finally
        {
            AuthBusy = false;
            LoginCommand.NotifyCanExecuteChanged();
            RegisterCommand.NotifyCanExecuteChanged();
        }
    }

    private void Logout()
    {
        _settings.ClearAuthSession(clearUserEmail: true);

        ReloadFromSettings();

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

            var uploadResult = await _sync.UploadAsync(ct, reason: "preferences_upload");
            await PersistSyncStateAfterUploadAsync(uploadResult, null, ct);
            ToastService.Instance.Show("已上传到 NAS");
        }
        catch (OperationCanceledException)
        {
            ToastService.Instance.Show("已取消");
        }
        catch (Exception ex)
        {
            WriteSyncErrorLog(ex);
            ToastService.Instance.Show(ExplainSyncException(ex));
        }
        finally
        {
            NasBusy = false;
            UploadSnapshotCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task SyncNowAsync(CancellationToken ct)
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
            SyncNowCommand.NotifyCanExecuteChanged();

            var manifest = await _sync.GetManifestInfoAsync(ct);
            var decision = _sync.DecideDirectionWithState(manifest);
            if (decision.Direction == SyncService.SyncDirection.None)
            {
                _sync.PersistSyncState(decision.LocalFingerprint, decision.Manifest);
                ToastService.Instance.Show("已是最新");
                return;
            }

            if (decision.Direction == SyncService.SyncDirection.Download)
            {
                await _sync.DownloadAndApplyAsync(ct, reason: "preferences_manual");
                _sync.PersistSyncState(_sync.ComputeLocalFingerprint(), decision.Manifest);
                ReloadFromSettings();
                ToastService.Instance.Show("同步完成（已下载）");
                return;
            }

            var uploadResult = await _sync.UploadAsync(ct, reason: "preferences_manual");
            await PersistSyncStateAfterUploadAsync(uploadResult, decision.Manifest, ct);
            ToastService.Instance.Show("同步完成（已上传）");
        }
        catch (OperationCanceledException)
        {
            ToastService.Instance.Show("已取消");
        }
        catch (Exception ex)
        {
            WriteSyncErrorLog(ex);
            ToastService.Instance.Show(ExplainSyncException(ex));
        }
        finally
        {
            NasBusy = false;
            SyncNowCommand.NotifyCanExecuteChanged();
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

            await _sync.DownloadAndApplyAsync(ct, reason: "preferences_restore");
            await PersistSyncStateAfterDownloadAsync(ct);
            ReloadFromSettings();
            ToastService.Instance.Show("已从 NAS 恢复");
        }
        catch (OperationCanceledException)
        {
            ToastService.Instance.Show("已取消");
        }
        catch (Exception ex)
        {
            WriteSyncErrorLog(ex);
            ToastService.Instance.Show(ExplainSyncException(ex));
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
        var token = await EnsureAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            ResetSubscriptionFields("未登录");
            return;
        }

        try
        {
            var client = new NasAgentClient(baseUrl);
            var status = await client.GetSubscriptionStatusAsync(token, ct);
            if (status is null)
            {
                ResetSubscriptionFields("查询失败");
                return;
            }

            if (!status.Subscribed)
            {
                ResetSubscriptionFields("未订阅");
                return;
            }

            if (status.ExpiresAt is not null && DateTimeOffset.TryParse(status.ExpiresAt, out var expires))
                SubscriptionStatusText = $"订阅至 {expires:yyyy-MM-dd}";
            else
                SubscriptionStatusText = "已订阅";

            SubscriptionTierText = string.IsNullOrWhiteSpace(status.Tier) ? "未知" : status.Tier!;
            SubscriptionStorageText = FormatStorage(status.StorageBytes, status.StorageLimitBytes);
            SubscriptionRetentionText = status.RetentionDays.HasValue && status.RetentionDays.Value > 0
                ? $"{status.RetentionDays.Value} 天"
                : "未限制";

            SubscriptionOverLimit = status.OverLimit
                ?? (status.StorageBytes.HasValue
                    && status.StorageLimitBytes.HasValue
                    && status.StorageLimitBytes.Value > 0
                    && status.StorageBytes.Value > status.StorageLimitBytes.Value);
            SubscriptionOverLimitText = SubscriptionOverLimit ? "已超限（可能影响同步）" : "正常";
        }
        catch
        {
            ResetSubscriptionFields("查询失败");
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
        var token = await EnsureAccessTokenAsync(ct);
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

    private async Task PersistSyncStateAfterDownloadAsync(CancellationToken ct)
    {
        if (_sync is null)
            return;

        try
        {
            var manifest = await _sync.GetManifestInfoAsync(ct);
            _sync.PersistSyncState(_sync.ComputeLocalFingerprint(), manifest);
        }
        catch
        {
        }
    }

    private static void WriteSyncErrorLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataRoot);
            var path = Path.Combine(AppPaths.AppDataRoot, "sync_last_error.txt");
            File.WriteAllText(path, ex.ToString());
        }
        catch
        {
        }
    }

    private static string ExplainSyncException(Exception ex)
    {
        if (ex is InvalidOperationException ioe)
        {
            var msg = (ioe.Message ?? string.Empty).Trim();
            if (string.Equals(msg, "not logged in", StringComparison.Ordinal))
                return "请先登录";
            if (string.Equals(msg, "missing NAS base url", StringComparison.Ordinal))
                return "请先填写 NAS 地址";
            if (string.Equals(msg, "subscription expired", StringComparison.OrdinalIgnoreCase)
                || string.Equals(msg, "订阅已过期", StringComparison.Ordinal))
                return "订阅已过期，请续费后重试";
            if (!string.IsNullOrWhiteSpace(msg))
                return msg;
        }

        if (ex is NasAgentApiException api)
        {
            if (api.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return "登录已失效，请重新登录";
            if (api.StatusCode == System.Net.HttpStatusCode.NotFound)
                return "云端还没有快照";
            if ((int)api.StatusCode == 413)
                return "同步数据过大，请清理后重试";
            if (api.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                if (IsOverLimit(api))
                    return "云端存储已超限，请清理旧快照或升级套餐";
                if (IsTierLimited(api))
                    return "当前套餐不支持该同步操作，请升级套餐后重试";
                if (IsExpired(api))
                    return "订阅已过期，请续费后重试";
                return "权限不足或订阅状态异常";
            }

            return $"同步失败({(int)api.StatusCode})";
        }

        if (ex is TimeoutException)
            return "请求超时，请检查网络和反向代理";
        if (ex is OperationCanceledException)
            return "已取消";

        var fallback = (ex.Message ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(fallback) ? "同步失败" : "同步失败：" + fallback;
    }

    private static bool IsExpired(NasAgentApiException api)
    {
        var body = api.ResponseBody ?? string.Empty;
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

    private void ResetSubscriptionFields(string statusText)
    {
        SubscriptionStatusText = statusText;
        SubscriptionTierText = "-";
        SubscriptionStorageText = "-";
        SubscriptionRetentionText = "-";
        SubscriptionOverLimitText = "-";
        SubscriptionOverLimit = false;
    }

    private static string FormatStorage(long? usedBytes, long? limitBytes)
    {
        var used = usedBytes.HasValue ? FormatBytes(usedBytes.Value) : "-";
        var limit = limitBytes.HasValue && limitBytes.Value > 0 ? FormatBytes(limitBytes.Value) : "-";
        return $"{used} / {limit}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            bytes = 0;

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private static bool HasSignedInSession(AppSettings s)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hasValidAccess = !string.IsNullOrWhiteSpace(s.AuthAccessToken)
            && s.AuthExpiresAtUnixMs > nowMs + 30_000;
        if (hasValidAccess)
            return true;

        return !string.IsNullOrWhiteSpace(s.AuthRefreshToken);
    }

    private async Task<string?> EnsureAccessTokenAsync(CancellationToken ct)
    {
        var current = _settings.Current;
        var baseUrl = (_settings.Current.NasAgentBaseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var access = (current.AuthAccessToken ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(access) && current.AuthExpiresAtUnixMs > nowMs + 30_000)
        {
            var email = await _authing.GetCurrentEmailAsync(baseUrl, access, ct);
            if (!string.IsNullOrWhiteSpace(email))
                return access;

            _settings.ClearAuthSession();
            return null;
        }

        var refresh = (current.AuthRefreshToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(refresh))
            return null;

        try
        {
            var token = await _authing.RefreshAsync(baseUrl, refresh, ct);
            _settings.Update(s =>
            {
                s.AuthAccessToken = token.AccessToken;
                s.AuthRefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? current.AuthRefreshToken : token.RefreshToken;
                s.AuthIdToken = string.IsNullOrWhiteSpace(token.IdToken) ? current.AuthIdToken : token.IdToken;
                s.AuthExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
                s.AuthUserEmail = string.IsNullOrWhiteSpace(token.Email) ? current.AuthUserEmail : token.Email;
            });

            ReloadFromSettings();
            return _settings.Current.AuthAccessToken;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _settings.ClearAuthSession();
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _settings.Changed -= OnSettingsChanged;
        }
        catch
        {
        }

        try
        {
            ToastService.Instance.ToastChanged -= OnToastChanged;
        }
        catch
        {
        }
    }
}
