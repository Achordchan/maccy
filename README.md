# Maccy

Maccy 是一个面向 Windows 的剪贴板管理工具，支持文本、图片和文件捕获，并提供搜索、备注、置顶、云同步和自动更新能力。

## 当前结构

- 客户端：`maccy/`
- Python 同步后端：`nas-agent-py/`
- 自动更新清单：`docs/updates/manifest.json`
- 安装包脚本：`installer/maccy.iss`

## 客户端开发

环境要求：

- Windows 10 / 11
- .NET 8 SDK
- Inno Setup 6

本地运行：

```powershell
dotnet build .\maccy\maccy.csproj
dotnet run --project .\maccy\maccy.csproj
```

发布目录：

```powershell
dotnet publish .\maccy\maccy.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\publish\win-x64
```

安装包：

```powershell
"D:\Inno Setup 6\ISCC.exe" .\installer\maccy.iss
```

## Python 后端

后端已经从旧 `.NET + Docker` 方案迁移为 Python 版 `nas-agent-py`。

本地启动：

```powershell
python -m venv .venv
.\.venv\Scripts\pip install -r .\nas-agent-py\requirements.txt
$env:STORAGE_ROOT = ".\tmp\nas-agent-data"
$env:ADMIN_KEY = "change-me"
$env:AUTH_SIGNING_KEY = "change-me-to-a-long-random-secret"
.\.venv\Scripts\python -m uvicorn main:app --app-dir .\nas-agent-py --host 127.0.0.1 --port 17655
```

宝塔部署：

- [docs/deploy/baota-python-nas-agent.md](/E:/test/maccy/docs/deploy/baota-python-nas-agent.md)

上传打包：

```powershell
python .\tools\package_nas_agent_py.py
```

## 自动更新

客户端读取：

- `docs/updates/manifest.json`

如果发现更高版本，会下载 `latest.installer.url` 指向的安装包，校验 `sha256` 后启动安装器。

## 发布仓库

- Gitee: https://gitee.com/Achordchan/maccy
