using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using maccy.Services;

namespace maccy.ViewModels;

public partial class PreferencesWindowViewModel : ViewModelBase
{
    public sealed record ThemeOption(string Value, string Display);

    private readonly AppSettingsService _settings;
    private readonly WindowsAutoStartService _autoStart;
    private readonly Action? _checkUpdates;

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
    private bool _mergeDuplicates;

    [ObservableProperty]
    private bool _excludePinnedFromLimits;

    [ObservableProperty]
    private string _maxItemsText = "200";

    [ObservableProperty]
    private string _maxMegabytesText = "300";

    public IRelayCommand CloseCommand { get; }

    public IRelayCommand CheckUpdatesCommand { get; }

    public PreferencesWindowViewModel(AppSettingsService settings, WindowsAutoStartService autoStart)
        : this(settings, autoStart, null)
    {
    }

    public PreferencesWindowViewModel(AppSettingsService settings, WindowsAutoStartService autoStart, Action? checkUpdates)
    {
        _settings = settings;
        _autoStart = autoStart;
        _checkUpdates = checkUpdates;

        var s = _settings.Current;
        var enabled = _autoStart.IsEnabled();
        StartWithWindows = enabled;
        if (s.StartWithWindows != enabled)
            _settings.Update(x => x.StartWithWindows = enabled);

        SelectedTheme = ThemeOptions.FirstOrDefault(x => string.Equals(x.Value, s.Theme, StringComparison.OrdinalIgnoreCase))
                        ?? ThemeOptions[0];
        CaptureText = s.CaptureText;
        CaptureImages = s.CaptureImages;
        CaptureFiles = s.CaptureFiles;
        MergeDuplicates = s.MergeDuplicates;
        ExcludePinnedFromLimits = s.ExcludePinnedFromLimits;
        MaxItemsText = s.MaxItems.ToString();
        MaxMegabytesText = s.MaxMegabytes.ToString();

        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
        CheckUpdatesCommand = new RelayCommand(() => _checkUpdates?.Invoke());
    }

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (value is null)
            return;
        _settings.Update(s => s.Theme = value.Value);
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        _autoStart.SetEnabled(value);
        _settings.Update(s => s.StartWithWindows = value);
    }

    partial void OnCaptureImagesChanged(bool value)
    {
        _settings.Update(s => s.CaptureImages = value);
    }

    partial void OnCaptureTextChanged(bool value)
    {
        _settings.Update(s => s.CaptureText = value);
    }

    partial void OnCaptureFilesChanged(bool value)
    {
        _settings.Update(s => s.CaptureFiles = value);
    }

    partial void OnMergeDuplicatesChanged(bool value)
    {
        _settings.Update(s => s.MergeDuplicates = value);
    }

    partial void OnExcludePinnedFromLimitsChanged(bool value)
    {
        _settings.Update(s => s.ExcludePinnedFromLimits = value);
    }

    partial void OnMaxItemsTextChanged(string value)
    {
        if (int.TryParse(value, out var n) && n > 0)
            _settings.Update(s => s.MaxItems = n);
    }

    partial void OnMaxMegabytesTextChanged(string value)
    {
        if (int.TryParse(value, out var n) && n > 0)
            _settings.Update(s => s.MaxMegabytes = n);
    }
}
