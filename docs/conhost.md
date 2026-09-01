# ConHost 调查：codex 滚动历史丢失问题与 passthrough ConPTY 方案

本文记录 2026-08 对“codex 在 KevinZonda Terminal 中滚轮无法查看历史”问题的完整调查过程、
被排除的假设、最终根因，以及落地实现的 passthrough ConPTY 架构。

## 1. 问题现象

在 KevinZonda Terminal 中运行 `codex resume <session-id>`：

- 会话内容超过当前可见高度后，鼠标滚轮无法看到前文，也没有 scrollbar。
- 把终端拉高再缩回后，scrollbar 出现，可以滚动——但只能滚到"拉高期间
  曾露出过的那一段"，更早的内容依然不可达。
- 同一 session 在 Windows Terminal（WT 1.24，MSYS2/Git Bash 环境）中滚轮正常。
- codex 内置的 `/raw` 模式（raw scrollback）也无法解决。

## 2. 调查过程与被排除的假设

### 2.1 假设一：缺 alternate scroll（部分成立，但不是本案根因）

最初怀疑 codex 是 alternate screen 全屏 TUI：alt buffer 按定义没有
scrollback，而 xterm.js 在"alt buffer + 应用未开鼠标上报"时不做
alternate scroll（滚轮转方向键），WT/conhost 有这个行为。

对 codex.exe 二进制grep 验证：含 `?1049h/l`、`?2026h`，不含任何鼠标捕获
序列（`?1000h/1002h/1003h/1006h`）。检查 xterm.js 6.0.0 源码
（`CoreBrowserTerminal.ts`、`Viewport.ts`）确认：xterm 自己的 wheel 监听
只在应用请求 WHEEL 鼠标协议时挂载，否则滚轮落到只能滚普通缓冲区
scrollback 的 `SmoothScrollableElement`，在 alt buffer 中滚动范围为零。

**处置**：这个假设对 vim/less/htop 等真 alt-screen 应用成立，实现了
alternate scroll（`terminal-controller.ts` 的 `handleWheel`：无鼠标上报且
处于 alt buffer 时，把滚轮增量按 40px/行换算成 `\x1b[A/B` 或 application
cursor 模式的 `\x1bOA/OB` 发给 ConPTY）。该修复保留，但对 codex 无效——
后续证明 codex 根本不在 alt screen。

### 2.2 实证工具：conpty-dump

为了不再猜测，写了一个一次性抓包工具（用后即删）：复用 KevinZonda Terminal 的 ConPTY
P/Invoke，在 ConPTY 中启动 `codex resume`，分阶段记录"启动 → 拉高 → 缩回"
的全量输出字节流。

工具过程中发现两个与主题无关但重要的坑：

1. **子进程 std 句柄继承**：带 `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` 启动
   的子进程，若父进程自己有 std 句柄（如从 bash 启动的管道），子进程会
   继承这些管道而非拿到 console 句柄，导致 codex 的 `is_terminal` 检查
   报 "stdin is not a terminal"。KevinZonda Terminal 是无 std 句柄的 GUI 程序所以没
   踩到。对策：工具里 `FreeConsole()` + `SetStdHandle(..., NULL)`。
2. 详见第 4.3 节的 handle list use-after-free。

### 2.3 抓包结论：codex 不用 alt screen，历史是"重绘"出来的

对 `codex resume` 输出流的分析：

- 无 `?1049h`（不进 alt screen）、无鼠标序列、无 DECSTBM、无 RI、无
  `\x1b[3J`——在**普通缓冲区**内联渲染。
- resume 的历史恢复是 N 个全屏重绘帧（`\x1b[H` + `\x1b[K\r\n`），每帧
  恰好视口高度、逐帧整体上移一行——内容从未真正滚出屏幕顶部。
- 用 headless xterm.js 6.0.0 回放该流：scrollback 只有 3 行空白。
  **终端 scrollback 里没有历史，滚轮自然无可滚动。**
- resize 时 codex 从内部 transcript 重绘更多行（拉高→多画几行）；xterm
  行数收缩时把放不下的行推入自己的 scrollback——这精确解释了"拉高再
  缩回后 scrollbar 出现、但只能滚那一段"的现象。

至此假设一被排除：问题不在滚轮事件转发，而是**历史行从未到达终端**。

