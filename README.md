# KevinZonda Terminal

KevinZonda Terminal 是一个面向 Windows 的最小化 Terminal Emulator MVP。原生宿主使用 .NET 10 WinForms 和 WebView2；终端前端使用 xterm.js/WebGL；每个 Pane 都连接独立的 ConPTY 和 Shell 进程。终端会话优先使用随应用分发的 passthrough ConPTY（`OpenConsole.exe`，来自 Windows Terminal，MIT），让 DECSTBM 等 VT 序列原样到达前端；该文件缺失时自动回退到系统 inbox conhost。

## 当前功能

- 多 Tab，每个 Tab 保存独立的递归分屏布局。
- 同一窗口支持左右、上下拆分以及 2×2 多终端。
- 每个 Pane 独立运行 PowerShell、PowerShell 7 或 CMD。
- WebGL 渲染失败时自动回退。
- 拖动分隔线和窗口 resize 会同步更新 ConPTY 行列数。
- 支持终端选择、`Ctrl+Shift+C` 复制与 `Ctrl+Shift+V` 粘贴。
- codex 等 inline TUI 的历史通过 DECSTBM region scroll 进入终端 scrollback，滚轮可直接查看（依赖 passthrough ConPTY）。
- vim、less 等 alternate screen 应用中，滚轮自动转为方向键（alternate scroll）。
- 新 Tab / 分屏按当前 Pane 尺寸创建 ConPTY，减少全屏 TUI 的二次重绘。
- 底部状态栏会在终端会话中运行 Codex 或 Kimi Code 时显示用量，并每 5 分钟刷新。
- 关闭 Pane、Tab 或应用时回收相应 Shell、ConPTY 和 Win32 handle。

## 快捷键

| 快捷键 | 操作 |
| --- | --- |
| `Alt+T` | 新建 Tab |
| `Alt+\` | 将聚焦 Pane 拆成左右两列 |
| `Alt+-` | 将聚焦 Pane 拆成上下两行 |
| `Ctrl+Shift+C` | 复制终端选择 |
| `Ctrl+Shift+V` | 粘贴到聚焦终端 |

快捷键只在 KevinZonda Terminal 位于前台时生效。

## 构建和运行

源代码构建需要：

- Windows 10 1903 或更高版本
- .NET 10 SDK
- Node.js 与 pnpm
- Microsoft Edge WebView2 Evergreen Runtime
- （可选）`tools/openconsole/OpenConsole.exe`：passthrough ConPTY 主机，构建时嵌入程序集，首次运行释放到 `%LOCALAPPDATA%\KTerm\bin`；缺失或释放失败时自动回退系统 conhost。另有 `OpenConsole.Enhanced.exe`（KTerm 补丁版，resize 后在应用沉默时重绘静态屏幕内容），随附嵌入、默认不启用——在设置 → Shell 勾选 "Enable enhanced OpenConsole"（对新标签页生效），或用环境变量 `KTERM_CONHOST=enhanced` 强制启用、`KTERM_CONHOST=kernel` 强制系统 conhost

```powershell
dotnet build KevinZonda.Terminal.slnx
dotnet run --project src\KevinZonda.Terminal\KevinZonda.Terminal.csproj
```

启动浏览器 Server（默认监听所有网卡的 `7132` 端口）：

```powershell
dotnet run --project src\KevinZonda.Terminal.Server\KevinZonda.Terminal.Server.csproj
```

然后在本机打开 `http://localhost:7132`，或在远程设备打开
`http://<KTerm 所在电脑的 IP>:7132`。可通过 `--urls` 修改监听地址，通过
`--working-directory` 指定新 Shell 的启动目录：

```powershell
dotnet run --project src\KevinZonda.Terminal.Server -- `
  --urls http://0.0.0.0:8080 `
  --working-directory C:\work
```

每个浏览器连接拥有独立的 Workspace、ConPTY 和 Shell；连接关闭后，Server 会回收该连接创建的全部进程。
当前 Server 按本地可信网络场景实现，不包含认证或 TLS。

`.csproj` 会执行前端的 `pnpm install --frozen-lockfile`（首次）和 `pnpm run build`，随后把 Vite 产物嵌入应用程序集。

为兼容已有安装，用户数据仍沿用 `%USERPROFILE%\.kterm` 和 `%LOCALAPPDATA%\KTerm`，诊断环境变量仍使用 `KTERM_*` 前缀，`make install` 仍安装命令别名 `zt.exe`。这些是稳定的内部兼容标识，对外产品名统一为 KevinZonda Terminal。

运行 Debug 端到端 smoke test（自动创建两个 Tab，并在活动 Tab 构造 2×2）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1
```

发布启用 ReadyToRun 的 framework-dependent win-x64 版本：

```powershell
dotnet publish src\KevinZonda.Terminal\KevinZonda.Terminal.csproj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true
```

## GitHub Actions 构建

推送到 `master`、创建 Pull Request，或在 GitHub Actions 页面手动运行
`Build Windows executable`，都会在 Windows runner 上构建单文件 win-x64 版本。
构建成功后，从对应 workflow run 的 **Artifacts** 区下载
`KevinZonda-Terminal-win-x64-YYYYMMDD-HHmmssZ-<short-hash>.zip`，其中包含
`KevinZonda.Terminal.exe` 和 `kterm-server.exe`。文件名中的时间戳使用 UTC，hash 为触发构建的 commit
SHA 前 7 位。
Artifact 保留 30 天；程序运行时需要 .NET 10 Desktop Runtime 和 WebView2 Runtime。

详细架构、消息协议与验收标准参见 [docs/plan.md](docs/plan.md)。
