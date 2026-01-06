using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using DrawingIcon = System.Drawing.Icon;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;

namespace maccy.Services;

public sealed class FileIconService
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public Bitmap? GetLargeIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        lock (CacheLock)
        {
            if (Cache.TryGetValue(path, out var cached))
                return cached;
        }

        if (!OperatingSystem.IsWindows())
        {
            lock (CacheLock)
            {
                Cache[path] = null;
            }
            return null;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            lock (CacheLock)
            {
                Cache[path] = null;
            }
            return null;
        }

        try
        {
            var icon = GetShellIcon(path);
            if (icon is null)
                icon = DrawingIcon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                lock (CacheLock)
                {
                    Cache[path] = null;
                }
                return null;
            }

            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, DrawingImageFormat.Png);
            ms.Position = 0;

            var avaloniaBitmap = new Bitmap(ms);
            lock (CacheLock)
            {
                Cache[path] = avaloniaBitmap;
            }
            return avaloniaBitmap;
        }
        catch
        {
            lock (CacheLock)
            {
                Cache[path] = null;
            }
            return null;
        }
    }

    private static DrawingIcon? GetShellIcon(string path)
    {
        try
        {
            var attrs = Directory.Exists(path) ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
            var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON | NativeMethods.SHGFI_USEFILEATTRIBUTES;
            var result = NativeMethods.SHGetFileInfo(path, attrs, out var info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);
            if (result == IntPtr.Zero)
                return null;

            if (info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                using var tmp = DrawingIcon.FromHandle(info.hIcon);
                return (DrawingIcon)tmp.Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        public const uint SHGFI_ICON = 0x000000100;
        public const uint SHGFI_LARGEICON = 0x000000000;
        public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            out SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
