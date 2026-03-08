using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = long.MaxValue);

var dataRoot = builder.Configuration["Storage:Root"] ?? "/data";
var adminKey = builder.Configuration["Admin:Key"] ?? "change-me";
var issuer = builder.Configuration["Auth:Issuer"] ?? "maccy-self-hosted";
var audience = builder.Configuration["Auth:Audience"] ?? "maccy-client";
var signingSecret = builder.Configuration["Auth:SigningKey"] ?? "change-me-to-a-long-random-secret";
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingSecret));
var dbPath = Path.Combine(dataRoot, "subscriptions.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddSingleton(new PasswordHasher<AuthMarker>());
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
await InitDbAsync(dbPath);

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Text("<!doctype html><html><body style=\"font-family:system-ui;padding:24px\"><h1>Maccy Agent</h1><p><a href=\"/health\">health</a> | <a href=\"/admin\">admin</a></p></body></html>", "text/html; charset=utf-8"));
app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapGet("/admin", () => Results.Text(ReadAdminHtml(), "text/html; charset=utf-8"));

app.MapPost("/auth/register", async (HttpContext ctx, PasswordHasher<AuthMarker> hasher) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<EmailPasswordRequest>(ctx.RequestAborted);
    if (req is null) return Results.BadRequest(new { error = "invalid request" });
    var email = NormalizeEmail(req.Email);
    var password = req.Password ?? string.Empty;
    if (!IsValidEmail(email)) return Results.BadRequest(new { error = "invalid email" });
    if (password.Length < 8) return Results.BadRequest(new { error = "password must be at least 8 characters" });
    if (await GetUserByEmailAsync(dbPath, email) is not null) return Results.BadRequest(new { error = "email already registered" });

    var now = DateTimeOffset.UtcNow.ToString("O");
    var userId = Guid.NewGuid().ToString("N");
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync(ctx.RequestAborted);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO users (id,email,password_hash,created_at,updated_at) VALUES ($id,$email,$hash,$now,$now)";
    cmd.Parameters.AddWithValue("$id", userId);
    cmd.Parameters.AddWithValue("$email", email);
    cmd.Parameters.AddWithValue("$hash", hasher.HashPassword(new AuthMarker(), password));
    cmd.Parameters.AddWithValue("$now", now);
    await cmd.ExecuteNonQueryAsync(ctx.RequestAborted);
    return Results.Ok(await IssueTokensAsync(dbPath, userId, email, issuer, audience, signingKey, ctx.RequestAborted));
});

app.MapPost("/auth/login", async (HttpContext ctx, PasswordHasher<AuthMarker> hasher) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<EmailPasswordRequest>(ctx.RequestAborted);
    if (req is null) return Results.BadRequest(new { error = "invalid request" });
    var email = NormalizeEmail(req.Email);
    var password = req.Password ?? string.Empty;
    var user = await GetUserByEmailAsync(dbPath, email);
    if (user is null) return Results.BadRequest(new { error = "invalid email or password" });
    if (hasher.VerifyHashedPassword(new AuthMarker(), user.PasswordHash, password) == PasswordVerificationResult.Failed)
        return Results.BadRequest(new { error = "invalid email or password" });
    return Results.Ok(await IssueTokensAsync(dbPath, user.Id, user.Email, issuer, audience, signingKey, ctx.RequestAborted));
});

app.MapPost("/auth/refresh", async (HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<RefreshRequest>(ctx.RequestAborted);
    if (req is null || string.IsNullOrWhiteSpace(req.RefreshToken)) return Results.BadRequest(new { error = "missing refresh token" });
    var tokenHash = HashText(req.RefreshToken.Trim());
    var stored = await GetRefreshTokenAsync(dbPath, tokenHash);
    if (stored is null || stored.RevokedAt is not null || DateTimeOffset.Parse(stored.ExpiresAt) <= DateTimeOffset.UtcNow)
        return Results.Unauthorized();
    var user = await GetUserByIdAsync(dbPath, stored.UserId);
    if (user is null) return Results.Unauthorized();
    await RevokeRefreshTokenAsync(dbPath, tokenHash, ctx.RequestAborted);
    return Results.Ok(await IssueTokensAsync(dbPath, user.Id, user.Email, issuer, audience, signingKey, ctx.RequestAborted));
});

app.MapGet("/auth/me", async (HttpContext ctx) =>
{
    var userId = GetSubject(ctx.User);
    if (userId is null) return Results.Unauthorized();
    var user = await GetUserByIdAsync(dbPath, userId);
    return user is null ? Results.Unauthorized() : Results.Ok(new { id = user.Id, email = user.Email });
}).RequireAuthorization();

