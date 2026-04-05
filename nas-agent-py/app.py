from __future__ import annotations

import asyncio
import base64
import hashlib
import json
import os
import secrets
import shutil
import sqlite3
import zlib
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any
from uuid import uuid4

import jwt
from fastapi import FastAPI, Header, HTTPException, Request
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse, StreamingResponse
from fastapi.staticfiles import StaticFiles

from password_hasher import AspNetPasswordHasher, PasswordVerificationResult


UTC = timezone.utc
TOKEN_LEEWAY_SECONDS = 30
ACCESS_TOKEN_HOURS = 2
REFRESH_TOKEN_DAYS = 30
ADMIN_TOKEN_HOURS = 12
ADMIN_AUDIENCE = "maccy-admin"


def load_dotenv_file(dotenv_path: Path) -> None:
    if not dotenv_path.exists():
        return

    for raw_line in dotenv_path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        if not key or key in os.environ:
            continue
        value = value.strip().strip("'\"")
        os.environ[key] = value


@dataclass(frozen=True, slots=True)
class Settings:
    storage_root: Path
    admin_key: str
    admin_email: str
    admin_password: str
    issuer: str
    audience: str
    signing_key: str


@dataclass(frozen=True, slots=True)
class TierPolicy:
    name: str
    retention_days: int
    storage_limit_bytes: int


@dataclass(frozen=True, slots=True)
class StorageUsage:
    current_snapshot_bytes: int
    backup_bytes: int
    blob_bytes: int
    total_bytes: int
    backup_count: int


@dataclass(frozen=True, slots=True)
class BackupEntry:
    directory_path: Path
    created_at: datetime
    size_bytes: int


@dataclass(frozen=True, slots=True)
class BackupPruneResult:
    usage: StorageUsage
    removed_bytes: int
    removed_count: int


@dataclass(frozen=True, slots=True)
class SyncAccessResult:
    allowed: bool
    error: JSONResponse | None
    tier: TierPolicy | None
    user_root: Path | None


class SyncEventBroker:
    def __init__(self) -> None:
        self._subscribers: dict[str, set[asyncio.Queue[str]]] = {}
        self._lock = asyncio.Lock()

    async def subscribe(self, user_id: str) -> asyncio.Queue[str]:
        queue: asyncio.Queue[str] = asyncio.Queue(maxsize=32)
        async with self._lock:
            subscribers = self._subscribers.setdefault(user_id, set())
            subscribers.add(queue)
        return queue

    async def unsubscribe(self, user_id: str, queue: asyncio.Queue[str]) -> None:
        async with self._lock:
            subscribers = self._subscribers.get(user_id)
            if not subscribers:
                return
            subscribers.discard(queue)
            if not subscribers:
                self._subscribers.pop(user_id, None)

    async def publish(self, user_id: str, event_name: str, data: dict[str, Any]) -> None:
        payload = format_sse_event(event_name, data)
        async with self._lock:
            subscribers = list(self._subscribers.get(user_id, ()))

        for queue in subscribers:
            if queue.full():
                try:
                    queue.get_nowait()
                except asyncio.QueueEmpty:
                    pass

            try:
                queue.put_nowait(payload)
            except asyncio.QueueFull:
                pass


APP_DIR = Path(__file__).resolve().parent
load_dotenv_file(APP_DIR / ".env")
PASSWORD_HASHER = AspNetPasswordHasher()
SETTINGS = Settings(
    storage_root=Path(os.getenv("STORAGE_ROOT", "/data")).expanduser(),
    admin_key=os.getenv("ADMIN_KEY", "change-me"),
    admin_email=os.getenv("ADMIN_EMAIL", "admin@example.com"),
    admin_password=os.getenv("ADMIN_PASSWORD", "change-me"),
    issuer=os.getenv("AUTH_ISSUER", "maccy-self-hosted"),
    audience=os.getenv("AUTH_AUDIENCE", "maccy-client"),
    signing_key=os.getenv("AUTH_SIGNING_KEY", "change-me-to-a-long-random-secret"),
)
DB_PATH = SETTINGS.storage_root / "subscriptions.db"
SYNC_EVENT_BROKER = SyncEventBroker()


app = FastAPI()
app.mount("/static", StaticFiles(directory=str(APP_DIR / "static")), name="static")


@app.on_event("startup")
def startup() -> None:
    DB_PATH.parent.mkdir(parents=True, exist_ok=True)
    init_db(DB_PATH)


@app.get("/", response_class=HTMLResponse)
async def index() -> str:
    return (
        "<!doctype html><html><body style=\"font-family:system-ui;padding:24px\">"
        "<h1>Maccy Agent</h1><p><a href=\"/health\">health</a> | "
        "<a href=\"/admin\">admin</a></p></body></html>"
    )


@app.get("/health")
async def health() -> dict[str, bool]:
    return {"ok": True}


@app.get("/admin", response_class=HTMLResponse)
async def admin_page() -> str:
    return read_admin_html()


@app.post("/admin/auth/login")
async def admin_auth_login(request: Request) -> JSONResponse:
    payload = await read_json(request)
    if payload is None:
        return error_json(400, "invalid request")

    if not is_admin_password_login_configured(SETTINGS):
        return error_json(503, "admin password login not configured")

    email = normalize_email(payload.get("email"))
    password = str(payload.get("password") or "")
    if email != normalize_email(SETTINGS.admin_email) or password != SETTINGS.admin_password:
        raise HTTPException(status_code=401)

    return JSONResponse(issue_admin_token(SETTINGS))


@app.get("/admin/auth/me")
async def admin_auth_me(authorization: str | None = Header(default=None)) -> JSONResponse:
    admin = require_admin(authorization)
    return JSONResponse(
        {
            "email": admin["email"],
            "authType": admin["authType"],
            "accessTokenExpiresAt": admin.get("accessTokenExpiresAt"),
        }
    )


