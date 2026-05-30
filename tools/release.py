import argparse
import datetime
import getpass
import hashlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import traceback
import queue
import time
import urllib.parse
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

try:
    sys.stdout.reconfigure(errors="replace")
    sys.stderr.reconfigure(errors="replace")
except Exception:
    pass


def _message_box(title: str, text: str) -> None:
    if os.name != "nt":
        return
    try:
        import ctypes  # type: ignore

        ctypes.windll.user32.MessageBoxW(0, text, title, 0x00000010)
    except Exception:
        return


def _infer_base_url_from_manifest(manifest_path: Path) -> str:
    try:
        data = json.loads(manifest_path.read_text(encoding="utf-8"))
        latest = data.get("latest") or {}
        installer = latest.get("installer") or {}
        url = (installer.get("url") or "").strip()
        if not url:
            return ""

        m = re.match(r"^(.*?)/v\d+\.\d+\.\d+/maccy-\d+\.\d+\.\d+-setup\.exe$", url)
        if m:
            return m.group(1)
        return url.rsplit("/", 1)[0]
    except Exception:
        return ""


def _prompt(text: str, default: str = "", *, required: bool = False) -> str:
    while True:
        suffix = f" [{default}]" if default else ""
        v = input(f"{text}{suffix}: ").strip()
        if not v:
            v = default
        if required and not v:
            continue
        return v


def _prompt_secret(text: str, default: str = "") -> str:
    while True:
        suffix = "" if not default else " [***]"
        v = getpass.getpass(f"{text}{suffix}: ").strip()
        if not v:
            v = default
        if not v:
            continue
        return v


def _prompt_bool(text: str, default: bool = False) -> bool:
    d = "y" if default else "n"
    while True:
        raw = _prompt(text + " (y/n)", d, required=True).lower()
        if raw in ("y", "yes"):
            return True
        if raw in ("n", "no"):
            return False


def _prompt_notes(default: str) -> str:
    print("输入更新说明（可多行）。输入单独一行 END 结束：")
    lines: list[str] = []
    while True:
        try:
            line = input()
        except EOFError:
            break
        if line.strip() == "END":
            break
        lines.append(line)

    notes = "\n".join(lines).replace("\r\n", "\n").rstrip("\n")
    if notes:
        return notes
    return default


def _collect_config_interactive(repo: Path, manifest_path: Path) -> dict:
    defaults_base_url = _infer_base_url_from_manifest(manifest_path)
    defaults_notes = ""
    try:
        data = json.loads(manifest_path.read_text(encoding="utf-8"))
        latest = data.get("latest") or {}
        defaults_notes = (latest.get("notes") or "").replace("\r\n", "\n").rstrip("\n")
    except Exception:
        defaults_notes = ""

    while True:
        v = _prompt("版本号 (例如 1.0.2)", "", required=True)
        try:
            v = _validate_version(v)
            break
        except Exception:
            continue

    base_url = _prompt("Release 下载根地址 (不含 /vX.Y.Z/...)", defaults_base_url, required=False)
    mandatory = _prompt_bool("是否强制更新", False)

    notes_file = _prompt("更新说明文件路径（可空）", "")
    notes = ""
    if notes_file:
        try:
            notes = Path(notes_file).read_text(encoding="utf-8")
        except Exception:
            notes = ""
    if not notes.strip():
        notes = _prompt_notes(defaults_notes)

    default_iscc = ""
    try:
        found = _try_find_iscc()
        if found is not None:
            default_iscc = str(found)
    except Exception:
        default_iscc = ""

    iscc = _prompt("ISCC.exe 路径（可空，自动寻找）", default_iscc)
    runtime = _prompt("runtime", "win-x64", required=True)
    configuration = _prompt("configuration", "Release", required=True)

    publish_gitee = _prompt_bool("是否自动发布到 Gitee Release（包含 git push + 上传安装包）", False)
    if (not publish_gitee) and (not base_url):
        base_url = _prompt("Release 下载根地址 (不含 /vX.Y.Z/...)", defaults_base_url, required=True)
    force_republish = False
    gitee_owner = ""
    gitee_repo = ""
    gitee_token = os.environ.get("GITEE_TOKEN", "").strip()
    if publish_gitee:
        inferred = _infer_repo_from_git(repo)
        if inferred is not None:
            gitee_owner, gitee_repo = inferred

        gitee_owner = _prompt("Gitee owner", gitee_owner, required=True)
        gitee_repo = _prompt("Gitee repo", gitee_repo, required=True)
        if not gitee_token:
            gitee_token = _prompt_secret("Gitee Token (环境变量 GITEE_TOKEN 也可)")

        force_republish = _prompt_bool("强制重发同版本（危险：会删除远端 tag/release）", False)

        confirmed = _prompt_bool("确认立即发布？（将 git commit/tag/push，并创建 Gitee Release 上传安装包）", False)
        if not confirmed:
            publish_gitee = False
            gitee_owner = ""
            gitee_repo = ""
            gitee_token = ""

    return {
        "version": v,
        "base_url": base_url,
        "mandatory": mandatory,
        "notes": notes,
        "notes_file": "",
        "iscc": iscc,
        "runtime": runtime,
        "configuration": configuration,
        "publish_gitee": publish_gitee,
        "force_republish": bool(force_republish),
        "gitee_owner": gitee_owner,
        "gitee_repo": gitee_repo,
        "gitee_token": gitee_token,
    }