## 3. 根因：Win10 inbox ConPTY 吃掉了 codex 的历史插入序列

### 3.1 codex 的历史写入机制（源码：codex-rs/tui/src/insert_history.rs）

codex 设计上依赖终端 scrollback 存放已完成历史，插入方式是 DECSTBM
region scroll：

1. `SetScrollRegion(1..视口顶)`（区域顶 = 屏幕第 0 行）；
2. 光标停在区域底部，用 `\r\n` 逐行喂历史，让行从屏幕顶部滚出；
3. `ResetScrollRegion`（`\x1b[r`）复位。

视口不在屏幕底部时，先用 `\x1bM`（Reverse Index）把视口逐行下滚腾位。

因此终端能否滚轮看历史，取决于它收到"top margin == 0 的换行滚动"时
是否把行放进 scrollback：

- **WT**：`AdaptDispatch::_DoLineFeed` 中仅"换行驱动 + topMargin == 页顶
  + 无水平边距"的分支把顶部行移入 scrollback（`SetViewportPosition(+1)`）；
  RI/IL/SU/SD 等主动滚动一律不进。codex 的插入恰好落在该分支。✓
- **xterm.js**：`BufferService.scroll` 只看 `scrollTop === 0`，同样会进
  scrollback。✓

两端客户端规则都兼容——**前提是原始 DECSTBM 序列能到达客户端**。

### 3.2 Win10 的 inbox conhost 是重绘式渲染器

KevinZonda Terminal 用 kernel32 `CreatePseudoConsole`，由 OS 拉起 `System32\conhost.exe`。
Win10 22H2（19045）的这个 conhost 会把应用的 region scroll 消化进自己的
屏幕缓冲区，再**整屏重绘**给终端客户端。抓包实证：输出流中
DECSTBM/RI/IL/SU/SD 出现次数全部为 0。历史行在这一层就丢了，前端做什么
都救不了。

### 3.3 Windows Terminal 为什么正常：自带 passthrough ConPTY

WT 1.24 **不调用** inbox `CreatePseudoConsole`。它静态链接自己的
winconpty 实现（`src/winconpty/winconpty.cpp`）：

1. 直接向 ConDrv 驱动 `NtOpenFile("\\Device\\ConDrv\\Server")` 创建
   server handle，再开 `\Reference` client handle；
2. 自建 signal pipe，spawn **安装包内自带的 OpenConsole.exe**：
   `OpenConsole.exe --headless --width X --height Y --signal 0x.. --server 0x..`；
3. 这个 2026 架构的 OpenConsole 是 passthrough：`WriteCharsVT` 把 VT 解析
   进内部 buffer 的同时**原始序列逐字转发**给终端（无任何 Windows 版本
   分支），与 OS 版本无关，Win10 上同样是这套。

所以 WT 里 codex 的 DECSTBM 历史插入直达 WT 缓冲区 → scrollback 正常。

### 3.4 附带结论

- `/raw` 无效的原因：它只改写入行的内容与换行策略，插入机制还是同一套
  DECSTBM，照样被老 conhost 吃掉。
- "WT 正常" 的前提是 WT 使用自带的 OpenConsole；与 MSYS2 无直接关系，
  MSYS2 只是运行壳。
- 同类上游问题记录：openai/codex#35335（WT/VSCode 丢 scrollback）、
  #36474（--no-alt-screen 也只有一屏）、#27644（xterm.js host 的 region
  scroll 丢行）、#30745（resize 时 codex 从 transcript 重放修复显示）。

## 4. 方案与实现：IConHost 抽象 + winconpty 协议移植

### 4.1 结构

`TerminalSession` 不再直接绑死 kernel32 三件套，改面向接口：

```
TerminalSession
   └─ IConHost (PseudoConsoleHandle, Resize, Dispose)
        ├─ KernelConHost      系统 inbox conhost（回退）
        └─ OpenConsoleConHost OpenConsole.exe --headless（首选）
   ConHost (工厂)：应用目录/架构子目录有 OpenConsole.exe 就用 passthrough，
                   失败回退 kernel；KTERM_CONHOST=kernel 强制回退
```

`OpenConsoleConHost` 是 winconpty 的 C# 移植，要点：

