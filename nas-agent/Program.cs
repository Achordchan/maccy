using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = null;
});

builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = long.MaxValue;
});

var issuer = builder.Configuration["Auth:Issuer"] ?? "https://achord-maccy.authing.cn/oidc";
var metadataAddress = builder.Configuration["Auth:MetadataAddress"] ?? "https://achord-maccy.authing.cn/oidc/.well-known/openid-configuration";
var adminKey = builder.Configuration["Admin:Key"] ?? "change-me-in-production";

builder.Services.AddHttpClient();
builder.Services.AddSingleton(new OidcDiscovery(metadataAddress));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, UserInfoBearerHandler>(JwtBearerDefaults.AuthenticationScheme, _ => { });

builder.Services.AddAuthorization();

var app = builder.Build();

// Initialize database
var dbPath = Path.Combine(app.Configuration["Storage:Root"] ?? "/data", "subscriptions.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
await InitializeDatabaseAsync(dbPath);

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Text(
    "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"/><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/><title>Maccy NAS Agent</title></head><body style=\"font-family:system-ui,-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;max-width:720px;margin:40px auto;padding:0 16px\"><h1 style=\"margin:0 0 12px\">Maccy NAS Agent</h1><p style=\"margin:0 0 16px;color:#444\">This is a minimal status page.</p><a href=\"/health\" style=\"display:inline-block;background:#2563eb;color:#fff;text-decoration:none;padding:10px 14px;border-radius:8px\">状态检查</a> <a href=\"/admin\" style=\"display:inline-block;background:#059669;color:#fff;text-decoration:none;padding:10px 14px;border-radius:8px;margin-left:8px\">管理后台</a></body></html>",
    "text/html; charset=utf-8"));

app.MapGet("/health", () => Results.Ok(new { ok = true }));