def _collect_config_gui(repo: Path, manifest_path: Path) -> dict | None:
    try:
        import tkinter as tk
        from tkinter import filedialog, messagebox
        from tkinter import simpledialog
    except Exception:
        return None

    defaults_base_url = _infer_base_url_from_manifest(manifest_path)
    defaults_notes = ""
    try:
        data = json.loads(manifest_path.read_text(encoding="utf-8"))
        latest = data.get("latest") or {}
        defaults_notes = (latest.get("notes") or "").replace("\r\n", "\n").rstrip("\n")
    except Exception:
        defaults_notes = ""

    result: dict | None = None

    root = tk.Tk()
    root.title("Maccy Release")
    root.geometry("720x520")

    frm = tk.Frame(root, padx=12, pady=12)
    frm.pack(fill=tk.BOTH, expand=True)

    def row(label: str, widget: tk.Widget, r: int) -> None:
        tk.Label(frm, text=label).grid(row=r, column=0, sticky="w", pady=6)
        widget.grid(row=r, column=1, sticky="we", pady=6)

    frm.columnconfigure(1, weight=1)

    default_iscc = ""
    try:
        found = _try_find_iscc()
        if found is not None:
            default_iscc = str(found)
    except Exception:
        default_iscc = ""

    inferred_owner = ""
    inferred_repo = ""
    inferred = _infer_repo_from_git(repo)
    if inferred is not None:
        inferred_owner, inferred_repo = inferred

    version_var = tk.StringVar(value="")
    base_url_var = tk.StringVar(value=defaults_base_url)
    mandatory_var = tk.BooleanVar(value=False)
    iscc_var = tk.StringVar(value=default_iscc)
    runtime_var = tk.StringVar(value="win-x64")
    config_var = tk.StringVar(value="Release")
    publish_gitee_var = tk.BooleanVar(value=False)
    force_republish_var = tk.BooleanVar(value=False)
    gitee_owner_var = tk.StringVar(value=inferred_owner)
    gitee_repo_var = tk.StringVar(value=inferred_repo)
    gitee_token_var = tk.StringVar(value=os.environ.get("GITEE_TOKEN", ""))

    version_entry = tk.Entry(frm, textvariable=version_var)
    base_entry = tk.Entry(frm, textvariable=base_url_var)
    iscc_entry = tk.Entry(frm, textvariable=iscc_var)
    runtime_entry = tk.Entry(frm, textvariable=runtime_var)
    config_entry = tk.Entry(frm, textvariable=config_var)
    mandatory_chk = tk.Checkbutton(frm, text="强制更新", variable=mandatory_var)

    row("版本号 (例如 1.0.2)", version_entry, 0)
    row("Release 下载根地址", base_entry, 1)
    row("ISCC.exe 路径（可空）", iscc_entry, 2)
    row("runtime", runtime_entry, 3)
    row("configuration", config_entry, 4)
    mandatory_chk.grid(row=5, column=1, sticky="w", pady=6)

    publish_chk = tk.Checkbutton(frm, text="自动发布到 Gitee Release（含 git push + 上传安装包）", variable=publish_gitee_var)
    publish_chk.grid(row=6, column=1, sticky="w", pady=6)

    force_chk = tk.Checkbutton(frm, text="强制重发同版本（危险：删除远端 tag/release）", variable=force_republish_var)
    force_chk.grid(row=7, column=1, sticky="w", pady=6)

    owner_entry = tk.Entry(frm, textvariable=gitee_owner_var)
    repo_entry = tk.Entry(frm, textvariable=gitee_repo_var)
    token_entry = tk.Entry(frm, textvariable=gitee_token_var, show="*")

    row("Gitee owner", owner_entry, 8)
    row("Gitee repo", repo_entry, 9)
    row("Gitee token（可用环境变量 GITEE_TOKEN）", token_entry, 10)

    tk.Label(frm, text="更新说明").grid(row=11, column=0, sticky="nw", pady=6)
    notes_txt = tk.Text(frm, height=10)
    notes_txt.grid(row=11, column=1, sticky="nsew", pady=6)
    frm.rowconfigure(11, weight=1)
    notes_txt.insert("1.0", defaults_notes)

    btns = tk.Frame(frm)
    btns.grid(row=12, column=0, columnspan=2, sticky="e", pady=10)

    def pick_iscc() -> None:
        p = filedialog.askopenfilename(title="选择 ISCC.exe", filetypes=[("ISCC.exe", "ISCC.exe"), ("All", "*")])
        if p:
            iscc_var.set(p)

    def submit() -> None:
        nonlocal result
        v = version_var.get().strip()
        try:
            v = _validate_version(v)
        except Exception:
            messagebox.showerror("错误", "版本号格式不对，应为 1.0.2")
            return

        notes = notes_txt.get("1.0", tk.END).replace("\r\n", "\n").rstrip("\n")
        publish_gitee = bool(publish_gitee_var.get())
        force_republish = bool(force_republish_var.get())
        b = base_url_var.get().strip()
        if (not b) and (not publish_gitee):
            messagebox.showerror("错误", "Release 下载根地址不能为空")
            return

        owner = gitee_owner_var.get().strip()
        repo_name = gitee_repo_var.get().strip()
        token = gitee_token_var.get().strip()

        if publish_gitee:
            if not owner or not repo_name:
                messagebox.showerror("错误", "启用自动发布时，Gitee owner/repo 不能为空")
                return
            if not token:
                messagebox.showerror("错误", "启用自动发布时，需要提供 Gitee token（或设置环境变量 GITEE_TOKEN）")
                return

            ok = messagebox.askyesno("确认发布", "即将执行：git commit/tag/push + 创建 Gitee Release + 上传安装包。\n确认继续？")
            if not ok:
                return

            if force_republish:
                ok2 = messagebox.askyesno(
                    "危险操作确认",
                    "你选择了【强制重发同版本】。\n\n这会删除远端的 tag/release，然后重新发布同一个版本号。\n\n确认继续？",
                )
                if not ok2:
                    return

                typed = simpledialog.askstring("再次确认", f"请输入版本号 {v} 以继续")
                if (typed or "").strip() != v:
                    messagebox.showerror("错误", "版本号确认失败，已取消。")
                    return

        result = {
            "version": v,
            "base_url": b,
            "mandatory": bool(mandatory_var.get()),
            "notes": notes,
            "notes_file": "",
            "iscc": iscc_var.get().strip(),
            "runtime": runtime_var.get().strip() or "win-x64",
            "configuration": config_var.get().strip() or "Release",
            "publish_gitee": publish_gitee,
            "force_republish": force_republish,
            "gitee_owner": owner,
            "gitee_repo": repo_name,
            "gitee_token": token,
        }
        root.destroy()

    def cancel() -> None:
        root.destroy()

    tk.Button(btns, text="选择 ISCC.exe", command=pick_iscc).pack(side=tk.LEFT, padx=6)
    tk.Button(btns, text="开始", command=submit).pack(side=tk.LEFT, padx=6)
    tk.Button(btns, text="取消", command=cancel).pack(side=tk.LEFT, padx=6)

    root.mainloop()
    return result