- `NtOpenFile("\Device\ConDrv\Server", GENERIC_ALL)` → server handle；
  首次失败时 `NtSetSystemInformation(132, DriverLoaded=1)` 加载 ConDrv 重试；
- 以 server 为父对象 `NtOpenFile("\Reference")` → reference handle
  （它维持 console 存活，全部客户端断开且 reference 关闭后 OpenConsole
  自行退出）；
- `CreatePipe` 建 signal pipe，把子进程需继承的句柄
  `SetHandleInformation(HANDLE_FLAG_INHERIT)`；
- `STARTF_USESTDHANDLES` + `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`
  （server、input、output、signal 四句柄）spawn `OpenConsole.exe --headless
  --width --height --signal 0x<sig> --server 0x<srv>`；
- **HPCON 的真身**是三句柄结构体 `{ hSignal, hPtyReference, hConPtyProcess }`
  （winconpty.h 注明 "This structure is part of an ABI shared with the rest
  of the operating system"）——`AllocHGlobal` 按此布局填充，地址传给
  `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`，客户端挂载方式与原来完全一致；
- resize 改为向 signal pipe 写 3 个 ushort：`[8, cols, rows]`
  （PTY_SIGNAL_RESIZE_WINDOW）；关闭按序释放 signal、reference、进程句柄。

### 4.2 二进制来源与单文件分发

WT 安装包里有 `OpenConsole.exe`（约 1MB，MIT 许可），但**没有**
`winconpty.dll`（WT 静态链接进 TerminalConnection.dll），因此选择直接移植
协议而非复用 dll。

分发方式：`OpenConsole.exe` 存放于 `tools/openconsole/`，构建时作为
`EmbeddedResource`（逻辑名 `KevinZonda.Terminal.Binaries/OpenConsole.exe`）嵌入程序集；
首次创建终端会话时释放到 `%LOCALAPPDATA%\KTerm\bin\<sha256前8位>\
OpenConsole.exe`（写临时文件 + 原子移动）。缓存复用前会对文件做 SHA256
复核：与嵌入副本不一致（损坏或被篡改）时**绝不执行该文件**，弹窗告知
用户并可选择重新释放（释放后再校验一次）或回退系统 conhost；同一进程内
选择回退后不再重复弹窗。查找顺序：应用目录 →
架构子目录 → 嵌入资源释放 → 回退 kernel conhost。

### 4.3 过程中抓到的一个真 bug：handle list use-after-free

初版实现 spawn OpenConsole 时 `CreateProcessW` 报
`ERROR_INVALID_PARAMETER (87)`，且测试矩阵呈现"换个句柄顺序就成功"的
诡异规律。根因：`PROC_THREAD_ATTRIBUTE_HANDLE_LIST` 的句柄数组在
`UpdateProcThreadAttribute` 后就被 `FreeHGlobal`，而 `CreateProcessW`
在创建进程时才读这块内存——典型的 use-after-free，读到什么全看堆复用
情况。修复：数组生命周期延长到 `CreateProcessW` 返回之后。

## 5. 验证

- 临时 dump 工具（复用新 interop 源码）重跑 `codex resume`：
  - 修复前流中 DECSTBM/RI 为 0；修复后出现 `\x1b[1;23r`、`\x1b[1;30r` 等
    DECSTBM ×18、`\x1bM` ×79、`\x1b[3J` ×2（resize 重放前的 scrollback
    清理）——序列逐字直通；
  - headless xterm.js 回放：scrollback 从 3 行空白变为 9 行真实
    transcript 内容（该会话历史较短，超出视口的部分正确入 scrollback）。
- 反射驱动真实构建产物验证单文件链路：无旁置 exe 时资源释放到缓存目录、
  成功拿到 `OpenConsoleConHost`、resize/dispose 正常；`PublishSingleFile`
  产物为单个约 5MB 的 exe。
- 真机：`codex resume` 后滚轮可查看完整历史。

## 6. resize 与 ConPTY 缓冲区语义：windowsPty 选项

passthrough 上线后又暴露出一个同源问题：resize 窗口时，正在刷新的
BCE 进度条（如 PowerShell 的 `Write-Progress`、kimi-code 安装器的
下载条）会留下多份残影，且不随后续输出清除。

### 6.1 红鲱鱼：OSC 11