@app.post("/auth/register")
async def auth_register(request: Request) -> JSONResponse:
    payload = await read_json(request)
    if payload is None:
        return error_json(400, "invalid request")

    email = normalize_email(payload.get("email"))
    password = str(payload.get("password") or "")
    if not is_valid_email(email):
        return error_json(400, "invalid email")
    if len(password) < 8:
        return error_json(400, "password must be at least 8 characters")
    if get_user_by_email(DB_PATH, email) is not None:
        return error_json(400, "email already registered")

    now = utc_now_text()
    user_id = uuid4().hex
    with db_connection(DB_PATH) as conn:
        conn.execute(
            "INSERT INTO users (id,email,password_hash,tier,created_at,updated_at) VALUES (?,?,?,?,?,?)",
            (user_id, email, PASSWORD_HASHER.hash_password(password), None, now, now),
        )
        conn.commit()

    return JSONResponse(issue_tokens(DB_PATH, user_id, email, SETTINGS))


@app.post("/auth/login")
async def auth_login(request: Request) -> JSONResponse:
    payload = await read_json(request)
    if payload is None:
        return error_json(400, "invalid request")

    email = normalize_email(payload.get("email"))
    password = str(payload.get("password") or "")
    user = get_user_by_email(DB_PATH, email)
    if user is None:
        return error_json(400, "invalid email or password")

    verify = PASSWORD_HASHER.verify_hashed_password(user["password_hash"], password)
    if verify == PasswordVerificationResult.FAILED:
        return error_json(400, "invalid email or password")

    return JSONResponse(issue_tokens(DB_PATH, user["id"], user["email"], SETTINGS))


@app.post("/auth/refresh")
async def auth_refresh(request: Request) -> JSONResponse:
    payload = await read_json(request)
    refresh_token = None if payload is None else payload.get("refreshToken")
    if not isinstance(refresh_token, str) or not refresh_token.strip():
        return error_json(400, "missing refresh token")

    token_hash = hash_text(refresh_token.strip())
    stored = get_refresh_token(DB_PATH, token_hash)
    if stored is None:
        raise HTTPException(status_code=401)

    if stored["revoked_at"] is not None or parse_utc(stored["expires_at"]) <= now_utc():
        raise HTTPException(status_code=401)

    user = get_user_by_id(DB_PATH, stored["user_id"])
    if user is None:
        raise HTTPException(status_code=401)

    revoke_refresh_token(DB_PATH, token_hash)
    return JSONResponse(issue_tokens(DB_PATH, user["id"], user["email"], SETTINGS))


@app.get("/auth/me")
async def auth_me(authorization: str | None = Header(default=None)) -> JSONResponse:
    user = require_user(authorization)
    return JSONResponse({"id": user["id"], "email": user["email"]})


@app.get("/subscription/status")
async def subscription_status(authorization: str | None = Header(default=None)) -> JSONResponse:
    user = require_user(authorization)
    expires_at = get_subscription(DB_PATH, user["id"])
    subscribed = expires_at is not None and parse_utc(expires_at) > now_utc()
    tier = get_tier_policy(user["tier"])
    usage = get_storage_usage(get_user_root(SETTINGS.storage_root, user["id"]))
    over_limit = tier is not None and usage.total_bytes > tier.storage_limit_bytes
    return JSONResponse(
        {
            "subscribed": subscribed,
            "expiresAt": expires_at,
            "tier": None if tier is None else tier.name,
            "storageBytes": usage.total_bytes,
            "storageLimitBytes": None if tier is None else tier.storage_limit_bytes,
            "retentionDays": None if tier is None else tier.retention_days,
            "overLimit": over_limit,
        }
    )


@app.post("/card/redeem")
async def redeem_card(request: Request, authorization: str | None = Header(default=None)) -> JSONResponse:
    user = require_user(authorization)
    payload = await read_json(request)
    code = None if payload is None else payload.get("code")
    if not isinstance(code, str) or not code.strip():
        return error_json(400, "missing code")

    code = code.strip().upper()
    card = get_card(DB_PATH, code)
    if card is None or card["used_by"]:
        return error_json(400, "card not found or used")

    now = now_utc()
    current_expires_at = get_subscription(DB_PATH, user["id"])
    if current_expires_at is not None and parse_utc(current_expires_at) > now:
        expires_at = format_utc(parse_utc(current_expires_at) + timedelta(days=int(card["duration_days"])))
    else:
        expires_at = format_utc(now + timedelta(days=int(card["duration_days"])))

    with db_connection(DB_PATH) as conn:
        conn.execute(
            "UPDATE cards SET used_at=?, used_by=? WHERE code=?",
            (format_utc(now), user["id"], code),
        )
        upsert_subscription(conn, user["id"], expires_at)
        conn.commit()

    return JSONResponse({"success": True, "expiresAt": expires_at})


@app.get("/sync/manifest")
async def sync_manifest(authorization: str | None = Header(default=None)) -> FileResponse:
    user = require_user(authorization)
    access = validate_sync_access(DB_PATH, SETTINGS.storage_root, user["id"])
    if not access.allowed:
        return access.error  # type: ignore[return-value]

    path = access.user_root / "manifest.json"
    if not path.exists():
        raise HTTPException(status_code=404)
    return FileResponse(path, media_type="application/json; charset=utf-8")


@app.get("/sync/snapshot")
async def sync_snapshot(request: Request, authorization: str | None = Header(default=None)) -> Any:
    user = require_user(authorization)
    access = validate_sync_access(DB_PATH, SETTINGS.storage_root, user["id"])
    if not access.allowed:
        return access.error  # type: ignore[return-value]

    path = access.user_root / "snapshot.sqlite"
    if not path.exists():
        raise HTTPException(status_code=404)
    if accepts_gzip(request):
        return StreamingResponse(
            stream_gzip_file(path),
            media_type="application/octet-stream",
            headers={
                "Content-Encoding": "gzip",
                "Vary": "Accept-Encoding",
                "Content-Disposition": 'attachment; filename="snapshot.sqlite"',
            },
        )
    return FileResponse(path, media_type="application/octet-stream", filename="snapshot.sqlite")


