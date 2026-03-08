# Maccy

Maccy 是一款面向 Windows 的剪贴板管理工具，支持文本、图片和文件的捕获、搜索、预览与快速粘贴。

## 主要功能

- 剪贴板历史记录
- 文本、图片、文件三类内容捕获
- 搜索与快速筛选
- 收藏与备注
- 文件货架（Shelf）
- 云同步
- 自动更新

## 当前架构

- 客户端：`maccy/`
- 自托管同步与账号服务：`nas-agent/`
- 自动更新清单：`docs/updates/manifest.json`
- 安装包脚本：`installer/maccy.iss`

## 环境要求

- Windows 10 / 11
- .NET 8 SDK（开发）
- Inno Setup 6（打包安装包）

## 本地运行

```powershell
dotnet build .\maccy\maccy.csproj
dotnet run --project .\maccy\maccy.csproj
```

## 构建客户端发布目录

```powershell
dotnet publish .\maccy\maccy.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\publish\win-x64
```

## 构建 nas-agent

```powershell
dotnet build .\nas-agent\NasAgent.csproj -c Release
```

## 打包安装包

使用 Inno Setup 编译：

```powershell
"D:\Inno Setup 6\ISCC.exe" .\installer\maccy.iss
```

安装包输出目录：

- `artifacts/installer/`

## 自动更新

客户端会读取：

- `docs/updates/manifest.json`

如果发现更高版本，会下载 `latest.installer.url` 指向的安装包，校验 `sha256` 后启动安装器。

## 云同步与账号

当前版本已移除第三方 Authing，改为：

- `nas-agent` 自建邮箱 + 密码账号体系
- 自建 JWT / refresh token
- 自托管订阅、卡密和同步快照

## 发布仓库

- Gitee: https://gitee.com/Achordchan/maccy

