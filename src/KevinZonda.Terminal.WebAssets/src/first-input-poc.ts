import { Terminal } from '@xterm/xterm';
import type { IDisposable } from '@xterm/xterm';
import '@xterm/xterm/css/xterm.css';
import { installImeInputReconciler } from './ime-input-reconciler';

const host = document.getElementById('terminal')!;
const log = document.getElementById('log')!;
const summary = document.getElementById('summary')!;
const recent = document.getElementById('recent')!;
const plain = document.getElementById('plain') as HTMLTextAreaElement;
const events: Record<string, unknown>[] = [];
let terminal: Terminal | undefined;
let fallback: IDisposable | undefined;
let firstPrintableInput = true;

function record(event: Record<string, unknown>): void {
  events.push({ at: Math.round(performance.now()), ...event });
  log.textContent = events.map(item => JSON.stringify(item)).join('\n');
  log.scrollTop = log.scrollHeight;
  const browserInput = events.filter(item => item.source === 'browser' && item.type === 'input');
  const output = events.filter(item => item.source === 'xterm.onData' || item.source === 'fallback');
  const printable = output.filter(item => typeof item.data === 'string' &&
    Array.from(item.data).some(char => char.codePointAt(0)! >= 32 && char !== '\x7f'));
  const keydowns = events.filter(item => item.source === 'browser' && item.type === 'keydown');
  const deletes = output.filter(item => item.data === '\x7f' || item.data === '\x1b[3~');
  summary.textContent = `焦点: ${describeFocus()} · Delete: ${deletes.length} · 浏览器 input: ${browserInput.length} · ` +
    `可打印转发: ${printable.length} · 最近按键: ${JSON.stringify(keydowns.at(-1)?.key ?? '')} / ` +
    `${keydowns.at(-1)?.keyCode ?? ''} · 最近输入: ${JSON.stringify(browserInput.at(-1)?.data ?? '')} · ` +
    `最近转发: ${JSON.stringify(output.at(-1)?.data ?? '')}`;
  recent.replaceChildren(...events.slice(-14).map(item => {
    const entry = document.createElement('li');
    entry.textContent = JSON.stringify(item);
    return entry;
  }));
}

function describeFocus(): string {
  const active = document.activeElement;
  return active === terminal?.textarea ? 'terminal textarea' :
    active === plain ? 'plain textarea' :
    `${active?.tagName.toLowerCase() ?? 'none'}${active?.id ? `#${active.id}` : ''}`;
}

for (const type of ['keydown', 'keyup', 'beforeinput', 'input', 'compositionstart',
                    'compositionupdate', 'compositionend', 'focusin', 'focusout'] as const) {
  document.addEventListener(type, event => {
    if (type !== 'focusin' && type !== 'focusout' &&
        event.target !== terminal?.textarea && event.target !== plain) {
      return;
    }
    const key = event as KeyboardEvent;
    const input = event as InputEvent;
    record({
      source: 'browser', type, focus: describeFocus(),
      target: event.target === plain ? 'plain' : 'terminal',
      key: key.key, code: key.code, keyCode: key.keyCode, shiftKey: key.shiftKey,
      isComposing: input.isComposing, inputType: input.inputType, data: input.data,
      composed: event.composed,
      value: event.target === plain ? plain.value : terminal?.textarea?.value,
      firstPrintableInput
    });
  }, { capture: true });
}

function reset(): void {
  fallback?.dispose();
  terminal?.dispose();
  host.replaceChildren();
  events.length = 0;
  log.textContent = '';
  recent.replaceChildren();
  firstPrintableInput = true;
  terminal = new Terminal({ cols: 80, rows: 10 });
  terminal.open(host);
  terminal.onData(data => {
    record({ source: 'xterm.onData', data, firstPrintableInput });
    if (Array.from(data).some(char => char.codePointAt(0)! >= 32 && char !== '\x7f')) {
      firstPrintableInput = false;
    }
    if (data !== '\x7f' && data !== '\x1b[3~') {
      terminal?.write(data);
    }
  });
  if (new URLSearchParams(location.search).has('fix')) {
    fallback = installImeInputReconciler(terminal, data => {
      record({ source: 'fallback', data, firstPrintableInput });
      firstPrintableInput = false;
      terminal?.write(data);
    });
  } else {
    fallback = undefined;
  }
  terminal.focus();
  record({ source: 'ready', focus: describeFocus(), value: terminal.textarea?.value });
}

document.getElementById('reset')!.addEventListener('click', reset);
document.getElementById('replay')!.addEventListener('click', () => {
  reset();
  const textarea = terminal!.textarea!;
  const dispatchKey = (type: 'keydown' | 'keyup', key: string,
                       code: string, keyCode: number): void => {
    const event = new KeyboardEvent(type, { key, code, bubbles: true, composed: true });
    Object.defineProperty(event, 'keyCode', { value: keyCode });
    textarea.dispatchEvent(event);
  };
  dispatchKey('keydown', 'Backspace', 'Backspace', 8);
  textarea.dispatchEvent(new InputEvent('beforeinput', {
    data: '！', inputType: 'insertText', bubbles: true, composed: true
  }));
  textarea.value += '！';
  textarea.dispatchEvent(new InputEvent('input', {
    data: '！', inputType: 'insertText', bubbles: true, composed: true
  }));
  dispatchKey('keyup', 'Backspace', 'Backspace', 8);
  dispatchKey('keydown', '!', 'Digit1', 229);
  dispatchKey('keyup', '!', 'Digit1', 229);
});
document.getElementById('plain-reset')!.addEventListener('click', () => {
  events.length = 0;
  log.textContent = '';
  recent.replaceChildren();
  firstPrintableInput = true;
  plain.value = '';
  plain.focus();
  record({ source: 'ready', focus: describeFocus(), value: plain.value });
});
document.getElementById('copy')!.addEventListener('click', () => {
  void navigator.clipboard.writeText(JSON.stringify(events, null, 2));
});
reset();

declare global {
  interface Window { firstInputPocEvents: Record<string, unknown>[]; }
}
window.firstInputPocEvents = events;