@app.get("/sync/events")
async def sync_events(request: Request, authorization: str | None = Header(default=None)) -> StreamingResponse:
    user = require_user(authorization)
    access = validate_sync_access(DB_PATH, SETTINGS.storage_root, user["id"])
    if not access.allowed:
        return access.error  # type: ignore[return-value]

    queue = await SYNC_EVENT_BROKER.subscribe(user["id"])

    async def event_stream() -> Any:
        try:
            yield format_sse_event("connected", {"serverTime": utc_now_text()})
            while True:
                if await request.is_disconnected():
                    break

                try:
                    payload = await asyncio.wait_for(queue.get(), timeout=20)
                    yield payload
                except asyncio.TimeoutError:
                    yield ": keepalive\n\n"
        finally:
            await SYNC_EVENT_BROKER.unsubscribe(user["id"], queue)

    return StreamingResponse(
        event_stream(),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
            "X-Accel-Buffering": "no",
        },
    )


@app.post("/sync/blobs/check")
async def sync_blob_check(request: Request, authorization: str | None = Header(default=None)) -> JSONResponse:
    user = require_user(authorization)
    access = validate_sync_access(DB_PATH, SETTINGS.storage_root, user["id"])
    if not access.allowed:
        return access.error  # type: ignore[return-value]

    payload = await read_json(request) or {}
    hashes = payload.get("hashes")
    if not isinstance(hashes, list):
        return error_json(400, "missing hashes")

    missing: list[str] = []
    for value in hashes:
        normalized = normalize_blob_sha256(value)
        if normalized is None:
            continue
        if not get_blob_path(access.user_root, normalized).exists():
            missing.append(normalized)

    return JSONResponse({"missing": missing})


@app.put("/sync/blobs/{blob_sha256}")
async def upload_blob(blob_sha256: str, request: Request, authorization: str | None = Header(default=None)) -> JSONResponse:
    user = require_user(authorization)
    access = validate_sync_access(DB_PATH, SETTINGS.storage_root, user["id"])
    if not access.allowed:
        return access.error  # type: ignore[return-value]

    normalized = normalize_blob_sha256(blob_sha256)
    if normalized is None:
        return error_json(400, "invalid blob hash")

    blob_path = get_blob_path(access.user_root, normalized)
    if blob_path.exists():
        return JSONResponse({"sha256": normalized, "size": blob_path.stat().st_size, "existing": True})

    tmp_path = Path(str(blob_path) + ".upload")
    tmp_path.parent.mkdir(parents=True, exist_ok=True)
    try_delete_file(tmp_path)
    content_encoding = (request.headers.get("content-encoding") or "").lower()

    try:
        if "gzip" in content_encoding:
            decompressor = zlib.decompressobj(16 + zlib.MAX_WBITS)
            with tmp_path.open("wb") as handle:
                async for chunk in request.stream():
                    if not chunk:
                        continue
                    data = decompressor.decompress(chunk)
                    if data:
                        handle.write(data)
                tail = decompressor.flush()
                if tail:
                    handle.write(tail)
        else:
            with tmp_path.open("wb") as handle:
                async for chunk in request.stream():
                    if chunk:
                        handle.write(chunk)
    except zlib.error:
        try_delete_file(tmp_path)
        return error_json(400, "invalid gzip payload")

    if not tmp_path.exists():
        return error_json(400, "missing blob payload")

    actual_sha256 = compute_sha256_hex(tmp_path)
    if actual_sha256 != normalized:
        try_delete_file(tmp_path)
        return error_json(400, "blob sha256 mismatch")

    blob_path.parent.mkdir(parents=True, exist_ok=True)
    try_move(tmp_path, blob_path)
    return JSONResponse({"sha256": normalized, "size": blob_path.stat().st_size, "existing": False})


@app.get("/sync/blobs/{blob_sha256}")
async def download_blob(blob_sha256: str, request: Request, authorization: str | None = Header(default=None)) -> Any:
    user = require_user(authorization)
    access = validate_sync_access(DB_PATH, SETTINGS.storage_root, user["id"])
    if not access.allowed:
        return access.error  # type: ignore[return-value]

    normalized = normalize_blob_sha256(blob_sha256)
    if normalized is None:
        return error_json(400, "invalid blob hash")

    path = get_blob_path(access.user_root, normalized)
    if not path.exists():
        raise HTTPException(status_code=404)

    if accepts_gzip(request):
        return StreamingResponse(
            stream_gzip_file(path),
            media_type="application/octet-stream",
            headers={"Content-Encoding": "gzip", "Vary": "Accept-Encoding"},
        )

    return FileResponse(path, media_type="application/octet-stream")


