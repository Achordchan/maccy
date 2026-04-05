from __future__ import annotations

import shutil
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "nas-agent-py"
OUTPUT = ROOT / "artifacts" / "nas-agent-py"
CURRENT = OUTPUT / "current"
ZIP_PATH = OUTPUT / "nas-agent-py-upload.zip"

FILES = [
    ".env.example",
    "AdminPage.html",
    "app.py",
    "main.py",
    "password_hasher.py",
    "requirements.txt",
    "run.sh",
    "tests/compat_smoke.py",
]


def reset_output() -> None:
    if OUTPUT.exists():
        shutil.rmtree(OUTPUT)
    CURRENT.mkdir(parents=True, exist_ok=True)


def copy_files() -> None:
    for relative in FILES:
        src = SOURCE / relative
        dst = CURRENT / relative
        if not src.exists():
            raise FileNotFoundError(f"missing required file: {src}")
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)


def write_readme() -> None:
    content = """nas-agent-py upload bundle

Directory layout:
- current/: upload this directory to your Baota Python project root

Startup:
- install dependencies: pip install -r requirements.txt
- configure env vars from .env.example
- start command: sh run.sh

Reference:
- docs/deploy/baota-python-nas-agent.md
"""
    (OUTPUT / "README.txt").write_text(content, encoding="utf-8")


def build_zip() -> None:
    with ZipFile(ZIP_PATH, "w", compression=ZIP_DEFLATED) as archive:
        for path in CURRENT.rglob("*"):
            if path.is_file():
                archive.write(path, path.relative_to(OUTPUT))
        archive.write(OUTPUT / "README.txt", "README.txt")


def main() -> None:
    reset_output()
    copy_files()
    write_readme()
    build_zip()
    print(f"Prepared upload bundle: {OUTPUT}")
    print(f"Prepared zip: {ZIP_PATH}")


if __name__ == "__main__":
    main()