def _run(cmd: list[str], cwd: Path) -> None:
    p = subprocess.run(cmd, cwd=str(cwd), capture_output=True, text=True, encoding="utf-8", errors="replace", shell=False)
    if p.stdout:
        sys.stdout.write(p.stdout)
    if p.stderr:
        sys.stderr.write(p.stderr)
    if p.returncode != 0:
        raise SystemExit(p.returncode)


def _git_status_porcelain(repo_root: Path) -> str:
    try:
        return _run_text(["git", "status", "--porcelain"], cwd=repo_root)
    except Exception:
        return ""


def _ensure_worktree_clean_for_release(repo_root: Path) -> None:
    out = _git_status_porcelain(repo_root)
    if not out.strip():
        return

    allowed = {
        "maccy/maccy.csproj",
        "installer/maccy.iss",
        "docs/updates/manifest.json",
    }

    bad: list[str] = []
    for line in out.splitlines():
        s = line.rstrip("\n")
        if not s.strip():
            continue
        path = s[3:].strip()
        if " -> " in path:
            path = path.split(" -> ", 1)[1].strip()
        path = path.replace("\\", "/")
        if path.startswith("artifacts/"):
            continue
        if path in allowed:
            continue
        bad.append(s)

    if bad:
        raise RuntimeError("工作区不干净（会影响发布）。请先提交/还原这些改动：\n" + "\n".join(bad))


def _remote_tag_exists(repo_root: Path, tag: str) -> bool:
    try:
        out = _run_text(["git", "ls-remote", "--tags", "origin", f"refs/tags/{tag}"], cwd=repo_root)
        return bool(out.strip())
    except Exception:
        return False


def _delete_remote_tag(repo_root: Path, tag: str) -> None:
    try:
        _git(repo_root, ["push", "origin", "--delete", tag])
    except SystemExit:
        pass


def _delete_local_tag(repo_root: Path, tag: str) -> None:
    try:
        _git(repo_root, ["tag", "-d", tag])
    except SystemExit:
        pass


def _gitee_delete_release_by_tag(*, owner: str, repo_name: str, token: str, tag: str) -> None:
    api = "https://gitee.com/api/v5"
    releases_by_tag = f"{api}/repos/{owner}/{repo_name}/releases/tags/{urllib.parse.quote(tag)}?access_token={urllib.parse.quote(token)}"
    release: dict | None = None
    try:
        release = _http_json("GET", releases_by_tag)
    except Exception:
        release = None

    if not isinstance(release, dict):
        return

    rid = str(release.get("id") or "").strip()
    if not rid:
        return

    del_url = f"{api}/repos/{owner}/{repo_name}/releases/{rid}?access_token={urllib.parse.quote(token)}"
    try:
        urllib.request.urlopen(urllib.request.Request(del_url, method="DELETE"), timeout=60).read()
    except Exception:
        return


def _gitee_get_release_by_tag(*, owner: str, repo_name: str, token: str, tag: str) -> dict | None:
    api = "https://gitee.com/api/v5"
    url = f"{api}/repos/{owner}/{repo_name}/releases/tags/{urllib.parse.quote(tag)}?access_token={urllib.parse.quote(token)}"
    try:
        obj = _http_json("GET", url)
    except Exception:
        return None
    if isinstance(obj, dict):
        rid = str(obj.get("id") or "").strip()
        if rid:
            return obj
    return None


class _QueueWriter(io.TextIOBase):
    def __init__(self, q: "queue.Queue[str]"):
        super().__init__()
        self._q = q

    def write(self, s: str) -> int:
        if s:
            self._q.put(s)
        return len(s)

    def flush(self) -> None:
        return


