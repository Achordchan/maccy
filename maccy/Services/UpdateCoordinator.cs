using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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
    private readonly string _updateRoot;
    private UpdateInfo? _availableUpdate;
    private int _checking;
    private int _applying;
    private int _pendingManualCheck;
    private UpdateCheckWindow? _manualCheckWindow;

    public event Action<UpdateInfo?>? AvailabilityChanged;
    public event Action<bool>? ApplyingChanged;

    public UpdateCoordinator(UpdateService service, Func<Window?> getOwner, Action shutdown)
    {
        _service = service;
        _getOwner = getOwner;
        _shutdown = shutdown;
        _updateRoot = Path.Combine(AppPaths.AppDataRoot, "updates");
    }

    public async Task RefreshAvailabilityAsync()
    {
        if (Volatile.Read(ref _applying) == 1)
            return;

        if (Interlocked.CompareExchange(ref _checking, 1, 0) == 1)
            return;

        try
        {
            var result = await _service.CheckAsync(CancellationToken.None);
            if (result.Status == UpdateStatus.UpdateAvailable && result.Update is not null)
            {
                SetAvailableUpdate(result.Update);
                return;
            }

            if (result.Status == UpdateStatus.UpToDate)
                SetAvailableUpdate(null);
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    public async Task ApplyAvailableUpdateAsync()
    {
        if (Interlocked.CompareExchange(ref _applying, 1, 0) == 1)
            return;

        SetApplying(true);
        try
        {
            var update = _availableUpdate;
            if (update is null)
            {
                if (Interlocked.CompareExchange(ref _checking, 1, 0) == 1)
                {
                    await ShowOrUpdateManualCheckWindowAsync("正在检查更新，请稍后...");
                    return;
                }

                UpdateCheckResult result;
                try
                {
                    await ShowOrUpdateManualCheckWindowAsync("正在检查是否有新版本...");
                    result = await _service.CheckAsync(CancellationToken.None, UpdateManualCheckStatus);
                }
                finally
                {
                    Interlocked.Exchange(ref _checking, 0);
                    await CloseManualCheckWindowAsync();
                }

                if (result.Status == UpdateStatus.UpToDate)
                {
                    SetAvailableUpdate(null);
                    await ShowMessageAsync("检查更新", "当前已经是最新版本。");
                    return;
                }

                if (result.Status == UpdateStatus.Error || result.Update is null)
                {
                    await ShowMessageAsync("检查更新失败", result.ErrorMessage ?? "未知错误");
                    return;
                }

                update = result.Update;
                SetAvailableUpdate(update);
            }

            var owner = _getOwner();
            if (owner is null)
                return;

            await DownloadAndApplyUpdateAsync(update, owner);
        }
        finally
        {
            Interlocked.Exchange(ref _applying, 0);
            SetApplying(false);
            await CloseManualCheckWindowAsync();
        }
    }

    public async Task CheckAndPromptAsync(bool manual)
    {
        if (Volatile.Read(ref _applying) == 1)
        {
            if (manual)
                await ShowMessageAsync("更新", "更新正在进行中，请稍后...");
            return;
        }

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
                    SetAvailableUpdate(null);

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

                SetAvailableUpdate(result.Update);

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
                    await DownloadAndApplyUpdateAsync(result.Update, owner);
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

    private void SetAvailableUpdate(UpdateInfo? update)
    {
        _availableUpdate = update;
        AvailabilityChanged?.Invoke(update);
    }

    private void SetApplying(bool applying)
    {
        ApplyingChanged?.Invoke(applying);
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

    private async Task DownloadAndApplyUpdateAsync(UpdateInfo update, Window owner)
    {
        if (update.HasSupportedPackage)
        {
            var packageStarted = await TryDownloadAndApplyPackageAsync(update, owner);
            if (packageStarted)
                return;
        }

        await DownloadAndInstallAsync(update, owner);
    }

    private async Task<bool> TryDownloadAndApplyPackageAsync(UpdateInfo update, Window owner)
    {
        var progressWindow = await ShowProgressWindowAsync(owner);
        PreparedPackageUpdate prepared;
        try
        {
            prepared = await _service.DownloadPackageAsync(
                update,
                _updateRoot,
                (read, total) => progressWindow.UpdateProgress(read, total),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await CloseProgressWindowAsync(progressWindow);

            if (string.IsNullOrWhiteSpace(update.InstallerUrl))
            {
                await ShowMessageAsync("轻量更新失败", ex.Message);
                return true;
            }

            await ShowMessageAsync("轻量更新失败", ex.Message + "\n\n将改用完整安装包更新。");
            return false;
        }

        await CloseProgressWindowAsync(progressWindow);

        try
        {
            StartPackageUpdateAfterExit(prepared);
        }
        catch (Exception ex)
        {
            if (string.IsNullOrWhiteSpace(update.InstallerUrl))
            {
                await ShowMessageAsync("启动轻量更新失败", ex.Message);
                return true;
            }

            await ShowMessageAsync("启动轻量更新失败", ex.Message + "\n\n将改用完整安装包更新。");
            return false;
        }

        ShutdownApp();
        return true;
    }

    private async Task DownloadAndInstallAsync(UpdateInfo update, Window owner)
    {
        if (string.IsNullOrWhiteSpace(update.InstallerUrl))
        {
            await ShowMessageAsync("更新失败", "没有可用的安装包下载地址。");
            return;
        }

        var progressWindow = await ShowProgressWindowAsync(owner);

        string installerPath;
        try
        {
            installerPath = await _service.DownloadInstallerAsync(
                update,
                _updateRoot,
                (read, total) => progressWindow.UpdateProgress(read, total),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await CloseProgressWindowAsync(progressWindow);
            await ShowMessageAsync("下载失败", ex.Message);
            return;
        }

        await CloseProgressWindowAsync(progressWindow);

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

    private async Task<UpdateProgressWindow> ShowProgressWindowAsync(Window owner)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
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
    }

    private static async Task CloseProgressWindowAsync(UpdateProgressWindow progressWindow)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => progressWindow.Close());
        }
        catch
        {
        }
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

    private void StartPackageUpdateAfterExit(PreparedPackageUpdate update)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            throw new InvalidOperationException("current executable path is unavailable");

        var installDir = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(installDir))
            throw new InvalidOperationException("install directory is unavailable");

        Directory.CreateDirectory(_updateRoot);
        var backupDir = Path.Combine(
            _updateRoot,
            "backups",
            update.Version + "-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var installedManifest = Path.Combine(_updateRoot, "installed-package-files.json");
        var logPath = Path.Combine(_updateRoot, "last_light_update.log");
        var scriptPath = Path.Combine(_updateRoot, "apply-light-update-" + update.Version + ".ps1");

        File.WriteAllText(
            scriptPath,
            BuildPackageUpdateScript(
                Environment.ProcessId,
                installDir,
                update.StagingPath,
                update.PackageManifestPath,
                installedManifest,
                backupDir,
                logPath),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Process.Start(new ProcessStartInfo(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + QuoteProcessArgument(scriptPath))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static string BuildPackageUpdateScript(
        int processId,
        string installDir,
        string stagingPath,
        string packageManifestPath,
        string installedManifestPath,
        string backupDir,
        string logPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$processIdToWait = " + processId.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("$installDir = " + PsQuote(installDir));
        sb.AppendLine("$stagingDir = " + PsQuote(stagingPath));
        sb.AppendLine("$packageManifest = " + PsQuote(packageManifestPath));
        sb.AppendLine("$installedManifest = " + PsQuote(installedManifestPath));
        sb.AppendLine("$backupDir = " + PsQuote(backupDir));
        sb.AppendLine("$logPath = " + PsQuote(logPath));
        sb.AppendLine("$protectedRoots = @('images','files','blobs','sync','updates')");
        sb.AppendLine("$protectedFiles = @('settings.json','history.json','preferences_last_error.txt','sync_last_error.txt','auth_last_error.txt','capture_debug.log')");
        sb.AppendLine("function Write-UpdateLog([string]$message) { New-Item -ItemType Directory -Force -Path (Split-Path -Parent $logPath) | Out-Null; Add-Content -LiteralPath $logPath -Encoding UTF8 -Value ('[{0}] {1}' -f (Get-Date).ToString('s'), $message) }");
        sb.AppendLine("function Normalize-Rel([string]$path) { if ([string]::IsNullOrWhiteSpace($path)) { throw 'empty package path' }; $p = $path.Trim().Replace('/', [IO.Path]::DirectorySeparatorChar).Replace('\\', [IO.Path]::DirectorySeparatorChar); if ([IO.Path]::IsPathRooted($p)) { throw ('rooted package path: ' + $path) }; $p = $p.TrimStart([IO.Path]::DirectorySeparatorChar); $parts = $p.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries); if ($parts.Length -eq 0) { throw ('empty package path: ' + $path) }; foreach ($part in $parts) { if ($part -eq '.' -or $part -eq '..') { throw ('unsafe package path: ' + $path) } }; return [IO.Path]::Combine([string[]]$parts) }");
        sb.AppendLine("function To-Key([string]$path) { return $path.Replace('\\','/').TrimStart('/').ToLowerInvariant() }");
        sb.AppendLine("function Is-ProtectedRel([string]$path) { $k = To-Key $path; if ($protectedFiles -contains $k) { return $true }; foreach ($root in $protectedRoots) { if ($k -eq $root -or $k.StartsWith($root + '/')) { return $true } }; return $false }");
        sb.AppendLine("function Join-Under([string]$root, [string]$rel) { $fullRoot = [IO.Path]::GetFullPath($root); $full = [IO.Path]::GetFullPath([IO.Path]::Combine($fullRoot, $rel)); $prefix = $fullRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar; if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw ('path escapes root: ' + $rel) }; return $full }");
        sb.AppendLine("function File-Sha256([string]$path) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToUpperInvariant() }");
        sb.AppendLine("$rollback = New-Object System.Collections.Generic.List[object]");
        sb.AppendLine("try {");
        sb.AppendLine("  if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue }");
        sb.AppendLine("  Write-UpdateLog 'waiting for app exit'");
        sb.AppendLine("  $p = Get-Process -Id $processIdToWait -ErrorAction SilentlyContinue");
        sb.AppendLine("  if ($p) { try { $p.WaitForExit() } catch {} }");
        sb.AppendLine("  Start-Sleep -Milliseconds 300");
        sb.AppendLine("  $manifest = Get-Content -LiteralPath $packageManifest -Raw -Encoding UTF8 | ConvertFrom-Json");
        sb.AppendLine("  if (-not $manifest.files -or $manifest.files.Count -lt 1) { throw 'package file list is empty' }");
        sb.AppendLine("  $newFiles = @{}");
        sb.AppendLine("  foreach ($file in $manifest.files) {");
        sb.AppendLine("    $rel = Normalize-Rel ([string]$file.path)");
        sb.AppendLine("    if (Is-ProtectedRel $rel) { throw ('protected path in package: ' + $file.path) }");
        sb.AppendLine("    $src = Join-Under $stagingDir $rel");
        sb.AppendLine("    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { throw ('package file missing: ' + $file.path) }");
        sb.AppendLine("    $expected = ([string]$file.sha256).Trim().ToUpperInvariant()");
        sb.AppendLine("    if ([string]::IsNullOrWhiteSpace($expected)) { throw ('missing sha256: ' + $file.path) }");
        sb.AppendLine("    if ((File-Sha256 $src) -ne $expected) { throw ('sha256 mismatch before copy: ' + $file.path) }");
        sb.AppendLine("    $newFiles[(To-Key $rel)] = $true");
        sb.AppendLine("  }");
        sb.AppendLine("  if (-not $newFiles.ContainsKey('maccy.exe')) { throw 'package missing maccy.exe' }");
        sb.AppendLine("  New-Item -ItemType Directory -Force -Path $backupDir | Out-Null");
        sb.AppendLine("  foreach ($file in $manifest.files) {");
        sb.AppendLine("    $rel = Normalize-Rel ([string]$file.path)");
        sb.AppendLine("    $src = Join-Under $stagingDir $rel");
        sb.AppendLine("    $dest = Join-Under $installDir $rel");
        sb.AppendLine("    $backup = Join-Under $backupDir $rel");
        sb.AppendLine("    $existed = Test-Path -LiteralPath $dest -PathType Leaf");
        sb.AppendLine("    if ($existed) { New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null; Copy-Item -LiteralPath $dest -Destination $backup -Force }");
        sb.AppendLine("    $rollback.Add([pscustomobject]@{ Path = $dest; Backup = $backup; Existed = $existed })");
        sb.AppendLine("    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null");
        sb.AppendLine("    Copy-Item -LiteralPath $src -Destination $dest -Force");
        sb.AppendLine("  }");
        sb.AppendLine("  foreach ($file in $manifest.files) {");
        sb.AppendLine("    $rel = Normalize-Rel ([string]$file.path)");
        sb.AppendLine("    $dest = Join-Under $installDir $rel");
        sb.AppendLine("    $expected = ([string]$file.sha256).Trim().ToUpperInvariant()");
        sb.AppendLine("    if ((File-Sha256 $dest) -ne $expected) { throw ('sha256 mismatch after copy: ' + $file.path) }");
        sb.AppendLine("  }");
        sb.AppendLine("  if (Test-Path -LiteralPath $installedManifest -PathType Leaf) {");
        sb.AppendLine("    $previous = Get-Content -LiteralPath $installedManifest -Raw -Encoding UTF8 | ConvertFrom-Json");
        sb.AppendLine("    foreach ($old in $previous.files) {");
        sb.AppendLine("      $oldRel = Normalize-Rel ([string]$old.path)");
        sb.AppendLine("      $oldKey = To-Key $oldRel");
        sb.AppendLine("      if ((-not $newFiles.ContainsKey($oldKey)) -and (-not (Is-ProtectedRel $oldRel))) {");
        sb.AppendLine("        $oldDest = Join-Under $installDir $oldRel");
        sb.AppendLine("        if (Test-Path -LiteralPath $oldDest -PathType Leaf) {");
        sb.AppendLine("          $oldBackup = Join-Under $backupDir ('_deleted' + [IO.Path]::DirectorySeparatorChar + $oldRel)");
        sb.AppendLine("          New-Item -ItemType Directory -Force -Path (Split-Path -Parent $oldBackup) | Out-Null");
        sb.AppendLine("          Copy-Item -LiteralPath $oldDest -Destination $oldBackup -Force");
        sb.AppendLine("          $rollback.Add([pscustomobject]@{ Path = $oldDest; Backup = $oldBackup; Existed = $true })");
        sb.AppendLine("          Remove-Item -LiteralPath $oldDest -Force");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $installedManifest) | Out-Null");
        sb.AppendLine("  Copy-Item -LiteralPath $packageManifest -Destination $installedManifest -Force");
        sb.AppendLine("  Write-UpdateLog 'light update applied'");
        sb.AppendLine("  Start-Process -FilePath (Join-Path $installDir 'maccy.exe')");
        sb.AppendLine("} catch {");
        sb.AppendLine("  $err = $_.Exception.ToString()");
        sb.AppendLine("  Write-UpdateLog ('failed: ' + $err)");
        sb.AppendLine("  for ($i = $rollback.Count - 1; $i -ge 0; $i--) {");
        sb.AppendLine("    $item = $rollback[$i]");
        sb.AppendLine("    try {");
        sb.AppendLine("      if ($item.Existed) { New-Item -ItemType Directory -Force -Path (Split-Path -Parent $item.Path) | Out-Null; Copy-Item -LiteralPath $item.Backup -Destination $item.Path -Force }");
        sb.AppendLine("      elseif (Test-Path -LiteralPath $item.Path -PathType Leaf) { Remove-Item -LiteralPath $item.Path -Force }");
        sb.AppendLine("    } catch { Write-UpdateLog ('rollback failed: ' + $_.Exception.ToString()) }");
        sb.AppendLine("  }");
        sb.AppendLine("  try {");
        sb.AppendLine("    $ws = New-Object -ComObject WScript.Shell");
        sb.AppendLine("    [void]$ws.Popup(('轻量更新失败，已尝试回滚。日志：' + $logPath), 0, 'Maccy 更新失败', 16)");
        sb.AppendLine("  } catch {}");
        sb.AppendLine("  $exe = Join-Path $installDir 'maccy.exe'");
        sb.AppendLine("  if (Test-Path -LiteralPath $exe -PathType Leaf) { try { Start-Process -FilePath $exe } catch {} }");
        sb.AppendLine("  exit 1");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string QuoteProcessArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string PsQuote(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
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
