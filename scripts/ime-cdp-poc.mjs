// Run while `pnpm dev --host 127.0.0.1` is serving WebAssets on port 5173.
// Uses Chrome's IME protocol so the browser generates its own DOM events.
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const chrome = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const profile = await mkdtemp(join(tmpdir(), 'kterm-ime-cdp-'));
const child = spawn(chrome, [
  '--headless', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'
], { stdio: 'ignore' });

try {
  let port;
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      port = Number((await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]);
      break;
    } catch {
      await new Promise(resolve => setTimeout(resolve, 100));
    }
  }
  if (!port) throw new Error('Chrome did not start its debugging endpoint.');

  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const page = targets.find(target => target.type === 'page');
  if (!page) throw new Error('Chrome did not expose a page target.');
  const socket = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });

  let id = 0;
  const pending = new Map();
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    if (!message.id) return;
    const response = pending.get(message.id);
    pending.delete(message.id);
    if (message.error) response.reject(new Error(message.error.message));
    else response.resolve(message.result);
  });
  const call = (method, params = {}) => new Promise((resolve, reject) => {
    const next = ++id;
    pending.set(next, { resolve, reject });
    socket.send(JSON.stringify({ id: next, method, params }));
  });
  const evaluate = async expression => {
    const result = await call('Runtime.evaluate', { expression, returnByValue: true });
    return result.result.value;
  };

  await call('Page.enable');
  const fix = process.argv.includes('--fix') ? '?fix=1' : '';
  await call('Page.navigate', { url: `http://127.0.0.1:5173/ime-poc.html${fix}` });
  for (let attempt = 0; attempt < 100; attempt++) {
    if (await evaluate('Boolean(window.imePocEvents)')) break;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  if (!await evaluate('Boolean(window.imePocEvents)')) throw new Error('PoC page did not load.');

  for (const symbol of ['？', '，', '｜', '+', '*']) {
    await evaluate('window.imePocEvents.length = 0; document.querySelector(".xterm-helper-textarea").focus()');
    await call('Input.imeSetComposition', {
      text: symbol, selectionStart: symbol.length, selectionEnd: symbol.length
    });
    await call('Input.insertText', { text: symbol });
    await new Promise(resolve => setTimeout(resolve, 100));
    const events = await evaluate('window.imePocEvents');
    console.log('composition', symbol, JSON.stringify(events.filter(event =>
      event.source === 'xterm.onData' || event.source === 'fallback' ||
      event.type === 'keydown' || event.type === 'input')));
  }

  for (const { symbol, code, keyCode } of [
    { symbol: '？', code: 'Slash', keyCode: 191 },
    { symbol: '｜', code: 'Backslash', keyCode: 220 },
    { symbol: '+', code: 'Equal', keyCode: 187 },
    { symbol: '*', code: 'Digit8', keyCode: 56 },
    { symbol: '？', code: 'Slash', keyCode: 229 }
  ]) {
    await evaluate('window.imePocEvents.length = 0; document.querySelector(".xterm-helper-textarea").focus()');
    await call('Input.dispatchKeyEvent', {
      type: 'keyDown', key: 'Process', code,
      windowsVirtualKeyCode: keyCode, nativeVirtualKeyCode: keyCode
    });
    await call('Input.insertText', { text: symbol });
    await call('Input.dispatchKeyEvent', {
      type: 'keyUp', key: 'Process', code,
      windowsVirtualKeyCode: keyCode, nativeVirtualKeyCode: keyCode
    });
    await new Promise(resolve => setTimeout(resolve, 100));
    const events = await evaluate('window.imePocEvents');
    console.log('process', symbol, keyCode, JSON.stringify(events.filter(event =>
      event.source === 'xterm.onData' || event.source === 'fallback' ||
      event.type === 'keydown' || event.type === 'input')));
  }

  await evaluate('window.imePocEvents.length = 0; document.querySelector(".xterm-helper-textarea").focus()');
  await call('Input.dispatchKeyEvent', {
    type: 'keyDown', key: '?', code: 'Slash', text: '?', unmodifiedText: '/',
    modifiers: 8, windowsVirtualKeyCode: 191, nativeVirtualKeyCode: 191
  });
  await call('Input.dispatchKeyEvent', {
    type: 'keyUp', key: '?', code: 'Slash', modifiers: 8,
    windowsVirtualKeyCode: 191, nativeVirtualKeyCode: 191
  });
  await new Promise(resolve => setTimeout(resolve, 100));
  console.log('ascii ?', JSON.stringify((await evaluate('window.imePocEvents')).filter(event =>
    event.source === 'xterm.onData' || event.source === 'fallback')));
  socket.close();
} finally {
  child.kill();
  await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