app.MapGet("/subscription/status", async (HttpContext ctx) =>
{
    var userId = GetSubject(ctx.User);
    if (userId is null) return Results.Unauthorized();
    var expiresAt = await GetSubscriptionAsync(dbPath, userId);
    if (expiresAt is null) return Results.Ok(new { subscribed = false, expiresAt = (string?)null });
    return Results.Ok(new { subscribed = DateTimeOffset.Parse(expiresAt) > DateTimeOffset.UtcNow, expiresAt });
}).RequireAuthorization();

app.MapPost("/card/redeem", async (HttpContext ctx) =>
{
    var userId = GetSubject(ctx.User);
    if (userId is null) return Results.Unauthorized();
    var req = await ctx.Request.ReadFromJsonAsync<RedeemRequest>(ctx.RequestAborted);
    if (req is null || string.IsNullOrWhiteSpace(req.Code)) return Results.BadRequest(new { error = "missing code" });
    var code = req.Code.Trim().ToUpperInvariant();
    var card = await GetCardAsync(dbPath, code);
    if (card is null || !string.IsNullOrWhiteSpace(card.UsedBy)) return Results.BadRequest(new { error = "card not found or used" });
    var now = DateTimeOffset.UtcNow;
    var expiresAt = (await GetSubscriptionAsync(dbPath, userId)) switch
    {
        string value when DateTimeOffset.Parse(value) > now => DateTimeOffset.Parse(value).AddDays(card.DurationDays).ToString("O"),
        _ => now.AddDays(card.DurationDays).ToString("O")
    };
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync(ctx.RequestAborted);
    await using var tx = await conn.BeginTransactionAsync(ctx.RequestAborted);
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "UPDATE cards SET used_at=$now, used_by=$userId WHERE code=$code";
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$userId", userId);
        cmd.Parameters.AddWithValue("$code", code);
        await cmd.ExecuteNonQueryAsync(ctx.RequestAborted);
    }
    await UpsertSubscriptionAsync(conn, userId, expiresAt, ctx.RequestAborted);
    await tx.CommitAsync(ctx.RequestAborted);
    return Results.Ok(new { success = true, expiresAt });
}).RequireAuthorization();

app.MapGet("/sync/manifest", async (HttpContext ctx) =>
{
    var userId = GetSubject(ctx.User);
    if (userId is null) return Results.Unauthorized();
    if (!await CheckSubscriptionAsync(dbPath, userId)) return Results.Json(new { error = "订阅已过期" }, statusCode: 403);
    var path = Path.Combine(GetUserRoot(dataRoot, userId), "manifest.json");
    return File.Exists(path) ? Results.File(path, "application/json; charset=utf-8") : Results.NotFound();
}).RequireAuthorization();

app.MapGet("/sync/snapshot", async (HttpContext ctx) =>
{
    var userId = GetSubject(ctx.User);
    if (userId is null) return Results.Unauthorized();
    if (!await CheckSubscriptionAsync(dbPath, userId)) return Results.Json(new { error = "订阅已过期" }, statusCode: 403);
    var path = Path.Combine(GetUserRoot(dataRoot, userId), "snapshot.sqlite");
    return File.Exists(path) ? Results.File(path, "application/octet-stream", "snapshot.sqlite") : Results.NotFound();
}).RequireAuthorization();

app.MapPut("/sync/snapshot", async (HttpContext ctx) =>
{
    var userId = GetSubject(ctx.User);
    if (userId is null) return Results.Unauthorized();
    if (!await CheckSubscriptionAsync(dbPath, userId)) return Results.Json(new { error = "订阅已过期" }, statusCode: 403);
    var root = GetUserRoot(dataRoot, userId);
    Directory.CreateDirectory(root);
    var snapshotPath = Path.Combine(root, "snapshot.sqlite");
    var tmpPath = snapshotPath + ".upload";
    TryDelete(tmpPath);
    await using (var fs = File.Create(tmpPath))
        await ctx.Request.Body.CopyToAsync(fs, ctx.RequestAborted);
    var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
    var sha256 = ComputeSha256Hex(tmpPath);
    var size = new FileInfo(tmpPath).Length;
    var manifestPath = Path.Combine(root, "manifest.json");
    var previousVersion = TryReadJsonValue(manifestPath, "latest", "version");
    if (File.Exists(snapshotPath))
    {
        var backupDir = Path.Combine(root, "backups", string.IsNullOrWhiteSpace(previousVersion) ? "unknown" : previousVersion);
        Directory.CreateDirectory(backupDir);
        TryMove(snapshotPath, Path.Combine(backupDir, "snapshot.sqlite"));
        TryMove(manifestPath, Path.Combine(backupDir, "manifest.json"));
    }
    TryMove(tmpPath, snapshotPath);
    var manifest = JsonSerializer.Serialize(new
    {
        schema = 1,
        issuer,
        subject = userId,
        latest = new
        {
            version,
            updatedAt = DateTimeOffset.UtcNow.ToString("O"),
            deviceId = "",
            createdFromVersion = "",
            snapshot = new
            {
                type = "sqlite",
                fileName = "snapshot.sqlite",
                sha256,
                size
            }
        }
    });
    await File.WriteAllTextAsync(manifestPath, manifest, Encoding.UTF8, ctx.RequestAborted);
    return Results.Ok(new { version, sha256, size });
}).RequireAuthorization();

