# Avalonia 中 Delete 后首个 `！` 丢失的 PoC

这个 PoC 用真实的 Avalonia `NativeWebView` 加载一个独立的 xterm 页面。页面显示浏览器事件、xterm 的 `onData` 和输入协调器转发的事件；普通输入框用来判断字符是否在到达 xterm 前就丢失。

在两个终端窗口运行：

```bash
cd src/KevinZonda.Terminal.WebAssets
pnpm dev --host 127.0.0.1
```

```bash
KTERM_IME_POC_URL='http://127.0.0.1:5173/first-input-poc.html' \
  dotnet run --project src/KevinZonda.Terminal.AvaloniaDesktop
```

第二个命令在仓库根目录运行。选择中文拼音输入法，点击“新建空终端”，按 Mac 键盘 Delete（退格）三次，再按 `Shift+1` 输入全角 `！`。重复几轮，观察“浏览器 input”和“可打印转发”。点击“复制事件日志”可保存完整顺序。网址添加 `?fix=1` 可对比输入协调逻辑。`KTERM_IME_POC_URL` 只在 Debug 构建中生效。

“重放 Delete / ！ 交错事件”按钮直接发出下面的浏览器事件顺序，可在没有中文输入法时重复验证处理逻辑。

实测失败轮次的关键顺序是：

```text
Backspace keydown -> xterm.onData DEL
beforeinput/input data="！" -> 没有 xterm.onData
! keydown keyCode=229 -> 没有 xterm.onData
```

xterm 在 Backspace `keydown` 后认为按键仍在处理，因此忽略了随后的 `insertText`；迟到的 IME `keydown` 又以 textarea 已含 `！` 为基准，找不到新字符。输入协调器让非组合状态的 IME 按键由浏览器提交的 `insertText` 负责，并比较 xterm 在该次输入中已经发送的内容，因此在这个顺序下转发一次 `！`，正常键盘输入也不会重复转发。

上游的 [#5887](https://github.com/xtermjs/xterm.js/issues/5887) 和 [#6045](https://github.com/xtermjs/xterm.js/issues/6045) 分别记录了 `input` 早于 `keydown` 时的丢字，以及延迟 textarea 差分在按键交错时的重复或丢字。

无需手动切换输入法的确定性对照：

```bash
node scripts/first-input-poc.mjs
node scripts/first-input-poc.mjs --fix
```

脚本还覆盖新终端、空行 Delete 三次、输入 `abc` 后删光，以及普通 composition、`Process`、直接 `insertText`、重复的相同字符和英文 `!`。`--fix` 模式会断言每个提交的可打印字符只转发一次。