def _run_pipeline(repo: Path, cfg: dict, *, is_gui: bool, yes: bool) -> tuple[int, dict]:
    csproj = repo / "maccy" / "maccy.csproj"
    iss = repo / "installer" / "maccy.iss"
    manifest = repo / "docs" / "updates" / "manifest.json"

    version = _validate_version(cfg["version"])
    base_url = str(cfg.get("base_url") or "").strip()
    mandatory = bool(cfg.get("mandatory") or False)
    notes = str(cfg.get("notes") or "").replace("\r\n", "\n").rstrip("\n")
    iscc_arg = str(cfg.get("iscc") or "").strip()
    runtime = str(cfg.get("runtime") or "win-x64").strip() or "win-x64"
    configuration = str(cfg.get("configuration") or "Release").strip() or "Release"

    publish_gitee = bool(cfg.get("publish_gitee") or False)
    force_republish = bool(cfg.get("force_republish") or False)
    gitee_owner = str(cfg.get("gitee_owner") or "").strip()
    gitee_repo = str(cfg.get("gitee_repo") or "").strip()
    gitee_token = str(cfg.get("gitee_token") or os.environ.get("GITEE_TOKEN", "")).strip()

    if publish_gitee:
        if (not is_gui) and (not yes):
            raise ValueError("publish-gitee requires --yes in non-interactive mode")
        inferred = _infer_repo_from_git(repo)
        if inferred is not None:
            if not gitee_owner:
                gitee_owner = inferred[0]
            if not gitee_repo:
                gitee_repo = inferred[1]
        if not gitee_owner or not gitee_repo:
            raise ValueError("missing gitee owner/repo")
        if not gitee_token:
            raise ValueError("missing gitee token")

    if (not base_url) and (not publish_gitee):
        raise ValueError("missing base url")

    # Preflight
    iscc = _find_iscc(iscc_arg or None)
    _ensure_worktree_clean_for_release(repo)
    tag = "v" + version
    tag_exists = False
    if publish_gitee:
        tag_exists = _remote_tag_exists(repo, tag)
        if tag_exists and (not force_republish):
            rel = _gitee_get_release_by_tag(owner=gitee_owner, repo_name=gitee_repo, token=gitee_token, tag=tag)
            if rel is not None:
                raise RuntimeError(f"远端 tag 已存在：{tag}。说明这个版本号已发布过，请换一个新版本号。")
            print(f"[preflight] remote tag exists but no release found, will resume: {tag}")
        if tag_exists and force_republish:
            print(f"[preflight] remote tag exists, will force re-publish: {tag}")

    completed: list[str] = []

    publish_dir = repo / "artifacts" / "publish" / runtime
    installer_dir = repo / "artifacts" / "installer"

    print(f"[1/5] set csproj version -> {version}")
    _set_csproj_version(csproj, version)
    _set_iss_version(iss, version)
    completed.append("更新版本号（maccy.csproj）")

    print(f"[2/5] dotnet publish ({configuration}, {runtime})")
    publish_dir.mkdir(parents=True, exist_ok=True)
    _run(
        [
            "dotnet",
            "publish",
            str(csproj),
            "-c",
            configuration,
            "-r",
            runtime,
            "-o",
            str(publish_dir),
        ],
        cwd=repo,
    )
    completed.append("生成发布目录（dotnet publish）")

    print("[3/5] build installer (Inno Setup)")
    installer_dir.mkdir(parents=True, exist_ok=True)
    _run([str(iscc), str(iss)], cwd=repo)
    completed.append("生成安装包（Inno Setup）")

    installer_path = installer_dir / f"maccy-{version}-setup.exe"
    if not installer_path.exists():
        raise FileNotFoundError(f"installer not found: {installer_path}")

    print("[4/6] build lightweight package")
    package_path = _create_update_package(publish_dir, installer_dir, version=version, runtime=runtime)
    completed.append("build lightweight zip package")

    print("[5/6] compute sha256/size")
    sha256 = _sha256_file(installer_path)
    size = installer_path.stat().st_size
    package_sha256 = _sha256_file(package_path)
    package_size = package_path.stat().st_size
    completed.append("计算安装包 sha256/size")

    published_url = ""
    published_package_url = ""
    if publish_gitee:
        if tag_exists and force_republish:
            print(f"[pre-clean] delete remote release/tag: {tag}")
            _gitee_delete_release_by_tag(owner=gitee_owner, repo_name=gitee_repo, token=gitee_token, tag=tag)
            _delete_remote_tag(repo, tag)
            _delete_local_tag(repo, tag)

        print("[5/7] publish to gitee")
        published = _publish_to_gitee(
            repo,
            owner=gitee_owner,
            repo_name=gitee_repo,
            token=gitee_token,
            version=version,
            notes=notes,
            installer_path=installer_path,
            package_path=package_path,
        )
        published_url = published.get("installer", "")
        published_package_url = published.get("package", "")
        completed.append("发布到 Gitee Release（git push + 上传安装包）")

        print("[6/7] update manifest.json")
        _update_manifest(
            manifest,
            version=version,
            mandatory=mandatory,
            notes=notes,
            base_url=base_url or "https://gitee.com",
            sha256=sha256,
            size=size,
            installer_url=published_url,
            package_sha256=package_sha256,
            package_size=package_size,
            package_runtime=runtime,
            package_url=published_package_url,
        )
        completed.append("更新更新清单（docs/updates/manifest.json）")

        try:
            _git(repo, ["add", "docs/updates/manifest.json"])
            _git(repo, ["commit", "-m", "update manifest for " + "v" + version])
        except SystemExit:
            pass

        _push_current_to_upstream(repo)
        completed.append("推送 manifest 更新（git push）")
    else:
        print("[5/5] update manifest.json")
        _update_manifest(
            manifest,
            version=version,
            mandatory=mandatory,
            notes=notes,
            base_url=base_url,
            sha256=sha256,
            size=size,
            package_sha256=package_sha256,
            package_size=package_size,
            package_runtime=runtime,
        )
        completed.append("更新更新清单（docs/updates/manifest.json）")

    info = {
        "completed": completed,
        "installer": str(installer_path),
        "package": str(package_path),
        "sha256": sha256,
        "size": int(size),
        "package_sha256": package_sha256,
        "package_size": int(package_size),
        "manifest": str(manifest),
        "published_url": published_url,
        "published_package_url": published_package_url,
        "publish_gitee": bool(publish_gitee),
        "version": version,
        "base_url": base_url,
    }
    return 0, info


def _run_pipeline_gui(repo: Path, cfg: dict) -> int:
    try:
        import tkinter as tk
        from tkinter import messagebox
    except Exception:
        _message_box("Maccy Release", "GUI 不可用（缺少 tkinter）。\n\n请安装带 tkinter 的 Python（Windows 官方安装包通常自带），或换一个 Python 解释器。")
        return 2

    root = tk.Tk()
    root.title("Maccy Release")
    root.geometry("900x600")

    frm = tk.Frame(root)
    frm.pack(fill=tk.BOTH, expand=True)

    text = tk.Text(frm, wrap="word")
    yscroll = tk.Scrollbar(frm, command=text.yview)
    text.configure(yscrollcommand=yscroll.set)
    text.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
    yscroll.pack(side=tk.RIGHT, fill=tk.Y)

    q: "queue.Queue[str]" = queue.Queue()
    done: "queue.Queue[tuple[bool, object]]" = queue.Queue()

    def append(s: str) -> None:
        text.insert(tk.END, s)
        text.see(tk.END)

    def worker() -> None:
        old_out = sys.stdout
        old_err = sys.stderr
        sys.stdout = _QueueWriter(q)
        sys.stderr = _QueueWriter(q)
        try:
            code, info = _run_pipeline(repo, cfg, is_gui=True, yes=True)
            done.put((True, (code, info)))
        except Exception as e:
            tb = traceback.format_exc()
            q.put(tb + "\n")
            done.put((False, e))
        finally:
            sys.stdout = old_out
            sys.stderr = old_err

    t = threading.Thread(target=worker, daemon=True)
    t.start()

    def pump() -> None:
        try:
            while True:
                s = q.get_nowait()
                append(s)
        except queue.Empty:
            pass

        try:
            ok, payload = done.get_nowait()
        except queue.Empty:
            root.after(100, pump)
            return

        if ok:
            code, info = payload  # type: ignore
            append("\n完成清单：\n")
            for item in info.get("completed", []):
                append("[OK] " + str(item) + "\n")
            append("\n产物信息：\n")
            append("installer: " + str(info.get("installer")) + "\n")
            append("package: " + str(info.get("package")) + "\n")
            append("sha256: " + str(info.get("sha256")) + "\n")
            append("size: " + str(info.get("size")) + "\n")
            append("package sha256: " + str(info.get("package_sha256")) + "\n")
            append("package size: " + str(info.get("package_size")) + "\n")
            append("manifest: " + str(info.get("manifest")) + "\n")
            if info.get("published_url"):
                append("gitee download: " + str(info.get("published_url")) + "\n")
            if info.get("published_package_url"):
                append("gitee package: " + str(info.get("published_package_url")) + "\n")
            messagebox.showinfo("完成", "发布流程已结束。请在窗口中查看详细输出。")
            return

        messagebox.showerror("失败", "发布失败。请在窗口中查看错误详情。")
        return

    root.after(100, pump)
    root.mainloop()
    return 0


