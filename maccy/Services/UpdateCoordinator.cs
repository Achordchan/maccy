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
    private int _pendingManualCheck;
    private UpdateCheckWindow? _manualCheckWindow;

    public UpdateCoordinator(UpdateService service, Func<Window?> getOwner, Action shutdown)
    {
        _service = service;
        _getOwner = getOwner;
        _shutdown = shutdown;
        _downloadFolder = Path.Combine(AppPaths.AppDataRoot, "updates");
    }

    public async Task CheckAndPromptAsync(bool manual)
    {
        if (Interlocked.CompareExchange(ref _checking, 1, 0) == 1)
        {
            if (manual)
            {
                Interlocked.Exchange(ref _pendingManualCheck, 1);
                await ShowOrUpdateManualCheckWindowAsync("已有检查正在进行，稍后继续...");
            }
            return;
        }

        try
        {
            if (manual)
                await ShowOrUpdateManualCheckWindowAsync("正在连接更新服务器，请稍候...");

            var runManual = manual;
            while (true)
            {
                if (runManual)
                    await ShowOrUpdateManualCheckWindowAsync("正在检查是否有新版本...");

                var result = await _service.CheckAsync(
                    CancellationToken.None,
                    runManual ? UpdateManualCheckStatus : null);

                if (result.Status == UpdateStatus.UpToDate)
                {
                    if (runManual)
                        await CloseManualCheckWindowAsync();

                    if (runManual)
                        await ShowMessageAsync("检查更新", "当前已经是最新版本。");

                    if (!ConsumePendingManualCheck())
                        return;

                    runManual = true;
                    continue;
                }

                if (result.Status == UpdateStatus.Error)
                {
                    if (runManual)
                        await CloseManualCheckWindowAsync();

                    if (runManual)
                        await ShowMessageAsync("检查更新失败", result.ErrorMessage ?? "未知错误");

                    if (!ConsumePendingManualCheck())
                        return;

                    runManual = true;
                    continue;
                }

                if (result.Update is null)
                {
                    if (runManual)
                        await CloseManualCheckWindowAsync();

                    if (!ConsumePendingManualCheck())
                        return;

                    runManual = true;
                    continue;
                }

                var owner = _getOwner();
                if (owner is null)
                {
                    await CloseManualCheckWindowAsync();
                    return;
                }

                if (runManual)
                    await CloseManualCheckWindowAsync();

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
                        prompt.Topmost = runManual || result.Update.Mandatory;
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
                {
                    ShutdownApp();
                    return;
                }

                if (!ConsumePendingManualCheck())
                    return;

                runManual = true;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
            await CloseManualCheckWindowAsync();
        }
    }

    private bool ConsumePendingManualCheck()
    {
        return Interlocked.Exchange(ref _pendingManualCheck, 0) == 1;
    }

    private async Task ShowOrUpdateManualCheckWindowAsync(string status)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var owner = _getOwner();

            if (_manualCheckWindow is null)
            {
                _manualCheckWindow = new UpdateCheckWindow();
                _manualCheckWindow.Closed += (_, _) =>
                {
                    _manualCheckWindow = null;
                };

                if (owner is not null && owner.IsVisible)
                {
                    _manualCheckWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    _manualCheckWindow.Show(owner);
                }
                else
                {
                    _manualCheckWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    _manualCheckWindow.Show();
                }
            }

            _manualCheckWindow.Topmost = true;
            _manualCheckWindow.SetStatus(status);
            _manualCheckWindow.Activate();
        });
    }

    private void UpdateManualCheckStatus(string status)
    {
        _ = ShowOrUpdateManualCheckWindowAsync(status);
    }

    private async Task CloseManualCheckWindowAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_manualCheckWindow is null)
                return;

            try
            {
                _manualCheckWindow.Close();
            }
            catch
            {
            }

            _manualCheckWindow = null;
        });
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

        var pid = Environment.ProcessId;
        var installer = p.Replace("'", "''");

        var script =
            "Start-Sleep -Milliseconds 200; " +
            "$p = Get-Process -Id " + pid + " -ErrorAction SilentlyContinue; " +
            "if ($p) { try { $p.WaitForExit() } catch {} }; " +
            "Start-Process -FilePath '" + installer + "'";

        var args = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"" + script + "\"";

        Process.Start(new ProcessStartInfo("powershell.exe", args)
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
