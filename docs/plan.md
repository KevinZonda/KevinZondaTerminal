# KevinZonda Terminal MVP 实现计划

## 1. 目标与当前状态

本项目的 MVP 是一个仅面向 Windows 的本地 Terminal Emulator。它使用一个 WinForms 主窗口承载一个 WebView2，在同一个 WebView2 页面中实现多个 Tab、可递归拆分的 Pane，以及每个 Pane 对应的独立 xterm.js + ConPTY 会话。

本文只定义设计、实现步骤和验收标准。当前阶段不创建解决方案、不安装依赖、不编写产品代码。

MVP 必须支持：

- 应用启动后自动创建一个 Tab 和一个终端 Pane。
- `Alt+T`：创建新 Tab，新 Tab 内启动一个默认 Shell。
- `Alt+\`：将当前聚焦 Pane 拆成左右两个 Pane（沿宽度分割，分隔线为竖线）。
- `Alt+-`：将当前聚焦 Pane 拆成上下两个 Pane（沿高度分割，分隔线为横线）。
- 每个 Pane 拥有独立的 Shell 进程、ConPTY、xterm.js 缓冲区和输入输出通道。
- 一个 Tab 可以组合出 2×2 以及更复杂的递归布局。
- 调整窗口或拖动分隔线时，正确更新对应 ConPTY 的行列数。
- 使用 xterm.js WebGL renderer，并在 WebGL 不可用或 context lost 时安全回退。

快捷键只在 KevinZonda Terminal 位于前台时生效，不注册系统全局热键，也不安装低级键盘钩子。

## 2. MVP 技术选型

### 2.1 原生宿主

- .NET 10
- WinForms
- `Microsoft.Web.WebView2`
- Windows ConPTY，通过 C# P/Invoke 调用 Win32 API

选择 WinForms 的目的，是让原生层保持为薄宿主。WebView2 自己使用 Edge/Chromium 的 renderer 与 GPU 进程，GPU 能力不依赖 WPF。Tab、Pane、分隔线和设置界面都位于同一个 WebView2 页面内，因此不需要 WPF 的布局、绑定和合成能力，也避免 WPF `HwndHost` airspace 问题。

### 2.2 Web 前端

- TypeScript
- Vite，仅作为构建工具
- `@xterm/xterm`
- `@xterm/addon-fit`
- `@xterm/addon-webgl`

前端构建产物作为本地静态资源随应用发布。运行时不依赖 Node.js、不启动 HTTP 服务、不访问 CDN。WebView2 使用本地虚拟主机映射加载资源，获得稳定且受控的 HTTPS origin。

### 2.3 关键原则

- 一个应用窗口只创建一个 WebView2。
- 一个 Tab 不创建一个 WebView2。
- 一个 Pane 不创建一个 WebView2。
- 每个终端会话创建一个 xterm.js `Terminal` 实例和一个 ConPTY session。
- 所有 WebView2 控件共享一个 `CoreWebView2Environment` 和一个 user data folder；MVP 实际上只有一个控件，但仍由单例运行时服务管理。
- 原生层管理操作系统资源，Web 层管理工作区状态和视觉布局。

## 3. 总体架构

```text
KevinZonda Terminal WinForms Process
├─ MainForm
│  └─ WebView2（铺满 client area）
│     └─ Web App Shell
│        ├─ TabStrip
│        ├─ Workspace
│        │  └─ Split Tree
│        │     ├─ TerminalPane → xterm.js
│        │     └─ TerminalPane → xterm.js
│        └─ Command/Bridge Layer
├─ WebViewRuntimeService
├─ TerminalSessionManager
│  ├─ TerminalSession → ConPTY → Shell Process
│  └─ TerminalSession → ConPTY → Shell Process
├─ WebMessageBridge
├─ SettingsService
└─ NativeIntegrationService
```

职责边界：

| 模块 | 职责 |
| --- | --- |
| WinForms | 主窗口、应用生命周期、WebView2 容器、原生对话框和系统集成 |
| WebView2 | 承载整个前端并提供 Chromium/GPU 渲染与 .NET/JS 消息通道 |
| TypeScript Workspace | Tab、分屏树、焦点、快捷键、拖动分隔线和状态协调 |
| xterm.js | VT/ANSI 解析、终端缓冲区、选择、光标、字符及 WebGL 渲染 |
| TerminalSessionManager | session ID 分配、ConPTY 创建/销毁、输入输出路由和进程退出 |
| ConPTY interop | Pipe、Pseudo Console、子进程、resize 和 Win32 handle 生命周期 |

## 4. 建议的仓库结构

实现阶段按以下结构初始化：

```text
kevinzonda-terminal/
├─ docs/
│  └─ plan.md
├─ src/
│  ├─ KevinZonda.Terminal/
│  │  ├─ KevinZonda.Terminal.csproj
│  │  ├─ Program.cs
│  │  ├─ MainForm.cs
│  │  ├─ WebView/
│  │  ├─ Terminal/
│  │  ├─ Interop/
│  │  ├─ Messaging/
│  │  └─ wwwroot/           # 前端构建产物，不手工编辑
│  └─ KevinZonda.Terminal.WebAssets/
│     ├─ KevinZonda.Terminal.WebAssets.csproj
│     ├─ EmbeddedWebAssets.cs
│     ├─ package.json
│     ├─ vite.config.ts
│     └─ src/
│        ├─ main.ts
│        ├─ workspace/
│        ├─ terminal/
│        ├─ bridge/
│        └─ styles/
├─ tests/
│  ├─ KevinZonda.Terminal.Tests/
│  └─ KevinZonda.Terminal.Web.Tests/
└─ KevinZonda.Terminal.slnx
```

前端和宿主分开开发，但发布时前端只是 KevinZonda.Terminal 的静态资源，不会附带 Node.js runtime。

## 5. Pane Tab 与 Split 数据模型

工作区状态由 TypeScript 管理。递归分割树的叶节点是 Pane，每个 Pane 独立保存自己的 Terminal Tab：

```typescript
type SessionId = string;
type PaneId = string;