def _sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def _load_xml(path: Path) -> ET.ElementTree:
    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
    return ET.parse(str(path), parser=parser)


def _set_csproj_version(csproj: Path, version: str) -> None:
    tree = _load_xml(csproj)
    root = tree.getroot()

    def find_or_create(parent: ET.Element, tag: str) -> ET.Element:
        el = parent.find(tag)
        if el is None:
            el = ET.SubElement(parent, tag)
        return el

    pg = root.find("PropertyGroup")
    if pg is None:
        pg = ET.SubElement(root, "PropertyGroup")

    find_or_create(pg, "Version").text = version
    find_or_create(pg, "AssemblyVersion").text = version
    find_or_create(pg, "FileVersion").text = version

    tree.write(str(csproj), encoding="utf-8", xml_declaration=True)


def _set_iss_version(iss: Path, version: str) -> None:
    text = iss.read_text(encoding="utf-8")
    text2 = re.sub(
        r'(#define\s+MyAppVersionShort\s+")([^"]+)(")',
        r"\g<1>" + version + r"\3",
        text,
        count=1,
    )
    if text2 == text:
        raise RuntimeError("MyAppVersionShort not found in " + str(iss))
    iss.write_text(text2, encoding="utf-8")


def _create_update_package(publish_dir: Path, output_dir: Path, *, version: str, runtime: str) -> Path:
    if not publish_dir.exists():
        raise FileNotFoundError(str(publish_dir))

    files: list[dict] = []
    for path in sorted(publish_dir.rglob("*")):
        if not path.is_file():
            continue
        rel = path.relative_to(publish_dir).as_posix()
        if rel == "maccy-package.json":
            continue
        files.append(
            {
                "path": rel,
                "sha256": _sha256_file(path),
                "size": path.stat().st_size,
            }
        )

    if not any(str(x.get("path", "")).lower() == "maccy.exe" for x in files):
        raise RuntimeError("publish output missing maccy.exe")

    manifest = {
        "appId": "maccy",
        "version": version,
        "runtime": runtime,
        "files": files,
    }

    output_dir.mkdir(parents=True, exist_ok=True)
    package_path = output_dir / f"maccy-{version}-{runtime}.zip"
    if package_path.exists():
        package_path.unlink()

    kwargs = {"compression": zipfile.ZIP_DEFLATED}
    try:
        kwargs["compresslevel"] = 9
    except Exception:
        pass

    with zipfile.ZipFile(package_path, "w", **kwargs) as zf:
        for item in files:
            rel = str(item["path"])
            zf.write(publish_dir / rel, rel)
        zf.writestr(
            "maccy-package.json",
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        )

    return package_path


def _try_find_iscc() -> Path | None:
    candidates: list[Path] = []

    from_path = shutil.which("ISCC") or shutil.which("ISCC.exe")
    if from_path:
        candidates.append(Path(from_path))

    program_files = os.environ.get("ProgramFiles(x86)") or os.environ.get("ProgramFiles")
    if program_files:
        candidates.append(Path(program_files) / "Inno Setup 6" / "ISCC.exe")
        candidates.append(Path(program_files) / "Inno Setup 5" / "ISCC.exe")

    # Try registry (portable installs / non-default paths)
    try:
        import winreg  # type: ignore

        def add_install_location(root, subkey: str) -> None:
            try:
                with winreg.OpenKey(root, subkey) as k:
                    loc, _ = winreg.QueryValueEx(k, "InstallLocation")
                    if loc:
                        candidates.append(Path(str(loc)) / "ISCC.exe")
            except Exception:
                return

        uninstall_roots = [
            winreg.HKEY_LOCAL_MACHINE,
            winreg.HKEY_CURRENT_USER,
        ]
        uninstall_paths = [
            r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
            r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 5_is1",
            r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
            r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 5_is1",
        ]
        for r in uninstall_roots:
            for p in uninstall_paths:
                add_install_location(r, p)
    except Exception:
        pass

    for c in candidates:
        try:
            if c.exists():
                return c
        except Exception:
            continue

    return None


def _find_iscc(explicit: str | None) -> Path:
    if explicit:
        p = Path(explicit)
        if p.exists():
            return p
        raise FileNotFoundError(f"ISCC not found: {explicit}")

    found = _try_find_iscc()
    if found is not None:
        return found

    raise FileNotFoundError(
        "ISCC.exe not found. Install Inno Setup 6 (recommended) or pass --iscc <full_path_to_ISCC.exe>."
    )


def _run_text(cmd: list[str], cwd: Path) -> str:
    p = subprocess.run(cmd, cwd=str(cwd), capture_output=True, text=True, encoding="utf-8", errors="replace", shell=False)
    if p.returncode != 0:
        sys.stdout.write(p.stdout)
        sys.stderr.write(p.stderr)
        raise SystemExit(p.returncode)
    return (p.stdout or "").strip()


