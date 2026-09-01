# Fix A：resize settle 后清空 xterm 视口（缓解方案）

对应 `OpenCon.md` 第 5 节方案 A。目标：把"永久错误的残影格子"换成
"临时空白"，等待应用下一次输出填回。不追求逐格正确——那需要
`OpenCon.FixB.md` 的方案。

## 1. 设计

### 1.1 触发点：resize settle，而非每次 resize

拖动 resize 期间 `onResize` 会连续触发（`terminal-controller.ts` 的
`terminal.onResize` → `bridge.resize`）。不能在每个中间帧清屏（会一直
闪），需要等尺寸稳定：

- 每次 `onResize` 重置一个 settle 定时器（建议 200ms 左右，需真机调）；
- 定时器到期 = 本轮 resize 结束，执行一次清视口；
- resize 开始（第一个 onResize）时**不做任何事**，让应用和 OpenConsole
  先跑。

注意与现有 40ms 的 `scheduleFit` debounce 区分：那个是 fit 节流，这个
是"resize 已结束"判定，时长应明显大于拖动事件间隔、小于人能感知的
卡顿（150–300ms 量级）。

### 1.2 动作：本地 `\x1b[2J`，只擦视口

settle 时向前端的 xterm 实例本地注入：

```ts
this.terminal.write('\x1b[2J');
```

要点：

- **只擦视口，不动 scrollback**（不要 `\x1b[3J`），不动光标
  （`\x1b[2J` 在 xterm.js 中不移动光标）——ConPTY 侧本来就靠
  DSR/CPR 重新同步光标（见 `OpenCon.md` 2.3），我们不要自己挪。
- 这是**纯本地**注入：`terminal.write()` 的数据不会发给 ConPTY，
  只影响前端显示，不会干扰应用输入流。
- 不要用 `terminal.clear()`：它会把 prompt 行提到顶部、改变
  scrollback 结构，语义过重。

### 1.3 alt buffer 跳过

应用在 alt buffer（`terminal.modes` / active buffer 类型可查）时跳过
清屏：

- OpenConsole 不 reflow alt buffer（GH#3493），desync 主要发生在普通
  buffer；
- alt 应用（vim、codex 全屏模式等）收到 resize 事件后本来就会整屏
  重绘，清屏只带来一次无谓的闪烁。

## 2. 清除之后屏幕如何恢复

| 应用 | 恢复机制 |
|---|---|
| codex 等 TUI | resize 后从 transcript 整屏重放（已有行为），清屏几乎无感 |
| PowerShell 进度条 | 下一步重绘自身区域；bar 之外的区域留白直到有新输出 |
| PSReadLine prompt | ConPTY 的 buffer-size 事件触发 PSReadLine 重绘输入行 |
| 普通滚动输出（ls 等） | prompt 之上留白，直到用户 `clear` 或有新输出滚过 |

核心权衡：残影是**错误信息**（永不消失），空白是**缺失信息**（可被
后续输出覆盖、且一眼可知"这里需要重绘"）。后者严格更好。

## 3. 实现位置

`src/KevinZonda.Terminal.WebAssets/src/terminal-controller.ts`：

- `onResize` 回调中（已有 `lastCols/lastRows` 去重逻辑处）重置
  settle 定时器；
- 新增 `private settleTimer` 字段，`dispose()` 中清理；
- settle 回调里判断非 alt buffer 后 `terminal.write('\x1b[2J')`。

注意该文件为 CRLF/LF 混合行尾，用 Edit 需谨慎（建议改前先 Read）。

## 4. 验证

1. `scripts/resize-progress.ps1`：拖动各方向 resize，settle 后残影应在
   ~200ms 内变为空白并被下一步进度条填回，无永久残块。
2. `ls` 一个长目录后收窄再拉宽：折行碎片应在 settle 后消失（变空白），
   回车后新输出正常。
3. codex resume：resize 后 transcript 重放不受清屏影响（重放在清屏
   之后到达），滚轮历史完整。
4. vim/htop（alt buffer）：resize 不触发清屏，无额外闪烁。

## 5. 已知局限

- settle 判定是启发式的：极慢的连续拖动（事件间隔 >200ms）会在拖动
  中途触发清屏，表现为偶发闪空。可通过调大阈值缓解。
- 清屏后到应用重绘之间有一帧空白，应用输出越慢越明显。
- 不能修复"xterm 与 OpenConsole 布局不同"本身——增量输出仍可能落在
  视觉错位处，只是错误内容不再永久驻留。彻底修复见 Fix B。