@app.put("/sync/snapshot")
async def upload_snapshot(request: Request, authorization: str | None = Header(default=None)) -> JSONResponse:
    user = require_user(authorization)
    access = validate_sync_access(DB_PATH, SETTINGS.storage_root, user["id"])
    if not access.allowed:
        return access.error  # type: ignore[return-value]

    root = access.user_root
    tier = access.tier
    assert root is not None
    assert tier is not None
    root.mkdir(parents=True, exist_ok=True)

    snapshot_path = root / "snapshot.sqlite"
    tmp_path = Path(str(snapshot_path) + ".upload")
    try_delete_file(tmp_path)
    content_encoding = (request.headers.get("content-encoding") or "").lower()

    try:
        if "gzip" in content_encoding:
            decompressor = zlib.decompressobj(16 + zlib.MAX_WBITS)
            with tmp_path.open("wb") as handle:
                async for chunk in request.stream():
                    if not chunk:
                        continue
                    data = decompressor.decompress(chunk)
                    if data:
                        handle.write(data)
                tail = decompressor.flush()
                if tail:
                    handle.write(tail)
        else:
            with tmp_path.open("wb") as handle:
                async for chunk in request.stream():
                    if chunk:
                        handle.write(chunk)
    except zlib.error:
        try_delete_file(tmp_path)
        return error_json(400, "invalid gzip payload")

    version = str(int(now_utc().timestamp() * 1000))
    sha256 = compute_sha256_hex(tmp_path)
    size = tmp_path.stat().st_size
    if size > tier.storage_limit_bytes:
        try_delete_file(tmp_path)
        return JSONResponse(
            status_code=413,
            content={
                "code": "snapshot_too_large",
                "error": "snapshot size exceeds tier limit",
                "tier": tier.name,
                "snapshotBytes": size,
                "storageLimitBytes": tier.storage_limit_bytes,
            },
        )

    manifest_path = root / "manifest.json"
    previous_version = try_read_json_value(manifest_path, "latest", "version")
    if snapshot_path.exists():
        backup_dir = root / "backups" / (previous_version or "unknown")
        backup_dir.mkdir(parents=True, exist_ok=True)
        try_move(snapshot_path, backup_dir / "snapshot.sqlite")
        try_move(manifest_path, backup_dir / "manifest.json")

    try_move(tmp_path, snapshot_path)
    if not snapshot_path.exists():
        try_delete_file(tmp_path)
        raise HTTPException(status_code=500)

    manifest = {
        "schema": 1,
        "issuer": SETTINGS.issuer,
        "subject": user["id"],
        "latest": {
            "version": version,
            "updatedAt": utc_now_text(),
            "deviceId": "",
            "createdFromVersion": "",
            "snapshot": {
                "type": "sqlite",
                "fileName": "snapshot.sqlite",
                "sha256": sha256,
                "size": size,
            },
        },
    }
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False), encoding="utf-8")

    prune = prune_backups_for_tier(root, tier)
    if prune.usage.total_bytes > tier.storage_limit_bytes:
        return JSONResponse(
            status_code=409,
            content={
                "code": "storage_limit_exceeded",
                "error": "storage usage exceeds tier limit after pruning",
                "tier": tier.name,
                "storageBytes": prune.usage.total_bytes,
                "storageLimitBytes": tier.storage_limit_bytes,
            },
        )

    await SYNC_EVENT_BROKER.publish(
        user["id"],
        "sync-updated",
        {
            "version": version,
            "updatedAt": manifest["latest"]["updatedAt"],
            "sha256": sha256,
            "size": size,
        },
    )

    return JSONResponse(
        {
            "version": version,
            "sha256": sha256,
            "size": size,
            "tier": tier.name,
            "storageBytes": prune.usage.total_bytes,
            "storageLimitBytes": tier.storage_limit_bytes,
            "removedBackupCount": prune.removed_count,
            "removedBackupBytes": prune.removed_bytes,
        }
    )


@app.get("/admin/users/list")
async def admin_users(authorization: str | None = Header(default=None)) -> JSONResponse:
    require_admin(authorization)
    return JSONResponse({"users": list_users(DB_PATH, SETTINGS.storage_root)})


@app.get("/admin/subscriptions/list")
async def admin_subscriptions(authorization: str | None = Header(default=None)) -> JSONResponse:
    require_admin(authorization)
    return JSONResponse({"subscriptions": list_subscriptions(DB_PATH, SETTINGS.storage_root)})


@app.get("/admin/cards/list")
async def admin_cards(authorization: str | None = Header(default=None)) -> JSONResponse:
    require_admin(authorization)
    return JSONResponse({"cards": list_cards(DB_PATH)})


@app.post("/admin/card/generate")
async def admin_generate_cards(request: Request, authorization: str | None = Header(default=None)) -> JSONResponse:
    require_admin(authorization)
    payload = await read_json(request) or {}
    count = clamp(as_int(payload.get("count"), 1), 1, 100)
    duration_days = clamp(as_int(payload.get("durationDays"), 30), 1, 3650)
    codes = [generate_card_code() for _ in range(count)]

    with db_connection(DB_PATH) as conn:
        for code in codes:
            conn.execute(
                "INSERT INTO cards (code,duration_days,created_at) VALUES (?,?,?)",
                (code, duration_days, utc_now_text()),
            )
        conn.commit()

    return JSONResponse({"codes": codes, "durationDays": duration_days})


@app.post("/admin/user/upgrade")
async def admin_upgrade_user(request: Request, authorization: str | None = Header(default=None)) -> JSONResponse:
    require_admin(authorization)
    payload = await read_json(request)
    if payload is None or not str(payload.get("emailOrUserId") or "").strip():
        return error_json(400, "missing emailOrUserId")

    user = resolve_user(DB_PATH, str(payload["emailOrUserId"]).strip())
    if user is None:
        return error_json(400, "user not found")

    duration_days = clamp(as_int(payload.get("durationDays"), 0), 0, 3650)
    raw_tier = payload.get("tier")
    has_tier_field = raw_tier is not None and str(raw_tier).strip() != ""
    normalized_tier = normalize_tier_name(raw_tier)
    if has_tier_field and normalized_tier is None:
        return JSONResponse(status_code=400, content={"error": "invalid tier", "supportedTiers": get_supported_tier_names()})
    if duration_days <= 0 and not has_tier_field:
        return error_json(400, "missing operation (durationDays or tier required)")

    expires_at = get_subscription(DB_PATH, user["id"])
    if duration_days > 0:
        if expires_at is not None and parse_utc(expires_at) > now_utc():
            expires_at = format_utc(parse_utc(expires_at) + timedelta(days=duration_days))
        else:
            expires_at = format_utc(now_utc() + timedelta(days=duration_days))

    with db_connection(DB_PATH) as conn:
        if duration_days > 0 and expires_at:
            upsert_subscription(conn, user["id"], expires_at)
        if has_tier_field:
            update_user_tier(conn, user["id"], normalized_tier)
        conn.commit()

    return JSONResponse(
        {
            "success": True,
            "expiresAt": expires_at,
            "email": user["email"],
            "userId": user["id"],
            "tier": normalized_tier or normalize_tier_name(user["tier"]),
        }
    )


