using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using maccy.ViewModels;
using maccy.Views;

namespace maccy.Services;

public sealed class ShelfService : IDisposable
{
    private readonly Window _owner;
    private readonly FileIconService _iconService;

    private readonly AppSettingsService? _settings;
    private ShelfTriggerModifier _triggerModifier;
    private bool _enabled;

    private readonly List<ShelfWindow> _openShelves = new();

    private readonly WindowsMouseHookService _mouseHook;

    private readonly MouseShakeDetector _shakeDetector = new();

    private bool _leftDown;
    private bool _spawnedInThisPress;

    private DateTimeOffset _leftDownAt;
    private PixelPoint? _leftDownPoint;
    private bool _dragThresholdReached;

    private bool _dragSessionActive;

    public ShelfService(Window owner, AppSettingsService? settings = null)
    {
        _owner = owner;
        _settings = settings;
        _iconService = new FileIconService();
        _mouseHook = new WindowsMouseHookService();
        _mouseHook.LeftButtonPressed += OnLeftButtonPressed;
        _mouseHook.LeftButtonReleased += OnLeftButtonReleased;
        _mouseHook.MouseMoved += OnMouseMoved;

        RefreshTriggerModifier();
        if (_settings is not null)
            _settings.Changed += RefreshTriggerModifier;

        // Global activation: keep the hook running so we can detect shake anywhere.
        _mouseHook.Start();
    }

    private void RefreshTriggerModifier()
    {
        var s = _settings?.Current;
        var raw = s?.ShelfTriggerModifier;

        _triggerModifier = ParseModifier(raw);

        // Backward-compat: older configs may have stored "Disabled" in the modifier.
        var enabled = s?.ShelfEnabled ?? true;
        if (_triggerModifier == ShelfTriggerModifier.Disabled)
            enabled = false;

        _enabled = enabled;
    }

    public void BeginFileDragSession()
    {
        if (_dragSessionActive)
            return;

        _dragSessionActive = true;
        // Hook is started in the constructor for global activation.
    }

    private void OnLeftButtonPressed()
    {
        _leftDown = true;
        _spawnedInThisPress = false;
        _dragSessionActive = true;
        _leftDownAt = DateTimeOffset.UtcNow;
        _leftDownPoint = null;
        _dragThresholdReached = false;
        _shakeDetector.Reset();
    }

    private void OnMouseMoved(int x, int y)
    {
        if (!_leftDown)
            return;

        if (_spawnedInThisPress)
            return;

        if (!_enabled)
            return;

        if (_triggerModifier == ShelfTriggerModifier.Disabled)
            return;

        if (!IsModifierPressed(_triggerModifier))
            return;

        // Avoid false positives: do not start shake detection until user actually drags a bit.
        var p = new PixelPoint(x, y);
        if (_leftDownPoint is null)
        {
            _leftDownPoint = p;
            return;
        }

        if (!_dragThresholdReached)
        {
            var dx0 = p.X - _leftDownPoint.Value.X;
            var dy0 = p.Y - _leftDownPoint.Value.Y;
            if ((dx0 * dx0 + dy0 * dy0) < (8 * 8))
                return;

            // Now we consider this a real drag gesture; start a fresh shake window.
            _dragThresholdReached = true;
            _shakeDetector.Reset();
        }

        // Avoid "click-and-swipe" accidental shakes; require a short hold.
        if ((DateTimeOffset.UtcNow - _leftDownAt) < TimeSpan.FromMilliseconds(110))
            return;

        // We only use X-axis direction changes; screen coordinates are fine.
        if (_shakeDetector.Update(new Point(x, y)))
        {
            _spawnedInThisPress = true;
            SpawnPendingShelf(new PixelPoint(x, y));
        }
    }

    public void SpawnPendingShelf(PixelPoint cursorScreenPoint)
    {
        BeginFileDragSession();

        Dispatcher.UIThread.Post(() =>
        {
            var w = new ShelfWindow();
            var vm = new ShelfWindowViewModel(_iconService);
            w.DataContext = vm;

            vm.RequestClose += () => CloseShelf(w);

            PositionShelf(w, cursorScreenPoint);

            _openShelves.Add(w);

            w.Closed += (_, _) =>
            {
                _openShelves.Remove(w);
            };

            w.Show();
        });
    }