app.MapGet("/admin/users/list", async (HttpContext ctx) =>
{
    if (!IsAdmin(ctx.Request, adminKey)) return Results.Unauthorized();
    return Results.Ok(new { users = await ListUsersAsync(dbPath) });
});

app.MapGet("/admin/subscriptions/list", async (HttpContext ctx) =>
{
    if (!IsAdmin(ctx.Request, adminKey)) return Results.Unauthorized();
    return Results.Ok(new { subscriptions = await ListSubscriptionsAsync(dbPath) });
});

app.MapGet("/admin/cards/list", async (HttpContext ctx) =>
{
    if (!IsAdmin(ctx.Request, adminKey)) return Results.Unauthorized();
    return Results.Ok(new { cards = await ListCardsAsync(dbPath) });
});

app.MapPost("/admin/card/generate", async (HttpContext ctx) =>
{
    if (!IsAdmin(ctx.Request, adminKey)) return Results.Unauthorized();
    var req = await ctx.Request.ReadFromJsonAsync<GenerateCardsRequest>(ctx.RequestAborted);
    var count = Math.Clamp(req?.Count ?? 1, 1, 100);
    var durationDays = Math.Clamp(req?.DurationDays ?? 30, 1, 3650);
    var codes = Enumerable.Range(0, count).Select(_ => GenerateCardCode()).ToList();
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync(ctx.RequestAborted);
    foreach (var code in codes)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO cards (code,duration_days,created_at) VALUES ($code,$days,$now)";
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$days", durationDays);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ctx.RequestAborted);
    }
    return Results.Ok(new { codes, durationDays });
});

app.MapPost("/admin/user/upgrade", async (HttpContext ctx) =>
{
    if (!IsAdmin(ctx.Request, adminKey)) return Results.Unauthorized();
    var req = await ctx.Request.ReadFromJsonAsync<UpgradeUserRequest>(ctx.RequestAborted);
    if (req is null || string.IsNullOrWhiteSpace(req.EmailOrUserId)) return Results.BadRequest(new { error = "missing emailOrUserId" });
    var user = await ResolveUserAsync(dbPath, req.EmailOrUserId.Trim());
    if (user is null) return Results.BadRequest(new { error = "user not found" });
    var durationDays = Math.Clamp(req.DurationDays, 1, 3650);
    var expiresAt = (await GetSubscriptionAsync(dbPath, user.Id)) switch
    {
        string value when DateTimeOffset.Parse(value) > DateTimeOffset.UtcNow => DateTimeOffset.Parse(value).AddDays(durationDays).ToString("O"),
        _ => DateTimeOffset.UtcNow.AddDays(durationDays).ToString("O")
    };
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync(ctx.RequestAborted);
    await UpsertSubscriptionAsync(conn, user.Id, expiresAt, ctx.RequestAborted);
    return Results.Ok(new { success = true, expiresAt, email = user.Email, userId = user.Id });
});

app.Run();

