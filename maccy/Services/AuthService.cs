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

    public async Task<int> SendPasswordCodeAsync(string baseUrl, string purpose, string? email, string? accessToken, CancellationToken ct)
    {
        var normalizedPurpose = (purpose ?? string.Empty).Trim().ToLowerInvariant();
        object payload = normalizedPurpose == "change"
            ? new { purpose = normalizedPurpose }
            : new { purpose = normalizedPurpose, email = (email ?? string.Empty).Trim() };

        using var doc = await SendJsonRequestAsync(baseUrl, "/auth/password/code", payload, ct, accessToken);
        if (doc.RootElement.TryGetProperty("cooldownSeconds", out var cooldownEl) && cooldownEl.TryGetInt32(out var cooldown))
            return Math.Max(0, cooldown);
        return 60;
    }

    public async Task ResetPasswordAsync(string baseUrl, string email, string code, string newPassword, CancellationToken ct)
    {
        var payload = new
        {
            email = (email ?? string.Empty).Trim(),
            code = (code ?? string.Empty).Trim(),
            newPassword = newPassword ?? string.Empty
        };
        using var _ = await SendJsonRequestAsync(baseUrl, "/auth/password/reset", payload, ct);
    }

    public async Task ChangePasswordAsync(string baseUrl, string accessToken, string code, string newPassword, CancellationToken ct)
    {
        var payload = new
        {
            code = (code ?? string.Empty).Trim(),
            newPassword = newPassword ?? string.Empty
        };
        using var _ = await SendJsonRequestAsync(baseUrl, "/auth/password/change", payload, ct, accessToken);
    }

    private async Task<TokenSet> SendTokenRequestAsync(string baseUrl, string path, object payload, CancellationToken ct)
    {
        using var doc = await SendJsonRequestAsync(baseUrl, path, payload, ct);

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

    private async Task<JsonDocument> SendJsonRequestAsync(string baseUrl, string path, object payload, CancellationToken ct, string? accessToken = null)
    {
        var url = NormalizeBaseUrl(baseUrl) + path;
        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(accessToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var doc = ParseJsonOrEmpty(text);

        if (!resp.IsSuccessStatusCode)
        {
            using (doc)
            {
                var error = TryReadString(doc.RootElement, "error");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"auth failed ({(int)resp.StatusCode})" : error);
            }
        }

        return doc;
    }

    private static JsonDocument ParseJsonOrEmpty(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return JsonDocument.Parse("{}");
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
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
