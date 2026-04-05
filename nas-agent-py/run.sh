#!/usr/bin/env sh
set -eu

HOST="${LISTEN_HOST:-127.0.0.1}"
PORT="${LISTEN_PORT:-17655}"

exec uvicorn main:app --host "$HOST" --port "$PORT"
