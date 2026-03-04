using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace maccy.Services;

public sealed class NasAgentApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public string? ResponseBody { get; }

    public NasAgentApiException(HttpStatusCode statusCode, string message, string? responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}

public sealed record SubscriptionStatus(bool Subscribed, string? ExpiresAt);

public sealed record RedeemResult(bool Success, string? ExpiresAt, string? Error);

public sealed class NasAgentClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public NasAgentClient(string baseUrl, HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
    }

    public async Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));

        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/health");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
            return false;

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
        if (!doc.RootElement.TryGetProperty("ok", out var ok))
            return false;

        return ok.ValueKind == JsonValueKind.True;
    }

    public async Task<SubscriptionStatus?> GetSubscriptionStatusAsync(string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return null;
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/subscription/status");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

        var subscribed = doc.RootElement.TryGetProperty("subscribed", out var subEl) && subEl.ValueKind == JsonValueKind.True;
        var expiresAt = doc.RootElement.TryGetProperty("expiresAt", out var expEl) ? expEl.GetString() : null;

        return new SubscriptionStatus(subscribed, expiresAt);
    }

    public async Task<RedeemResult?> RedeemCardAsync(string accessToken, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return null;
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;
        if (string.IsNullOrWhiteSpace(code))
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { code });
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/card/redeem")
        {
            Content = content
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(resp, cts.Token);
            return new RedeemResult(false, null, body);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

        var success = doc.RootElement.TryGetProperty("success", out var sucEl) && sucEl.ValueKind == JsonValueKind.True;
        var expiresAt = doc.RootElement.TryGetProperty("expiresAt", out var expEl) ? expEl.GetString() : null;

        return new RedeemResult(success, expiresAt, null);
    }

    public async Task<string?> GetManifestJsonAsync(string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return null;
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/sync/manifest");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
            return null;

        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    public async Task UploadSnapshotAsync(string accessToken, string snapshotPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new InvalidOperationException("missing base url");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("missing access token");
        if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath))
            throw new FileNotFoundException("snapshot not found", snapshotPath);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        await using var fs = await OpenReadSharedWithRetryAsync(snapshotPath, cts.Token);
        using var content = new StreamContent(fs);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var req = new HttpRequestMessage(HttpMethod.Put, _baseUrl + "/sync/snapshot")
        {
            Content = content,
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(resp, cts.Token);
            var www = TryGetHeader(resp, "WWW-Authenticate");
            var failure = TryGetHeader(resp, "X-Auth-Failure");
            var combined = CombineBody(CombineBody(www, failure), body);
            throw new NasAgentApiException(resp.StatusCode, "upload snapshot failed", combined);
        }
    }

    private static async Task<FileStream> OpenReadSharedWithRetryAsync(string path, CancellationToken ct)
    {
        for (var i = 0; i < 12; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) when (i < 11)
            {
                await Task.Delay(150, ct);
            }
            catch (UnauthorizedAccessException) when (i < 11)
            {
                await Task.Delay(150, ct);
            }
        }

        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    public async Task DownloadSnapshotAsync(string accessToken, string targetPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new InvalidOperationException("missing base url");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("missing access token");
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("targetPath is empty", nameof(targetPath));

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/sync/snapshot");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(resp, cts.Token);
            var www = TryGetHeader(resp, "WWW-Authenticate");
            var failure = TryGetHeader(resp, "X-Auth-Failure");
            var combined = CombineBody(CombineBody(www, failure), body);
            throw new NasAgentApiException(resp.StatusCode, "download snapshot failed", combined);
        }

        var tmp = targetPath + ".download";
        try
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
        catch
        {
        }

        await using (var input = await resp.Content.ReadAsStreamAsync(cts.Token))
        await using (var output = File.Create(tmp))
        {
            await input.CopyToAsync(output, cts.Token);
        }

        try
        {
            if (File.Exists(targetPath))
                File.Delete(targetPath);
        }
        catch
        {
        }

        File.Move(tmp, targetPath);
    }

    private static async Task<string?> SafeReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetHeader(HttpResponseMessage resp, string name)
    {
        try
        {
            if (resp.Headers.TryGetValues(name, out var values))
                return string.Join(" ", values);
        }
        catch
        {
        }

        return null;
    }

    private static string? CombineBody(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a))
            return string.IsNullOrWhiteSpace(b) ? null : b;
        if (string.IsNullOrWhiteSpace(b))
            return a;
        return a + "\n" + b;
    }
}
