# KevinZonda Terminal

KevinZonda Terminal 是一个使用 .NET 10 和 xterm.js/WebGL 的 Terminal Emulator。Windows 稳定版宿主使用 WinForms、WebView2 和 ConPTY；macOS/Linux 预览版宿主使用 Avalonia、NativeWebView 和 Unix PTY。Windows 终端会话优先使用随应用分发的 passthrough ConPTY（`OpenConsole.exe`，来自 Windows Terminal，MIT），让 DECSTBM 等 VT 序列原样到达前端；该文件缺失时自动回退到系统 inbox conhost。

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
- Windows 任务栏右键菜单显示最近打开的 10 个 Workspace，点击后以对应目录启动新窗口。
- 底部状态栏会在终端会话中运行 Codex 或 Kimi Code 时显示用量，并每 5 分钟刷新。
- 关闭 Pane、Tab 或应用时回收相应 Shell、ConPTY 和 Win32 handle。

## 快捷键

| 快捷键 | 操作 |
| --- | --- |
| Windows/Linux `Alt+T`；macOS `⌘T` | 新建 Tab |
| Windows/Linux `Alt+\`；macOS `⌘\` | 将聚焦 Pane 拆成左右两列 |
| Windows/Linux `Alt+-`；macOS `⌘-` | 将聚焦 Pane 拆成上下两行 |
| Windows/Linux `Alt+W`；macOS `⌘W` | 关闭聚焦 Pane；仅有一个 Pane 时关闭当前 Tab |
| Windows/Linux `Alt+S`；macOS `⌘S` 或 `⌘,` | 打开 Settings |
| `Ctrl+Shift+C` | 复制终端选择 |
| `Ctrl+Shift+V` | 粘贴到聚焦终端 |

快捷键只在 KevinZonda Terminal 位于前台时生效。

最近 Workspace 只记录窗口的启动目录，不恢复 Tab、Pane 或 Shell 状态；记录保存在
`%USERPROFILE%\.kterm\recent_workspaces.json`。不存在的目录会自动移除，在 Windows Jump List 中手动删除的项目也会受到尊重。

## 构建和运行

Windows 桌面端构建需要：

- Windows 10 1903 或更高版本
- .NET 10 SDK
- Node.js 与 pnpm
- Microsoft Edge WebView2 Evergreen Runtime
- （可选）`tools/openconsole/OpenConsole.exe`：passthrough ConPTY 主机，构建时嵌入程序集，首次运行释放到 `%LOCALAPPDATA%\KTerm\bin`；缺失或释放失败时自动回退系统 conhost。另有 `OpenConsole.Enhanced.exe`（KTerm 补丁版，resize 后在应用沉默时重绘静态屏幕内容），随附嵌入、默认不启用——在设置 → Shell 勾选 "Enable enhanced OpenConsole"（对新标签页生效），或用环境变量 `KTERM_CONHOST=enhanced` 强制启用、`KTERM_CONHOST=kernel` 强制系统 conhost

```powershell
dotnet build KevinZonda.Terminal.slnx
dotnet run --project src\KevinZonda.Terminal.WinFormsDesktop\KevinZonda.Terminal.WinFormsDesktop.csproj
```

macOS/Linux Avalonia 预览版复用同一套终端前端，运行方法如下：

```bash
dotnet run --project src/KevinZonda.Terminal.AvaloniaDesktop
```

macOS 使用系统 WKWebView；Linux 的嵌入式 WebView 需要 WPE WebKit。更多说明见
[Avalonia Desktop README](src/KevinZonda.Terminal.AvaloniaDesktop/README.md)。

启动浏览器 Server（默认监听所有网卡的 `7132` 端口）：

```powershell
dotnet run --project src\KevinZonda.Terminal.Server\KevinZonda.Terminal.Server.csproj
```

也可以直接运行根目录的 `server.cmd`；命令行参数会原样传给 Server：

```powershell
server.cmd --urls http://0.0.0.0:8080
```

也可以使用 `kterm-server-launcher.exe` 在系统托盘管理 Server。Launcher 默认自动启动同目录下的
`kterm-server.exe`，右键托盘图标可选择 `Start`、`Stop`、`Settings...`、`Credential Management...`、
`Logs` 和 `Exit`；双击图标也会打开日志窗口。
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
    "customUsername": "kterm",
    "icpRegistration": null,
    "workingDirectory": null,
    "runtimeRetentionMinutes": 30,
    "certificate": {
      "publicCertificatePath": null,
      "privateKeyPath": null
    },
    "additionalArguments": []
  }
}
```