// Admin panel HTML page
app.MapGet("/admin", () => Results.Text("""
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>Maccy 管理后台</title>
<style>
*{box-sizing:border-box}
body{font-family:system-ui,-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;max-width:900px;margin:0 auto;padding:16px;background:#f5f5f5}
.card{background:#fff;border-radius:12px;padding:20px;margin-bottom:16px;box-shadow:0 1px 3px rgba(0,0,0,.1)}
h1,h2,h3{margin:0 0 16px}
h1{font-size:24px}
h2{font-size:18px;color:#374151;border-bottom:1px solid #e5e7eb;padding-bottom:8px}
input,button,select{font-size:14px;padding:8px 12px;border:1px solid #d1d5db;border-radius:6px}
input{width:200px}
button{background:#2563eb;color:#fff;border:none;cursor:pointer}
button:hover{background:#1d4ed8}
button:disabled{background:#9ca3af;cursor:not-allowed}
button.danger{background:#dc2626}
button.danger:hover{background:#b91c1c}
table{width:100%;border-collapse:collapse;margin-top:12px}
th,td{text-align:left;padding:10px 8px;border-bottom:1px solid #e5e7eb}
th{background:#f9fafb;font-weight:600}
.status-ok{color:#059669}
.status-expired{color:#dc2626}
.copy-btn{background:#6b7280;padding:4px 8px;font-size:12px;margin-left:4px}
.toast{position:fixed;bottom:20px;right:20px;background:#1f2937;color:#fff;padding:12px 20px;border-radius:8px;opacity:0;transition:opacity .3s}
.toast.show{opacity:1}
</style>
</head>
<body>
<h1>Maccy 管理后台</h1>

<div class="card">
<h2>卡密生成</h2>
<div style="display:flex;gap:12px;align-items:center;flex-wrap:wrap">
<label>数量: <input type="number" id="cardCount" value="1" min="1" max="100" style="width:80px"></label>
<label>天数: <input type="number" id="cardDays" value="30" min="1" max="3650" style="width:80px"></label>
<button onclick="generateCards()">生成卡密</button>
</div>
<div id="cardResult" style="margin-top:12px"></div>
</div>

<div class="card">
<h2>手动升级用户</h2>
<div style="display:flex;gap:12px;align-items:center;flex-wrap:wrap">
<label>用户ID: <input type="text" id="upgradeUserId" placeholder="Authing sub" style="width:280px"></label>
<label>天数: <input type="number" id="upgradeDays" value="30" min="1" max="3650" style="width:80px"></label>
<button onclick="upgradeUser()">升级</button>
</div>
</div>

<div class="card">
<h2>订阅列表 <button onclick="loadSubscriptions()" style="margin-left:8px;padding:4px 8px;font-size:12px">刷新</button></h2>
<div id="subscriptionsTable">加载中...</div>
</div>

<div class="card">
<h2>卡密列表 <button onclick="loadCards()" style="margin-left:8px;padding:4px 8px;font-size:12px">刷新</button></h2>
<div id="cardsTable">加载中...</div>
</div>

<div id="toast" class="toast"></div>

<script>
const adminKey = 'achord666';
const headers = {'Authorization':'Bearer '+adminKey,'Content-Type':'application/json'};

function showToast(msg){
const t=document.getElementById('toast');
t.textContent=msg;
t.classList.add('show');
setTimeout(()=>t.classList.remove('show'),3000);
}

async function generateCards(){
const count=parseInt(document.getElementById('cardCount').value)||1;
const days=parseInt(document.getElementById('cardDays').value)||30;
const btn=event.target;
btn.disabled=true;
try{
const res=await fetch('/admin/card/generate',{method:'POST',headers,body:JSON.stringify({count,durationDays:days})});
const data=await res.json();
if(!res.ok){showToast(data.error||'生成失败');return}
const codes=data.codes||[];
document.getElementById('cardResult').innerHTML='<strong>生成的卡密:</strong><br>'+codes.map(c=>c+' <button class="copy-btn" onclick="navigator.clipboard.writeText(\''+c+'\')">复制</button>').join('<br>');
showToast('成功生成 '+codes.length+' 个卡密');
}catch(e){showToast('请求失败: '+e.message)}
finally{btn.disabled=false}
}

async function upgradeUser(){
const userId=document.getElementById('upgradeUserId').value.trim();
const days=parseInt(document.getElementById('upgradeDays').value)||30;
if(!userId){showToast('请输入用户ID');return}
const btn=event.target;
btn.disabled=true;
try{
const res=await fetch('/admin/user/upgrade',{method:'POST',headers,body:JSON.stringify({userId,durationDays:days})});
const data=await res.json();
if(!res.ok){showToast(data.error||'升级失败');return}
showToast('升级成功，有效期至: '+(data.expiresAt||''));
loadSubscriptions();
}catch(e){showToast('请求失败: '+e.message)}
finally{btn.disabled=false}
}

async function loadSubscriptions(){
const el=document.getElementById('subscriptionsTable');
try{
const res=await fetch('/admin/subscriptions/list',{headers});
const data=await res.json();
if(!res.ok){el.innerHTML='<p style="color:red">'+(data.error||'加载失败')+'</p>';return}
const list=data.subscriptions||[];
if(!list.length){el.innerHTML='<p style="color:#6b7280">暂无订阅记录</p>';return}
el.innerHTML='<table><thead><tr><th>用户ID</th><th>到期时间</th><th>状态</th><th>更新时间</th></tr></thead><tbody>'+
list.map(s=>{
const exp=new Date(s.expires_at);
const now=new Date();
const ok=exp>now;
return'<tr><td style="font-size:12px;word-break:break-all">'+s.user_id+'</td><td>'+exp.toLocaleString()+'</td><td class="'+(ok?'status-ok':'status-expired')+'">'+(ok?'有效':'已过期')+'</td><td>'+new Date(s.updated_at).toLocaleString()+'</td></tr>';
}).join('')+'</tbody></table>';
}catch(e){el.innerHTML='<p style="color:red">加载失败: '+e.message+'</p>'}
}

async function loadCards(){
const el=document.getElementById('cardsTable');
try{
const res=await fetch('/admin/cards/list',{headers});
const data=await res.json();
if(!res.ok){el.innerHTML='<p style="color:red">'+(data.error||'加载失败')+'</p>';return}
const list=data.cards||[];
if(!list.length){el.innerHTML='<p style="color:#6b7280">暂无卡密记录</p>';return}
el.innerHTML='<table><thead><tr><th>卡密</th><th>天数</th><th>状态</th><th>使用者</th><th>使用时间</th></tr></thead><tbody>'+
list.map(c=>{
const used=!!c.used_by;
return'<tr><td><code>'+c.code+'</code></td><td>'+c.duration_days+'天</td><td class="'+(used?'status-expired':'status-ok')+'">'+(used?'已使用':'未使用')+'</td><td>'+(c.used_by||'-')+'</td><td>'+(c.used_at?new Date(c.used_at).toLocaleString():'-')+'</td></tr>';
}).join('')+'</tbody></table>';
}catch(e){el.innerHTML='<p style="color:red">加载失败: '+e.message+'</p>'}
}

loadSubscriptions();
loadCards();
</script>
</body>
</html>
""", "text/html; charset=utf-8"));

