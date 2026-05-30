# Maccy

Maccy 是一个面向 Windows 的剪贴板管理工具。当前仓库包含桌面客户端、Python 同步后端、自动更新清单和安装包脚本。

## 功能概览

- 记录剪贴板历史，支持文本、图片和文件列表
- 支持搜索、备注、置顶、删除和重复内容合并
- 支持历史持久化，以及按条目数/容量自动清理
- 支持主题、开机启动、捕获开关、文件扩展名和文件大小限制
- 提供全局快捷键 `Ctrl + Alt + 2` 呼出主窗口
- 支持自建 `nas-agent-py` 后端进行账号登录、订阅状态查询、快照同步、Blob 去重传输和 SSE 事件触发同步
- 支持通过更新清单检查新版本、下载安装包并校验 `sha256`

## 仓库结构

- `maccy/`：Windows 桌面客户端，技术栈为 `.NET 8 + Avalonia 11`
- `nas-agent-py/`：Python 同步后端，技术栈为 `FastAPI + uvicorn + httpx + PyJWT`
- `docs/updates/manifest.json`：客户端自动更新清单
- `installer/maccy.iss`：Inno Setup 安装包脚本
- `tools/package_nas_agent_py.py`：后端上传包生成脚本
- `docs/deploy/baota-python-nas-agent.md`：宝塔 Python 项目部署说明

## 客户端开发

### 环境要求

- Windows 10 / 11
- .NET 8 SDK
- Inno Setup 6

### 本地运行

```powershell
dotnet build .\maccy\maccy.csproj
dotnet run --project .\maccy\maccy.csproj
```

启动后默认通过全局快捷键 `Ctrl + Alt + 2` 呼出主窗口。

### 发布目录

```powershell
dotnet publish .\maccy\maccy.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\publish\win-x64
```

### 打包安装程序

```powershell
"D:\Inno Setup 6\ISCC.exe" .\installer\maccy.iss
```

## 同步后端 `nas-agent-py`

`nas-agent-py` 是当前客户端实际对接的后台服务，不是旧的 `.NET + Docker` 方案。它主要负责：

- `GET /health`：健康检查
- `POST /auth/register`、`/auth/login`、`/auth/refresh`
- `GET /auth/me`
- `GET /subscription/status`
- `POST /card/redeem`
- `GET /sync/manifest`
- `GET/PUT /sync/snapshot`
- `POST /sync/blobs/check`
- `GET/PUT /sync/blobs/{blob_sha256}`
- `GET /sync/events`
- `GET /admin` 及一组后台管理接口

### 环境变量

可以参考 `nas-agent-py/.env.example`：

- `STORAGE_ROOT`：后端数据目录
- `ADMIN_KEY`：后台管理密钥
- `ADMIN_EMAIL`：后台登录邮箱
- `ADMIN_PASSWORD`：后台登录密码
- `AUTH_ISSUER`：JWT issuer
- `AUTH_AUDIENCE`：JWT audience
- `AUTH_SIGNING_KEY`：JWT 签名密钥
- `LISTEN_HOST`：监听地址
- `LISTEN_PORT`：监听端口

### 本地启动

```powershell
python -m venv .venv
.\.venv\Scripts\pip install -r .\nas-agent-py\requirements.txt
Copy-Item .\nas-agent-py\.env.example .\nas-agent-py\.env
# 按需修改 .\nas-agent-py\.env
.\.venv\Scripts\python -m uvicorn main:app --app-dir .\nas-agent-py --host 127.0.0.1 --port 17655
```

启动后可访问：

- `http://127.0.0.1:17655/health`
- `http://127.0.0.1:17655/admin`

在 Linux / 宝塔环境中，也可以直接使用 `nas-agent-py/run.sh` 启动，它会读取 `LISTEN_HOST` 和 `LISTEN_PORT`。

### 兼容性烟雾测试

```powershell
.\.venv\Scripts\python .\nas-agent-py\tests\compat_smoke.py --base-url http://127.0.0.1:17655 --admin-key change-me --skip-upload
```

如果要覆盖快照上传下载链路，可以去掉 `--skip-upload`。

### 打包上传

```powershell
python .\tools\package_nas_agent_py.py
```

输出目录：

- `artifacts/nas-agent-py/current/`
- `artifacts/nas-agent-py/nas-agent-py-upload.zip`

## 自动更新

客户端读取：

- `docs/updates/manifest.json`

更新流程基于以下字段：

- `latest.version`
- `latest.notes`
- `latest.installer.url`
- `latest.installer.sha256`
- `latest.installer.size`

如果发现更高版本，客户端会下载 `latest.installer.url` 指向的安装包，校验 `sha256` 后启动安装器。

## 部署参考

- [宝塔部署 `nas-agent-py`](docs/deploy/baota-python-nas-agent.md)

## 发布仓库

- Gitee: https://gitee.com/Achordchan/maccy