可用 `--config <path>` 指定其他 Launcher 配置文件。配置中的参数先生效，直接传给 Launcher 的 Server
命令行参数最后生效，可用于临时覆盖。Server 运行期间保存设置时，Launcher 会询问是否立即重启。
`workingDirectory` 为 `null` 或在 Settings 中留空时，Launcher 默认使用当前用户的 `%USERPROFILE%`；
仅当用户目录不可用时才回退到 Launcher 的当前目录。

`Credential Management...` 管理 Server 最终生效的 `--auth-file`（默认
`%USERPROFILE%\.kterm\server_auth.json`）。窗口只显示 Argon2id 哈希的短指纹，可以手工新增密码、生成并复制
32 字符随机密码，或删除选中的密码。随机密码明文只在生成后的窗口中显示一次。凭据修改后，运行中的 Server
需要重启才能加载；Launcher 会询问是否立即重启。删除最后一个密码时会明确警告：`auto` 模式将回退为无密码，
`required` 模式将无法启动。

Launcher Settings 支持选择 PEM 格式的 Public certificate 和 Private key；两者必须同时配置、能够互相匹配，
且私钥不能加密。Launcher 会把路径转换为 Kestrel 的默认 HTTPS 证书参数，不在配置文件中保存证书内容或密码。
`Generate self-signed certificate...` 会要求输入 Server 域名以及 CA Common Name（默认为
`KTerm Local Certificate Authority`），并可填写 Country/Region、State/Province、Locality、
Organization 和 Organizational Unit 等证书 Subject 信息；后五项可留空，Country/Region 使用两位国家代码
（例如 `CN` 或 `US`）。Server 证书的 CN 自动使用 Server 域名。然后生成：

```text
%USERPROFILE%\.kterm\cert\<domain>\pub.pem
%USERPROFILE%\.kterm\cert\<domain>\priv.pem
%USERPROFILE%\.kterm\cert\<domain>\ca.pem
```

`pub.pem` 是 KTerm Server 证书，`priv.pem` 是未加密的 Server 私钥，`ca.pem` 是签发它的自签名 CA 公钥证书，
用于复制到 VPS1 并配置为 Nginx 的 `proxy_ssl_trusted_certificate`。Server 证书包含输入域名以及 `localhost`、
`127.0.0.1`、`::1`；Nginx 的 `proxy_ssl_name` 必须使用其中一个名称。重新生成已有域名的证书会更换 CA，
因此也必须同步更新 VPS1 上的 `ca.pem`。

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

配置存在且 `allowedHash` 非空时，浏览器会在 `/auth/login` 显示 KTerm 登录页面；用户名默认为
`kterm`，可通过 `--custom-username <name>` 覆盖。用户名大小写敏感，不能包含冒号或控制字符，最长 128 字符。
Launcher Settings 中的 `ICP registration` 可配置登录页底部显示的备案号，例如
`沪ICP备12345678号-1`；Server 也支持 `--icp-registration <number>`。备案号链接固定指向
`https://beian.miit.gov.cn/`，配置为空时登录页不会生成备案元素。
表单使用 CSRF Token，验证成功后 Server 会签发 HttpOnly Cookie，页面资源和 `/ws` WebSocket 都通过该
Cookie 鉴权；密码只在提交登录表单时发送。登录页面由独立的 `KevinZonda.Terminal.Server.Login` 项目嵌入
Server，不进入桌面 Terminal 或 Dashboard 前端资源。`/healthz` 保持公开。
配置不存在或 `allowedHash` 为空时，`auto` 模式会输出 `No Pass Hash, fallback to No Pass.` 并按无密码模式运行。

Server Dashboard 位于 `/dashboard`，用于查看 Runtime、Session、Shell PID、连接状态和缓冲区占用，也可以关闭单个
Session 或整个 Runtime。Dashboard 前端由独立的 `KevinZonda.Terminal.Server.Dashboard` 项目构建并嵌入
`kterm-server`，不会进入桌面 Terminal 的前端资源。管理操作只在密码认证启用时开放；无密码模式下 Dashboard
只显示管理功能已禁用的提示。Dashboard 的 `Local Configuration` 页签不依赖管理权限，可通过当前 Origin 的
`kterm.fontFamily`、`kterm.fontSize` 和 `kterm.theme` Local Storage 项调整本浏览器中的所有 Terminal 页面。
密码认证启用时，Dashboard 的 `Logout` 会通过 CSRF 保护的请求注销认证 Cookie，并停留在公开的退出完成页。

