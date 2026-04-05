"""Compatibility smoke tests for the nas-agent Python backend."""

from __future__ import annotations

import argparse
import hashlib
import os
import sys
import uuid

import httpx


DEFAULT_BASE_URL = os.environ.get("NAS_BASE_URL", "http://127.0.0.1:17655")
DEFAULT_ADMIN_KEY = os.environ.get("NAS_ADMIN_KEY", "change-me")


def log(message: str) -> None:
    print(message)


def fail(message: str) -> None:
    print(f"ERROR: {message}")
    raise SystemExit(1)


def parse_json(response: httpx.Response) -> dict:
    try:
        value = response.json()
    except ValueError as exc:
        fail(f"Failed to parse JSON from {response.request.url}: {exc}")
    if not isinstance(value, dict):
        fail(f"Expected object JSON from {response.request.url}")
    return value


def ensure_success(response: httpx.Response) -> httpx.Response:
    try:
        response.raise_for_status()
    except httpx.HTTPStatusError as exc:
        fail(str(exc))
    return response


def register_or_login(client: httpx.Client, email: str, password: str) -> dict:
    payload = {"email": email, "password": password}
    response = client.post("/auth/register", json=payload)
    if response.status_code == 200:
        log("Registered new smoke-test user")
    elif response.status_code == 400:
        body = parse_json(response)
        if "email already" not in str(body.get("error", "")).lower():
            fail(f"Unexpected register error: {body}")
        log("User already exists, falling back to login")
    else:
        ensure_success(response)

    response = ensure_success(client.post("/auth/login", json=payload))
    data = parse_json(response)
    if not data.get("accessToken"):
        fail("auth/login missing accessToken")
    if not data.get("refreshToken"):
        fail("auth/login missing refreshToken")
    if not data.get("accessTokenExpiresAt"):
        fail("auth/login missing accessTokenExpiresAt")
    return data


def refresh_token(client: httpx.Client, refresh_token_value: str) -> None:
    response = ensure_success(client.post("/auth/refresh", json={"refreshToken": refresh_token_value}))
    data = parse_json(response)
    if not data.get("accessToken"):
        fail("auth/refresh missing accessToken")
    if not data.get("refreshToken"):
        fail("auth/refresh missing refreshToken")
    log("Refresh token flow returned a rotated token")


def check_health(client: httpx.Client) -> None:
    data = parse_json(ensure_success(client.get("/health")))
    if data.get("ok") is not True:
        fail("health endpoint did not return ok=true")
    log("Health endpoint passed")


def check_subscription_status(client: httpx.Client, access_token: str) -> None:
    headers = {"Authorization": f"Bearer {access_token}"}
    data = parse_json(ensure_success(client.get("/subscription/status", headers=headers)))
    required_fields = ["subscribed", "storageBytes", "storageLimitBytes", "retentionDays", "overLimit"]
    for field in required_fields:
        if field not in data:
            fail(f"subscription/status missing {field}")
    log("Subscription status contract passed")


def check_manifest(client: httpx.Client, access_token: str) -> None:
    headers = {"Authorization": f"Bearer {access_token}"}
    response = client.get("/sync/manifest", headers=headers)
    if response.status_code == 404:
        log("Manifest missing, which is valid for a fresh account")
        return
    ensure_success(response)
    data = parse_json(response)
    latest = data.get("latest")
    if not isinstance(latest, dict):
        fail("manifest.latest missing")
    for field in ["version", "updatedAt"]:
        if field not in latest:
            fail(f"manifest.latest missing {field}")
    snapshot = latest.get("snapshot")
    if snapshot is not None:
        if not isinstance(snapshot, dict):
            fail("manifest.latest.snapshot must be an object")
        for field in ["sha256", "size"]:
            if field not in snapshot:
                fail(f"manifest.latest.snapshot missing {field}")
    log("Manifest contract passed")


def upload_and_download_snapshot(client: httpx.Client, access_token: str) -> None:
    headers = {"Authorization": f"Bearer {access_token}", "Content-Type": "application/octet-stream"}
    payload = os.urandom(1024)
    upload_response = ensure_success(client.put("/sync/snapshot", headers=headers, content=payload))
    upload = parse_json(upload_response)
    for field in ["version", "sha256", "size"]:
        if field not in upload:
            fail(f"sync/snapshot upload missing {field}")
    if upload["sha256"] != hashlib.sha256(payload).hexdigest():
        fail("Uploaded snapshot sha256 mismatch")
    if int(upload["size"]) != len(payload):
        fail("Uploaded snapshot size mismatch")

    download_response = ensure_success(
        client.get("/sync/snapshot", headers={"Authorization": f"Bearer {access_token}"})
    )
    if download_response.content != payload:
        fail("Downloaded snapshot bytes do not match uploaded bytes")
    log("Raw snapshot upload/download contract passed")


def check_admin(client: httpx.Client, admin_key: str) -> None:
    response = ensure_success(client.get("/admin/users/list", headers={"Authorization": f"Bearer {admin_key}"}))
    data = parse_json(response)
    if "users" not in data or not isinstance(data["users"], list):
        fail("admin/users/list missing users array")
    log("Admin auth and users list contract passed")


def provision_sync_access(client: httpx.Client, admin_key: str, email: str, duration_days: int, tier: str) -> None:
    headers = {"Authorization": f"Bearer {admin_key}"}
    response = ensure_success(
        client.post(
            "/admin/user/upgrade",
            headers=headers,
            json={"emailOrUserId": email, "durationDays": duration_days, "tier": tier},
        )
    )
    data = parse_json(response)
    if data.get("tier") != tier:
        fail("admin/user/upgrade did not apply the expected tier")
    log(f"Provisioned {duration_days} days of {tier} tier for smoke-test user")


def main() -> None:
    parser = argparse.ArgumentParser(description="Compatibility smoke tests for nas-agent Python backend")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="NAS base URL")
    parser.add_argument("--admin-key", default=DEFAULT_ADMIN_KEY, help="Admin key for /admin endpoints")
    parser.add_argument("--email", default=os.environ.get("NAS_SMOKE_EMAIL"), help="Smoke-test email")
    parser.add_argument(
        "--password",
        default=os.environ.get("NAS_SMOKE_PASSWORD", "Sm0ke@123"),
        help="Smoke-test password",
    )
    parser.add_argument(
        "--skip-upload",
        action="store_true",
        help="Skip snapshot upload/download to avoid mutating a shared environment",
    )
    parser.add_argument("--tier", default="Pro", help="Tier to assign before sync checks")
    parser.add_argument("--duration-days", type=int, default=30, help="Subscription days to assign before sync checks")
    args = parser.parse_args()

    email = args.email or f"smoke+{uuid.uuid4().hex[:8]}@example.com"

    with httpx.Client(base_url=args.base_url.rstrip("/"), timeout=120.0, follow_redirects=True) as client:
        check_health(client)
        tokens = register_or_login(client, email, args.password)
        access_token = tokens["accessToken"]
        refresh_token(client, tokens["refreshToken"])
        check_admin(client, args.admin_key)
        provision_sync_access(client, args.admin_key, email, args.duration_days, args.tier)
        check_subscription_status(client, access_token)
        check_manifest(client, access_token)
        if not args.skip_upload:
            upload_and_download_snapshot(client, access_token)

    log("Compatibility smoke suite succeeded")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(130)