static async Task InitDbAsync(string dbPath)
{
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
CREATE TABLE IF NOT EXISTS users (
  id TEXT PRIMARY KEY,
  email TEXT NOT NULL UNIQUE,
  password_hash TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS refresh_tokens (
  token_hash TEXT PRIMARY KEY,
  user_id TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  created_at TEXT NOT NULL,
  revoked_at TEXT
);
CREATE TABLE IF NOT EXISTS cards (
  code TEXT PRIMARY KEY,
  duration_days INTEGER NOT NULL,
  created_at TEXT NOT NULL,
  used_at TEXT,
  used_by TEXT
);
CREATE TABLE IF NOT EXISTS subscriptions (
  user_id TEXT PRIMARY KEY,
  expires_at TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
""";
    await cmd.ExecuteNonQueryAsync();
}

static async Task<object> IssueTokensAsync(string dbPath, string userId, string email, string issuer, string audience, SymmetricSecurityKey signingKey, CancellationToken ct)
{
    var now = DateTimeOffset.UtcNow;
    var accessExpiresAt = now.AddHours(2);
    var refreshExpiresAt = now.AddDays(30);
    var jwt = new JwtSecurityToken(issuer, audience,
    [
        new Claim(JwtRegisteredClaimNames.Sub, userId),
        new Claim(JwtRegisteredClaimNames.Email, email),
        new Claim(ClaimTypes.NameIdentifier, userId),
        new Claim(ClaimTypes.Email, email)
    ], now.UtcDateTime, accessExpiresAt.UtcDateTime, new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
    var accessToken = new JwtSecurityTokenHandler().WriteToken(jwt);
    var refreshToken = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO refresh_tokens (token_hash,user_id,expires_at,created_at,revoked_at) VALUES ($hash,$userId,$expiresAt,$createdAt,NULL)";
    cmd.Parameters.AddWithValue("$hash", HashText(refreshToken));
    cmd.Parameters.AddWithValue("$userId", userId);
    cmd.Parameters.AddWithValue("$expiresAt", refreshExpiresAt.ToString("O"));
    cmd.Parameters.AddWithValue("$createdAt", now.ToString("O"));
    await cmd.ExecuteNonQueryAsync(ct);
    return new { accessToken, refreshToken, accessTokenExpiresAt = accessExpiresAt.ToString("O"), email, userId };
}

static async Task<UserRecord?> GetUserByEmailAsync(string dbPath, string email)
{
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id,email,password_hash FROM users WHERE email=$email";
    cmd.Parameters.AddWithValue("$email", email);
    await using var reader = await cmd.ExecuteReaderAsync();
    return await reader.ReadAsync() ? new UserRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
}

static async Task<UserRecord?> GetUserByIdAsync(string dbPath, string userId)
{
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id,email,password_hash FROM users WHERE id=$userId";
    cmd.Parameters.AddWithValue("$userId", userId);
    await using var reader = await cmd.ExecuteReaderAsync();
    return await reader.ReadAsync() ? new UserRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
}

static async Task<RefreshTokenRecord?> GetRefreshTokenAsync(string dbPath, string tokenHash)
{
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT token_hash,user_id,expires_at,revoked_at FROM refresh_tokens WHERE token_hash=$hash";
    cmd.Parameters.AddWithValue("$hash", tokenHash);
    await using var reader = await cmd.ExecuteReaderAsync();
    return await reader.ReadAsync() ? new RefreshTokenRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)) : null;
}

static async Task RevokeRefreshTokenAsync(string dbPath, string tokenHash, CancellationToken ct)
{
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE refresh_tokens SET revoked_at=$now WHERE token_hash=$hash";
    cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
    cmd.Parameters.AddWithValue("$hash", tokenHash);
    await cmd.ExecuteNonQueryAsync(ct);
}

static async Task<CardRecord?> GetCardAsync(string dbPath, string code)
{
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT code,duration_days,used_by FROM cards WHERE code=$code";
    cmd.Parameters.AddWithValue("$code", code);
    await using var reader = await cmd.ExecuteReaderAsync();
    return await reader.ReadAsync() ? new CardRecord(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2)) : null;
}

static async Task<string?> GetSubscriptionAsync(string dbPath, string userId)
{
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT expires_at FROM subscriptions WHERE user_id=$userId";
    cmd.Parameters.AddWithValue("$userId", userId);
    await using var reader = await cmd.ExecuteReaderAsync();
    return await reader.ReadAsync() ? reader.GetString(0) : null;
}

static async Task<bool> CheckSubscriptionAsync(string dbPath, string userId)
{
    var value = await GetSubscriptionAsync(dbPath, userId);
    return value is not null && DateTimeOffset.Parse(value) > DateTimeOffset.UtcNow;
}

static async Task UpsertSubscriptionAsync(SqliteConnection conn, string userId, string expiresAt, CancellationToken ct)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO subscriptions (user_id,expires_at,created_at,updated_at) VALUES ($userId,$expiresAt,$now,$now) ON CONFLICT(user_id) DO UPDATE SET expires_at=$expiresAt, updated_at=$now";
    cmd.Parameters.AddWithValue("$userId", userId);
    cmd.Parameters.AddWithValue("$expiresAt", expiresAt);
    cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
    await cmd.ExecuteNonQueryAsync(ct);
}

static async Task<UserRecord?> ResolveUserAsync(string dbPath, string emailOrUserId)
{
    return await GetUserByEmailAsync(dbPath, NormalizeEmail(emailOrUserId))
        ?? await GetUserByIdAsync(dbPath, emailOrUserId);
}

static async Task<List<object>> ListUsersAsync(string dbPath)
{
    var rows = new List<object>();
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT u.id,u.email,u.created_at,u.updated_at,s.expires_at FROM users u LEFT JOIN subscriptions s ON s.user_id=u.id ORDER BY u.created_at DESC";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        rows.Add(new { id = reader.GetString(0), email = reader.GetString(1), created_at = reader.GetString(2), updated_at = reader.GetString(3), expires_at = reader.IsDBNull(4) ? null : reader.GetString(4) });
    return rows;
}

static async Task<List<object>> ListSubscriptionsAsync(string dbPath)
{
    var rows = new List<object>();
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT s.user_id,u.email,s.expires_at,s.created_at,s.updated_at FROM subscriptions s LEFT JOIN users u ON u.id=s.user_id ORDER BY s.updated_at DESC";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        rows.Add(new { user_id = reader.GetString(0), email = reader.IsDBNull(1) ? null : reader.GetString(1), expires_at = reader.GetString(2), created_at = reader.GetString(3), updated_at = reader.GetString(4) });
    return rows;
}

static async Task<List<object>> ListCardsAsync(string dbPath)
{
    var rows = new List<object>();
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT code,duration_days,used_by,used_at,created_at FROM cards ORDER BY created_at DESC";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        rows.Add(new { code = reader.GetString(0), duration_days = reader.GetInt32(1), used_by = reader.IsDBNull(2) ? null : reader.GetString(2), used_at = reader.IsDBNull(3) ? null : reader.GetString(3), created_at = reader.GetString(4) });
    return rows;
}

static bool IsAdmin(HttpRequest request, string adminKey)
{
    var auth = request.Headers.Authorization.ToString();
    return !string.IsNullOrWhiteSpace(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && string.Equals(auth[7..].Trim(), adminKey, StringComparison.Ordinal);
}

static string? GetSubject(ClaimsPrincipal user) => user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
static string GetUserRoot(string dataRoot, string userId) => Path.Combine(dataRoot, MakeSafeSegment(userId));
static string MakeSafeSegment(string input) => new string((input ?? string.Empty).Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_').ToArray()).Trim('_') switch { "" => "unknown", var v => v };
static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
static void TryMove(string src, string dest) { try { if (File.Exists(src)) { TryDelete(dest); File.Move(src, dest); } } catch { } }
static string ComputeSha256Hex(string path) { using var sha = SHA256.Create(); using var fs = File.OpenRead(path); return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant(); }
static string HashText(string value) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant(); }
static string? TryReadJsonValue(string path, string level1, string level2) { try { if (!File.Exists(path)) return null; using var doc = JsonDocument.Parse(File.ReadAllText(path)); return doc.RootElement.GetProperty(level1).GetProperty(level2).GetString(); } catch { return null; } }
static string NormalizeEmail(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
static bool IsValidEmail(string email) => !string.IsNullOrWhiteSpace(email) && email.Contains('@') && email.Length <= 200;
static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
static string GenerateCardCode()
{
    var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    return $"{code[..4]}-{code[4..8]}-{code[8..12]}-{code[12..16]}";
}
static string ReadAdminHtml()
{
    var path = Path.Combine(AppContext.BaseDirectory, "AdminPage.html");
    return File.Exists(path)
        ? File.ReadAllText(path, Encoding.UTF8)
        : "<!doctype html><html><body><h1>Maccy 管理后台</h1><p>后台页面缺失。</p></body></html>";
}
sealed record EmailPasswordRequest(string? Email, string? Password);
sealed record RefreshRequest(string? RefreshToken);
sealed record RedeemRequest(string? Code);
sealed record GenerateCardsRequest(int Count, int DurationDays);
sealed record UpgradeUserRequest(string? EmailOrUserId, int DurationDays);
sealed record UserRecord(string Id, string Email, string PasswordHash);
sealed record RefreshTokenRecord(string TokenHash, string UserId, string ExpiresAt, string? RevokedAt);
sealed record CardRecord(string Code, int DurationDays, string? UsedBy);
sealed class AuthMarker;
