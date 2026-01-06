using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace maccy.Services;

public sealed class WindowsMouseHookService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;

    private nint _hook;
    private HookProc? _proc;

    public event Action? LeftButtonPressed;
    public event Action? LeftButtonReleased;
    public event Action<int, int>? MouseMoved;

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (_hook != nint.Zero)
            return;

        _proc = HookCallback;

        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = curModule is null ? nint.Zero : NativeMethods.GetModuleHandle(curModule.ModuleName);

        _hook = NativeMethods.SetWindowsHookEx(WH_MOUSE_LL, _proc, moduleHandle, 0);
    }

    public void Stop()
    {
        if (_hook == nint.Zero)
            return;

        try
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
        }
        catch
        {
        }

        _hook = nint.Zero;
        _proc = null;
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            if (msg == WM_MOUSEMOVE)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var x = hookStruct.pt.x;
                var y = hookStruct.pt.y;
                Dispatcher.UIThread.Post(() => MouseMoved?.Invoke(x, y));
            }
            else if (msg == WM_LBUTTONDOWN)
            {
                Dispatcher.UIThread.Post(() => LeftButtonPressed?.Invoke());
            }
            if (msg == WM_LBUTTONUP)
            {
                Dispatcher.UIThread.Post(() => LeftButtonReleased?.Invoke());
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
    }

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(nint hhk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern nint GetModuleHandle(string? lpModuleName);
    }
}
