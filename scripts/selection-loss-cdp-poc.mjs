// Starts the real TerminalController PoC and verifies lost mouseup recovery.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repository = dirname(dirname(fileURLToPath(import.meta.url)));
const webAssets = join(repository, 'src', 'KevinZonda.Terminal.WebAssets');
const chrome = process.platform === 'darwin'
  ? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
  : 'google-chrome';
const profile = await mkdtemp(join(tmpdir(), 'kterm-selection-loss-poc-'));
const port = await getAvailablePort();
const vite = spawn('pnpm', ['exec', 'vite', '--host', '127.0.0.1', '--port', String(port), '--strictPort'], {
  cwd: webAssets,
  stdio: ['ignore', 'pipe', 'pipe']
});
const browser = spawn(chrome, [
  '--headless', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'
], { stdio: 'ignore' });

try {
  const url = `http://127.0.0.1:${port}/selection-loss-poc.html`;
  await waitForUrl(url);
  const debugPort = await waitForDebugPort(profile);
  const targets = await (await fetch(`http://127.0.0.1:${debugPort}/json/list`)).json();
  const page = targets.find(target => target.type === 'page');
  if (!page) throw new Error('Chrome did not expose a page target.');

  const socket = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
  const call = createCdpCaller(socket);
  const evaluate = async expression => {
    const response = await call('Runtime.evaluate', {
      expression,
      awaitPromise: true,
      returnByValue: true
    });
    if (response.exceptionDetails) {
      throw new Error(response.exceptionDetails.text ?? 'Browser evaluation failed.');
    }
    return response.result.value;
  };

  await call('Page.enable');
  await call('Page.navigate', { url });
  await waitForExpression(evaluate, 'Boolean(window.selectionLossPoc)');
  await evaluate('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))');

  const geometry = await evaluate('window.selectionLossPoc.geometry()');
  const dispatch = (type, point, buttons, button = 'none') => call('Input.dispatchMouseEvent', {
    type,
    x: point.x,
    y: point.y,
    button,
    buttons,
    clickCount: type === 'mousePressed' || type === 'mouseReleased' ? 1 : 0
  });
  const select = async () => {
    await dispatch('mousePressed', geometry.start, 1, 'left');
    await dispatch('mouseMoved', geometry.end, 1, 'left');
    return evaluate('window.selectionLossPoc.state()');
  };
  const reset = () => evaluate('window.selectionLossPoc.reset()');

  await reset();
  const normalDragging = await select();
  assert.equal(normalDragging.hasSelection, true, 'Normal drag did not create a selection.');
  await dispatch('mouseReleased', geometry.end, 0, 'left');
  await dispatch('mouseMoved', { x: geometry.end.x + 2, y: geometry.end.y }, 0);
  const normalFinal = await evaluate('window.selectionLossPoc.state()');
  assert.equal(normalFinal.hasSelection, true, 'A normal mouseup should preserve the selection.');
  assert.equal(normalFinal.selection, normalDragging.selection,
    'A normal mouseup changed the selected text.');

  const trials = [];
  for (let index = 0; index < 20; index++) {
    await reset();
    const before = await select();
    assert.equal(before.hasSelection, true, `Trial ${index}: drag did not create a selection.`);

    // Reproduce WKWebView's lost mouseup sequence: the next observable event says
    // the primary button is already up, but no mouseup reached the document.
    await dispatch('mouseMoved', { x: geometry.end.x + 2, y: geometry.end.y }, 0);
    const after = await evaluate('window.selectionLossPoc.state()');
    assert.equal(after.hasSelection, true, `Trial ${index}: recovery discarded the selection.`);
    assert.equal(after.selection, before.selection, `Trial ${index}: recovery changed the selection.`);

    // Once recovered, ordinary movement must not keep extending the old drag.
    await dispatch('mouseMoved', geometry.start, 0);
    const settled = await evaluate('window.selectionLossPoc.state()');
    assert.equal(settled.selection, before.selection,
      `Trial ${index}: old drag listener remained active after recovery.`);
    trials.push({ index, before, after, settled });

    // Balance CDP's physical mouse state before the next trial. Production has
    // already completed xterm's drag with a synthetic mouseup at this point.
    await dispatch('mouseReleased', geometry.end, 0, 'left');
  }

  await reset();
  const beforeBlur = await select();
  await evaluate("window.dispatchEvent(new Event('blur'))");
  await dispatch('mouseMoved', geometry.start, 0);
  const afterBlur = await evaluate('window.selectionLossPoc.state()');
  assert.equal(afterBlur.selection, beforeBlur.selection,
    'Window blur recovery discarded or extended the selection.');
  await dispatch('mouseReleased', geometry.end, 0, 'left');

  await reset();
  const beforeNextPress = await select();
  const shorterEnd = { x: (geometry.start.x + geometry.end.x) / 2, y: geometry.end.y };
  await dispatch('mousePressed', geometry.start, 1, 'left');
  await dispatch('mouseMoved', shorterEnd, 1, 'left');
  await dispatch('mouseReleased', shorterEnd, 0, 'left');
  const afterNextPress = await evaluate('window.selectionLossPoc.state()');
  assert.equal(afterNextPress.hasSelection, true,
    'The first click after a lost mouseup could not start a new selection.');
  assert.notEqual(afterNextPress.selection, beforeNextPress.selection,
    'The first click after a lost mouseup kept the stale selection.');

  console.log(JSON.stringify({
    normal: {
      beforeMouseUp: summarize(normalDragging),
      afterMouseUpAndMove: summarize(normalFinal)
    },
    lostMouseUp: {
      preserved: trials.filter(trial => trial.after.hasSelection).length,
      trials: trials.length,
      first: {
        before: summarize(trials[0].before),
        after: summarize(trials[0].after)
      }
    },
    windowBlur: {
      before: summarize(beforeBlur),
      after: summarize(afterBlur)
    },
    nextMouseDown: {
      before: summarize(beforeNextPress),
      after: summarize(afterNextPress)
    }
  }, null, 2));
  socket.close();
} finally {
  vite.kill();
  browser.kill();
  await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}

function summarize(state) {
  return {
    hasSelection: state.hasSelection,
    selectionLength: state.selection.length,
    selectionChanges: state.selectionChanges,
    lastEvent: state.events.at(-1)
  };
}

function getAvailablePort() {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      const port = typeof address === 'object' && address ? address.port : 0;
      server.close(error => error ? reject(error) : resolve(port));
    });
  });
}

async function waitForUrl(url) {
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch {
      // Vite is still starting.
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Vite did not start at ${url}.`);
}

async function waitForDebugPort(profilePath) {
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      return Number((await readFile(join(profilePath, 'DevToolsActivePort'), 'utf8')).split('\n')[0]);
    } catch {
      await new Promise(resolve => setTimeout(resolve, 100));
    }
  }
  throw new Error('Chrome did not start its debugging endpoint.');
}

function createCdpCaller(socket) {
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
  return (method, params = {}) => new Promise((resolve, reject) => {
    const next = ++id;
    pending.set(next, { resolve, reject });
    socket.send(JSON.stringify({ id: next, method, params }));
  });
}

async function waitForExpression(evaluate, expression) {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (await evaluate(expression)) return;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Expression did not become true: ${expression}`);
}