    private void PositionShelf(Window w, PixelPoint cursor)
    {
        var desired = new PixelPoint(cursor.X + 18, cursor.Y + 18);
        var screen = _owner.Screens.ScreenFromPoint(desired) ?? _owner.Screens.Primary;
        if (screen is null)
        {
            w.Position = desired;
            return;
        }

        var wa = screen.WorkingArea;
        var ww = (int)Math.Round(w.Width);
        var wh = (int)Math.Round(w.Height);

        var x = Math.Clamp(desired.X, wa.X, Math.Max(wa.X, wa.Right - ww));
        var y = Math.Clamp(desired.Y, wa.Y, Math.Max(wa.Y, wa.Bottom - wh));

        w.Position = new PixelPoint(x, y);
    }

    private void OnLeftButtonReleased()
    {
        if (!_dragSessionActive)
            return;

        _dragSessionActive = false;
        _leftDown = false;
        _spawnedInThisPress = false;
        _leftDownPoint = null;
        _dragThresholdReached = false;
        _shakeDetector.Reset();
    }

    private static bool IsLikelyExternalDragCursor()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        try
        {
            var ci = new CURSORINFO
            {
                cbSize = Marshal.SizeOf<CURSORINFO>()
            };

            if (!NativeMethods.GetCursorInfo(ref ci))
                return true;

            var cur = ci.hCursor;
            if (cur == nint.Zero)
                return true;

            // Common cursors during normal interactions; treat them as "not external drag".
            var arrow = NativeMethods.LoadCursor(nint.Zero, IDC_ARROW);
            var ibeam = NativeMethods.LoadCursor(nint.Zero, IDC_IBEAM);
            var hand = NativeMethods.LoadCursor(nint.Zero, IDC_HAND);
            var sizeAll = NativeMethods.LoadCursor(nint.Zero, IDC_SIZEALL);

            if (cur == arrow || cur == ibeam || cur == hand || cur == sizeAll)
                return false;

            return true;
        }
        catch
        {
            return true;
        }
    }

    private const int IDC_ARROW = 32512;
    private const int IDC_IBEAM = 32513;
    private const int IDC_HAND = 32649;
    private const int IDC_SIZEALL = 32646;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorInfo(ref CURSORINFO pci);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern nint LoadCursor(nint hInstance, int lpCursorName);
    }

    private void CloseShelf(ShelfWindow w)
    {
        try
        {
            if (w.DataContext is ShelfWindowViewModel vm)
            {
                vm.Pinned = false;
                vm.Items.Clear();
                vm.StackItems.Clear();
            }
        }
        catch
        {
        }

        try
        {
            w.Close();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_settings is not null)
            _settings.Changed -= RefreshTriggerModifier;

        _mouseHook.Dispose();

        foreach (var w in _openShelves.ToList())
        {
            try
            {
                w.Close();
            }
            catch
            {
            }
        }

        _openShelves.Clear();
    }

    private enum ShelfTriggerModifier
    {
        Ctrl,
        Alt,
        Shift,
        Disabled,
    }

    private static ShelfTriggerModifier ParseModifier(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ShelfTriggerModifier.Ctrl;

        if (string.Equals(raw, "Ctrl", StringComparison.OrdinalIgnoreCase))
            return ShelfTriggerModifier.Ctrl;
        if (string.Equals(raw, "Alt", StringComparison.OrdinalIgnoreCase))
            return ShelfTriggerModifier.Alt;
        if (string.Equals(raw, "Shift", StringComparison.OrdinalIgnoreCase))
            return ShelfTriggerModifier.Shift;
        if (string.Equals(raw, "Disabled", StringComparison.OrdinalIgnoreCase))
            return ShelfTriggerModifier.Disabled;

        return ShelfTriggerModifier.Ctrl;
    }

    private static bool IsModifierPressed(ShelfTriggerModifier modifier)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        try
        {
            return modifier switch
            {
                ShelfTriggerModifier.Ctrl => (NativeKeyState.GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0,
                ShelfTriggerModifier.Alt => (NativeKeyState.GetAsyncKeyState(VK_MENU) & 0x8000) != 0,
                ShelfTriggerModifier.Shift => (NativeKeyState.GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0,
                ShelfTriggerModifier.Disabled => false,
                _ => true,
            };
        }
        catch
        {
            return true;
        }
    }

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private static class NativeKeyState
    {
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
    }
}