// Subscription status API
app.MapGet("/subscription/status", async (HttpContext ctx) =>
{
    var sub = GetSubject(ctx.User);
    if (sub is null)
        return Results.Unauthorized();

    var subscription = await GetSubscriptionAsync(dbPath, sub);
    if (subscription is null)
        return Results.Ok(new { subscribed = false, expiresAt = (string?)null });

    var expiresAt = DateTimeOffset.Parse(subscription);
    var isExpired = expiresAt < DateTimeOffset.UtcNow;

    return Results.Ok(new
    {
        subscribed = !isExpired,
        expiresAt = subscription
    });
}).RequireAuthorization();

// Card redeem API
app.MapPost("/card/redeem", async (HttpContext ctx) =>
{
    var sub = GetSubject(ctx.User);
    if (sub is null)
        return Results.Unauthorized();

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    using var doc = JsonDocument.Parse(body);
    if (!doc.RootElement.TryGetProperty("code", out var codeEl))
        return Results.BadRequest(new { error = "missing code" });

    var code = codeEl.GetString()?.Trim();
    if (string.IsNullOrWhiteSpace(code))
        return Results.BadRequest(new { error = "invalid code" });

    var card = await GetCardAsync(dbPath, code);
    if (card is null)
        return Results.BadRequest(new { error = "卡密不存在或已使用" });

    if (!string.IsNullOrEmpty(card.Value.UsedBy))
        return Results.BadRequest(new { error = "卡密已被使用" });

    // Mark card as used and extend subscription
    var now = DateTimeOffset.UtcNow.ToString("O");
    var newExpiresAt = (await GetSubscriptionAsync(dbPath, sub)) switch
    {
        string existing when DateTimeOffset.Parse(existing) > DateTimeOffset.UtcNow
            => DateTimeOffset.Parse(existing).AddDays(card.Value.DurationDays).ToString("O"),
        _ => DateTimeOffset.UtcNow.AddDays(card.Value.DurationDays).ToString("O")
    };

    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    // Mark card used
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "UPDATE cards SET used_at = $now, used_by = $sub WHERE code = $code";
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$sub", sub);
        cmd.Parameters.AddWithValue("$code", code);
        await cmd.ExecuteNonQueryAsync();
    }

    // Upsert subscription
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
INSERT INTO subscriptions (user_id, expires_at, created_at, updated_at)
VALUES ($sub, $expires, $now, $now)
ON CONFLICT(user_id) DO UPDATE SET expires_at = $expires, updated_at = $now";
        cmd.Parameters.AddWithValue("$sub", sub);
        cmd.Parameters.AddWithValue("$expires", newExpiresAt);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();

    return Results.Ok(new { success = true, expiresAt = newExpiresAt });
}).RequireAuthorization();

// Admin: Generate cards
app.MapPost("/admin/card/generate", async (HttpContext ctx) =>
{
    var auth = ctx.Request.Headers.Authorization.ToString();
    if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return Results.Unauthorized();
    var key = auth[7..].Trim();
    if (key != adminKey)
        return Results.Unauthorized();

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    using var doc = JsonDocument.Parse(body);

    var count = doc.RootElement.TryGetProperty("count", out var countEl) ? countEl.GetInt32() : 1;
    var durationDays = doc.RootElement.TryGetProperty("durationDays", out var durEl) ? durEl.GetInt32() : 30;

    var codes = new List<string>();
    for (var i = 0; i < count; i++)
        codes.Add(GenerateCardCode());

    var now = DateTimeOffset.UtcNow.ToString("O");
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    foreach (var code in codes)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO cards (code, duration_days, created_at) VALUES ($code, $days, $now)";
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$days", durationDays);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync();
    }

    return Results.Ok(new { codes, durationDays });
});

