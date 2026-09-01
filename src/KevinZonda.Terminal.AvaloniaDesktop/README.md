# KevinZonda.Terminal.AvaloniaDesktop

这是 KevinZonda Terminal 的 macOS/Linux Avalonia 桌面宿主。它复用现有的
`KevinZonda.Terminal.WebAssets` 前端，并通过 `KevinZonda.Terminal.UnixPty`
启动本机登录 Shell。

当前已支持：

- xterm.js Workspace、Tab 和分屏界面。
- PTY 输入、输出、二进制输入、resize、退出状态和进程组清理。
- Avalonia WebView 与现有 WebView2 Bridge 消息协议的适配。
- 系统剪贴板、外部链接、新窗口和字体大小持久化。
- macOS 原生应用菜单与 KevinZonda Terminal About 窗口。
- macOS/Linux 系统 CPU 与物理内存用量监控。
- Codex 与 Kimi Code 用量监控；对应 Agent 在终端进程树中运行时自动显示并每 5 分钟刷新。
- 与 Windows 客户端共享 `~/.kterm/config.json`，更新字体大小时保留未知配置项。

应用级快捷键在 macOS 使用 Command（例如 `⌘T`、`⌘\\`、`⌘-`、`⌘W`），Linux
使用 Alt。`⌘W`/`Alt+W` 关闭聚焦 Pane；仅有一个 Pane 时关闭当前 Tab。macOS
的 Option 不会被这些应用命令截获。

运行：

```bash
dotnet run --project src/KevinZonda.Terminal.AvaloniaDesktop
```

在 macOS 上生成当前 CPU 架构的自包含 `.app`：

```bash
make app-avalonia
open "artifacts/macos/osx-arm64/KevinZonda Terminal.app"
```

上面的路径是 Apple Silicon 示例。产物默认使用 ad-hoc 签名，适合本机测试。需要正式签名时可以传入
Developer ID Application 证书名称；签名凭据不会写入仓库：

```bash
make app-avalonia MACOS_SIGN_IDENTITY="Developer ID Application: Example (TEAMID)"
```

该命令目前生成并签名 `.app`，不执行 Apple notarization；对外分发前仍需使用 `notarytool`
提交公证并 staple 公证票据。

打包会复用 Windows 桌面端的 `WinFormsDefault.ico` 并自动转换为 `.icns`。由于 Unix PTY
helper 在构建时按宿主架构编译，目前不允许在 Apple Silicon 上交叉打包 `osx-x64`，反之亦然。

可以用目录参数或 `--working-directory` 指定 Shell 的启动目录：

```bash
dotnet run --project src/KevinZonda.Terminal.AvaloniaDesktop -- ~/work
dotnet run --project src/KevinZonda.Terminal.AvaloniaDesktop -- --working-directory ~/work
```

可以显式运行一次本机 Codex 进程检测与用量读取集成测试：

```bash
dotnet run --project tests/KevinZonda.Terminal.AvaloniaDesktop.Tests -- --live-agent-usage
```

macOS 使用系统自带的 WKWebView，不需要额外运行时。Linux 的嵌入式
`NativeWebView` 需要 WPE WebKit；例如 Ubuntu 24.04+：

```bash
sudo apt install libwpewebkit-2.0-1
```

当前原生 Settings 编辑器尚未接入 Avalonia 宿主；终端主链路不依赖该功能。
