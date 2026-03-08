using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace maccy.Services;

public sealed class SyncService
{
    private const string SubscriptionExpiredError = "subscription expired";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly ClipboardHistoryService _history;
    private readonly AppSettingsService _settings;
    private readonly ClipboardPersistenceService _persistence;
    private readonly SnapshotSqliteService _snapshot;
    private readonly AuthService _authing;

    public SyncService(ClipboardHistoryService history, AppSettingsService settings, ClipboardPersistenceService persistence)
    {
        _history = history;
        _settings = settings;
        _persistence = persistence;
        _snapshot = new SnapshotSqliteService();
        _authing = new AuthService();
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
                    if (IsSubscriptionExpired(ex))
                        throw new InvalidOperationException(SubscriptionExpiredError);
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
                    if (IsSubscriptionExpired(ex))
                        throw new InvalidOperationException(SubscriptionExpiredError);
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
                if (IsSubscriptionExpired(ex))
                    throw new InvalidOperationException(SubscriptionExpiredError);
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

    public async Task<SyncDirection> DecideDirectionAsync(CancellationToken ct)
    {
        var manifestJson = await GetManifestJsonAsync(ct);
        var localLatest = GetLocalLatestCapturedAt();
        var remoteUpdatedAt = TryReadRemoteUpdatedAt(manifestJson);
        return DecideDirection(localLatest, remoteUpdatedAt);
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
        return ex.StatusCode == HttpStatusCode.Unauthorized;
    }

    private static bool IsSubscriptionExpired(NasAgentApiException ex)
    {
        if (ex.StatusCode != HttpStatusCode.Forbidden)
            return false;

        var body = ex.ResponseBody ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        return body.Contains("\u8ba2\u9605\u5df2\u8fc7\u671f", StringComparison.OrdinalIgnoreCase)
            || (body.Contains("subscription", StringComparison.OrdinalIgnoreCase)
                && body.Contains("expired", StringComparison.OrdinalIgnoreCase));
    }

    private static string SelectBearerToken(AppSettings s)
    {
        var access = (s.AuthAccessToken ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(access))
            return access;
        return string.Empty;
    }

    private DateTimeOffset? GetLocalLatestCapturedAt()
    {
        try
        {
            if (_history.Items.Count == 0)
                return null;
            return _history.Items.Max(x => x.CapturedAt);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadRemoteUpdatedAt(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (!doc.RootElement.TryGetProperty("latest", out var latest))
                return null;
            if (!latest.TryGetProperty("updatedAt", out var updatedAt))
                return null;
            if (updatedAt.ValueKind != JsonValueKind.String)
                return null;
            if (DateTimeOffset.TryParse(updatedAt.GetString(), out var parsed))
                return parsed;
        }
        catch
        {
        }

        return null;
    }

    private static SyncDirection DecideDirection(DateTimeOffset? localLatest, DateTimeOffset? remoteUpdatedAt)
    {
        if (remoteUpdatedAt is null)
            return localLatest is null ? SyncDirection.None : SyncDirection.Upload;
        if (localLatest is null)
            return SyncDirection.Download;

        var localUtc = localLatest.Value.UtcDateTime;
        var remoteUtc = remoteUpdatedAt.Value.UtcDateTime;
        var diff = localUtc - remoteUtc;
        if (Math.Abs(diff.TotalSeconds) <= 2)
            return SyncDirection.None;
        return diff.TotalSeconds > 0 ? SyncDirection.Upload : SyncDirection.Download;
    }

    private async Task<bool> TryRefreshAndPersistAsync(CancellationToken ct)
    {
        var current = _settings.Current;
        var refresh = (current.AuthRefreshToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(refresh))
            return false;

        try
        {
            var token = await _authing.RefreshAsync(_settings.Current.NasAgentBaseUrl, refresh, ct);
            _settings.Update(s =>
            {
                s.AuthAccessToken = token.AccessToken;
                s.AuthRefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? current.AuthRefreshToken : token.RefreshToken;
                s.AuthIdToken = string.IsNullOrWhiteSpace(token.IdToken) ? current.AuthIdToken : token.IdToken;
                s.AuthExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
                s.AuthUserEmail = string.IsNullOrWhiteSpace(token.Email) ? current.AuthUserEmail : token.Email;
            });
            return true;
        }
        catch
        {
            return false;
        }
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

    public enum SyncDirection
    {
        None,
        Upload,
        Download,
    }
}