// Admin: List subscriptions
app.MapGet("/admin/subscriptions/list", async (HttpContext ctx) =>
{
    var auth = ctx.Request.Headers.Authorization.ToString();
    if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return Results.Unauthorized();
    var key = auth[7..].Trim();
    if (key != adminKey)
        return Results.Unauthorized();

    var list = new List<object>();
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT user_id, expires_at, created_at, updated_at FROM subscriptions ORDER BY updated_at DESC";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        list.Add(new
        {
            user_id = reader.GetString(0),
            expires_at = reader.GetString(1),
            created_at = reader.GetString(2),
            updated_at = reader.GetString(3)
        });
    }

    return Results.Ok(new { subscriptions = list });
});

// Admin: List cards
app.MapGet("/admin/cards/list", async (HttpContext ctx) =>
{
    var auth = ctx.Request.Headers.Authorization.ToString();
    if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return Results.Unauthorized();
    var key = auth[7..].Trim();
    if (key != adminKey)
        return Results.Unauthorized();

    var list = new List<object>();
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT code, duration_days, used_by, used_at, created_at FROM cards ORDER BY created_at DESC";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        list.Add(new
        {
            code = reader.GetString(0),
            duration_days = reader.GetInt32(1),
            used_by = reader.IsDBNull(2) ? null : reader.GetString(2),
            used_at = reader.IsDBNull(3) ? null : reader.GetString(3),
            created_at = reader.GetString(4)
        });
    }

    return Results.Ok(new { cards = list });
});

// Admin: Upgrade user manually
app.MapPost("/admin/user/upgrade", async (HttpContext ctx) =>
{
    var auth = ctx.Request.Headers.Authorization.ToString();
    if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return Results.Unauthorized();
    var key = auth[7..].Trim();
    if (key != adminKey)
        return Results.Unauthorized();

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    using var doc = JsonDocument.Parse(body);

    if (!doc.RootElement.TryGetProperty("userId", out var userIdEl))
        return Results.BadRequest(new { error = "missing userId" });
    if (!doc.RootElement.TryGetProperty("durationDays", out var durEl))
        return Results.BadRequest(new { error = "missing durationDays" });

    var userId = userIdEl.GetString()?.Trim();
    var durationDays = durEl.GetInt32();
    if (string.IsNullOrWhiteSpace(userId))
        return Results.BadRequest(new { error = "invalid userId" });

    var now = DateTimeOffset.UtcNow.ToString("O");
    var existing = await GetSubscriptionAsync(dbPath, userId);
    var newExpiresAt = existing switch
    {
        string e when DateTimeOffset.Parse(e) > DateTimeOffset.UtcNow
            => DateTimeOffset.Parse(e).AddDays(durationDays).ToString("O"),
        _ => DateTimeOffset.UtcNow.AddDays(durationDays).ToString("O")
    };

    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
INSERT INTO subscriptions (user_id, expires_at, created_at, updated_at)
VALUES ($sub, $expires, $now, $now)
ON CONFLICT(user_id) DO UPDATE SET expires_at = $expires, updated_at = $now";
    cmd.Parameters.AddWithValue("$sub", userId);
    cmd.Parameters.AddWithValue("$expires", newExpiresAt);
    cmd.Parameters.AddWithValue("$now", now);
    await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new { success = true, expiresAt = newExpiresAt });
});

// Sync APIs with subscription check
app.MapGet("/sync/manifest", async (HttpContext ctx) =>
{
    var sub = GetSubject(ctx.User);
    if (sub is null)
        return Results.Unauthorized();

    if (!await CheckSubscriptionAsync(dbPath, sub))
        return Results.Json(new { error = "订阅已过期" }, statusCode: 403);

    var root = GetUserRoot(app.Configuration, sub);
    var path = Path.Combine(root, "manifest.json");
    if (!File.Exists(path))
        return Results.NotFound();

    return Results.File(path, "application/json; charset=utf-8");
}).RequireAuthorization();

app.MapGet("/sync/snapshot", async (HttpContext ctx) =>
{
    var sub = GetSubject(ctx.User);
    if (sub is null)
        return Results.Unauthorized();

    if (!await CheckSubscriptionAsync(dbPath, sub))
        return Results.Json(new { error = "订阅已过期" }, statusCode: 403);

    var root = GetUserRoot(app.Configuration, sub);
    var path = Path.Combine(root, "snapshot.sqlite");
    if (!File.Exists(path))
        return Results.NotFound();

    return Results.File(path, "application/octet-stream", "snapshot.sqlite");
}).RequireAuthorization();