最初怀疑 OSC 11（背景色序列）处理缺陷。核查结论：xterm.js 6.0.0 对
OSC 10/11/12 的设置与查询均完整支持，`_handleColorEvent` 有
`_themeService` 守卫（pre-open 期间静默丢弃），KTerm 前端也不拦截
这些序列。与残影无关，排除。

### 6.2 根因：xterm.js 的默认 resize 语义与 ConPTY 缓冲区模型不一致

xterm.js 默认按"独立终端"工作：窗口变窄时自己 reflow（重排换行），
拉高时从自己的 scrollback 回拉行补进视口。但在 ConPTY 架构下，
屏幕内容的所有权在 ConPTY/OpenConsole 一侧——resize 后由它重发
视口内容，xterm 本地的 scrollback 与 ConPTY 的缓冲区并不共享。

两端各自做一套的后果：BCE 进度条这类"整行背景色 + 原地重绘"的
内容，在 xterm reflow 重排后被画到与 ConPTY 重发内容不同的位置，
旧行无人擦除，形成残影。这与主线问题（拉高后前文重现）同源：
都是**客户端本地缓冲区语义与 ConPTY 缓冲区语义冲突**。

### 6.3 修复：`windowsPty: { backend: 'conpty', buildNumber: 19045 }`

xterm.js 6.0.0 的 `windowsPty` 选项（WindowsPtyType）正是为此设计，
在 `terminal-controller.ts` 创建 Terminal 时设置：

- 只要设置了 `backend`/`buildNumber`，拉高窗口时 xterm 不再从本地
  scrollback 回拉行，而是在底部补空行，把视口内容留给 ConPTY 重发；
- `buildNumber < 21376`（Win11 22H2 之前）时，进一步禁用 xterm 自己
  的 reflow——变窄不再重排换行，完全交给 ConPTY 端处理。

真机用 `scripts/resize-progress.ps1`（400 步 BCE 进度条 + 提示
随意 resize）两轮对比：

- `buildNumber: 26100`（只启用补空行、保留 reflow）：残影依旧严重；
- `buildNumber: 19045`（补空行 + 禁 reflow）：各种 resize 方向下
  画面干净，进度条行为与 conhost/WT 一致。

代价：窗口变窄时长行不再自动重排换行（截断显示），这正是真实
conhost 的行为，可以接受。修复提交：`ae72da5`。

## 7. 遗留事项与边界

- codex 对 resize 重放有按终端的行数上限（`resize_reflow_cap.rs`：WT
  9001 / VSCode 1000 / 未知终端走 fallback）。KevinZonda Terminal 是未知终端，超长
  历史在 resize 后可能被截断；需要时可通过环境变量伪装终端身份，或等
  codex 上游调整。
- codex 的 resize 重放会先 `\x1b[3J` 清 scrollback 再重放，resize 后
  scrollback 内容被替换为重放版本，属 codex 的预期行为。
- `%LOCALAPPDATA%\KTerm\bin\` 缓存目录随版本哈希增长，旧版本目录目前
  不主动清理（单文件约 1MB，影响可忽略）。
- `KTERM_CONHOST=kernel` 环境变量可强制回退 inbox conhost，用于对比
  调试。
- Win10 之外的系统（Win11 24H2+ 的 inbox conhost 行为变化）未逐一
  验证；当前实现在所有版本上统一优先使用自带的 OpenConsole，行为一致
  且可控。

## 8. 参考资料

- codex 源码：`codex-rs/tui/src/insert_history.rs`、`app/resize_reflow.rs`、
  `custom_terminal.rs`
- Windows Terminal 源码：`src/winconpty/winconpty.cpp`、`winconpty.h`、
  `src/server/DeviceHandle.cpp`、`src/host/_stream.cpp`（WriteCharsVT）、
  `src/terminal/adapter/adaptDispatch.cpp`（_DoLineFeed）
- KevinZonda Terminal 实现：`src/KevinZonda.Terminal.Core/Interop/`（IConHost、ConHost、
  KernelConHost、OpenConsoleConHost、NativeMethods.Conpty）、
  `tools/openconsole/README.md`、`scripts/resize-progress.ps1`（resize
  残影复现脚本）
- 上游 issue：openai/codex#27644、#30745、#35335、#36474、#37635
- Microsoft 文档：[Pseudoconsoles](https://learn.microsoft.com/en-us/windows/console/pseudoconsoles)
