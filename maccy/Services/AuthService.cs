using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace maccy.Services;

public sealed class AuthService
{
    private readonly HttpClient _http;

    public AuthService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
    }

    public sealed record TokenSet(string AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset ExpiresAtUtc, string? Email);

    public async Task<TokenSet> LoginAsync(string baseUrl, string email, string password, CancellationToken ct)
    {
        var payload = new { email = (email ?? string.Empty).Trim(), password = password ?? string.Empty };
        return await SendTokenRequestAsync(baseUrl, "/auth/login", payload, ct);
    }

    public async Task<TokenSet> RegisterAsync(string baseUrl, string email, string password, CancellationToken ct)
    {
        var payload = new { email = (email ?? string.Empty).Trim(), password = password ?? string.Empty };
        return await SendTokenRequestAsync(baseUrl, "/auth/register", payload, ct);
    }

    public async Task<TokenSet> RefreshAsync(string baseUrl, string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("refreshToken is empty", nameof(refreshToken));

        var payload = new { refreshToken = refreshToken.Trim() };
        return await SendTokenRequestAsync(baseUrl, "/auth/refresh", payload, ct);
    }

    public async Task<string?> GetCurrentEmailAsync(string baseUrl, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, NormalizeBaseUrl(baseUrl) + "/auth/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("email", out var emailEl) || emailEl.ValueKind != JsonValueKind.String)
            return null;
        return emailEl.GetString();
    }

    private async Task<TokenSet> SendTokenRequestAsync(string baseUrl, string path, object payload, CancellationToken ct)
    {
        var url = NormalizeBaseUrl(baseUrl) + path;
        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!resp.IsSuccessStatusCode)
        {
            var error = TryReadString(doc.RootElement, "error");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"auth failed ({(int)resp.StatusCode})" : error);
        }

        var accessToken = TryReadString(doc.RootElement, "accessToken") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("token response missing accessToken");

        var refreshToken = TryReadString(doc.RootElement, "refreshToken");
        var email = TryReadString(doc.RootElement, "email");
        var expiresAt = DateTimeOffset.UtcNow.AddHours(2);
        var expiresAtText = TryReadString(doc.RootElement, "accessTokenExpiresAt");
        if (!string.IsNullOrWhiteSpace(expiresAtText) && DateTimeOffset.TryParse(expiresAtText, out var parsed))
            expiresAt = parsed;

        return new TokenSet(accessToken, refreshToken, null, expiresAt, email);
    }

    private static string NormalizeBaseUrl(string? value)
    {
        var url = (value ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
            return ServerDefaults.OfficialSyncBaseUrl;
        return url;
    }

    private static string? TryReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }
}
