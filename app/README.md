# app：Klip 1.0.11 的 Go 实现

阶段 2 的内容：与 1.0.10 及更低版本互通的数据层，还没有界面。

| 包 | 内容 |
|---|---|
| `internal/model` | `Item`（ClipboardItem）、`Settings`（AppSettings），带 C# 的 JSON 格式：大驼峰字段、枚举为整数、时间为 DateTimeOffset（保留 `+08:00` 之类的偏移，最多 7 位小数，UTC 写成 `+00:00`） |
| `internal/paths` | 数据目录：Windows 沿用 `%LOCALAPPDATA%\maccy`，macOS `~/Library/Application Support/Klip`，Linux `$XDG_DATA_HOME/klip` |
| `internal/settings` | `settings.json` 读写；不认识的字段原样保留；旧同步地址迁移；先写临时文件再替换 |
| `internal/history` | `history.json` 读写；启动时按 `Normalize` 丢弃内容已不存在的条目、校正大小 |
| `internal/snapshot` | 同步快照（SQLite，schema 2，兼容读 schema 1）的导出与导入 |

## 对照测试

```bash
cd app && go test ./...
```

测试里有四类对照：

1. **直接读 C# 源码比较**（`../klip/Services/*.cs`、`Models/*.cs`）：`AppSettings` 的字段名、顺序和默认值，`ClipboardItem` 的字段顺序，`ClipboardContentKind` 的枚举顺序，快照的 DDL 文本和 schema 版本。C# 代码一改，这些测试就会失败。C# 源码不在仓库时这几项自动跳过。
2. **C# 写出的样例文件**（`testdata/`）：读进来再写回去，所有字段、null、非 ASCII 转义和未知字段都不变。
3. **独立手写 SQL 模拟 C# 的写法**造出 schema 1 和 schema 2 的快照，确认 Go 能读。
4. **两台设备往返**：设备 A 导出快照，设备 B 没有任何内容，通过 blob 回调按哈希取回后导入，图片和文件字节一致。

## 没有验证的部分

- **没有真正运行 C# 客户端**：本机没有 dotnet，所以“C# 读 Go 写出的快照/JSON”只能靠上面的源码对照和格式推理保证，没有端到端验证。需要在装有 .NET 的机器上补一次：用 Go 导出的 `snapshot.sqlite` 和 `history.json` 喂给 1.0.10。
- 时间格式只在字符串层面比对，没有用 .NET 的解析器验证。
- `Normalize` 里 Windows 路径的行为（盘符、大小写）只在 macOS 上测过。

## SQLite：已换成 MyGo 的 plugins/sqlite

`internal/snapshot` 现在用 `github.com/egoist/mygo/plugins/sqlite`（SQLite 3.53.4，Zig 编译的动态库，通过 purego 加载，无 cgo）。数据库访问集中在 `writeDB`、`itemStatements` 和 `Read` 里的几个读取函数。全部测试不改动通过；测试里另外用 modernc 打开快照，作为独立的 SQLite 实现互相校验（只在测试里，不进程序）。

| 项目 | 结果 |
|---|---|
| 库文件大小 | darwin-arm64 1.48MB，darwin-amd64 1.38MB，linux-amd64 1.40MB，windows-amd64 1.40MB，windows-arm64 1.27MB |
| 对二进制的影响 | 纯 Go 的 modernc 约增加 5MB（只含快照读写的程序 7.0MB，纯标准库对照 1.8MB）；插件方式下库不进二进制，只多一个 1.3–1.5MB 的文件，代码部分很小。整个应用里 MyGo 本身已经在，所以净节省约 2MB 左右（估算，未用完整应用实测） |
| 加载方式 | macOS / Linux：`dlopen`（Linux 加 `RTLD_DEEPBIND`，避免和 WebKit 的系统 SQLite 冲突）；Windows：`syscall.LoadLibrary` + `GetProcAddress`，调用走 `purego.SyscallN` |
| 库的位置 | 依次找：环境变量 `MYGO_SQLITE_LIBRARY`、应用资源目录、可执行文件所在目录；都没有且未打包时，下载到用户缓存目录并校验 SHA-256；已打包的应用缺库会直接报错，不下载 |
| 支持的平台 | darwin/linux/windows 的 amd64 和 arm64。**不支持 32 位**：现在 C# 版的 `win-x86` 用户升级不了，需要确认是否还有这类用户 |
| 性能 | 5000 条文本：导出 42ms（840KB），读取 18ms |

要注意的限制：
- **打包要带上库**：Windows 是 `mygo-sqlite3.dll`，macOS 是 `libmygo-sqlite3.dylib`。`mygo build` 会自动放进应用；我们自己的发布流程（升级 zip 的文件清单、Inno 安装包）要把它列进去，并且 `maccy-package.json` 里要有它的 sha256。
- **首次运行测试需要联网**，下载库到 `~/Library/Caches/mygo/natives/`；CI 里可以预置或设置 `MYGO_SQLITE_LIBRARY`。
- 插件每次只执行一条语句，所以建表脚本在代码里按分号拆开，PRAGMA 单独执行；插件拒绝事务控制语句，批量写入用它的 `Transaction`（`BEGIN IMMEDIATE`）。
- 插件依赖整个 `mygo` 包（取资源目录），所以数据层的单元测试也会链接 MyGo 核心；这对最终应用没有额外代价。
