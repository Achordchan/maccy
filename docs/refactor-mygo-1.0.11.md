# Klip 1.0.11 重构方案：基于 MyGo（Go）

状态：方案阶段，未写代码。依据：当前仓库源码（1.0.10 版，见 `klip/Services/*`）与 [egoist/mygo](https://github.com/egoist/mygo) 文档。

## 1. 目标与约束

- 用 Go + MyGo 重写桌面客户端，单个二进制，体积和内存明显小于现在的 .NET 8 + Avalonia（当前 zip 约 12MB，且自带运行时）。
- 支持 Windows / macOS / Linux，开发机是 macOS。
- 版本号 1.0.11；**1.0.10 及更低版本必须能自动升级到它，用户的历史记录、设置、登录态不丢**。
- 后端 `nas-agent-py` 的 HTTP 协议不变，1.0.10 和 1.0.11 的设备可以同时同步同一个账号。

## 2. MyGo 能力评估

| 需要的能力 | MyGo 现状 | 结论 |
|---|---|---|
| 纯 Go、无 cgo、跨平台交叉编译 | 是 | 满足 |
| 托盘、无 Dock 图标的“启动器”面板、失焦自动隐藏 | `docs/launchers.md` 就是这个场景 | 满足 |
| 全局快捷键 | `mygo.GlobalShortcut.Register`，Windows `RegisterHotKey`、macOS Carbon、Linux X11/portal | 满足，沿用 `Ctrl+Alt+2` |
| 剪贴板读写（文本/图片/文件列表/HTML） | `mygo.Clipboard`，含 `ReadFiles`/`WriteFiles` | 满足 |
| **剪贴板变化通知** | **没有**（只有读写和 `Formats`，未见 change 事件） | **缺口，需自己补** |
| 开机自启 | 有（macOS 登录项、Windows Run 项、Linux autostart） | 满足 |
| 拖拽文件/内容到外部 | `ui/drag-and-drop`、`transfer` | 满足 |
| 文件图标 | `Shell.FileIcon` | 满足 |
| 本地 SQLite（无 cgo） | `plugins/sqlite`，purego 加载 | 满足，用于同步快照 |
| 自动更新 | 自带签名更新 + delta，但是**自有格式** | 1.0.11 用不上，见第 6 节 |
| Windows 安装包 | NSIS | 与现有 Inno 安装包不同，见第 6 节 |
| 前台应用识别（来源应用） | 无 | 缺口，需自己补 |
| 全局鼠标钩子（鼠标晃动呼出 Shelf） | 无 | 缺口，需自己补，且 macOS 需“输入监控”权限 |

风险提示：MyGo 很新（需要 Go 1.27+，仓库当天仍在提交），API 可能变动。**方案里每个依赖都要先做“跑通验证”再动手重写**，见第 8 节。

## 3. 原生 UI 还是 webview：建议原生 UI

建议用 MyGo 的 `ui` 包画原生界面，不用 webview。

- 这是个托盘常驻、快捷键呼出的小面板，要求“按下立刻出现”和低内存；原生 UI 不加载网页引擎，窗口即时打开。
- webview 在 Windows 10 上要求安装 WebView2，在 Linux 上要求 WebKitGTK，会增加用户侧依赖和体积。
- 界面都是列表、搜索框、设置页、图片预览，`ui` 包有虚拟化列表、输入框、表格、Toast、拖拽，足够覆盖。
- 代价：现有 Avalonia 的 XAML 样式不能复用，所有界面要重画一遍；MyGo 原生 UI 比 webview 更年轻。
- 兜底：如果原生 UI 某个控件跑不通（例如图片预览缩放），单个窗口可以改用 webview，MyGo 允许同一个应用混用两种窗口。

## 4. 模块划分

建议新目录 `app/`（Go module），与现有 `klip/`（C#）并存到 1.0.11 发布验收后再删除 C# 部分。

```
app/
  main.go                 入口：单实例、托盘、面板窗口、快捷键
  internal/
    paths/                数据目录（见第 5 节，保持旧路径）
    settings/             settings.json 读写（保留未知字段）
    history/              history.json 读写、去重、置顶、容量/条数清理
    capture/              剪贴板捕获（平台适配：watch_windows.go / watch_darwin.go / watch_linux.go）
    source/               前台应用识别（平台适配）
    apply/                把历史项写回剪贴板
    shelf/                Shelf 窗口与鼠标晃动（先只做 Windows）
    sync/                 与 nas-agent-py 通信：auth、subscription、manifest、snapshot、blobs、SSE
    snapshot/             SQLite 快照导入导出（schema 与 1.0.10 完全一致）
    update/               旧格式更新清单的检查与下载（过渡期）
    ui/                   主面板、偏好设置、关于、图片预览、更新提示
  assets/
installer/                Windows 安装脚本（沿用 Inno，见第 6 节）
tools/release.py          发布脚本（改成构建 Go 产物）
```

与现有 C# 的对应关系：

| 现有 C# | Go 模块 |
|---|---|
| `AppPaths` / `AppSettings*` | `paths` / `settings` |
| `ClipboardHistoryService` / `ClipboardPersistenceService` | `history` |
| `ClipboardCaptureService` + `WindowsClipboardWatcher` | `capture` |
| `ClipboardApplyService` | `apply` |
| `ShelfService` / `MouseShakeDetector` / `WindowsMouseHookService` | `shelf` |
| `NasAgentClient` / `AuthService` / `SyncService` / `SyncEventStreamService` | `sync` |
| `SnapshotSqliteService` | `snapshot` |
| `UpdateService` / `UpdateCoordinator` | `update` |
| `Views/*` + `ViewModels/*` | `ui` |

## 5. 数据兼容（用户数据不丢）

1.0.10 的数据都在 `%LOCALAPPDATA%\maccy\`，1.0.11 在 Windows 上必须继续用这个目录：

- `settings.json`：C# 默认序列化，**属性名是大驼峰**（`Theme`、`MaxItems`、`NasAgentBaseUrl`、`AuthAccessToken` 等）。Go 读写时用同样的键名；读取时保留不认识的字段，原样写回，方便降级。
- `history.json`：`ClipboardItem` 列表，字段 `Id`(Guid)、`Kind`(整数枚举)、`CapturedAt`(ISO 时间)、`ApproxBytes`、`Text`、`ImageFilePath`、`FilePaths`、`Pinned`、`CopyCount`、`FirstCapturedAt`、`Note`、`ContentHash`、`SourceAppName`、`SourceAppPath`。图片项存的是**绝对路径**（指向 `images\`），Go 版启动时要像 `Normalize` 一样校验文件是否存在并重算大小。`Kind` 是整数枚举：0=Text、1=Image、2=FileList（见 `klip/Models/ClipboardItemKind.cs`）。
- `images/`、`files/`、`blobs/`、`sync/`、`updates/`：目录结构不变。
- 写入方式沿用“先写 `.tmp` 再替换”，防止升级中途崩溃损坏文件。
- macOS / Linux 是新平台，没有旧数据，用各自的标准用户数据目录；同步快照里是文件名 + sha256，不含绝对路径，跨平台可以互通。
- 1.0.11 在第一个版本里**不改变** `history.json` 的格式，先保证用户可以降回 1.0.10。是否迁到 SQLite 留到之后的版本。
- 同步快照（`snapshot.sqlite`，表 `meta/settings/items/item_images/item_files`）的结构和 `meta` 键值必须与 1.0.10 完全一致，1.0.10 的设备要能读 1.0.11 导出的快照，反之亦然。

## 6. 升级兼容（核心风险）

### 6.1 旧客户端怎么升级（已读源码确认）

1.0.10 及更低版本的客户端：

1. 从 `https://gitee.com/Achordchan/maccy/raw/master/docs/updates/manifest.json` 读取 `latest`，版本比自己新就提示。
2. 若清单里有 `package`（`kind=zip`、`runtime=win-x64`），走“轻量更新”：下载 zip → 解压到暂存目录 → 要求有 `maccy-package.json`，其 `appId=maccy`、`version` 与清单一致、`runtime` 一致，且每个文件的 sha256/大小都对得上，文件列表里必须有 `maccy.exe` → 等旧进程退出后用 PowerShell 把文件**覆盖进现有安装目录**，删除上次记录里已不存在的文件，最后启动 `maccy.exe`；失败会回滚。
3. 没有 zip 或轻量更新失败，才下载 `installer`（Inno 安装包）并直接启动，**不是静默安装**。

### 6.2 1.0.11 的做法

- **Windows 产物**：Go 编译出的程序**必须命名为 `maccy.exe`**（升级包校验和重启都认这个名字），随 zip 带上 `maccy-package.json`（`appId: "maccy"`）。旧的 .NET 文件（dll 等）会残留在安装目录里，不影响运行；之后的版本再清理。
- **产物里的显示名称**：窗口标题、托盘提示、快捷方式名称等可见文字用 Klip，只有这几个“协议名”保持 maccy（我已在 `993fab8` 提交里还原了 C# 版的这些标识符）。
- **更新清单格式不变**：`latest.version/mandatory/notes/publishedAt/installer/package` 原样，只更新数值。清单放在 gitee 的 `master` 分支同一路径，不能搬。
- **完整安装包继续用 Inno Setup**，沿用同一个 `AppId` GUID 和 `AppMutex=maccy_mutex`，这样 Inno 会原地覆盖旧安装。**不要换成 MyGo 自带的 NSIS 安装包**：NSIS 的安装注册信息和 Inno 不同，会装出第二份，旧的卸载项也留着。NSIS 安装包只留给 macOS/Linux 之外不需要的场景，1.0.11 不用。
- **MyGo 自带的签名更新（updater）**：旧客户端读不了它的格式，所以 1.0.11 里不启用它。更新检查用我们自己的 `update/` 模块读旧格式清单。等 1.0.11 铺开后，再评估是否在 1.0.12 切到 MyGo updater（那时是新旧格式并存的第二次迁移，要另做方案）。
- **macOS / Linux 的更新**：新平台没有历史用户，1.0.11 先做手动下载（清单里新增 `darwin-arm64`、`linux-amd64` 等 runtime 的 package 字段不会影响旧 Windows 客户端，它们只认 `package.runtime == win-x64`；需要在清单结构里支持多个 package 时，要保证旧客户端读到的 `package` 仍是单个 win-x64 对象）。
- **回滚**：保留 1.0.10 的发布包；`history.json`/`settings.json` 格式不变，所以用户随时可以装回 1.0.10。

### 6.3 需要特别验证的点

- Go 程序启动时的单实例互斥：旧进程退出后才会拉起新进程，但升级脚本先复制文件再启动，要确认 Go 版不会因为残留的 `maccy_mutex` 拒绝启动（Windows 用同名命名互斥量，保持与 1.0.10 一致）。
- 开机自启：1.0.10 在注册表 Run 项里写的值名是 `maccy`，Go 版要沿用同名，否则升级后会出现两个自启项。
- 升级过程中旧版会保护 `images/files/blobs/sync/updates` 和几个日志文件不被覆盖，zip 包里不能带这些路径。

## 7. 缺口怎么补

1. **剪贴板变化监听**（最关键）：MyGo 没有 change 事件。按平台各写一个很小的适配层，用 purego 调系统 API（不用 cgo）：Windows 读 `GetClipboardSequenceNumber`，macOS 读 `NSPasteboard.changeCount`，Linux 先用定时轮询 `Formats` 加内容哈希。统一成“序号变了就读一次”的轮询，间隔约 300–500ms，开销很小。
2. **来源应用识别**：Windows 用 `GetForegroundWindow` + 进程路径（和现在 C# 一致）；macOS 用 `NSWorkspace.frontmostApplication`；Linux 暂不支持，字段留空即可（`SourceAppName/SourceAppPath` 本来就可空）。
3. **鼠标晃动呼出 Shelf**：Windows 先照搬鼠标钩子；macOS 需要 CGEventTap 和用户授权“输入监控”，Linux 只有 X11 可行。1.0.11 只保证 Windows，其他平台隐藏这个开关，不做。
4. **macOS 权限**：全局快捷键用 Carbon 热键，一般不需要额外授权。现有代码里没有模拟按键（未发现 SendInput 等调用），“应用”历史项只是写回剪贴板，所以 1.0.11 不需要“辅助功能”权限。

## 8. 跑通验证（先验证再重写）

每一步都有明确的通过标准，上一步不过就不进入下一步。

**阶段 0：环境（macOS）**
1. 本机 Go 已是 1.27.1，满足 MyGo 要求。`go run github.com/egoist/mygo/cmd/mygo@latest init -template native klip-spike`，`go tool mygo dev` 能弹出窗口。

**阶段 1：技术验证（各做一个最小程序，放 `spikes/` 里，不进发布）**
2. 托盘 + 无 Dock 图标面板 + 失焦隐藏 + 全局快捷键 `Ctrl+Alt+2` 呼出/收起。
3. 剪贴板轮询：复制文本、图片、文件列表，各自能被检测并读出；记录 CPU 占用。
4. 虚拟化列表放 1000 条文本 + 图片缩略图，滚动流畅，并记录空闲内存。
5. 把历史项写回剪贴板、拖拽文件到外部应用。
6. 纯 Go 交叉编译出 `windows/amd64` 和 `linux/amd64` 二进制，记录体积。

**阶段 2：数据与协议兼容（可在 macOS 上用单元测试完成）**
7. 取 1.0.10 真实的 `settings.json`/`history.json` 样本（从 Windows 机器拷贝），Go 读 → 写回 → 与原文件逐字段比对无丢失、无新增错误。
8. 用 `SnapshotSqliteService` 的表结构，做“C# 导出 → Go 导入”和“Go 导出 → C# 导入”的往返测试。
9. 用 `nas-agent-py/tests/compat_smoke.py` 作为基础，本地起 `nas-agent-py`，Go 客户端完成注册/登录/刷新/订阅状态/快照上传下载/blob 去重/SSE 触发同步。

**阶段 3：升级验证（需要一台 Windows 机器或虚拟机）**
10. 用 1.0.10 之前的提交（`754e50c^`）编出“旧客户端测试版”，只把更新清单地址改成本机 HTTP 服务。
11. 本机起一个 HTTP 服务当“假 gitee”，发布 1.0.11 的 zip 和清单。
12. 安装旧版、写入若干历史记录和设置，触发更新：期望数据完整、`maccy.exe` 变成新程序、开机自启项只有一个、能正常启动。
13. 故意破坏 zip 里的一个文件（改 sha256），期望旧客户端回滚、旧版仍可用。
14. 完整安装包（Inno）覆盖安装 1.0.10：期望原地升级、数据保留、只剩一个卸载项。
15. 1.0.11 → 手动装回 1.0.10：期望数据仍可读（降级路径）。

**阶段 4：发布**
16. `tools/release.py` 改为构建 Go 产物，沿用 gitee 发布流程；先用测试 tag 走一遍，再发正式版。
17. 清单更新放最后一步；确认前不要改 gitee 上的 `manifest.json`，否则用户会立刻收到升级。

## 9. 需要你确认的事项

- gitee 仓库（读清单的那个）是否与本地从 GitHub 克隆的是同一份代码；发布脚本依赖 `master` 分支。
- 是否有 Windows 机器或虚拟机可用于阶段 3 的升级验证；没有的话升级兼容只能靠单元测试，风险较高。
- 1.0.11 要不要包含 Shelf（鼠标晃动）功能；建议只在 Windows 上保留。
