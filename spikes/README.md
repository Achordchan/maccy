# spikes：阶段 1 技术验证

只用于验证 MyGo（v0.5.5，Go 1.27.1）能否满足 Klip 1.0.11 的需求，**不进入发布**。

## shell/

一个集成程序：托盘图标、`Ctrl+Alt+2` 呼出的面板、剪贴板轮询、可注入大量假数据的虚拟化列表。

```bash
cd spikes/shell
go tool mygo dev                 # 开发模式
CGO_ENABLED=0 go build -o /tmp/klip-spike .   # 或直接编译，加 -seq=false 可关闭变化计数器
go test -run TestListPerf -v     # 无窗口的列表性能测试
```

- `changeseq_*.go`：各平台的剪贴板变化计数器（macOS 用 purego 调 `NSPasteboard.changeCount`，Windows 调 `GetClipboardSequenceNumber`，其他平台返回 -1 并退回哈希轮询）。
- `perf_test.go`：用 `ui.NewTester` 无窗口渲染，测首帧、滚动、搜索耗时。

## 结果（macOS arm64，本机实测）

| 项目 | 结果 |
|---|---|
| 全局快捷键 `Ctrl+Alt+2` | 注册成功，日志确认 |
| 托盘 + 无 Dock 图标 + 面板显示/失焦隐藏 | 面板可显示，失焦后自动隐藏（日志确认 show/blur 两条路径）；窗口画面未能截图，需要人工在本机按快捷键目视确认 |
| 剪贴板捕获：文本 / 图片 / 文件列表 | 三种都能识别并读出（文件列表格式为 `application/x-mygo-file-list` 加 `text/uri-list`）；重复内容按哈希去重；连续 8 次复制全部捕获，延迟约 1 秒内 |
| 轮询开销（400ms 一次，空闲） | 用变化计数器：约 **0.03% CPU**；不用计数器、每次都读剪贴板：约 **1.3% CPU** |
| 列表（无窗口渲染） | 1 千 / 1 万 / 10 万条：滚动一帧约 **0.5ms**，与条数无关；首帧约 1ms；10 万条时堆约 24MB |
| 搜索（简单子串匹配） | 1 万条约 6ms，10 万条约 **47ms**（线性），真实版需预存小写文本或加防抖 |
| 内存 | 常驻（面板未显示）约 33–46MB；面板第一次显示后约 76MB（`top` 的 MEM 列；`ps` 的 RSS 含共享库，约 100–170MB，不作参考） |
| 二进制体积（去符号） | darwin/arm64 约 9.6MB，windows/amd64 约 10.2MB，linux/amd64 约 9.9MB；不含 .NET 运行时 |
| 交叉编译 | 在 macOS 上用 `CGO_ENABLED=0` 成功编出 windows/amd64、linux/amd64、darwin/arm64 |

## 发现与对方案的影响

1. **MyGo 没有剪贴板变化事件**：必须自己写变化计数器（见 `changeseq_*.go`），已验证可行，且能把轮询开销降到接近 0。
2. **剪贴板读写都会切到主线程**：轮询用后台协程调用没问题，但读取大图片时会占用主线程；真实版里大图要先看大小再读。
3. **体积约 10MB**：比预期大，来自 MyGo 的 UI 引擎和字体；和现有 12MB 的 zip（带运行时）相比差距不大，“更轻便”主要体现在内存和启动速度，而不是体积。后续可以用 `-ldflags="-s -w"` 和裁剪资源继续压。
4. **Windows 和 Linux 部分只验证了能编译**，没有运行：托盘、快捷键、`GetClipboardSequenceNumber` 需要在 Windows 机器上跑一遍；Linux 托盘需要 `libayatana-appindicator3`。
5. `Window.Update` 会在回调里改状态并触发重绘，轮询协程应只在面板可见时才频繁调用，否则会产生多余的重绘。
