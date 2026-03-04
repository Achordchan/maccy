using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using maccy.ViewModels;
using maccy.Views;
using maccy.Services;
using maccy.Models;

namespace maccy;

public partial class App : Application
{
    private WindowsClipboardWatcher? _clipboardWatcher;
    private ClipboardCaptureService? _clipboardCapture;
    private WindowsHotkeyService? _hotkey;
    private ClipboardPersistenceService? _persistence;

    private ClipboardHistoryService? _history;

    private SyncService? _sync;

    private ShelfService? _shelf;

    private AppSettingsService? _settings;
    private WindowsAutoStartService? _autoStart;
    private PreferencesWindow? _prefsWindow;

    private UpdateCoordinator? _updateCoordinator;
    private DispatcherTimer? _updateCheckTimer;

    private bool _allowExit;

    private DispatcherTimer? _autoHideRetryTimer;

    private IClassicDesktopStyleApplicationLifetime? _desktop;

    private Mutex? _appMutex;

    private DateTimeOffset _lastTrayClickUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan TrayDoubleClickThreshold = TimeSpan.FromMilliseconds(380);

    private const string AppMutexName = "maccy_mutex";

    private const string UpdateManifestUrl = "https://gitee.com/Achordchan/maccy/raw/master/docs/updates/manifest.json";

    private bool HasVisibleOwnedWindows(Window owner)
    {
        try
        {
            if (owner.OwnedWindows is not null && owner.OwnedWindows.Any(w => w.IsVisible))
                return true;
        }
        catch
        {
        }

        return false;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void SetupTrayIcon(MainWindow window)
    {
        var icon = new WindowIcon(new Bitmap(AssetLoader.Open(new Uri("avares://maccy/Assets/avalonia-logo.ico"))));

        var menu = new NativeMenu();

        var open = new NativeMenuItem("打开");
        open.Click += (_, _) => Dispatcher.UIThread.Post(() => ToggleWindowCentered(window));
        menu.Items.Add(open);

        var prefs = new NativeMenuItem("设置...");
        prefs.Click += (_, _) => Dispatcher.UIThread.Post(() => ShowPreferences(window));
        menu.Items.Add(prefs);

        var update = new NativeMenuItem("检测更新...");
        update.Click += (_, _) => Dispatcher.UIThread.Post(() => TriggerManualUpdateCheck());
        menu.Items.Add(update);

        menu.Items.Add(new NativeMenuItemSeparator());

        var quit = new NativeMenuItem("退出");
        quit.Click += (_, _) => Dispatcher.UIThread.Post(() => _desktop?.Shutdown());
        menu.Items.Add(quit);

        var tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Maccy剪贴板工具",
            Menu = menu,
        };

        tray.Clicked += (_, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastTrayClickUtc <= TrayDoubleClickThreshold)
            {
                _lastTrayClickUtc = DateTimeOffset.MinValue;
                Dispatcher.UIThread.Post(() => ToggleWindowCentered(window));
                return;
            }

            _lastTrayClickUtc = now;
        };

        var icons = new TrayIcons { tray };