def _infer_repo_from_git(repo_root: Path) -> tuple[str, str] | None:
    try:
        url = _run_text(["git", "remote", "get-url", "origin"], cwd=repo_root)
    except Exception:
        return None

    u = url.strip()
    m = re.search(r"gitee\.com[:/](?P<owner>[^/]+)/(?P<repo>[^/]+?)(?:\.git)?$", u)
    if not m:
        return None

    return m.group("owner"), m.group("repo")


def _http_json(method: str, url: str, *, fields: dict[str, str] | None = None, headers: dict[str, str] | None = None) -> object:
    data = None
    if fields is not None:
        data = urllib.parse.urlencode(fields).encode("utf-8")

    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Accept", "application/json")
    if data is not None:
        req.add_header("Content-Type", "application/x-www-form-urlencoded; charset=utf-8")
    if headers:
        for k, v in headers.items():
            req.add_header(k, v)

    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            raw = resp.read().decode("utf-8", errors="replace")
            return json.loads(raw)
    except urllib.error.HTTPError as e:
        body = ""
        try:
            body = (e.read() or b"").decode("utf-8", errors="replace")
        except Exception:
            body = ""

        msg = f"HTTP {getattr(e, 'code', '?')} {getattr(e, 'reason', '')} for {method} {url}"
        if body.strip():
            msg += "\n" + body.strip()
        sys.stderr.write(msg + "\n")
        raise RuntimeError(msg)


def _encode_multipart(fields: dict[str, str], file_field: str, file_path: Path) -> tuple[bytes, str]:
    boundary = "----maccy" + hashlib.sha256(os.urandom(16)).hexdigest()
    parts: list[bytes] = []

    for name, value in fields.items():
        parts.append(("--" + boundary + "\r\n").encode("utf-8"))
        parts.append((f'Content-Disposition: form-data; name="{name}"\r\n\r\n').encode("utf-8"))
        parts.append(value.encode("utf-8"))
        parts.append(b"\r\n")

    filename = file_path.name
    parts.append(("--" + boundary + "\r\n").encode("utf-8"))
    parts.append((f'Content-Disposition: form-data; name="{file_field}"; filename="{filename}"\r\n').encode("utf-8"))
    parts.append(b"Content-Type: application/octet-stream\r\n\r\n")
    parts.append(file_path.read_bytes())
    parts.append(b"\r\n")
    parts.append(("--" + boundary + "--\r\n").encode("utf-8"))

    body = b"".join(parts)
    return body, boundary


def _http_multipart_json(url: str, *, fields: dict[str, str], file_field: str, file_path: Path) -> dict:
    body, boundary = _encode_multipart(fields, file_field, file_path)
    req = urllib.request.Request(url, data=body, method="POST")
    req.add_header("Accept", "application/json")
    req.add_header("Content-Type", f"multipart/form-data; boundary={boundary}")
    with urllib.request.urlopen(req, timeout=300) as resp:
        raw = resp.read().decode("utf-8")
        return json.loads(raw)


def _git(repo_root: Path, args: list[str]) -> None:
    _run(["git", *args], cwd=repo_root)


