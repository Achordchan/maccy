using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using maccy.Views;

namespace maccy.Services;

public sealed class UpdateCoordinator
{
    private readonly UpdateService _service;
    private readonly Func<Window?> _getOwner;
    private readonly Action _shutdown;
    private readonly string _downloadFolder;
    private int _checking;

    public UpdateCoordinator(UpdateService service, Func<Window?> getOwner, Action shutdown)
    {
        _service = service;
        _getOwner = getOwner;
        _shutdown = shutdown;
        _downloadFolder = Path.Combine(AppPaths.AppDataRoot, "updates");
    }

    public async Task CheckAndPromptAsync(bool manual)
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
            return;

        try
        {
            var result = await _service.CheckAsync(CancellationToken.None);

            if (result.Status == UpdateStatus.UpToDate)
            {
                if (manual)
                    await ShowMessageAsync("检测更新", "当前已是最新版本。");
                return;
            }

            if (result.Status == UpdateStatus.Error)
            {
                if (manual)
                    await ShowMessageAsync("检测更新失败", result.ErrorMessage ?? "未知错误");
                return;
            }

            if (result.Update is null)
                return;

            var owner = _getOwner();
            if (owner is null)
                return;

            var action = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    if (owner.IsVisible)
                        owner.Activate();
                }
                catch
                {
                }

                var prompt = new UpdatePromptWindow(result.Update, result.Update.Mandatory);
                if (owner.IsVisible)
                {
                    prompt.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    prompt.Topmost = manual || result.Update.Mandatory;
                    return await prompt.ShowDialogAsync(owner);
                }

                prompt.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                prompt.Topmost = true;
                prompt.Show();
                prompt.Activate();
                return await prompt.WaitForResultAsync();
            });

            if (action == UpdatePromptResult.UpdateNow)
            {
                await DownloadAndInstallAsync(result.Update, owner);
                return;
            }

            if (result.Update.Mandatory)
                ShutdownApp();
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private async Task DownloadAndInstallAsync(UpdateInfo update, Window owner)
    {
        var progressWindow = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                if (owner.IsVisible)
                    owner.Activate();
            }
            catch
            {
            }

            var w = new UpdateProgressWindow();
            w.Topmost = true;
            if (owner.IsVisible)
            {
                w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                w.Show(owner);
            }
            else
            {
                w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                w.Show();
            }

            w.Activate();
            return w;
        });

        string installerPath;
        try
        {
            installerPath = await _service.DownloadInstallerAsync(
                update,
                _downloadFolder,
                (read, total) => progressWindow.UpdateProgress(read, total),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => progressWindow.Close());
            }
            catch
            {
            }

            await ShowMessageAsync("下载失败", ex.Message);
            return;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => progressWindow.Close());
        }
        catch
        {
        }

        try
        {
            StartInstallerAfterExit(installerPath);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("启动安装程序失败", ex.Message);
            return;
        }

        ShutdownApp();
    }

    private static void StartInstallerAfterExit(string installerPath)
    {
        var p = installerPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(p))
            throw new ArgumentException("installer path is empty", nameof(installerPath));

        // Avoid running the installer while our process still holds file locks (maccy.exe).
        // Spawn a detached shell that waits briefly, then starts the installer.
        var quoted = p.Replace("\"", "\"\"");
        var args = "/C ping 127.0.0.1 -n 3 >nul & start \"\" \"" + quoted + "\"";

        Process.Start(new ProcessStartInfo("cmd.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var owner = _getOwner();
        if (owner is null)
            return;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                if (owner.IsVisible)
                    owner.Activate();
            }
            catch
            {
            }

            var w = new MessageWindow(title, message);
            if (owner.IsVisible)
            {
                w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                w.Topmost = true;
                await w.ShowDialogAsync(owner);
                return;
            }

            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Topmost = true;
            w.Show();
            w.Activate();
            await w.WaitForCloseAsync();
        });
    }

    private void ShutdownApp()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _shutdown();
            }
            catch
            {
            }
        });
    }
}