@app.post("/admin/user/tier")
async def admin_set_tier(request: Request, authorization: str | None = Header(default=None)) -> JSONResponse:
    require_admin(authorization)
    payload = await read_json(request)
    if payload is None or not str(payload.get("emailOrUserId") or "").strip():
        return error_json(400, "missing emailOrUserId")

    user = resolve_user(DB_PATH, str(payload["emailOrUserId"]).strip())
    if user is None:
        return error_json(400, "user not found")

    normalized_tier = normalize_tier_name(payload.get("tier"))
    if payload.get("tier") not in (None, "") and normalized_tier is None:
        return JSONResponse(status_code=400, content={"error": "invalid tier", "supportedTiers": get_supported_tier_names()})

    with db_connection(DB_PATH) as conn:
        update_user_tier(conn, user["id"], normalized_tier)
        conn.commit()

    return JSONResponse({"success": True, "userId": user["id"], "email": user["email"], "tier": normalized_tier})


@app.post("/admin/user/reset-password")
async def admin_reset_password(request: Request, authorization: str | None = Header(default=None)) -> JSONResponse:
    require_admin(authorization)
    payload = await read_json(request)
    if payload is None or not str(payload.get("emailOrUserId") or "").strip():
        return error_json(400, "missing emailOrUserId")

    user = resolve_user(DB_PATH, str(payload["emailOrUserId"]).strip())
    if user is None:
        return error_json(400, "user not found")

    new_password = str(payload.get("newPassword") or "")
    if len(new_password) < 8:
        return error_json(400, "password must be at least 8 characters")

    with db_connection(DB_PATH) as conn:
        update_user_password(conn, user["id"], new_password)
        revoke_refresh_tokens_for_user(conn, user["id"])
        conn.commit()

    return JSONResponse({"success": True, "userId": user["id"], "email": user["email"]})


def init_db(db_path: Path) -> None:
    with db_connection(db_path) as conn:
        conn.executescript(
            """
CREATE TABLE IF NOT EXISTS users (
  id TEXT PRIMARY KEY,
  email TEXT NOT NULL UNIQUE,
  password_hash TEXT NOT NULL,
  tier TEXT,
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
"""
        )
        ensure_column(conn, "users", "tier", "TEXT")
        conn.commit()


def db_connection(db_path: Path) -> sqlite3.Connection:
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    return conn


def ensure_column(conn: sqlite3.Connection, table_name: str, column_name: str, column_definition: str) -> None:
    rows = conn.execute(f"PRAGMA table_info({table_name})").fetchall()
    for row in rows:
        if str(row["name"]).lower() == column_name.lower():
            return
    conn.execute(f"ALTER TABLE {table_name} ADD COLUMN {column_name} {column_definition}")


def issue_tokens(db_path: Path, user_id: str, email: str, settings: Settings) -> dict[str, Any]:
    now = now_utc()
    access_expires_at = now + timedelta(hours=ACCESS_TOKEN_HOURS)
    refresh_expires_at = now + timedelta(days=REFRESH_TOKEN_DAYS)
    claims = {
        "sub": user_id,
        "email": email,
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier": user_id,
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress": email,
        "iss": settings.issuer,
        "aud": settings.audience,
        "nbf": int(now.timestamp()),
        "iat": int(now.timestamp()),
        "exp": int(access_expires_at.timestamp()),
    }
    access_token = jwt.encode(claims, settings.signing_key, algorithm="HS256")
    refresh_token = base64_url_encode(secrets.token_bytes(32))

    with db_connection(db_path) as conn:
        conn.execute(
            "INSERT INTO refresh_tokens (token_hash,user_id,expires_at,created_at,revoked_at) VALUES (?,?,?,?,NULL)",
            (hash_text(refresh_token), user_id, format_utc(refresh_expires_at), format_utc(now)),
        )
        conn.commit()

    return {
        "accessToken": access_token,
        "refreshToken": refresh_token,
        "accessTokenExpiresAt": format_utc(access_expires_at),
        "email": email,
        "userId": user_id,
    }


def issue_admin_token(settings: Settings) -> dict[str, Any]:
    now = now_utc()
    access_expires_at = now + timedelta(hours=ADMIN_TOKEN_HOURS)
    claims = {
        "sub": "admin",
        "email": normalize_email(settings.admin_email),
        "role": "admin",
        "iss": settings.issuer,
        "aud": ADMIN_AUDIENCE,
        "nbf": int(now.timestamp()),
        "iat": int(now.timestamp()),
        "exp": int(access_expires_at.timestamp()),
    }
    access_token = jwt.encode(claims, settings.signing_key, algorithm="HS256")
    return {
        "accessToken": access_token,
        "accessTokenExpiresAt": format_utc(access_expires_at),
        "email": normalize_email(settings.admin_email),
    }


def decode_access_token(token: str, settings: Settings) -> dict[str, Any]:
    return jwt.decode(
        token,
        settings.signing_key,
        algorithms=["HS256"],
        audience=settings.audience,
        issuer=settings.issuer,
        leeway=TOKEN_LEEWAY_SECONDS,
    )