app.MapPut("/sync/snapshot", async (HttpContext ctx) =>
{
    var sub = GetSubject(ctx.User);
    if (sub is null)
        return Results.Unauthorized();

    if (!await CheckSubscriptionAsync(dbPath, sub))
        return Results.Json(new { error = "订阅已过期" }, statusCode: 403);

    var root = GetUserRoot(app.Configuration, sub);
    Directory.CreateDirectory(root);

    var snapshotPath = Path.Combine(root, "snapshot.sqlite");
    var tmpPath = snapshotPath + ".upload";

    TryDelete(tmpPath);

    await using (var fs = File.Create(tmpPath))
    {
        await ctx.Request.Body.CopyToAsync(fs, ctx.RequestAborted);
    }

    var sha256 = ComputeSha256Hex(tmpPath);
    var size = new FileInfo(tmpPath).Length;
    var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

    var manifestPath = Path.Combine(root, "manifest.json");
    var previousVersion = TryReadJsonValue(manifestPath, "latest", "version");

    if (File.Exists(snapshotPath))
    {
        var backupVersion = string.IsNullOrWhiteSpace(previousVersion) ? "unknown" : previousVersion;
        var backupDir = Path.Combine(root, "backups", backupVersion);
        Directory.CreateDirectory(backupDir);

        TryMove(snapshotPath, Path.Combine(backupDir, "snapshot.sqlite"));
        TryMove(manifestPath, Path.Combine(backupDir, "manifest.json"));
    }

    TryMove(tmpPath, snapshotPath);

    var manifestJson = $"{{\n  \"schema\": 1,\n  \"issuer\": \"{JsonEscape(issuer)}\",\n  \"subject\": \"{JsonEscape(sub)}\",\n  \"latest\": {{\n    \"version\": \"{JsonEscape(version)}\",\n    \"updatedAt\": \"{DateTimeOffset.UtcNow:O}\",\n    \"deviceId\": \"\",\n    \"createdFromVersion\": \"\",\n    \"snapshot\": {{\n      \"type\": \"sqlite\",\n      \"fileName\": \"snapshot.sqlite\",\n      \"sha256\": \"{JsonEscape(sha256)}\",\n      \"size\": {size}\n    }}\n  }}\n}}\n";

    await File.WriteAllTextAsync(manifestPath, manifestJson, Encoding.UTF8, ctx.RequestAborted);

    return Results.Ok(new { version, sha256, size });
}).RequireAuthorization();

app.Run();

static async Task InitializeDatabaseAsync(string dbPath)
{
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
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
";
    await cmd.ExecuteNonQueryAsync();
}

