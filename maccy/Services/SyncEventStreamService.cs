using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace maccy.Services;

public sealed class SyncEventStreamService : IDisposable
{
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(15);

    private readonly AppSettingsService _settings;
    private readonly AuthService _authing;
    private readonly HttpClient _http;
    private readonly object _gate = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private string _connectionSignature = string.Empty;
    private Action? _onRemoteSyncUpdated;
    private int _eventDispatchQueued;
    private bool _disposed;

    public SyncEventStreamService(AppSettingsService settings)
    {
        _settings = settings;
        _authing = new AuthService();
        _http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public void Start(Action onRemoteSyncUpdated)
    {
        _onRemoteSyncUpdated = onRemoteSyncUpdated;
        _settings.Changed += OnSettingsChanged;
        RefreshConnection();
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCurrentLoopNoLock();
            _connectionSignature = string.Empty;
        }
    }

    private void OnSettingsChanged()
    {
        if (_disposed)
            return;

        RefreshConnection();
    }

    private void RefreshConnection()
    {
        var nextSignature = BuildConnectionSignature(_settings.Current);

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(nextSignature))
            {
                StopCurrentLoopNoLock();
                _connectionSignature = string.Empty;
                return;
            }

            if (string.Equals(_connectionSignature, nextSignature, StringComparison.Ordinal)
                && _loopTask is not null
                && !_loopTask.IsCompleted)
            {
                return;
            }

            StopCurrentLoopNoLock();

            _connectionSignature = nextSignature;
            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var delay = InitialReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var baseUrl = ServerDefaults.OfficialSyncBaseUrl;

                var accessToken = await EnsureAccessTokenAsync(ct);
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    await Task.Delay(delay, ct);
                    continue;
                }

                await ListenOnceAsync(baseUrl, accessToken, ct);
                delay = InitialReconnectDelay;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                var nextMs = Math.Min(delay.TotalMilliseconds * 2, MaxReconnectDelay.TotalMilliseconds);
                delay = TimeSpan.FromMilliseconds(nextMs);
            }
        }
    }

    private async Task ListenOnceAsync(string baseUrl, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, NormalizeBaseUrl(baseUrl) + "/sync/events");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            if (await TryRefreshAndPersistAsync(ct))
                return;
            throw new InvalidOperationException("sync events unauthorized");
        }

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException("sync events failed: " + (int)resp.StatusCode);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? eventName = null;
        var dataBuilder = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                DispatchEvent(eventName, dataBuilder.ToString());
                eventName = null;
                dataBuilder.Clear();
                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (dataBuilder.Length > 0)
                    dataBuilder.Append('\n');
                dataBuilder.Append(line[5..].TrimStart());
            }
        }
    }

    private void DispatchEvent(string? eventName, string data)
    {
        if (!string.Equals(eventName, "sync-updated", StringComparison.Ordinal))
            return;

        if (Interlocked.Exchange(ref _eventDispatchQueued, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250);
                _onRemoteSyncUpdated?.Invoke();
            }
            finally
            {
                Interlocked.Exchange(ref _eventDispatchQueued, 0);
            }
        });
    }

    private async Task<string?> EnsureAccessTokenAsync(CancellationToken ct)
    {
        var current = _settings.Current;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var access = (current.AuthAccessToken ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(access) && current.AuthExpiresAtUnixMs > nowMs + 30_000)
            return access;

        return await TryRefreshAndPersistAsync(ct) ? _settings.Current.AuthAccessToken : null;
    }

    private async Task<bool> TryRefreshAndPersistAsync(CancellationToken ct)
    {
        var current = _settings.Current;
        var refresh = (current.AuthRefreshToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(refresh))
            return false;

        try
        {
            var token = await _authing.RefreshAsync(ServerDefaults.OfficialSyncBaseUrl, refresh, ct);
            _settings.Update(s =>
            {
                s.AuthAccessToken = token.AccessToken;
                s.AuthRefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? current.AuthRefreshToken : token.RefreshToken;
                s.AuthIdToken = string.IsNullOrWhiteSpace(token.IdToken) ? current.AuthIdToken : token.IdToken;
                s.AuthExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
                s.AuthUserEmail = string.IsNullOrWhiteSpace(token.Email) ? current.AuthUserEmail : token.Email;
                s.NasAgentBaseUrl = ServerDefaults.OfficialSyncBaseUrl;
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _settings.ClearAuthSession();
            return false;
        }
    }

    private static string BuildConnectionSignature(AppSettings settings)
    {
        var access = (settings.AuthAccessToken ?? string.Empty).Trim();
        var refresh = (settings.AuthRefreshToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(access) && string.IsNullOrWhiteSpace(refresh))
            return string.Empty;

        return string.Join(
            "|",
            ServerDefaults.OfficialSyncBaseUrl,
            access,
            refresh,
            settings.AuthExpiresAtUnixMs.ToString());
    }

    private static string NormalizeBaseUrl(string value)
    {
        return value.Trim().TrimEnd('/');
    }

    private void StopCurrentLoopNoLock()
    {
        try
        {
            _loopCts?.Cancel();
        }
        catch
        {
        }

        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        Stop();
        _http.Dispose();
    }
}