def decode_admin_token(token: str, settings: Settings) -> dict[str, Any]:
    return jwt.decode(
        token,
        settings.signing_key,
        algorithms=["HS256"],
        audience=ADMIN_AUDIENCE,
        issuer=settings.issuer,
        leeway=TOKEN_LEEWAY_SECONDS,
    )


def require_user(authorization: str | None) -> sqlite3.Row:
    token = extract_bearer_token(authorization)
    if token is None:
        raise HTTPException(status_code=401)
    try:
        claims = decode_access_token(token, SETTINGS)
    except jwt.PyJWTError as exc:
        raise HTTPException(status_code=401) from exc

    user_id = str(
        claims.get("sub")
        or claims.get("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")
        or ""
    ).strip()
    if not user_id:
        raise HTTPException(status_code=401)

    user = get_user_by_id(DB_PATH, user_id)
    if user is None:
        raise HTTPException(status_code=401)
    return user


def require_admin(authorization: str | None) -> dict[str, Any]:
    token = extract_bearer_token(authorization)
    if token is None:
        raise HTTPException(status_code=401)

    if SETTINGS.admin_key and token == SETTINGS.admin_key:
        return {
            "email": normalize_email(SETTINGS.admin_email) or "admin",
            "authType": "legacy-key",
            "accessTokenExpiresAt": None,
        }

    try:
        claims = decode_admin_token(token, SETTINGS)
    except jwt.PyJWTError as exc:
        raise HTTPException(status_code=401) from exc

    if str(claims.get("role") or "").strip().lower() != "admin":
        raise HTTPException(status_code=401)

    return {
        "email": normalize_email(claims.get("email")) or normalize_email(SETTINGS.admin_email) or "admin",
        "authType": "password",
        "accessTokenExpiresAt": unix_seconds_to_utc_text(claims.get("exp")),
    }


def is_admin_password_login_configured(settings: Settings) -> bool:
    return bool(normalize_email(settings.admin_email) and settings.admin_password)


def extract_bearer_token(authorization: str | None) -> str | None:
    if not authorization:
        return None
    value = authorization.strip()
    if not value.lower().startswith("bearer "):
        return None
    token = value[7:].strip()
    return token or None


def get_user_by_email(db_path: Path, email: str) -> sqlite3.Row | None:
    with db_connection(db_path) as conn:
        return conn.execute(
            "SELECT id,email,password_hash,tier FROM users WHERE email=?",
            (email,),
        ).fetchone()


def get_user_by_id(db_path: Path, user_id: str) -> sqlite3.Row | None:
    with db_connection(db_path) as conn:
        return conn.execute(
            "SELECT id,email,password_hash,tier FROM users WHERE id=?",
            (user_id,),
        ).fetchone()


def get_refresh_token(db_path: Path, token_hash: str) -> sqlite3.Row | None:
    with db_connection(db_path) as conn:
        return conn.execute(
            "SELECT token_hash,user_id,expires_at,revoked_at FROM refresh_tokens WHERE token_hash=?",
            (token_hash,),
        ).fetchone()


def revoke_refresh_token(db_path: Path, token_hash: str) -> None:
    with db_connection(db_path) as conn:
        conn.execute(
            "UPDATE refresh_tokens SET revoked_at=? WHERE token_hash=?",
            (utc_now_text(), token_hash),
        )
        conn.commit()


def get_card(db_path: Path, code: str) -> sqlite3.Row | None:
    with db_connection(db_path) as conn:
        return conn.execute(
            "SELECT code,duration_days,used_by FROM cards WHERE code=?",
            (code,),
        ).fetchone()


def get_subscription(db_path: Path, user_id: str) -> str | None:
    with db_connection(db_path) as conn:
        row = conn.execute(
            "SELECT expires_at FROM subscriptions WHERE user_id=?",
            (user_id,),
        ).fetchone()
    return None if row is None else str(row["expires_at"])


def check_subscription(db_path: Path, user_id: str) -> bool:
    value = get_subscription(db_path, user_id)
    return value is not None and parse_utc(value) > now_utc()


def upsert_subscription(conn: sqlite3.Connection, user_id: str, expires_at: str) -> None:
    now = utc_now_text()
    conn.execute(
        "INSERT INTO subscriptions (user_id,expires_at,created_at,updated_at) VALUES (?,?,?,?) "
        "ON CONFLICT(user_id) DO UPDATE SET expires_at=excluded.expires_at, updated_at=excluded.updated_at",
        (user_id, expires_at, now, now),
    )


def resolve_user(db_path: Path, email_or_user_id: str) -> sqlite3.Row | None:
    return get_user_by_email(db_path, normalize_email(email_or_user_id)) or get_user_by_id(db_path, email_or_user_id)


def list_users(db_path: Path, data_root: Path) -> list[dict[str, Any]]:
    with db_connection(db_path) as conn:
        rows = conn.execute(
            "SELECT u.id,u.email,u.tier,u.created_at,u.updated_at,s.expires_at "
            "FROM users u LEFT JOIN subscriptions s ON s.user_id=u.id ORDER BY u.created_at DESC"
        ).fetchall()

    result: list[dict[str, Any]] = []
    for row in rows:
        user_id = str(row["id"])
        tier_name = normalize_tier_name(row["tier"])
        tier_policy = get_tier_policy(tier_name)
        usage = get_storage_usage(get_user_root(data_root, user_id))
        result.append(
            {
                "id": user_id,
                "email": row["email"],
                "tier": tier_name,
                "created_at": row["created_at"],
                "updated_at": row["updated_at"],
                "expires_at": row["expires_at"],
                "storage_bytes": usage.total_bytes,
                "storage_limit_bytes": None if tier_policy is None else tier_policy.storage_limit_bytes,
                "retention_days": None if tier_policy is None else tier_policy.retention_days,
                "over_limit": tier_policy is not None and usage.total_bytes > tier_policy.storage_limit_bytes,
            }
        )
    return result