static async Task<(string Code, int DurationDays, string? UsedBy)?> GetCardAsync(string dbPath, string code)
{
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT code, duration_days, used_by FROM cards WHERE code = $code";
    cmd.Parameters.AddWithValue("$code", code);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
        return (reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    return null;
}

static async Task<string?> GetSubscriptionAsync(string dbPath, string userId)
{
    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT expires_at FROM subscriptions WHERE user_id = $userId";
    cmd.Parameters.AddWithValue("$userId", userId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
        return reader.GetString(0);
    return null;
}

static async Task<bool> CheckSubscriptionAsync(string dbPath, string userId)
{
    var expiresAt = await GetSubscriptionAsync(dbPath, userId);
    if (expiresAt is null)
        return false;
    return DateTimeOffset.Parse(expiresAt) > DateTimeOffset.UtcNow;
}

static string GenerateCardCode()
{
    var bytes = RandomNumberGenerator.GetBytes(8);
    var code = Convert.ToHexString(bytes);
    return $"{code[..4]}-{code[4..8]}-{code[8..12]}-{code[12..16]}";
}

static string? GetSubject(ClaimsPrincipal user)
{
    return user.FindFirst("sub")?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}

static string GetUserRoot(IConfiguration config, string sub)
{
    var dataRoot = config["Storage:Root"] ?? "/data";
    var safe = MakeSafeSegment(sub);
    return Path.Combine(dataRoot, safe);
}

static string MakeSafeSegment(string input)
{
    if (string.IsNullOrWhiteSpace(input))
        return "unknown";

    var sb = new StringBuilder(input.Length);
    foreach (var ch in input)
    {
        if ((ch >= 'a' && ch <= 'z') ||
            (ch >= 'A' && ch <= 'Z') ||
            (ch >= '0' && ch <= '9') ||
            ch == '-' || ch == '_')
        {
            sb.Append(ch);
        }
        else
        {
            sb.Append('_');
        }
    }

    var s = sb.ToString();
    if (s.Length == 0)
        return "unknown";

    return s;
}

static void TryDelete(string path)
{
    try
    {
        if (File.Exists(path))
            File.Delete(path);
    }
    catch
    {
    }
}

static void TryMove(string src, string dest)
{
    try
    {
        if (!File.Exists(src))
            return;

        TryDelete(dest);
        File.Move(src, dest);
    }
    catch
    {
    }
}

static string ComputeSha256Hex(string path)
{
    using var sha = SHA256.Create();
    using var fs = File.OpenRead(path);
    var hash = sha.ComputeHash(fs);
    var sb = new StringBuilder(hash.Length * 2);
    foreach (var b in hash)
        sb.Append(b.ToString("x2"));
    return sb.ToString();
}

static string? TryReadJsonValue(string jsonPath, string level1, string level2)
{
    try
    {
        if (!File.Exists(jsonPath))
            return null;

        var text = File.ReadAllText(jsonPath);
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty(level1, out var p1))
            return null;
        if (!p1.TryGetProperty(level2, out var p2))
            return null;
        return p2.ValueKind == System.Text.Json.JsonValueKind.String ? p2.GetString() : p2.ToString();
    }
    catch
    {
        return null;
    }
}

static string JsonEscape(string s)
{
    return s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "")
        .Replace("\n", "");
}

sealed class OidcDiscovery(string metadataAddress)
{
    private readonly string _metadataAddress = metadataAddress;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _userInfoEndpoint;

    public async Task<string> GetUserInfoEndpointAsync(HttpClient http, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_userInfoEndpoint))
            return _userInfoEndpoint;

        await _gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(_userInfoEndpoint))
                return _userInfoEndpoint;

            using var req = new HttpRequestMessage(HttpMethod.Get, _metadataAddress);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("userinfo_endpoint", out var u) || u.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("missing userinfo_endpoint");

            _userInfoEndpoint = u.GetString();
            if (string.IsNullOrWhiteSpace(_userInfoEndpoint))
                throw new InvalidOperationException("userinfo_endpoint is empty");

            return _userInfoEndpoint;
        }
        finally
        {
            _gate.Release();
        }
    }
}

sealed class UserInfoBearerHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public UserInfoBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISystemClock clock)
        : base(options, logger, encoder, clock)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            Context.Items["auth_failure"] = "MissingBearer";
            return AuthenticateResult.NoResult();
        }

        var token = auth[7..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Context.Items["auth_failure"] = "EmptyBearer";
            return AuthenticateResult.Fail("empty bearer token");
        }

        try
        {
            var http = Context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
            var discovery = Context.RequestServices.GetRequiredService<OidcDiscovery>();
            var endpoint = await discovery.GetUserInfoEndpointAsync(http, Context.RequestAborted);

            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, Context.RequestAborted);
            if (!resp.IsSuccessStatusCode)
            {
                Context.Items["auth_failure"] = "UserInfoHttp" + (int)resp.StatusCode;
                return AuthenticateResult.Fail("userinfo rejected token");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(Context.RequestAborted);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: Context.RequestAborted);
            var sub = doc.RootElement.TryGetProperty("sub", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(sub))
            {
                Context.Items["auth_failure"] = "UserInfoNoSub";
                return AuthenticateResult.Fail("userinfo missing sub");
            }

            var identity = new ClaimsIdentity(new[]
            {
                new Claim("sub", sub),
                new Claim(ClaimTypes.NameIdentifier, sub),
            }, Scheme.Name);

            var principal = new ClaimsPrincipal(identity);
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }
        catch (Exception ex)
        {
            Context.Items["auth_failure"] = ex.GetType().Name;
            return AuthenticateResult.Fail(ex);
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (!Response.HasStarted)
        {
            try
            {
                if (Context.Items.TryGetValue("auth_failure", out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
                    Response.Headers["X-Auth-Failure"] = s;
            }
            catch
            {
            }
        }

        return base.HandleChallengeAsync(properties);
    }
}