        TrayIcon.SetIcons(this, icons);
    }

    private void ShowPreferences(Window owner)
    {
        if (_settings is null || _autoStart is null)
            return;

        try
        {
            var ownerVisible = false;
            try
            {
                ownerVisible = owner.IsVisible;
            }
            catch
            {
            }

            if (_prefsWindow is not null)
            {
                try
                {
                    try
                    {
                        if (_prefsWindow.DataContext is PreferencesWindowViewModel prefsVm)
                            prefsVm.ReloadFromSettings();
                    }
                    catch
                    {
                    }

                    if (!_prefsWindow.IsVisible)
                    {
                        if (ownerVisible)
                            _prefsWindow.Show(owner);
                        else
                            _prefsWindow.Show();
                    }
                }
                catch
                {
                }

                _prefsWindow.Activate();
                return;
            }

            var vm = new PreferencesWindowViewModel(_settings, _autoStart, TriggerManualUpdateCheck, OpenStorageLocation, _sync);
            var w = new PreferencesWindow
            {
                DataContext = vm,
            };

            w.WindowStartupLocation = ownerVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            w.Topmost = !ownerVisible;
            _prefsWindow = w;

            vm.RequestClose += () => w.Close();
            w.Closed += (_, _) =>
            {
                _prefsWindow = null;
            };

            if (ownerVisible)
                w.Show(owner);
            else
                w.Show();

            w.Activate();
        }
        catch
        {
            _prefsWindow = null;
        }
    }

    private void OpenStorageLocation()
    {
        try
        {
            var path = AppPaths.AppDataRoot;
            try
            {
                System.IO.Directory.CreateDirectory(path);
            }
            catch
            {
            }

            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }

    private void TriggerManualUpdateCheck()
    {
        try
        {
            _ = _updateCoordinator?.CheckAndPromptAsync(manual: true);
        }
        catch
        {
        }
    }

    private void ShutdownForUpdate()
    {
        try
        {
            _allowExit = true;
        }
        catch
        {
        }

        try
        {
            _prefsWindow?.Close();
        }
        catch
        {
        }

        try
        {
            _desktop?.Shutdown();
        }
        catch
        {
        }
    }

    private void StartUpdateChecks(MainWindow window)
    {
        // Startup check (delayed slightly to avoid impacting startup experience)
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                _ = _updateCoordinator?.CheckAndPromptAsync(manual: false);
            }
            catch
            {
            }
        }, TimeSpan.FromSeconds(6));

        _updateCheckTimer?.Stop();
        _updateCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(12),
        };

        _updateCheckTimer.Tick += (_, _) =>
        {
            try
            {
                _ = _updateCoordinator?.CheckAndPromptAsync(manual: false);
            }
            catch
            {
            }
        };

        _updateCheckTimer.Start();
    }

    private void ApplyTheme(AppSettings s)
    {
        var value = s.Theme?.Trim();
        if (string.Equals(value, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            return;
        }

        if (string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase))
        {
            RequestedThemeVariant = ThemeVariant.Light;
            return;
        }

        RequestedThemeVariant = ThemeVariant.Default;
    }

    private void ApplySettings(ClipboardHistoryService history, ClipboardCaptureService? capture)
    {
        if (_settings is null)
            return;

        var s = _settings.Current;
        ApplyTheme(s);
        history.MaxItems = s.MaxItems;
        history.MaxBytes = (long)s.MaxMegabytes * 1024 * 1024;
        history.MergeDuplicates = s.MergeDuplicates;
        history.ExcludePinnedFromLimits = s.ExcludePinnedFromLimits;

        if (capture is not null)
        {
            capture.CaptureText = s.CaptureText;
            capture.CaptureImages = s.CaptureImages;
            capture.CaptureFiles = s.CaptureFiles;
            capture.CaptureFileExtensions = s.CaptureFileExtensions;
            capture.CaptureFileMaxBytes = (long)Math.Min(s.CaptureFileMaxMegabytes, s.MaxMegabytes) * 1024 * 1024;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                _appMutex = new Mutex(false, AppMutexName);
            }
            catch
            {
                _appMutex = null;
            }

            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            var window = new MainWindow();
            desktop.MainWindow = window;

            SetupTrayIcon(window);

            var clipboard = window.Clipboard;
            if (clipboard is null)
                return;

            _settings = new AppSettingsService();
            _settings.Load();
            _autoStart = new WindowsAutoStartService();

            _shelf = new ShelfService(window, _settings);
            window.SetShelfService(_shelf);

            window.Deactivated += (_, _) =>
            {
                if (_prefsWindow is not null)
                    return;
                if (!window.IsVisible)
                    return;

                if (HasVisibleOwnedWindows(window))
                    return;

                Dispatcher.UIThread.Post(() =>
                {
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (_prefsWindow is not null)
                            return;
                        if (!window.IsVisible)
                            return;

                        if (HasVisibleOwnedWindows(window))
                            return;

                        if (window.SuppressAutoHide)
                        {
                            EnsureAutoHideRetryTimer(window);
                            return;
                        }

                        window.Hide();
                    }, TimeSpan.FromMilliseconds(80));
                });
            };

            window.Activated += (_, _) =>
            {
                _autoHideRetryTimer?.Stop();
            };

            var history = new ClipboardHistoryService();
            _history = history;
            ApplySettings(history, null);
            _persistence = new ClipboardPersistenceService(history);
            var historyLoadTask = _persistence.LoadAsync();

            if (_settings is not null && _persistence is not null)
                _sync = new SyncService(history, _settings, _persistence);

            _clipboardCapture = new ClipboardCaptureService(history, clipboard, TryGetForegroundApp);
            ApplySettings(history, _clipboardCapture);
            var apply = new ClipboardApplyService(clipboard, _clipboardCapture);

            var vm = new MainWindowViewModel(history, apply, _sync, _settings, historyLoadTask);
            vm.RequestHide += () => window.Hide();
            vm.RequestFocusSearch += () => window.FocusSearch();
            vm.RequestOpenPreferences += () => ShowPreferences(window);
            vm.RequestCheckUpdates += () => TriggerManualUpdateCheck();
            vm.RequestEditNote += item => window.BeginEditNote(item);
            vm.ConfirmAsync = (title, message) => window.ShowConfirmAsync(title, message);
            window.DataContext = vm;
            Dispatcher.UIThread.Post(() => vm.StartAutoSync());

            var updateService = new UpdateService(UpdateManifestUrl);
            _updateCoordinator = new UpdateCoordinator(
                updateService,
                () => (_prefsWindow is not null && _prefsWindow.IsVisible) ? _prefsWindow : window,
                ShutdownForUpdate);
            StartUpdateChecks(window);

            if (_settings is not null)
            {
                _settings.Changed += () =>
                {
                    ApplySettings(history, _clipboardCapture);
                };
            }

            _hotkey = new WindowsHotkeyService(
                WindowsHotkeyService.MOD_CONTROL | WindowsHotkeyService.MOD_ALT,
                WindowsHotkeyService.VK_2);
            _hotkey.HotkeyPressed += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ToggleWindowNearCursor(window);
                });
            };
            _hotkey.Start();

            window.Hide();

            _clipboardWatcher = new WindowsClipboardWatcher();
            _clipboardWatcher.ClipboardChanged += (_, _) =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    if (_clipboardCapture is not null)
                        await _clipboardCapture.CaptureAsync();
                });
            };
            _clipboardWatcher.Start();

            desktop.Exit += (_, _) =>
            {
                _clipboardWatcher?.Dispose();
                _clipboardWatcher = null;
                _clipboardCapture = null;

                _shelf?.Dispose();
                _shelf = null;

                _hotkey?.Dispose();
                _hotkey = null;

                _persistence?.Dispose();
                _persistence = null;

                try
                {
                    _appMutex?.Dispose();
                }
                catch
                {
                }
                _appMutex = null;

                TrayIcon.SetIcons(this, null);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void EnsureAutoHideRetryTimer(MainWindow window)
    {
        _autoHideRetryTimer?.Stop();

        _autoHideRetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };

        _autoHideRetryTimer.Tick += (_, _) =>
        {
            if (_prefsWindow is not null)
            {
                _autoHideRetryTimer?.Stop();
                return;
            }

            if (!window.IsVisible)
            {
                _autoHideRetryTimer?.Stop();
                return;
            }

            if (window.IsActive)
            {
                _autoHideRetryTimer?.Stop();
                return;
            }

            if (window.SuppressAutoHide)
                return;

            window.Hide();
            _autoHideRetryTimer?.Stop();
        };

        _autoHideRetryTimer.Start();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }

    private static void ToggleWindowNearCursor(MainWindow window)
    {
        var (x, y) = WindowsHotkeyService.GetCursorPosition();
        var desired = new PixelPoint(x + 12, y + 12);

        var screen = window.Screens.ScreenFromPoint(desired) ?? window.Screens.Primary;
        if (screen is null)
        {
            window.Position = desired;
        }
        else
        {
            var wa = screen.WorkingArea;

            var w = (int)Math.Max(100, window.Width);
            var h = (int)Math.Max(100, window.Height);

            var clampedX = Math.Clamp(desired.X, wa.X, Math.Max(wa.X, wa.Right - w));
            var clampedY = Math.Clamp(desired.Y, wa.Y, Math.Max(wa.Y, wa.Bottom - h));

            window.Position = new PixelPoint(clampedX, clampedY);
        }

        window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        ForceForeground(window);
        window.PrepareForOpen();
        window.FocusSearch();
    }

    private static void ToggleWindowCentered(MainWindow window)
    {
        var (x, y) = WindowsHotkeyService.GetCursorPosition();
        var cursor = new PixelPoint(x, y);

        var screen = window.Screens.ScreenFromPoint(cursor) ?? window.Screens.Primary;
        var wa = screen?.WorkingArea;

        var w = (int)Math.Max(100, window.Width);
        var h = (int)Math.Max(100, window.Height);
        if (w <= 100 || h <= 100)
        {
            try
            {
                w = (int)Math.Max(100, window.Bounds.Width);
                h = (int)Math.Max(100, window.Bounds.Height);
            }
            catch
            {
            }
        }

        PixelPoint pos;
        if (wa is null)
        {
            pos = new PixelPoint(0, 0);
        }
        else
        {
            var cx = wa.Value.X + (wa.Value.Width - w) / 2;
            var cy = wa.Value.Y + (wa.Value.Height - h) / 2;
            pos = new PixelPoint(cx, cy);
        }

        window.Position = pos;
        window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        ForceForeground(window);
        window.PrepareForOpen();
        window.FocusSearch();
    }

    private static void ForceForeground(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero)
            return;

        var hwnd = handle.Handle;
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNORMAL);
        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetFocus(hwnd);
    }

    private static class NativeMethods
    {
        public const int SW_SHOWNORMAL = 1;

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }

    private static AppIdentity? TryGetForegroundApp()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return null;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
                return null;

            using var proc = Process.GetProcessById((int)pid);
            var path = string.Empty;
            try
            {
                path = proc.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                // access denied for some system processes
            }

            var name = string.IsNullOrWhiteSpace(path)
                ? proc.ProcessName
                : System.IO.Path.GetFileNameWithoutExtension(path);

            return new AppIdentity(name, path);
        }
        catch
        {
            return null;
        }
    }
}