type LayoutNode =
  | {
      type: 'pane';
      paneId: PaneId;
    }
  | {
      type: 'split';
      direction: 'columns' | 'rows';
      ratio: number;
      first: LayoutNode;
      second: LayoutNode;
    };

interface PaneState {
  id: PaneId;
  tabs: Array<{ sessionId: SessionId; title: string }>;
  activeSessionId: SessionId;
}
```

方向使用无歧义命名：

- `columns`：左右排列，分隔线为竖线，对应 `Alt+\`。
- `rows`：上下排列，分隔线为横线，对应 `Alt+-`。

拆分操作只替换当前 Pane 叶节点。例如对 pane A 执行左右拆分：

```text
pane(A) [tab A1, tab A2]
```

变为：

```text
split(columns, 0.5)
├─ pane(A) [tab A1, tab A2]
└─ pane(B) [tab B1]  # 新建 ConPTY，完成后获得 session B1
```

2×2 布局由嵌套分割自然表达：

```text
split(columns)
├─ split(rows)
│  ├─ pane(A)
│  └─ pane(B)
└─ split(rows)
   ├─ pane(C)
   └─ pane(D)
```

MVP 中，新建 Tab 会在当前聚焦 Pane 内启动新的默认 Shell；拆分会创建包含一个 Terminal Tab 的新 Pane。新 Pane 创建成功后立即获得焦点。分隔比例初始为 `0.5`，拖动后限制在合理范围，例如 `0.1–0.9`。

## 6. 快捷键与焦点

所有 Tab、Pane 和 terminal DOM 都位于同一页面，因此快捷键优先在 Web 页面捕获阶段处理：

```typescript
window.addEventListener('keydown', event => {
  if (!event.altKey || event.ctrlKey || event.shiftKey || event.metaKey) {
    return;
  }

  switch (event.code) {
    case 'KeyT':
      // new tab
      break;
    case 'Backslash':
      // split focused pane into columns
      break;
    case 'Minus':
      // split focused pane into rows
      break;
    default:
      return;
  }

  event.preventDefault();
  event.stopImmediatePropagation();
}, { capture: true });
```

使用 `KeyboardEvent.code` 而不是 `event.key`，使快捷键绑定到物理 `T`、反斜杠和减号键，减少输入法和键盘布局造成的字符差异。匹配成功的快捷键不会再发送给 Shell；其他 Alt 组合继续交给 xterm.js。

宿主层关闭 WebView2 默认浏览器快捷键，但保留网页和终端所需的编辑/导航按键。如果实际验证发现某个 Alt 组合在到达 JavaScript 前被宿主消费，则使用 `CoreWebView2Controller.AcceleratorKeyPressed` 作为第二级拦截：同步回调中只标记 `Handled`，随后通过 WinForms `BeginInvoke` 异步发送 workspace command，避免在同步输入回调内执行跨进程工作。

焦点规则：

- 鼠标点击 Pane 时更新 `focusedPaneId`。
- 新建 Tab 后将它设为当前 Pane 的 `activeSessionId` 并聚焦对应 terminal。
- 拆分后聚焦新 terminal。
- 切换 Pane Tab 后恢复对应 terminal 的焦点和尺寸。
- 关闭 Pane 后优先聚焦其相邻兄弟节点。
- 聚焦视觉效果由 Pane 外框表示，不依赖浏览器默认 outline。

## 7. xterm.js 生命周期与 GPU 策略

每个 `TerminalController` 持有：

- `Terminal`
- `FitAddon`
- 可选的 `WebglAddon`
- DOM container
- `ResizeObserver`
- xterm event disposables
- 对应的 `sessionId`

创建步骤：

1. 创建 terminal DOM container。
2. 创建 `Terminal` 和 `FitAddon`。
3. `terminal.open(container)`。
4. 尝试加载 `WebglAddon`。
5. 注册 WebGL context-loss 回调；丢失时 dispose WebGL addon，让 terminal 使用可用的默认 renderer。
6. 注册 `onData`、`onResize`、`onTitleChange` 和进程退出显示逻辑。
7. 首次布局稳定后执行 `fit()`，再向宿主发送 resize。

WebView2 默认使用 GPU。实现中不得传入 `--disable-gpu`，也不在生产配置中使用 `--ignore-gpu-blocklist`。GPU 或驱动不满足条件时允许 Chromium/xterm.js 自动降级。

多 Pane GPU 策略：

- 当前可见 Tab 的可见 Pane 可以启用 WebGL。
- 非活动 Tab 的 xterm.js 实例继续接收输出并维护 buffer，但不触发布局和 resize。
- 切换到隐藏 Tab 后，在 DOM 可见且尺寸稳定时重新 `fit()`。
- 不假设浏览器可以无限创建 WebGL context；若未来允许大量 Tab，优先只为可见 Pane保留 WebGL addon。
- MVP 先支持常见的 1、2、4 个可见 Pane，并通过压力测试确认 2×2 同时输出时的行为。

## 8. ConPTY 实现

每个 `TerminalSession` 拥有以下资源：

- 唯一 `sessionId`
- ConPTY 输入、输出 Pipe handles
- `HPCON`
- Shell process handle 和 process ID
- 输入写队列
- 输出读取任务
- 输出批处理器
- 最近一次 `cols`/`rows`
- cancellation token 与关闭状态

创建流程：

1. 创建 ConPTY 输入/输出管道。
2. 调用 `CreatePseudoConsole`，初始大小使用前端提供的行列数；前端尚未完成布局时使用安全默认值 `80×24`。
3. 初始化 `STARTUPINFOEX`。
4. 使用 `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` 绑定 HPCON。
5. `CreateProcess` 启动默认 Shell。
6. 关闭父进程不再使用的 Pipe ends 和 attribute list。
7. 启动独立的输出读取任务和进程退出等待任务。

默认 Shell 解析顺序：

1. 用户配置的 profile（后续设置能力）。
2. `pwsh.exe`，如果可以解析到。
3. `powershell.exe`。
4. `%COMSPEC%`，通常为 `cmd.exe`。

ConPTY 输出是 UTF-8。读取端使用跨 chunk 保持状态的 UTF-8 `Decoder`，不能对每一个读取块独立调用简单字符串转换，否则多字节中文或 emoji 在块边界可能损坏。

关闭顺序必须幂等：停止接受输入、取消读取、关闭 Pipe、关闭 HPCON、等待或终止仍存活的子进程、释放 process handle。进程自然退出、用户关闭 Pane、应用退出和 WebView2 崩溃都通过同一个关闭路径收敛。

## 9. Resize 实现

每个 Pane 使用 `ResizeObserver` 观察可用像素大小，并调用 `FitAddon.proposeDimensions()` 或 `fit()` 得到 `cols` 与 `rows`。

为了避免拖动分隔线时制造 resize 风暴：

- 前端对每个 session 做约 `30–50ms` debounce。
- 与上次行列数相同则不发送。
- 宿主再次去重后调用 `ResizePseudoConsole`。
- 隐藏 Tab 不发送 `0×0` 或无意义 resize。
- Pane Tab 重新显示后等待一帧，再 fit 并发送真实尺寸。

分隔线使用 Pointer Events，并在拖动期间只更新对应 split node 的 `ratio`。布局由 CSS Grid 或绝对布局计算，MVP 优先 CSS Grid。

## 10. WebView2 消息协议

控制消息使用有版本号的 JSON envelope：

```typescript
interface BridgeMessage<T> {
  version: 1;
  type: string;
  requestId?: string;
  sessionId?: string;
  payload: T;
}
```

前端到宿主：

- `session.create`
- `session.input`
- `session.resize`
- `session.close`
- `app.ready`
- `clipboard.read`
- `clipboard.write`

宿主到前端：

- `session.created`
- `session.output`
- `session.exited`
- `session.titleChanged`
- `session.error`
- `app.initialState`

`session.create` 使用 `requestId` 关联异步结果。前端在收到 `session.created` 后把返回的 `sessionId` 写入布局树；失败则移除 pending Pane 并显示可恢复错误。

输出通道的 MVP 实现：

- 每个 session 独立排队，避免一个高输出进程阻塞其他 Pane。
- 每 `8–16ms` 或累计约 `32–64KB` 批量发送。
- 不为每个字节、字符或 `ReadFile` 调用单独发送消息。
- 每条批量消息携带 `sessionId`，前端路由到对应 terminal。
- 设置每 session 和全局待发送字节上限，并记录丢弃/背压诊断；正常情况下不丢终端数据。
- MVP 先使用 `PostWebMessageAsJson`/`PostWebMessageAsString`。只有性能测量证明消息复制是瓶颈时，才引入 `CoreWebView2SharedBuffer` 环形缓冲，避免过早增加同步复杂度。

所有 WebView2 API 调用回到 WinForms UI 线程执行；ConPTY 阻塞读取和批处理不占用 UI 线程。

## 11. WebView2 初始化与安全

初始化顺序：

1. 创建固定的 app user data folder，例如 `%LOCALAPPDATA%\KTerm\WebView2`。
2. 创建单例 `CoreWebView2Environment`。
3. 调用 `EnsureCoreWebView2Async(environment)`。
4. 配置本地虚拟主机到前端构建目录的映射。
5. 注册 WebMessage、ProcessFailed、NewWindowRequested 和 NavigationStarting。
6. 导航到本地应用页面。
7. 等待 `app.ready` 后再创建首个 Pane/Tab/session。

MVP 安全约束：

- 页面资源全部本地打包，不使用 CDN。
- 禁止主 WebView 导航到非应用 origin。
- 外部链接交给系统默认浏览器。
- 拒绝或显式处理 `window.open`。
- Release 构建关闭 DevTools、状态栏和默认上下文菜单；Debug 构建保留 DevTools。
- 不向不受信任页面暴露 host objects。
- 使用结构化 WebMessage bridge，并验证 `type`、payload、session ID 和长度。
- 捕获 `CoreWebView2.ProcessFailed`，记录进程类型；MVP 至少给出错误界面和可重启应用的路径。

WebView2 进程模型包括 browser、renderer 和 GPU 等进程。单个页面承载全部 Pane，可减少 controller、renderer 和 user data environment 的数量。

## 12. MVP 周边设施

### 12.1 Windows 标题栏与 Pane Tab Strip

- 主窗体使用 Windows 原生标题栏，由系统负责窗口菜单、拖动、Resize、贴边和 Snap Layout。
- WebView2 只占据原生标题栏下方的客户区，不再绘制 HTML 标题栏或窗口 resize handles。
- Split Tree 的叶节点是 Pane；每个 Pane 拥有自己的 Tab Strip 和一组 Terminal session。
- Tab 标题初始为 Shell 名称，并随对应 session 报告的 title 更新。
- `Alt+T` 在当前聚焦 Pane 内创建并切换到新 Tab。
- `Alt+\` 和 `Alt+-` 拆分当前 Pane，新 Pane 默认包含一个新 Terminal Tab。
- 工作区只有一个 Pane 且该 Pane 只有一个 Tab 时隐藏 Pane Tab Strip；出现多 Tab 或多 Pane 后自动显示。
- Tab 关闭时只关闭对应 session；关闭 Pane 的最后一个 Tab 时折叠 Split Tree。
- 关闭最后一个 Pane 的最后一个 Tab 后自动创建新的默认 Pane，保持窗口可用。

### 12.2 Pane Chrome

- 聚焦边框。
- 关闭按钮可以后置；MVP 至少提供鼠标可操作的关闭入口，避免只能退出整个应用。
- 进程退出后保留终端内容并显示退出码，用户明确关闭 Pane 后再销毁 xterm buffer。
- 分隔线具备 hover/drag 状态和最小 Pane 尺寸。

### 12.3 剪贴板

- xterm.js 负责选择范围。
- `Ctrl+Shift+C`/`Ctrl+Shift+V` 可作为后续快捷键；MVP 至少通过右键菜单或浏览器 Clipboard API 完成复制粘贴。
- 如果 WebView2 权限行为不稳定，则通过 bridge 使用 .NET Clipboard，并确保在 STA UI 线程调用。

### 12.4 配置与日志

- 配置保存到 `%LOCALAPPDATA%\KTerm\settings.json`。
- MVP 配置只需默认 Shell、字体、字号和 scrollback；如果首轮实现需要继续缩小范围，可以先使用代码默认值，但数据模型应预留。
- 日志保存到 `%LOCALAPPDATA%\KTerm\logs`，记录 session 创建/退出、WebView2 初始化失败、GPU/WebGL 回退、ConPTY 错误和未处理异常。
- 日志不得记录终端输入输出正文，防止泄露命令、token 或密码。

## 13. 实现阶段

### 阶段 1：项目骨架与单终端贯通

- 创建 .NET 10 WinForms solution 和 TypeScript/Vite 前端。
- 嵌入单个 WebView2 并加载本地页面。
- 加载一个 xterm.js + FitAddon + WebglAddon。
- 完成最小 ConPTY P/Invoke。
- 打通单 session 输入、输出、resize 和退出。

完成标准：PowerShell/CMD 可以交互，中文输入输出不损坏，窗口 resize 后行列正确。

### 阶段 2：SessionManager 与消息协议

- 引入 session ID 和 `TerminalSessionManager`。
- 实现结构化 bridge、输出批处理和错误返回。
- 前端使用 `Map<sessionId, TerminalController>` 路由。
- 验证两个并行 session 不串流。

完成标准：两个不可见/可见 xterm 实例分别连接独立 Shell，输入输出严格隔离。

### 阶段 3：Pane Tab、Split Tree 与快捷键

- 实现 Pane-local Tab store 和递归 layout tree。
- 实现 `Alt+T`、`Alt+\`、`Alt+-`。
- 实现左右/上下 split、聚焦和拆分后的新 session 创建。
- 实现 2×2 布局和分隔线拖动。

完成标准：仅用上述三个快捷键即可在 Pane 内创建多个 Tab，并创建 2×2 四 Pane 布局；每个 Pane 均可独立切换和交互。

### 阶段 4：生命周期和稳定性

- 实现 Pane/Tab/session 幂等关闭。
- 处理 Shell 退出、应用退出和 WebView2 process failure。
- 完成隐藏 Tab resize、恢复焦点和 WebGL context loss。
- 加入每 session 公平批处理与背压保护。

完成标准：反复创建/关闭 Tab 和 Pane 不泄漏 Shell 进程或 Win32 handle，一个高输出 Pane 不冻结其他 Pane。

### 阶段 5：MVP 打磨与发布

- 添加最小主题、聚焦状态、错误 UI、复制粘贴和设置持久化。
- Release 构建前端并嵌入应用输出。
- 检测 WebView2 Evergreen Runtime，缺失时提供明确安装提示。
- 创建最小发布包和 README 使用说明。

## 14. 测试与验收

### 14.1 自动测试

- Split tree：叶节点替换、嵌套拆分、关闭后树折叠、焦点迁移。
- Bridge schema：未知类型、缺失 session、超长 payload 和错误 request ID。
- UTF-8 decoder：多字节字符跨读取边界。
- Output batcher：按时间/大小 flush、公平性、关闭时最后一次 flush。
- Session lifecycle：重复 close、进程先退出、Pipe 先断开和启动失败。

### 14.2 MVP 手工验收

1. 启动应用，出现一个可交互的默认 Shell。
2. 按 `Alt+T`，创建并切换到第二个 Tab。
3. 按 `Alt+\`，聚焦 Pane 被拆成左右两个独立 Shell。
4. 在其中一个 Pane 按 `Alt+-`，该 Pane 被拆成上下两个 Shell。
5. 继续拆分得到 2×2，四个 Pane 输入不同命令且输出不串流。
6. 拖动两种分隔线，Shell 内部报告的列数/行数随之变化。
7. 快速调整窗口大小，UI 不闪烁、不锁死，ConPTY 不崩溃。
8. 一个 Pane 连续产生大量输出时，其他 Pane 仍能及时响应输入。
9. 切换 Tab 后 buffer、焦点和尺寸正确恢复。
10. 关闭一个 Pane/Tab 后，相应 Shell 退出且没有残留进程。
11. 中文、emoji、ANSI 颜色、光标移动和常见全屏 TUI 正常显示。
12. WebGL context 不可用或丢失时，终端仍可用。

建议在 Debug 菜单提供诊断页，显示：WebView2 Runtime 版本、renderer、WebGL 是否启用、session 数、队列长度、最近一次 cols/rows 和 process ID，但不显示终端内容。

## 15. MVP 明确不做

- SSH/SFTP 内置客户端。
- tmux 协议集成；MVP 的分屏由 KevinZonda Terminal 自己管理，每个 Pane 是独立 ConPTY。
- 跨平台支持。
- 全局快捷键和后台唤起。
- 工作区/session 跨重启恢复。
- Pane 拖到新窗口或多窗口共享 session。
- GPU 强制覆盖驱动黑名单。
- 插件系统、命令面板扩展和复杂主题市场。
- 管理员权限会话与不同完整性级别进程桥接。

## 16. 已知风险与应对

| 风险 | MVP 应对 |
| --- | --- |
| 多个 xterm WebGL context 占用 GPU 资源 | 单 WebView2；优先只让可见 Pane 使用 WebGL；监听 context loss 并回退 |
| ConPTY 阻塞读写造成死锁 | 输入和输出使用独立任务/队列；UI 线程不做阻塞 I/O |
| 高频输出压垮 WebMessage/UI | 按 session 批处理、限制单次大小、设置背压和公平调度 |
| resize storm | 前后端去重并 debounce，隐藏 Tab 不发送 0×0 |
| xterm DOM 获得焦点后快捷键被终端消费 | `window` capture 阶段按 `event.code` 拦截并阻止传播 |
| WebView2 renderer/GPU 崩溃 | 监听 `ProcessFailed`，保留后端 session 状态并给出明确恢复路径 |
| Shell 启动失败 | profile fallback，向对应 Pane 返回结构化错误，不拖垮其他会话 |
| Win32 handle 或子进程泄漏 | 所有 native handle 封装为 `SafeHandle`，关闭路径幂等并加入集成测试 |

## 17. 实现前的最终决策

开始编码前按本计划采用以下默认值：

- UI 宿主：WinForms。
- WebView2 数量：每个主窗口一个。
- Terminal 数量：每个 session 一个 xterm.js 实例。
- 新 Tab/新 Pane：启动新的默认 Shell，而不是复制当前进程。
- `Alt+\`：左右拆分。
- `Alt+-`：上下拆分。
- 快捷键范围：仅应用前台，Web capture 阶段处理。
- 前端状态：TypeScript 内存 store，MVP 不跨重启恢复。
- 数据通道：先用批量 WebMessage，测量后才考虑 SharedBuffer。
- GPU：WebView2 默认 GPU + xterm WebGL addon，允许安全回退。

## 18. 参考资料

- [Microsoft：Pseudoconsoles](https://learn.microsoft.com/en-us/windows/console/pseudoconsoles)
- [Microsoft：创建 ConPTY session](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)
- [Microsoft：WebView2 process model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model)
- [Microsoft：WebView2 performance best practices](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/performance)
- [Microsoft：WebView2 AcceleratorKeyPressed](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controller.acceleratorkeypressed)
- [Microsoft：CoreWebView2 shared buffer](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.createsharedbuffer)
- [xterm.js documentation](https://xtermjs.org/docs/)
- [xterm.js repository and addon list](https://github.com/xtermjs/xterm.js)
