# KevinZonda.Terminal.AvaloniaDesktop

这是 KevinZonda Terminal 的 macOS/Linux Avalonia 桌面宿主。它复用现有的
`KevinZonda.Terminal.WebAssets` 前端，并通过 `KevinZonda.Terminal.UnixPty`
启动本机登录 Shell。

当前已支持：

- xterm.js Workspace、Tab 和分屏界面。
- PTY 输入、输出、二进制输入、resize、退出状态和进程组清理。
- Avalonia WebView 与现有 WebView2 Bridge 消息协议的适配。
- 系统剪贴板、外部链接、新窗口和字体大小持久化。
- macOS/Linux 系统 CPU 与物理内存用量监控。
- 与 Windows 客户端共享 `~/.kterm/config.json`，更新字体大小时保留未知配置项。

运行：

```bash
dotnet run --project src/KevinZonda.Terminal.AvaloniaDesktop
```

可以用目录参数或 `--working-directory` 指定 Shell 的启动目录：

```bash
dotnet run --project src/KevinZonda.Terminal.AvaloniaDesktop -- ~/work
dotnet run --project src/KevinZonda.Terminal.AvaloniaDesktop -- --working-directory ~/work
```

macOS 使用系统自带的 WKWebView，不需要额外运行时。Linux 的嵌入式
`NativeWebView` 需要 WPE WebKit；例如 Ubuntu 24.04+：

```bash
sudo apt install libwpewebkit-2.0-1
```

当前原生 Settings 编辑器和 Agent Usage 尚未接入 Avalonia 宿主；终端主链路
不依赖这些功能。
