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

也可以直接运行根目录的 `server.cmd`；命令行参数会原样传给 Server：

```powershell
server.cmd --urls http://0.0.0.0:8080
```

也可以使用 `kterm-server-launcher.exe` 在系统托盘管理 Server。Launcher 默认自动启动同目录下的
`kterm-server.exe`，右键托盘图标可选择 `Start`、`Stop`、`Settings...`、`Logs` 和 `Exit`；双击图标也会打开日志窗口。
传给 Launcher 的命令行参数会继续传给 Server，例如：

```powershell
kterm-server-launcher --urls http://0.0.0.0:8080 --auth-mode required
```

`Settings...` 会读写 `%USERPROFILE%\.kterm\server_launcher.json`，可配置自动启动、监听地址、鉴权模式、
Shell 启动目录、断线 Runtime 保留时间和其他 Server 参数：

```json
{
  "autoStart": true,
  "server": {
    "urls": "http://0.0.0.0:7132",
    "authMode": "auto",
    "workingDirectory": null,
    "runtimeRetentionMinutes": 30,
    "additionalArguments": []
  }
}
```

可用 `--config <path>` 指定其他 Launcher 配置文件。配置中的参数先生效，直接传给 Launcher 的 Server
命令行参数最后生效，可用于临时覆盖。Server 运行期间保存设置时，Launcher 会询问是否立即重启。
`workingDirectory` 为 `null` 或在 Settings 中留空时，Launcher 默认使用当前用户的 `%USERPROFILE%`；
仅当用户目录不可用时才回退到 Launcher 的当前目录。

`Stop` 和 `Exit` 会先请求 Server 优雅关闭；如果 Server 无响应，Launcher 会清理其完整进程树。
Launcher 为单实例程序，关闭日志窗口只会隐藏窗口，不会停止 Server。

然后在本机打开 `http://localhost:7132`，或在远程设备打开
`http://<KTerm 所在电脑的 IP>:7132`。可通过 `--urls` 修改监听地址，通过
`--working-directory` 指定新 Shell 的启动目录：

```powershell
dotnet run --project src\KevinZonda.Terminal.Server -- `
  --urls http://0.0.0.0:8080 `
  --working-directory C:\work
```

Server 默认以 `auto` 模式读取 `%USERPROFILE%\.kterm\server_auth.json`。可先创建一个
Argon2id 密码哈希（登录用户名固定为 `kterm`）：

```powershell
make auth-init
# 或使用已安装的 Server
kterm-server auth init
```

后续可用 `kterm-server auth add` 增加轮换密码，用 `kterm-server auth verify` 验证密码；三者都支持
`--file <path>`。

配置存在且 `allowedHash` 非空时，浏览器会在 `/auth/login` 显示原生 Basic Auth 登录框；验证成功后
Server 会签发 HttpOnly Cookie，页面资源和 `/ws` WebSocket 都通过该 Cookie 鉴权。`/healthz` 保持公开。
配置不存在或 `allowedHash` 为空时，`auto` 模式会输出 `No Pass Hash, fallback to No Pass.` 并按无密码模式运行。

可用 `--auth-file <path>` 指定其他配置文件；`--auth-mode required` 要求配置存在且非空，
`--auth-mode disabled` 则明确关闭密码验证：

```powershell
server.cmd --auth-mode required --auth-file C:\path\server_auth.json
```

每个浏览器页面拥有独立的 Workspace、ConPTY 和 Shell。WebSocket 断开后，页面会按指数退避自动重连，
并恢复原来的 ConPTY、Shell PID 和未确认输出。刷新当前页面时，还会恢复 Workspace、Pane、Tab、
活动项以及终端滚屏历史，并继续使用刷新前的 Shell；普通的新页面仍会创建独立 Runtime。
地址栏会使用 `#runtime=<id>&session=<id>` 标识当前 Runtime 和活动终端；把完整地址复制到新标签页会
接管并恢复同一套 Workspace 和 Shell，原标签页会停止重连。修改 `session` 值也可以直接定位对应 Tab。
断开的 Runtime 默认保留 30 分钟。可通过
`--runtime-retention-minutes` 调整保留时间。

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
`KevinZonda.Terminal.exe`、`kterm-server.exe` 和 `kterm-server-launcher.exe`。文件名中的时间戳使用 UTC，hash 为触发构建的 commit
SHA 前 7 位。
Artifact 保留 30 天；程序运行时需要 .NET 10 Desktop Runtime 和 WebView2 Runtime。

详细架构、消息协议与验收标准参见 [docs/plan.md](docs/plan.md)。
