

# Maccy

Maccy 是一款开源的 Windows 剪贴板管理工具，帮助用户高效管理和使用剪贴板内容，支持文本、图片和文件的捕获与粘贴。

## 主要特性

- **剪贴板历史记录**：自动保存剪贴板历史内容，方便随时调用和检索
- **多格式支持**：完整支持文本、图片和文件的捕获与粘贴，满足多样化需求
- **快速搜索**：提供实时搜索功能，通过关键词快速定位目标内容
- **固定条目**：支持将重要条目固定，防止被自动清除
- **文件架（Shelf）**：通过鼠标悬停或快捷键快速访问临时文件集合
- **主题模式**：支持跟随系统、深色和浅色三种主题
- **自动启动**：支持系统开机时自动运行
- **更新检查**：内置自动更新检测，确保软件始终保持最新

## 技术架构

- **框架**：基于 Avalonia UI 的现代化跨平台桌面应用框架
- **语言**：C# / .NET
- **平台**：专为本 Windows 系统优化
- **存储**：本地 JSON 文件持久化存储剪贴板历史

## 系统要求

- 操作系统：Windows 10/11
- 运行时：.NET 8.0 或更高版本

## 安装指南

### 方法一：安装包安装（推荐）

1. 从 [Gitee Releases](https://gitee.com/Achordchan/maccy/releases) 下载最新版本的安装程序
2. 运行安装程序并按照提示完成安装
3. 安装完成后，Maccy 将自动在系统托盘中显示图标

### 方法二：源码编译

```bash
# 克隆仓库
git clone https://gitee.com/Achordchan/maccy.git
cd maccy

# 编译项目
dotnet build -c Release

# 运行程序
dotnet run --project maccy/maccy.csproj -c Release
```

## 使用说明

### 基本操作

- **打开剪贴板窗口**：按下 `Ctrl + Alt + 2` 快捷键
- **搜索内容**：在剪贴板窗口的搜索框中输入关键词进行过滤
- **粘贴内容**：点击历史记录中的条目，将其粘贴到当前活动应用程序
- **固定条目**：右键点击条目选择"固定"，防止被自动清除
- **删除条目**：右键点击条目选择"删除"将其移除
- **编辑备注**：右键点击条目选择"编辑备注"添加描述信息

### 文件架（Shelf）功能

文件架是 Maccy 的特色功能，用于临时存放和快速访问文件：

- **触发方式**：在设置中配置触发快捷键（默认 `Ctrl`），按住快捷键并将鼠标悬停在文件上即可打开文件架
- **添加文件**：将文件拖拽到文件架窗口
- **管理文件**：在文件架中可以预览、打开或移除文件
- **视图切换**：支持紧凑视图和展开视图，可切换列表/网格显示模式

### 预览功能

- 将鼠标悬停在图片条目上可查看大图预览
- 预览窗口显示图片尺寸、文件大小、来源应用等信息
- 支持快速打开原文件

## 设置选项

在系统托盘中右键点击 Maccy 图标，选择"设置"可配置以下选项：

| 设置项 | 说明 |
|--------|------|
| 主题 | 跟随系统 / 深色 / 浅色 |
| 开机自启 | 系统启动时自动运行 |
| 最大条目数 | 保存的剪贴板历史条目数量上限 |
| 最大存储空间 | 剪贴板历史占用的磁盘空间上限 |
| 捕获类型 | 可分别开启/关闭文本、图片、文件的捕获 |
| 文件扩展名 | 捕获文件时匹配的文件类型 |
| 文件大小限制 | 单个捕获文件的大小上限 |
| 合并重复项 | 自动合并相同内容的剪贴板记录 |
| 排除固定项 | 固定条目不计入数量和空间限制 |
| 启用文件架 | 开启/关闭文件架功能 |
| 文件架触发键 | 配置文件架的触发快捷键 |

## 项目结构

```
maccy/
├── Models/                 # 数据模型
│   ├── ClipboardItem.cs   # 剪贴板条目模型
│   └── ...
├── Services/              # 业务服务
│   ├── ClipboardCaptureService.cs    # 剪贴板捕获
│   ├── ClipboardHistoryService.cs    # 历史管理
│   ├── ClipboardPersistenceService.cs # 持久化存储
│   ├── WindowsClipboardWatcher.cs    # 剪贴板监听
│   ├── WindowsHotkeyService.cs       # 快捷键处理
│   ├── ShelfService.cs               # 文件架功能
│   └── ...
├── ViewModels/            # 视图模型
│   ├── MainWindowViewModel.cs
│   ├── PreferencesWindowViewModel.cs
│   └── ShelfWindowViewModel.cs
├── Views/                 # 视图层
│   ├── MainWindow.axaml
│   ├── PreferencesWindow.axaml
│   └── ShelfWindow.axaml
├── Converters/           # 值转换器
└── App.axaml             # 应用入口配置
```

## 贡献指南

欢迎社区贡献者参与项目开发：

1. **Fork** 本项目
2. 创建特性分支：`git checkout -b feature/your-feature`
3. 提交更改：`git commit -am 'Add some feature'`
4. 推送分支：`git push origin feature/your-feature`
5. 创建 **Pull Request** 描述您的更改

### 开发提示

- 项目使用 .NET 8.0 和 Avalonia UI
- 请遵循现有的代码风格和项目结构
- 确保提交前运行 `dotnet build` 编译通过

## 许可证

本项目采用 MIT 许可证开源，详情请参阅 [LICENSE](LICENSE) 文件。

## 联系方式

- 项目地址：https://gitee.com/Achordchan/maccy
- 问题反馈：请在 Gitee Issues 中提交

---

感谢您使用 Maccy！如有任何问题或建议，欢迎在项目仓库中提出。