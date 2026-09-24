import { Terminal } from '@xterm/xterm';
import '@xterm/xterm/css/xterm.css';
import { installImeInputFallback } from './ime-input-fallback';

const terminal = new Terminal({ cols: 80, rows: 10, convertEol: true });
terminal.open(document.getElementById('terminal')!);
terminal.write('Type here with both input methods.\r\n');
terminal.focus();

const textarea = terminal.textarea!;
const log = document.getElementById('log')!;
const events: Record<string, unknown>[] = [];
const record = (event: Record<string, unknown>): void => {
  events.push({ at: Math.round(performance.now()), ...event });
  log.textContent = events.map(item => JSON.stringify(item)).join('\n');
  log.scrollTop = log.scrollHeight;
};

for (const type of ['keydown', 'keyup', 'beforeinput', 'input', 'compositionstart',
                    'compositionupdate', 'compositionend'] as const) {
  textarea.addEventListener(type, event => {
    const key = event as KeyboardEvent;
    const input = event as InputEvent;
    record({
      source: 'browser', type, key: key.key, code: key.code, keyCode: key.keyCode,
      shiftKey: key.shiftKey, isComposing: input.isComposing,
      inputType: input.inputType, data: input.data,
      defaultPrevented: event.defaultPrevented, value: textarea.value
    });
  });
}

terminal.onData(data => {
  record({ source: 'xterm.onData', data });
  terminal.write(data.replaceAll('\r', '\r\n'));
});

if (new URLSearchParams(location.search).has('fix')) {
  installImeInputFallback(terminal, data => {
    record({ source: 'fallback', data });
    terminal.write(data);
  });
}

document.getElementById('clear')!.addEventListener('click', () => {
  events.length = 0;
  log.textContent = '';
  terminal.focus();
});
document.getElementById('copy')!.addEventListener('click', () => {
  void navigator.clipboard.writeText(JSON.stringify(events, null, 2));
});
function simulate(data: string, key: string, code: string, keyCode: number): void {
  record({ source: 'simulation', data, key, code, keyCode });
  terminal.focus();
  const down = new KeyboardEvent('keydown', {
    key, code, bubbles: true, cancelable: true
  });
  Object.defineProperty(down, 'keyCode', { value: keyCode });
  textarea.dispatchEvent(down);
  textarea.value += data;
  textarea.dispatchEvent(new InputEvent('input', {
    data, inputType: 'insertText', bubbles: true, composed: true
  }));
  const up = new KeyboardEvent('keyup', { key, code, bubbles: true });
  Object.defineProperty(up, 'keyCode', { value: keyCode });
  textarea.dispatchEvent(up);
}

document.getElementById('synthetic-229')!.addEventListener('click', () => {
  simulate('？', 'Process', 'Slash', 229);
});
document.getElementById('synthetic-191')!.addEventListener('click', () => {
  simulate('？', 'Process', 'Slash', 191);
});
document.getElementById('synthetic-comma')!.addEventListener('click', () => {
  simulate('，', '，', 'Comma', 188);
});

const auto = new URLSearchParams(location.search).get('auto');
if (auto === '229' || auto === '191' || auto === 'comma') {
  document.getElementById(`synthetic-${auto}`)!.click();
}

declare global {
  interface Window { imePocEvents: Record<string, unknown>[]; }
}
window.imePocEvents = events;