def list_subscriptions(db_path: Path, data_root: Path) -> list[dict[str, Any]]:
    with db_connection(db_path) as conn:
        rows = conn.execute(
            "SELECT s.user_id,u.email,u.tier,s.expires_at,s.created_at,s.updated_at "
            "FROM subscriptions s LEFT JOIN users u ON u.id=s.user_id ORDER BY s.updated_at DESC"
        ).fetchall()

    result: list[dict[str, Any]] = []
    for row in rows:
        user_id = str(row["user_id"])
        tier_name = normalize_tier_name(row["tier"])
        tier_policy = get_tier_policy(tier_name)
        usage = get_storage_usage(get_user_root(data_root, user_id))
        result.append(
            {
                "user_id": user_id,
                "email": row["email"],
                "tier": tier_name,
                "expires_at": row["expires_at"],
                "created_at": row["created_at"],
                "updated_at": row["updated_at"],
                "storage_bytes": usage.total_bytes,
                "storage_limit_bytes": None if tier_policy is None else tier_policy.storage_limit_bytes,
                "retention_days": None if tier_policy is None else tier_policy.retention_days,
                "over_limit": tier_policy is not None and usage.total_bytes > tier_policy.storage_limit_bytes,
            }
        )
    return result


def list_cards(db_path: Path) -> list[dict[str, Any]]:
    with db_connection(db_path) as conn:
        rows = conn.execute(
            "SELECT code,duration_days,used_by,used_at,created_at FROM cards ORDER BY created_at DESC"
        ).fetchall()
    return [
        {
            "code": row["code"],
            "duration_days": row["duration_days"],
            "used_by": row["used_by"],
            "used_at": row["used_at"],
            "created_at": row["created_at"],
        }
        for row in rows
    ]


def update_user_tier(conn: sqlite3.Connection, user_id: str, tier_name: str | None) -> None:
    conn.execute(
        "UPDATE users SET tier=?, updated_at=? WHERE id=?",
        (tier_name, utc_now_text(), user_id),
    )


def update_user_password(conn: sqlite3.Connection, user_id: str, new_password: str) -> None:
    conn.execute(
        "UPDATE users SET password_hash=?, updated_at=? WHERE id=?",
        (PASSWORD_HASHER.hash_password(new_password), utc_now_text(), user_id),
    )


def revoke_refresh_tokens_for_user(conn: sqlite3.Connection, user_id: str) -> None:
    conn.execute(
        "UPDATE refresh_tokens SET revoked_at=? WHERE user_id=? AND revoked_at IS NULL",
        (utc_now_text(), user_id),
    )


def get_tier_policy(name: str | None) -> TierPolicy | None:
    normalized = normalize_tier_name(name)
    if normalized is None:
        return None
    if normalized == "Mini":
        return TierPolicy("Mini", 30, 100 * 1024 * 1024)
    if normalized == "Plus":
        return TierPolicy("Plus", 90, 500 * 1024 * 1024)
    if normalized == "Pro":
        return TierPolicy("Pro", 365, 2 * 1024 * 1024 * 1024)
    if normalized == "Ultra":
        return TierPolicy("Ultra", 730, 10 * 1024 * 1024 * 1024)
    return None


