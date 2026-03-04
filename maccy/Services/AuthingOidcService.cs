using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace maccy.Services;

public sealed class AuthingOidcService
{
    public const string Issuer = "https://achord-maccy.authing.cn/oidc";
    public const string ClientId = "6974991e79232eb58c755602";
    public const string RedirectUri = "http://127.0.0.1:17654/callback";
    public const string LogoutRedirectUri = "http://127.0.0.1:17654/logout-callback";

    // 官方云同步服务器地址
    public const string OfficialSyncBaseUrl = "http://47.122.83.145:17655";

    private readonly HttpClient _http;

    public AuthingOidcService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
    }

    public sealed record TokenSet(string AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset ExpiresAtUtc);

    public async Task<TokenSet> LoginAsync(CancellationToken ct)
    {
        var discovery = await GetDiscoveryAsync(ct);

        var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        var callback = await StartBrowserAndWaitForCallbackAsync(discovery.AuthorizationEndpoint, state, codeChallenge, ct);

        if (!string.Equals(callback.State, state, StringComparison.Ordinal))
            throw new InvalidOperationException("state mismatch");

        var token = await ExchangeCodeAsync(discovery.TokenEndpoint, callback.Code, codeVerifier, ct);
        return token;
    }

    public async Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("refreshToken is empty", nameof(refreshToken));

        var discovery = await GetDiscoveryAsync(ct);
        var token = await ExchangeRefreshTokenAsync(discovery.TokenEndpoint, refreshToken, ct);
        return token;
    }

    public string BuildLogoutUrl()
    {
        return Issuer.TrimEnd('/') + "/session/end?post_logout_redirect_uri=" + Uri.EscapeDataString(LogoutRedirectUri);
    }

    private sealed record Discovery(string AuthorizationEndpoint, string TokenEndpoint);

    private async Task<Discovery> GetDiscoveryAsync(CancellationToken ct)
    {
        var url = Issuer.TrimEnd('/') + "/.well-known/openid-configuration";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("authorization_endpoint", out var authEp))
            throw new InvalidOperationException("discovery missing authorization_endpoint");
        if (!doc.RootElement.TryGetProperty("token_endpoint", out var tokenEp))
            throw new InvalidOperationException("discovery missing token_endpoint");

        var a = authEp.GetString() ?? string.Empty;
        var t = tokenEp.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(t))
            throw new InvalidOperationException("discovery invalid");

        return new Discovery(a, t);
    }

    private sealed record CallbackResult(string Code, string State);

    private async Task<CallbackResult> StartBrowserAndWaitForCallbackAsync(string authorizationEndpoint, string state, string codeChallenge, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:17654/");

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("cannot start loopback listener on 127.0.0.1:17654", ex);
        }

        var authUrl = authorizationEndpoint
            + "?client_id=" + Uri.EscapeDataString(ClientId)
            + "&response_type=code"
            + "&scope=" + Uri.EscapeDataString("openid profile offline_access")
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            + "&state=" + Uri.EscapeDataString(state)
            + "&code_challenge=" + Uri.EscapeDataString(codeChallenge)
            + "&code_challenge_method=S256";

        try
        {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("cannot open browser", ex);
        }

        using var reg = ct.Register(() =>
        {
            try
            {
                listener.Stop();
            }
            catch
            {
            }
        });

        HttpListenerContext ctx;
        try
        {
            ctx = await listener.GetContextAsync();
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw new InvalidOperationException("login canceled or listener stopped", ex);
        }

        var req = ctx.Request;
        var code = req.QueryString["code"] ?? string.Empty;
        var gotState = req.QueryString["state"] ?? string.Empty;

        var html = "<!doctype html><html><head><meta charset=\"utf-8\"/></head><body style=\"font-family:system-ui;max-width:720px;margin:40px auto;padding:0 16px\"><h2>登录完成</h2><p>你可以关闭这个页面，回到 Maccy。</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);

        try
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        }
        catch
        {
        }
        finally
        {
            try
            {
                ctx.Response.OutputStream.Close();
                ctx.Response.Close();
            }
            catch
            {
            }
        }

        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("missing code");

        return new CallbackResult(code, gotState);
    }

    private async Task<TokenSet> ExchangeCodeAsync(string tokenEndpoint, string code, string codeVerifier, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = codeVerifier,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = content };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var accessToken = doc.RootElement.TryGetProperty("access_token", out var at) ? (at.GetString() ?? string.Empty) : string.Empty;
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var idToken = doc.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() : null;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt64() : 3600;

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("token response missing access_token");

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));
        return new TokenSet(accessToken, refreshToken, idToken, expiresAt);
    }

    private async Task<TokenSet> ExchangeRefreshTokenAsync(string tokenEndpoint, string refreshToken, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = "openid profile offline_access",
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = content };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var accessToken = doc.RootElement.TryGetProperty("access_token", out var at) ? (at.GetString() ?? string.Empty) : string.Empty;
        var newRefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var idToken = doc.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() : null;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt64() : 3600;

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("token response missing access_token");

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));
        return new TokenSet(accessToken, string.IsNullOrWhiteSpace(newRefreshToken) ? refreshToken : newRefreshToken, idToken, expiresAt);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