Terminal 网页包含 Web App Manifest、桌面/移动端安装图标、Apple Web App 元信息，以及 Terminal 和 Dashboard
快捷入口。Service Worker 只缓存带版本 hash 的前端资源和图标，不缓存 HTML、认证、API 或 WebSocket 请求；
Terminal 仍要求连接 Server，不提供离线 Shell。浏览器的完整安装能力取决于 Secure Context，`localhost` 可用于
本机开发，普通局域网 HTTP 地址可能只支持添加到主屏幕而不能启用完整 PWA 能力。

可用 `--auth-file <path>` 指定其他配置文件；`--auth-mode required` 要求配置存在且非空，
`--auth-mode disabled` 则明确关闭密码验证：

```powershell
server.cmd --auth-mode required --auth-file C:\path\server_auth.json
```

### Nginx 反向代理

仓库提供了一份可用于 `sites-enabled` 的双端 HTTPS 配置：[docs/nginx/kterm.conf](docs/nginx/kterm.conf)。示例拓扑为：

```text
Browser --HTTPS--> Nginx/VPS1 --HTTPS over FRP TCP--> KTerm ASP.NET Server
```

FRP 只转发 TCP，不终止 KTerm 的 HTTPS。Nginx 连接 VPS1 本机的 FRP 映射端口 `127.0.0.1:17132`，TLS
握手和证书校验发生在 Nginx 与 KTerm Server 之间。启用前需要替换：

- 公网域名 `terminal.example.com` 及其 Nginx 证书路径。
- FRP 在 VPS1 上提供给 Nginx 的本地 TCP 映射端口 `17132`。
- `proxy_ssl_name` 中的 `kterm-backend.example.com`，它必须匹配 KTerm Server 提供的 HTTPS 证书。
- `proxy_ssl_trusted_certificate`；公共 CA 证书可沿用示例中的系统 CA bundle，私有 CA 则改为对应 CA 文件。

然后启用配置：

```bash
sudo cp docs/nginx/kterm.conf /etc/nginx/sites-available/kterm
sudo ln -s /etc/nginx/sites-available/kterm /etc/nginx/sites-enabled/kterm
sudo nginx -t
sudo systemctl reload nginx
```

KTerm Server 与 `frpc` 位于同一台机器时，建议让两者通过回环地址连接：

```powershell
kterm-server `
  --urls https://127.0.0.1:7132 `
  --auth-mode required
```

对应的 `frpc` TCP 代理应将流量发送到 `127.0.0.1:7132`。这样 ASP.NET Server 看到的直接连接方是本机
`frpc`，符合 Forwarded Headers 默认的回环可信代理边界，不需要信任 VPS1 的地址。Kestrel 的 HTTPS 证书仍通过
标准 ASP.NET Core 配置提供。Nginx 会校验该后端证书、转发真实客户端地址和原始协议，并为 `/ws` 设置
WebSocket Upgrade、关闭代理缓冲和延长连接超时。

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
dotnet publish src\KevinZonda.Terminal.WinFormsDesktop\KevinZonda.Terminal.WinFormsDesktop.csproj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true
```

## GitHub Actions 构建

推送到 `master`、创建 Pull Request，或在 GitHub Actions 页面手动运行对应 workflow，会分别构建：

- `Build Windows`：`KevinZonda-Terminal-windows-x64.zip`
- `Build Linux`：`KevinZonda-Terminal-linux-x64.tar.gz`
- `Build macOS`：`KevinZonda-Terminal-macos-arm64.zip` 和 `KevinZonda-Terminal-macos-x64.zip`

Linux 和 macOS workflow 会运行 Unix PTY 与 Avalonia 桌面服务集成测试。Linux 产物为 self-contained，
macOS 产物使用 ad-hoc 签名且未经过 Apple notarization；首次打开下载的应用时可能需要在 Finder 中右键选择“打开”。

推送 `v*` tag 或手动运行 `Release` workflow，会复用三个平台的构建 workflow、生成 SHA-256 校验文件，
并将全部产物发布到对应的 GitHub Release。Windows ZIP 包含
`KevinZonda.Terminal.exe`、`kterm-server.exe` 和 `kterm-server-launcher.exe`。
Artifact 保留 30 天；Windows 程序运行时需要 .NET 10 Desktop Runtime 和 WebView2 Runtime。

详细架构、消息协议与验收标准参见 [docs/plan.md](docs/plan.md)。