def normalize_tier_name(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    if not text:
        return None
    lowered = text.lower()
    if lowered == "mini":
        return "Mini"
    if lowered == "plus":
        return "Plus"
    if lowered == "pro":
        return "Pro"
    if lowered == "ultra":
        return "Ultra"
    return None


def get_supported_tier_names() -> list[str]:
    return ["Mini", "Plus", "Pro", "Ultra"]


def validate_sync_access(db_path: Path, data_root: Path, user_id: str) -> SyncAccessResult:
    if not check_subscription(db_path, user_id):
        return SyncAccessResult(
            False,
            JSONResponse(status_code=403, content={"code": "subscription_expired", "error": "subscription expired"}),
            None,
            None,
        )

    user = get_user_by_id(db_path, user_id)
    if user is None:
        raise HTTPException(status_code=401)

    tier = get_tier_policy(user["tier"])
    if tier is None:
        return SyncAccessResult(
            False,
            JSONResponse(status_code=403, content={"code": "tier_not_assigned", "error": "tier not assigned"}),
            None,
            None,
        )

    return SyncAccessResult(True, None, tier, get_user_root(data_root, user_id))


def get_storage_usage(user_root: Path) -> StorageUsage:
    snapshot_path = user_root / "snapshot.sqlite"
    current_snapshot_bytes = try_get_file_size(snapshot_path)
    backups_root = user_root / "backups"
    blobs_root = user_root / "blobs"
    backup_bytes = 0
    blob_bytes = 0
    backup_count = 0
    if backups_root.exists():
        for directory in safe_get_directories(backups_root):
            backup_bytes += try_get_directory_size(directory)
            backup_count += 1
    if blobs_root.exists():
        blob_bytes = try_get_directory_size(blobs_root)
    return StorageUsage(
        current_snapshot_bytes,
        backup_bytes,
        blob_bytes,
        current_snapshot_bytes + backup_bytes + blob_bytes,
        backup_count,
    )


def prune_backups_for_tier(user_root: Path, tier: TierPolicy) -> BackupPruneResult:
    backups_root = user_root / "backups"
    if not backups_root.exists():
        return BackupPruneResult(get_storage_usage(user_root), 0, 0)

    removed_bytes = 0
    removed_count = 0
    cutoff = now_utc() - timedelta(days=abs(tier.retention_days))

    entries = get_backup_entries(backups_root)
    for entry in sorted((item for item in entries if item.created_at < cutoff), key=lambda item: item.created_at):
        if not try_delete_directory(entry.directory_path):
            continue
        removed_bytes += entry.size_bytes
        removed_count += 1

    usage = get_storage_usage(user_root)
    if usage.total_bytes <= tier.storage_limit_bytes:
        return BackupPruneResult(usage, removed_bytes, removed_count)

    for entry in sorted(get_backup_entries(backups_root), key=lambda item: item.created_at):
        if usage.total_bytes <= tier.storage_limit_bytes:
            break
        if not try_delete_directory(entry.directory_path):
            continue
        removed_bytes += entry.size_bytes
        removed_count += 1
        usage = get_storage_usage(user_root)

    return BackupPruneResult(usage, removed_bytes, removed_count)


def get_backup_entries(backups_root: Path) -> list[BackupEntry]:
    entries: list[BackupEntry] = []
    for directory in safe_get_directories(backups_root):
        created_at = try_parse_backup_dir_time(directory.name) or try_get_directory_time(directory)
        entries.append(BackupEntry(directory, created_at, try_get_directory_size(directory)))
    return entries


def try_parse_backup_dir_time(value: str | None) -> datetime | None:
    if not value:
        return None
    try:
        unix_ms = int(value)
    except ValueError:
        return None
    if unix_ms <= 0:
        return None
    return datetime.fromtimestamp(unix_ms / 1000, tz=UTC)


def try_get_directory_time(directory: Path) -> datetime:
    try:
        return datetime.fromtimestamp(directory.stat().st_mtime, tz=UTC)
    except OSError:
        return now_utc()


def safe_get_directories(root: Path) -> list[Path]:
    try:
        return [item for item in root.iterdir() if item.is_dir()]
    except OSError:
        return []


def try_delete_directory(directory: Path) -> bool:
    try:
        if not directory.exists():
            return True
        shutil.rmtree(directory)
        return True
    except OSError:
        return False


def try_get_directory_size(directory: Path) -> int:
    total = 0
    try:
        for path in directory.rglob("*"):
            if path.is_file():
                total += try_get_file_size(path)
    except OSError:
        return total
    return total


def try_get_file_size(path: Path) -> int:
    try:
        return path.stat().st_size if path.exists() else 0
    except OSError:
        return 0


def get_user_root(data_root: Path, user_id: str) -> Path:
    return data_root / make_safe_segment(user_id)


def get_blob_path(user_root: Path, blob_sha256: str) -> Path:
    normalized = normalize_blob_sha256(blob_sha256)
    if normalized is None:
        raise ValueError("invalid blob hash")
    return user_root / "blobs" / normalized[:2] / normalized[2:4] / f"{normalized}.blob"


def make_safe_segment(value: str) -> str:
    safe = "".join(ch if ch.isalnum() or ch in "-_" else "_" for ch in (value or ""))
    safe = safe.strip("_")
    return safe or "unknown"


def normalize_blob_sha256(value: Any) -> str | None:
    text = str(value or "").strip().lower()
    if len(text) != 64:
        return None
    if any(ch not in "0123456789abcdef" for ch in text):
        return None
    return text


def try_delete_file(path: Path) -> None:
    try:
        path.unlink(missing_ok=True)
    except OSError:
        pass


def try_move(src: Path, dest: Path) -> None:
    try:
        if not src.exists():
            return
        try_delete_file(dest)
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(src), str(dest))
    except OSError:
        pass


def compute_sha256_hex(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            sha.update(chunk)
    return sha.hexdigest()


def accepts_gzip(request: Request) -> bool:
    header = (request.headers.get("accept-encoding") or "").lower()
    return "gzip" in header


def stream_gzip_file(path: Path) -> Any:
    compressor = zlib.compressobj(level=1, wbits=16 + zlib.MAX_WBITS)
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            if not chunk:
                continue
            data = compressor.compress(chunk)
            if data:
                yield data
        tail = compressor.flush()
        if tail:
            yield tail


def hash_text(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def try_read_json_value(path: Path, level1: str, level2: str) -> str | None:
    try:
        if not path.exists():
            return None
        data = json.loads(path.read_text(encoding="utf-8"))
        return data[level1][level2]
    except Exception:
        return None


def normalize_email(value: Any) -> str:
    return str(value or "").strip().lower()


def is_valid_email(email: str) -> bool:
    return bool(email and "@" in email and len(email) <= 200)


def format_sse_event(event_name: str, data: dict[str, Any]) -> str:
    payload = json.dumps(data, ensure_ascii=False, separators=(",", ":"))
    return f"event: {event_name}\ndata: {payload}\n\n"


def base64_url_encode(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).decode("ascii").rstrip("=")


def generate_card_code() -> str:
    code = secrets.token_hex(8).upper()
    return f"{code[:4]}-{code[4:8]}-{code[8:12]}-{code[12:16]}"


def read_admin_html() -> str:
    path = APP_DIR / "AdminPage.html"
    if path.exists():
        return path.read_text(encoding="utf-8")
    return "<!doctype html><html><body><h1>Maccy Admin</h1><p>Admin page missing.</p></body></html>"


def now_utc() -> datetime:
    return datetime.now(tz=UTC)


def format_utc(value: datetime) -> str:
    return value.astimezone(UTC).isoformat()


def utc_now_text() -> str:
    return format_utc(now_utc())


def unix_seconds_to_utc_text(value: Any) -> str | None:
    try:
        return format_utc(datetime.fromtimestamp(int(value), tz=UTC))
    except (TypeError, ValueError, OSError):
        return None


def parse_utc(value: str) -> datetime:
    return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(UTC)


def as_int(value: Any, default: int) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def clamp(value: int, minimum: int, maximum: int) -> int:
    return max(minimum, min(maximum, value))


async def read_json(request: Request) -> dict[str, Any] | None:
    try:
        payload = await request.json()
    except Exception:
        return None
    return payload if isinstance(payload, dict) else None


def error_json(status_code: int, message: str) -> JSONResponse:
    return JSONResponse(status_code=status_code, content={"error": message})
