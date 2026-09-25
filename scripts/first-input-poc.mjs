// Start `pnpm dev --host 127.0.0.1` in WebAssets, then run this script.
// Every scenario navigates to a new empty terminal, optionally presses Delete,
// then sends ！ twice. Pass --fix to enable the current fallback.
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const chrome = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const profile = await mkdtemp(join(tmpdir(), 'kterm-first-input-'));
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
  const key = async (name, code, virtualCode, text) => {
    await call('Input.dispatchKeyEvent', {
      type: 'keyDown', key: name, code, windowsVirtualKeyCode: virtualCode,
      nativeVirtualKeyCode: virtualCode, ...(text ? { text } : {})
    });
    await call('Input.dispatchKeyEvent', {
      type: 'keyUp', key: name, code, windowsVirtualKeyCode: virtualCode,
      nativeVirtualKeyCode: virtualCode
    });
  };

  await call('Page.enable');
  const withFix = process.argv.includes('--fix');
  const fix = withFix ? '&fix=1' : '';
  const scenarios = [
    {
      name: 'IME composition',
      send: async () => {
        await call('Input.imeSetComposition', {
          text: '！', selectionStart: 1, selectionEnd: 1
        });
        await call('Input.insertText', { text: '！' });
      }
    },
    {
      name: 'Process / Digit1 / 49',
      send: async () => {
        await call('Input.dispatchKeyEvent', {
          type: 'keyDown', key: 'Process', code: 'Digit1',
          windowsVirtualKeyCode: 49, nativeVirtualKeyCode: 49
        });
        await call('Input.insertText', { text: '！' });
        await call('Input.dispatchKeyEvent', {
          type: 'keyUp', key: 'Process', code: 'Digit1',
          windowsVirtualKeyCode: 49, nativeVirtualKeyCode: 49
        });
      }
    },
    {
      name: 'Process / Digit1 / 229',
      send: async () => {
        await call('Input.dispatchKeyEvent', {
          type: 'keyDown', key: 'Process', code: 'Digit1',
          windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229
        });
        await call('Input.insertText', { text: '！' });
        await call('Input.dispatchKeyEvent', {
          type: 'keyUp', key: 'Process', code: 'Digit1',
          windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229
        });
      }
    },
    {
      name: 'insertText before ! / 229',
      send: async () => {
        await call('Input.insertText', { text: '！' });
        await call('Input.dispatchKeyEvent', {
          type: 'keyDown', key: '!', code: 'Digit1',
          windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229
        });
        await call('Input.dispatchKeyEvent', {
          type: 'keyUp', key: '!', code: 'Digit1',
          windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229
        });
      }
    },
    {
      name: 'interleaved Delete, insertText, ! / 229',
      send: async () => {
        await call('Input.dispatchKeyEvent', {
          type: 'keyDown', key: 'Backspace', code: 'Backspace',
          windowsVirtualKeyCode: 8, nativeVirtualKeyCode: 8
        });
        await call('Input.insertText', { text: '！' });
        await call('Input.dispatchKeyEvent', {
          type: 'keyUp', key: 'Backspace', code: 'Backspace',
          windowsVirtualKeyCode: 8, nativeVirtualKeyCode: 8
        });
        await call('Input.dispatchKeyEvent', {
          type: 'keyDown', key: '!', code: 'Digit1',
          windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229
        });
        await call('Input.dispatchKeyEvent', {
          type: 'keyUp', key: '!', code: 'Digit1',
          windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229
        });
      }
    },
    {
      name: 'direct insertText',
      send: () => call('Input.insertText', { text: '！' })
    },
    {
      name: 'printable keydown plus insertText',
      send: async () => {
        await call('Input.dispatchKeyEvent', {
          type: 'keyDown', key: '！', code: 'Digit1',
          modifiers: 8, windowsVirtualKeyCode: 49, nativeVirtualKeyCode: 49
        });
        await call('Input.insertText', { text: '！' });
        await call('Input.dispatchKeyEvent', {
          type: 'keyUp', key: '！', code: 'Digit1', modifiers: 8,
          windowsVirtualKeyCode: 49, nativeVirtualKeyCode: 49
        });
      }
    },
    {
      name: 'same symbol across normal and IME keys',
      expectedPerSend: 2,
      send: async () => {
        await call('Input.dispatchKeyEvent', {
          type: 'keyDown', key: '！', code: 'Digit1',
          modifiers: 8, windowsVirtualKeyCode: 49, nativeVirtualKeyCode: 49
        });
        await call('Input.dispatchKeyEvent', {
          type: 'keyUp', key: '！', code: 'Digit1', modifiers: 8,
          windowsVirtualKeyCode: 49, nativeVirtualKeyCode: 49
        });
        await call('Input.insertText', { text: '！' });
        await call('Input.dispatchKeyEvent', {
          type: 'keyDown', key: '!', code: 'Digit1',
          windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229
        });
        await call('Input.dispatchKeyEvent', {
          type: 'keyUp', key: '!', code: 'Digit1',
          windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229
        });
      }
    },
    {
      name: 'English !',
      send: async () => {
        await call('Input.dispatchKeyEvent', {
          type: 'keyDown', key: '!', code: 'Digit1', text: '!', unmodifiedText: '1',
          modifiers: 8, windowsVirtualKeyCode: 49, nativeVirtualKeyCode: 49
        });
        await call('Input.dispatchKeyEvent', {
          type: 'keyUp', key: '!', code: 'Digit1', modifiers: 8,
          windowsVirtualKeyCode: 49, nativeVirtualKeyCode: 49
        });
      }
    }
  ];

  const preparations = [
    { name: 'fresh', send: async () => {} },
    { name: 'Delete x3 on empty', send: async () => {
      for (let i = 0; i < 3; i++) await key('Backspace', 'Backspace', 8);
    } },
    { name: 'abc, Delete x3', send: async () => {
      for (const [letter, code] of [['a', 65], ['b', 66], ['c', 67]]) {
        await key(letter, `Key${letter.toUpperCase()}`, code, letter);
      }
      for (let i = 0; i < 3; i++) await key('Backspace', 'Backspace', 8);
    } }
  ];

  for (const [index, { scenario, preparation }] of preparations.flatMap(preparation =>
    scenarios.map(scenario => ({ scenario, preparation }))).entries()) {
    await call('Page.navigate', {
      url: `http://127.0.0.1:5173/first-input-poc.html?case=${index}${fix}`
    });
    for (let attempt = 0; attempt < 100; attempt++) {
      if (await evaluate(`location.search.includes('case=${index}') && window.firstInputPocEvents?.some(e => e.source === 'ready')`)) break;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    if (!await evaluate('window.firstInputPocEvents?.some(e => e.source === "ready")')) {
      throw new Error(`PoC page did not load for ${scenario.name}.`);
    }
    await preparation.send();
    await new Promise(resolve => setTimeout(resolve, 50));
    const before = await evaluate('window.firstInputPocEvents.length');
    for (const step of ['first', 'second']) {
      await evaluate(`window.firstInputPocEvents.push({ source: 'step', name: '${step}' })`);
      await scenario.send();
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    const events = (await evaluate('window.firstInputPocEvents')).slice(before);
    const transferred = events.filter(event =>
      event.source === 'xterm.onData' || event.source === 'fallback');
    const inputs = events.filter(event => event.source === 'browser' && event.type === 'input');
    console.log(JSON.stringify({
      preparation: preparation.name, scenario: scenario.name,
      inputs: inputs.map(event => event.data),
      transferred: transferred.map(event => event.data),
      eventTrace: events.filter(event => event.source !== 'browser' ||
        ['keydown', 'beforeinput', 'input'].includes(event.type)).map(event => ({
          source: event.source, type: event.type, key: event.key,
          keyCode: event.keyCode, inputType: event.inputType, data: event.data,
          composed: event.composed, value: event.value,
          firstPrintableInput: event.firstPrintableInput
        }))
    }));
    if (withFix) {
      const expected = scenario.name === 'English !' ? '!' : '！';
      const printable = transferred.map(event => event.data)
        .filter(data => data !== '\x7f' && data !== '\x1b[3~');
      const wanted = Array((scenario.expectedPerSend ?? 1) * 2).fill(expected);
      if (JSON.stringify(printable) !== JSON.stringify(wanted)) {
        throw new Error(`${preparation.name} / ${scenario.name}: expected ${JSON.stringify(wanted)}, got ${JSON.stringify(printable)}`);
      }
    }
  }
  await call('Page.navigate', {
    url: `http://127.0.0.1:5173/first-input-poc.html?replay=1${fix}`
  });
  for (let attempt = 0; attempt < 100; attempt++) {
    if (await evaluate('window.firstInputPocEvents?.some(e => e.source === "ready")')) break;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  await evaluate('document.getElementById("replay").click()');
  await new Promise(resolve => setTimeout(resolve, 100));
  const replayEvents = await evaluate('window.firstInputPocEvents');
  const replayOutput = replayEvents.filter(event =>
    event.source === 'xterm.onData' || event.source === 'fallback').map(event => event.data);
  console.log(JSON.stringify({ preparation: 'replay button', scenario: 'interleaved events',
    transferred: replayOutput }));
  if (withFix && replayOutput.filter(data => data === '！').length !== 1) {
    throw new Error(`Replay expected one ！, got ${JSON.stringify(replayOutput)}`);
  }
  socket.close();
} finally {
  child.kill();
  await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
