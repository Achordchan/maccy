using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace maccy.Services;

public sealed class NasAgentApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ResponseBody { get; }
    public string? ErrorCode { get; }

    public NasAgentApiException(HttpStatusCode statusCode, string message, string? responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        ErrorCode = TryReadErrorCode(responseBody);
    }

    private static string? TryReadErrorCode(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("code", out var value))
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}

public sealed record SubscriptionStatus(
    bool Subscribed,
    string? ExpiresAt,
    string? Tier,
    long? StorageBytes,
    long? StorageLimitBytes,
    int? RetentionDays,
    bool? OverLimit);
public sealed record RedeemResult(bool Success, string? ExpiresAt, string? Error);
public sealed record UploadSnapshotResult(string? Version, string? Sha256, long? Size);
public sealed record BlobCheckResult(string[] Missing);

public sealed class NasAgentClient
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(30);
    private const int GetRetryCount = 1;

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public NasAgentClient(string baseUrl, HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        if (httpClient is null)
            _http.Timeout = Timeout.InfiniteTimeSpan;
        _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
    }

    public async Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return false;

        HttpResponseMessage resp;
        try
        {
            resp = await SendGetWithRetryAsync("/health", null, HealthTimeout, ct, 0);
        }
        catch
        {
            return false;
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                return false;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("ok", out var ok))
                return false;

            return ok.ValueKind == JsonValueKind.True;
        }
    }

    public async Task<SubscriptionStatus?> GetSubscriptionStatusAsync(string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return null;
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        using var resp = await SendGetWithRetryAsync("/subscription/status", accessToken, StatusTimeout, ct, GetRetryCount);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(resp, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return new SubscriptionStatus(false, null, null, null, null, null, null);
            if (resp.StatusCode == HttpStatusCode.Forbidden)
                return new SubscriptionStatus(false, null, null, null, null, null, null);

            var www = TryGetHeader(resp, "WWW-Authenticate");
            var failure = TryGetHeader(resp, "X-Auth-Failure");
            var combined = CombineBody(CombineBody(www, failure), body);
            throw new NasAgentApiException(resp.StatusCode, "get subscription status failed", combined);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var subscribed = doc.RootElement.TryGetProperty("subscribed", out var subEl) && subEl.ValueKind == JsonValueKind.True;
        var expiresAt = doc.RootElement.TryGetProperty("expiresAt", out var expEl) ? expEl.GetString() : null;
        var tier = TryReadString(doc.RootElement, "tier");
        var storageBytes = TryReadInt64(doc.RootElement, "storageBytes");
        var storageLimitBytes = TryReadInt64(doc.RootElement, "storageLimitBytes");
        var retentionDays = TryReadInt32(doc.RootElement, "retentionDays");
        var overLimit = TryReadBool(doc.RootElement, "overLimit");
        return new SubscriptionStatus(subscribed, expiresAt, tier, storageBytes, storageLimitBytes, retentionDays, overLimit);
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

        using var resp = await SendGetWithRetryAsync("/sync/manifest", accessToken, ManifestTimeout, ct, GetRetryCount);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(resp, ct);
            var www = TryGetHeader(resp, "WWW-Authenticate");
            var failure = TryGetHeader(resp, "X-Auth-Failure");
            var combined = CombineBody(CombineBody(www, failure), body);
            throw new NasAgentApiException(resp.StatusCode, "get manifest failed", combined);
        }

        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<UploadSnapshotResult?> UploadSnapshotAsync(string accessToken, string snapshotPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new InvalidOperationException("missing base url");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("missing access token");
        if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath))
            throw new FileNotFoundException("snapshot not found", snapshotPath);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        using var content = new GzipFileContent(snapshotPath);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentEncoding.Add("gzip");

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

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
        var version = TryReadString(doc.RootElement, "version");
        var sha256 = TryReadString(doc.RootElement, "sha256");
        var size = TryReadInt64(doc.RootElement, "size");
        return new UploadSnapshotResult(version, sha256, size);
    }

    private static async Task<FileStream> OpenReadSharedWithRetryAsync(string path, CancellationToken ct)
    {
        for (var i = 0; i < 12; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

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

        await using (var rawInput = await resp.Content.ReadAsStreamAsync(cts.Token))
        await using (var output = File.Create(tmp))
        {
            Stream input = rawInput;
            if (resp.Content.Headers.ContentEncoding.Contains("gzip"))
                input = new GZipStream(rawInput, CompressionMode.Decompress, leaveOpen: false);

            await input.CopyToAsync(output, cts.Token);
            if (!ReferenceEquals(input, rawInput))
                await input.DisposeAsync();
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

    public async Task<BlobCheckResult> CheckMissingBlobsAsync(string accessToken, string[] hashes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new InvalidOperationException("missing base url");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("missing access token");
        if (hashes is null || hashes.Length == 0)
            return new BlobCheckResult(Array.Empty<string>());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { hashes });
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/sync/blobs/check") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(resp, cts.Token);
            throw new NasAgentApiException(resp.StatusCode, "check missing blobs failed", body);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
        if (!doc.RootElement.TryGetProperty("missing", out var missing) || missing.ValueKind != JsonValueKind.Array)
            return new BlobCheckResult(Array.Empty<string>());

        var values = missing.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();
        return new BlobCheckResult(values);
    }

    public async Task UploadBlobAsync(string accessToken, string sha256, string sourcePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new InvalidOperationException("missing base url");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("missing access token");
        if (string.IsNullOrWhiteSpace(sha256))
            throw new ArgumentException("missing blob hash", nameof(sha256));
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("blob source not found", sourcePath);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        using var content = new GzipFileContent(sourcePath);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentEncoding.Add("gzip");

        using var req = new HttpRequestMessage(HttpMethod.Put, _baseUrl + "/sync/blobs/" + sha256) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(resp, cts.Token);
            throw new NasAgentApiException(resp.StatusCode, "upload blob failed", body);
        }
    }

    public async Task DownloadBlobAsync(string accessToken, string sha256, string targetPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new InvalidOperationException("missing base url");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("missing access token");
        if (string.IsNullOrWhiteSpace(sha256))
            throw new ArgumentException("missing blob hash", nameof(sha256));

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/sync/blobs/" + sha256);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(resp, cts.Token);
            throw new NasAgentApiException(resp.StatusCode, "download blob failed", body);
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

        await using (var rawInput = await resp.Content.ReadAsStreamAsync(cts.Token))
        await using (var output = File.Create(tmp))
        {
            Stream input = rawInput;
            if (resp.Content.Headers.ContentEncoding.Contains("gzip"))
                input = new GZipStream(rawInput, CompressionMode.Decompress, leaveOpen: false);

            await input.CopyToAsync(output, cts.Token);
            if (!ReferenceEquals(input, rawInput))
                await input.DisposeAsync();
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

    private sealed class GzipFileContent : HttpContent
    {
        private readonly string _sourcePath;

        public GzipFileContent(string sourcePath)
        {
            _sourcePath = sourcePath;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var input = await OpenReadSharedWithRetryAsync(_sourcePath, CancellationToken.None);
            await using var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
            await input.CopyToAsync(gzip);
            await gzip.FlushAsync();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static long? TryReadInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            return number;
        return null;
    }

    private static int? TryReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;
        return null;
    }

    private static bool? TryReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.True)
            return true;
        if (value.ValueKind == JsonValueKind.False)
            return false;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private async Task<HttpResponseMessage> SendGetWithRetryAsync(
        string path,
        string? accessToken,
        TimeSpan timeout,
        CancellationToken ct,
        int retryCount)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
            if (!string.IsNullOrWhiteSpace(accessToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < retryCount)
            {
                await Task.Delay(350, ct);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("request timeout: GET " + _baseUrl + path, ex);
            }
            catch (HttpRequestException) when (attempt < retryCount)
            {
                await Task.Delay(350, ct);
            }
        }
    }

}
