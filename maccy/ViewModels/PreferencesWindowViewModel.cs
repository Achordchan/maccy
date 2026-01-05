using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using maccy.Services;

namespace maccy.ViewModels;

public partial class PreferencesWindowViewModel : ViewModelBase
{
    public sealed record ThemeOption(string Value, string Display);

    private const string OldDefaultCaptureFileExtensions = ".pdf,.ppt,.pptx,.doc,.docx,.xls,.xlsx,.txt";
    private const string DefaultCaptureFileExtensions = ".pdf,.ppt,.pptx,.doc,.docx,.xls,.xlsx,.txt,.md,.csv,.zip,.rar,.7z,.png,.jpg,.jpeg,.gif,.bmp,.webp";

    private readonly AppSettingsService _settings;
    private readonly WindowsAutoStartService _autoStart;
    private readonly Action? _checkUpdates;
    private readonly Action? _openStorage;

    private bool _normalizingCaptureFileMax;

    private int _settingsToastToken;

    public event Action? RequestClose;

    [ObservableProperty]
    private bool _startWithWindows;

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
    private string? _toastMessage;

    public IRelayCommand CloseCommand { get; }

    public IRelayCommand CheckUpdatesCommand { get; }

    public IRelayCommand OpenStorageCommand { get; }

    public PreferencesWindowViewModel(AppSettingsService settings, WindowsAutoStartService autoStart)
        : this(settings, autoStart, null)
    {
    }

    public PreferencesWindowViewModel(AppSettingsService settings, WindowsAutoStartService autoStart, Action? checkUpdates)
        : this(settings, autoStart, checkUpdates, null)
    {
    }

    public PreferencesWindowViewModel(AppSettingsService settings, WindowsAutoStartService autoStart, Action? checkUpdates, Action? openStorage)
    {
        _settings = settings;
        _autoStart = autoStart;
        _checkUpdates = checkUpdates;
        _openStorage = openStorage;

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
    }

    public void ReloadFromSettings()
    {
        var s = _settings.Current;

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
        _settings.Update(s => s.Theme = value.Value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        _autoStart.SetEnabled(value);
        _settings.Update(s => s.StartWithWindows = value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnCaptureImagesChanged(bool value)
    {
        _settings.Update(s => s.CaptureImages = value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnCaptureTextChanged(bool value)
    {
        _settings.Update(s => s.CaptureText = value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnCaptureFilesChanged(bool value)
    {
        _settings.Update(s => s.CaptureFiles = value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnCaptureFileExtensionsTextChanged(string value)
    {
        _settings.Update(s => s.CaptureFileExtensions = value ?? string.Empty);
        ShowSettingsToastDebounced();
    }

    partial void OnCaptureFileMaxMegabytesTextChanged(string value)
    {
        if (_normalizingCaptureFileMax)
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

            _settings.Update(s => s.CaptureFileMaxMegabytes = n);
            return;
        }

        _settings.Update(s => s.CaptureFileMaxMegabytes = n);
        ShowSettingsToastDebounced();
    }

    partial void OnMergeDuplicatesChanged(bool value)
    {
        _settings.Update(s => s.MergeDuplicates = value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnExcludePinnedFromLimitsChanged(bool value)
    {
        _settings.Update(s => s.ExcludePinnedFromLimits = value);
        ToastService.Instance.Show("设置已生效");
    }

    partial void OnMaxItemsTextChanged(string value)
    {
        if (int.TryParse(value, out var n) && n > 0)
            _settings.Update(s => s.MaxItems = n);
        ShowSettingsToastDebounced();
    }

    partial void OnMaxMegabytesTextChanged(string value)
    {
        if (int.TryParse(value, out var n) && n > 0)
            _settings.Update(s => s.MaxMegabytes = n);

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
        var token = Interlocked.Increment(ref _settingsToastToken);
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            if (token != _settingsToastToken)
                return;
            ToastService.Instance.Show("设置已生效");
        });
    }
}
