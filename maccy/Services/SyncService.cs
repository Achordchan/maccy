using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace maccy.Services;

public sealed class SyncService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly ClipboardHistoryService _history;
    private readonly AppSettingsService _settings;
    private readonly ClipboardPersistenceService _persistence;

    private readonly SnapshotSqliteService _snapshot;
    private readonly AuthingOidcService _authing;

    public SyncService(ClipboardHistoryService history, AppSettingsService settings, ClipboardPersistenceService persistence)
    {
        _history = history;
        _settings = settings;
        _persistence = persistence;
        _snapshot = new SnapshotSqliteService();
        _authing = new AuthingOidcService();
    }

    public async Task UploadAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var (baseUrl, token) = await GetConnectionAsync(ct);
            var client = new NasAgentClient(baseUrl);

            await _persistence.FlushAsync(ct);

            var dir = Path.Combine(AppPaths.AppDataRoot, "sync", "tmp");
            Directory.CreateDirectory(dir);
            var snapshotPath = Path.Combine(dir, "snapshot-" + Guid.NewGuid().ToString("N") + ".sqlite");

            try
            {
                await _snapshot.ExportAsync(snapshotPath, ct);
                try
                {
                    await client.UploadSnapshotAsync(token, snapshotPath, ct);
                }
                catch (NasAgentApiException ex)
                {
                    if (!IsAuthError(ex))
                        throw;
                    if (!await TryRefreshAndPersistAsync(ct))
                        throw;
                    var token2 = SelectBearerToken(_settings.Current);
                    await client.UploadSnapshotAsync(token2, snapshotPath, ct);
                }
            }
            finally
            {
                TryDeleteWithRetry(snapshotPath);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task DownloadAndApplyAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var (baseUrl, token) = await GetConnectionAsync(ct);
            var client = new NasAgentClient(baseUrl);

            var dir = Path.Combine(AppPaths.AppDataRoot, "sync", "tmp");
            Directory.CreateDirectory(dir);
            var snapshotPath = Path.Combine(dir, "snapshot-" + Guid.NewGuid().ToString("N") + ".sqlite");

            try
            {
                try
                {
                    await client.DownloadSnapshotAsync(token, snapshotPath, ct);
                }
                catch (NasAgentApiException ex)
                {
                    if (!IsAuthError(ex))
                        throw;
                    if (!await TryRefreshAndPersistAsync(ct))
                        throw;
                    var token2 = SelectBearerToken(_settings.Current);
                    await client.DownloadSnapshotAsync(token2, snapshotPath, ct);
                }
                await _snapshot.ImportAsync(snapshotPath, _history, _settings, ct);
                await _persistence.FlushAsync(ct);
            }
            finally
            {
                TryDeleteWithRetry(snapshotPath);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<string?> GetManifestJsonAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var (baseUrl, token) = await GetConnectionAsync(ct);
            var client = new NasAgentClient(baseUrl);
            try
            {
                return await client.GetManifestJsonAsync(token, ct);
            }
            catch (NasAgentApiException ex)
            {
                if (!IsAuthError(ex))
                    throw;
                if (!await TryRefreshAndPersistAsync(ct))
                    throw;
                var token2 = SelectBearerToken(_settings.Current);
                return await client.GetManifestJsonAsync(token2, ct);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<(string BaseUrl, string Token)> GetConnectionAsync(CancellationToken ct)
    {
        var s = _settings.Current;

        var baseUrl = (s.NasAgentBaseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("missing NAS base url");

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (s.AuthExpiresAtUnixMs <= nowMs + 60_000)
            await TryRefreshAndPersistAsync(ct);

        s = _settings.Current;
        var token = SelectBearerToken(s);
        if (string.IsNullOrWhiteSpace(token) || s.AuthExpiresAtUnixMs <= nowMs + 30_000)
            throw new InvalidOperationException("not logged in");

        return (baseUrl, token);
    }

    private static bool IsAuthError(NasAgentApiException ex)
    {
        return ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden;
    }

    private static string SelectBearerToken(AppSettings s)
    {
        var access = (s.AuthAccessToken ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(access))
            return access;
        return string.Empty;
    }

    private async Task<bool> TryRefreshAndPersistAsync(CancellationToken ct)
    {
        var current = _settings.Current;
        var refresh = (current.AuthRefreshToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(refresh))
            return false;

        try
        {
            var token = await _authing.RefreshAsync(refresh, ct);
            _settings.Update(s =>
            {
                s.AuthAccessToken = token.AccessToken;
                s.AuthRefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? current.AuthRefreshToken : token.RefreshToken;
                s.AuthIdToken = string.IsNullOrWhiteSpace(token.IdToken) ? current.AuthIdToken : token.IdToken;
                s.AuthExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsJwtLike(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        var first = token.IndexOf('.');
        if (first <= 0)
            return false;
        var second = token.IndexOf('.', first + 1);
        return second > first + 1;
    }

    private static void TryDeleteWithRetry(string path)
    {
        for (var i = 0; i < 6; i++)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                File.Delete(path);
                return;
            }
            catch
            {
                try
                {
                    Thread.Sleep(120);
                }
                catch
                {
                }
            }
        }
    }
}