def _push_current_to_upstream(repo_root: Path) -> None:
    try:
        upstream = _run_text(["git", "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], cwd=repo_root).strip()
        if "/" in upstream:
            remote, branch = upstream.split("/", 1)
            if remote and branch:
                _git(repo_root, ["push", remote, "HEAD:" + branch])
                return
    except Exception:
        pass

    _git(repo_root, ["push"])


def _publish_to_gitee(
    repo_root: Path,
    *,
    owner: str,
    repo_name: str,
    token: str,
    version: str,
    notes: str,
    installer_path: Path,
    package_path: Path | None = None,
) -> dict[str, str]:
    tag = "v" + version

    _git(repo_root, ["add", "maccy/maccy.csproj", "installer/maccy.iss"])
    try:
        _git(repo_root, ["commit", "-m", "release " + tag])
    except SystemExit:
        pass

    existing_tags = _run_text(["git", "tag", "-l", tag], cwd=repo_root)
    if not existing_tags:
        _git(repo_root, ["tag", "-a", tag, "-m", tag])

    _push_current_to_upstream(repo_root)
    _git(repo_root, ["push", "origin", tag])

    api = "https://gitee.com/api/v5"
    releases_by_tag = f"{api}/repos/{owner}/{repo_name}/releases/tags/{urllib.parse.quote(tag)}?access_token={urllib.parse.quote(token)}"
    release: dict | None = None
    try:
        release = _http_json("GET", releases_by_tag)
    except Exception:
        release = None

    if release is None or not str(release.get("id") or ""):
        create_url = f"{api}/repos/{owner}/{repo_name}/releases"
        target_commitish = "master"
        try:
            b = _run_text(["git", "rev-parse", "--abbrev-ref", "HEAD"], cwd=repo_root).strip()
            if b and b != "HEAD":
                target_commitish = b
        except Exception:
            target_commitish = "master"

        last_err: Exception | None = None
        for attempt in range(6):
            try:
                release = _http_json(
                    "POST",
                    create_url,
                    fields={
                        "access_token": token,
                        "tag_name": tag,
                        "name": tag,
                        "target_commitish": target_commitish,
                        "body": notes,
                        "prerelease": "false",
                    },
                )
                break
            except Exception as e:
                last_err = e
                if attempt < 5:
                    time.sleep(2)
                    continue
                raise

        if isinstance(release, dict) is False:
            if last_err is not None:
                raise last_err
            raise RuntimeError("create release failed")

    release_id = str(release.get("id") or "").strip()
    if not release_id:
        raise RuntimeError("create release failed")

    urls = {
        "installer": _upload_gitee_release_asset(
            owner=owner,
            repo_name=repo_name,
            token=token,
            release_id=release_id,
            file_path=installer_path,
        )
    }
    if package_path is not None:
        urls["package"] = _upload_gitee_release_asset(
            owner=owner,
            repo_name=repo_name,
            token=token,
            release_id=release_id,
            file_path=package_path,
        )

    return urls


def _upload_gitee_release_asset(*, owner: str, repo_name: str, token: str, release_id: str, file_path: Path) -> str:
    api = "https://gitee.com/api/v5"
    attach_list_url = f"{api}/repos/{owner}/{repo_name}/releases/{release_id}/attach_files?access_token={urllib.parse.quote(token)}"
    try:
        existing = _http_json("GET", attach_list_url)
        if isinstance(existing, list):
            for it in existing:
                if isinstance(it, dict) and (it.get("name") == file_path.name) and it.get("id"):
                    attach_id = str(it.get("id"))
                    del_url = f"{api}/repos/{owner}/{repo_name}/releases/{release_id}/attach_files/{attach_id}?access_token={urllib.parse.quote(token)}"
                    try:
                        urllib.request.urlopen(urllib.request.Request(del_url, method="DELETE"), timeout=60).read()
                    except Exception:
                        pass
    except Exception:
        pass

    upload_url = f"{api}/repos/{owner}/{repo_name}/releases/{release_id}/attach_files"
    attach = _http_multipart_json(
        upload_url,
        fields={
            "access_token": token,
        },
        file_field="file",
        file_path=file_path,
    )

    download_url = str(attach.get("browser_download_url") or "").strip()
    if not download_url:
        raise RuntimeError("upload attach file failed")

    return download_url


def _update_manifest(
    manifest_path: Path,
    *,
    version: str,
    mandatory: bool,
    notes: str,
    base_url: str,
    sha256: str,
    size: int,
    installer_url: str | None = None,
    package_sha256: str = "",
    package_size: int = 0,
    package_runtime: str = "win-x64",
    package_url: str | None = None,
) -> None:
    data = json.loads(manifest_path.read_text(encoding="utf-8"))
    latest = data.get("latest") or {}

    latest["version"] = version
    latest["mandatory"] = bool(mandatory)
    latest["notes"] = notes
    latest["publishedAt"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    installer = latest.get("installer") or {}
    if installer_url and installer_url.strip():
        installer["url"] = installer_url.strip()
    else:
        url = base_url.rstrip("/") + f"/v{version}/maccy-{version}-setup.exe"
        installer["url"] = url
    installer["sha256"] = sha256
    installer["size"] = int(size)
    latest["installer"] = installer

    if package_sha256 or package_url:
        package = latest.get("package") or {}
        package["kind"] = "zip"
        package["runtime"] = package_runtime
        if package_url and package_url.strip():
            package["url"] = package_url.strip()
        else:
            package["url"] = base_url.rstrip("/") + f"/v{version}/maccy-{version}-{package_runtime}.zip"
        package["sha256"] = package_sha256
        package["size"] = int(package_size)
        latest["package"] = package
    else:
        latest.pop("package", None)

    data["latest"] = latest
    manifest_path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def _validate_version(version: str) -> str:
    v = version.strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", v):
        raise ValueError("version must be like 1.0.2")
    return v


def main() -> int:
    # If launched without any args, default to GUI mode (so users don't need CLI flags).
    # This makes double-click / basic execution open the guided form.
    if len(sys.argv) == 1:
        sys.argv.append("--gui")

    ap = argparse.ArgumentParser(prog="release.py")
    ap.add_argument("--interactive", action="store_true")
    ap.add_argument("--gui", action="store_true")
    ap.add_argument("--publish-gitee", action="store_true")
    ap.add_argument("--yes", action="store_true")
    ap.add_argument("--gitee-owner", default="")
    ap.add_argument("--gitee-repo", default="")
    ap.add_argument("--gitee-token", default="")
    ap.add_argument("--version", default="")
    ap.add_argument("--notes", default="")
    ap.add_argument("--notes-file", default="")
    ap.add_argument("--mandatory", action="store_true")
    ap.add_argument("--base-url", default="")
    ap.add_argument("--iscc", default="")
    ap.add_argument("--runtime", default="win-x64")
    ap.add_argument("--configuration", default="Release")

    args = ap.parse_args()

    repo = Path(__file__).resolve().parents[1]
    csproj = repo / "maccy" / "maccy.csproj"
    iss = repo / "installer" / "maccy.iss"
    publish_dir = repo / "artifacts" / "publish" / args.runtime
    installer_dir = repo / "artifacts" / "installer"
    manifest = repo / "docs" / "updates" / "manifest.json"

    if not csproj.exists():
        raise FileNotFoundError(str(csproj))
    if not iss.exists():
        raise FileNotFoundError(str(iss))
    if not manifest.exists():
        raise FileNotFoundError(str(manifest))

    cfg: dict
    if args.gui:
        cfg = _collect_config_gui(repo, manifest)
        if cfg is None:
            _message_box("Maccy Release", "GUI 不可用（缺少 tkinter）。\n\n请安装带 tkinter 的 Python（Windows 官方安装包通常自带），或换一个 Python 解释器。")
            return 2

        # Run the full pipeline in a GUI log/progress window.
        return _run_pipeline_gui(repo, cfg)
    elif args.interactive:
        cfg = _collect_config_interactive(repo, manifest)
    else:
        if not args.version.strip():
            raise ValueError("missing --version (or use --interactive)")
        if (not args.base_url.strip()) and (not args.publish_gitee):
            raise ValueError("missing --base-url (or use --interactive)")

        notes = args.notes
        if args.notes_file:
            notes = Path(args.notes_file).read_text(encoding="utf-8")
        notes = notes.replace("\r\n", "\n").rstrip("\n")

        cfg = {
            "version": args.version,
            "base_url": args.base_url,
            "mandatory": bool(args.mandatory),
            "notes": notes,
            "notes_file": args.notes_file,
            "iscc": args.iscc,
            "runtime": args.runtime,
            "configuration": args.configuration,
            "publish_gitee": bool(args.publish_gitee),
            "gitee_owner": args.gitee_owner,
            "gitee_repo": args.gitee_repo,
            "gitee_token": args.gitee_token,
            "yes": bool(args.yes),
        }

    version = _validate_version(cfg["version"])
    base_url = str(cfg["base_url"]).strip()
    mandatory = bool(cfg["mandatory"])
    notes = str(cfg["notes"]).replace("\r\n", "\n").rstrip("\n")
    iscc_arg = str(cfg.get("iscc") or "").strip()
    runtime = str(cfg.get("runtime") or "win-x64").strip() or "win-x64"
    configuration = str(cfg.get("configuration") or "Release").strip() or "Release"

    publish_gitee = bool(cfg.get("publish_gitee") or False)
    yes = bool(cfg.get("yes") or False)
    gitee_owner = str(cfg.get("gitee_owner") or "").strip()
    gitee_repo = str(cfg.get("gitee_repo") or "").strip()
    gitee_token = str(cfg.get("gitee_token") or os.environ.get("GITEE_TOKEN", "")).strip()

    if publish_gitee:
        if (not args.gui) and (not args.interactive) and (not yes):
            raise ValueError("publish-gitee requires --yes in non-interactive mode")

        inferred = _infer_repo_from_git(repo)
        if inferred is not None:
            if not gitee_owner:
                gitee_owner = inferred[0]
            if not gitee_repo:
                gitee_repo = inferred[1]
        if not gitee_owner or not gitee_repo:
            raise ValueError("missing gitee owner/repo")
        if not gitee_token:
            raise ValueError("missing gitee token (set env GITEE_TOKEN or provide --gitee-token)")

    if not base_url:
        if not publish_gitee:
            raise ValueError("missing base url (use --base-url or fill it in interactive/gui)")

    # Preflight: resolve ISCC early so we don't mutate files then fail.
    iscc = _find_iscc(iscc_arg or None)

    completed: list[str] = []

    print(f"[1/5] set csproj version -> {version}")
    _set_csproj_version(csproj, version)
    _set_iss_version(iss, version)
    completed.append("更新版本号（maccy.csproj）")

    publish_dir = repo / "artifacts" / "publish" / runtime
    installer_dir = repo / "artifacts" / "installer"

    print(f"[2/5] dotnet publish ({configuration}, {runtime})")
    publish_dir.mkdir(parents=True, exist_ok=True)
    _run(
        [
            "dotnet",
            "publish",
            str(csproj),
            "-c",
            configuration,
            "-r",
            runtime,
            "-o",
            str(publish_dir),
        ],
        cwd=repo,
    )
    completed.append("生成发布目录（dotnet publish）")

    print("[3/5] build installer (Inno Setup)")
    installer_dir.mkdir(parents=True, exist_ok=True)
    _run([str(iscc), str(iss)], cwd=repo)
    completed.append("生成安装包（Inno Setup）")

    installer_path = installer_dir / f"maccy-{version}-setup.exe"
    if not installer_path.exists():
        raise FileNotFoundError(f"installer not found: {installer_path}")

    print("[4/6] build lightweight package")
    package_path = _create_update_package(publish_dir, installer_dir, version=version, runtime=runtime)
    completed.append("build lightweight zip package")

    print("[5/6] compute sha256/size")
    sha256 = _sha256_file(installer_path)
    size = installer_path.stat().st_size
    package_sha256 = _sha256_file(package_path)
    package_size = package_path.stat().st_size
    completed.append("计算安装包 sha256/size")

    published_url = ""
    published_package_url = ""
    if publish_gitee:
        print("[5/7] publish to gitee")
        published = _publish_to_gitee(
            repo,
            owner=gitee_owner,
            repo_name=gitee_repo,
            token=gitee_token,
            version=version,
            notes=notes,
            installer_path=installer_path,
            package_path=package_path,
        )
        published_url = published.get("installer", "")
        published_package_url = published.get("package", "")
        completed.append("发布到 Gitee Release（git push + 上传安装包）")

        print("[6/7] update manifest.json")
        _update_manifest(
            manifest,
            version=version,
            mandatory=mandatory,
            notes=notes,
            base_url=base_url or "https://gitee.com",
            sha256=sha256,
            size=size,
            installer_url=published_url,
            package_sha256=package_sha256,
            package_size=package_size,
            package_runtime=runtime,
            package_url=published_package_url,
        )
        completed.append("更新更新清单（docs/updates/manifest.json）")

        try:
            _git(repo, ["add", "docs/updates/manifest.json"])
            _git(repo, ["commit", "-m", "update manifest for " + "v" + version])
        except SystemExit:
            pass

        _push_current_to_upstream(repo)
        completed.append("推送 manifest 更新（git push）")
    else:
        print("[5/5] update manifest.json")
        _update_manifest(
            manifest,
            version=version,
            mandatory=mandatory,
            notes=notes,
            base_url=base_url,
            sha256=sha256,
            size=size,
            package_sha256=package_sha256,
            package_size=package_size,
            package_runtime=runtime,
        )
        completed.append("更新更新清单（docs/updates/manifest.json）")

    print("\n完成清单：")
    for item in completed:
        print(f"[OK] {item}")

    print("\n产物信息：")
    print(f"installer: {installer_path}")
    print(f"package: {package_path}")
    print(f"sha256: {sha256}")
    print(f"size: {size}")
    print(f"package sha256: {package_sha256}")
    print(f"package size: {package_size}")
    print(f"manifest: {manifest}")

    if published_url:
        print(f"gitee download: {published_url}")
    if published_package_url:
        print(f"gitee package: {published_package_url}")

    print("\n接下来你还需要做：")
    if publish_gitee:
        print("[ ] 确认 Release 和附件是否已生成，并在你的网络环境可访问")
    else:
        print("[ ] git add/commit 并 push 到 gitee（脚本不会自动推送）")
        print("    git status")
        print("    git add maccy/maccy.csproj installer/maccy.iss docs/updates/manifest.json")
        print("    git commit -m \"release v" + version + "\"")
        print("    git push")
        print("[ ] 在 Gitee Releases 创建 tag/release: v" + version + "，上传安装包：" + installer_path.name)
        print("[ ] 确认 installer.url 可访问：" + base_url.rstrip("/") + "/v" + version + "/" + installer_path.name)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
