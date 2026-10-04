import type { Terminal } from '@xterm/xterm';
import '@xterm/xterm/css/xterm.css';
import './styles.css';
import type { NativeBridge } from './bridge';
import { TerminalController } from './terminal-controller';
import type { TerminalCallbacks } from './terminal-controller';

interface SelectionState {
  hasSelection: boolean;
  selection: string;
  selectionChanges: number;
  events: Array<{ type: string; button: number; buttons: number }>;
}

interface TerminalControllerInternals {
  terminal: Terminal;
}

const events: SelectionState['events'] = [];
let selectionChanges = 0;

const bridge = {
  sendInput: () => undefined,
  sendBinaryInput: () => undefined,
  resize: () => undefined,
  openExternal: () => undefined,
  writeClipboard: () => undefined,
  acknowledgeOutput: () => undefined
} as unknown as NativeBridge;

const callbacks: TerminalCallbacks = {
  onBell: () => undefined,
  onControlModifierChanged: () => undefined,
  onFocus: () => undefined,
  onFontSizeChanged: () => undefined,
  onTitle: () => undefined,
  onTerminalCheckpoint: () => Promise.resolve()
};

const controller = new TerminalController(
  { sessionId: 'selection-loss-poc', shellName: 'poc', processId: 1 },
  bridge,
  callbacks,
  { family: 'monospace', size: 16, lineHeight: 1.1, enableLigatures: false },
  { name: 'Campbell' },
  { shape: 'block', blink: false }
);
const fixture = document.getElementById('fixture')!;
fixture.append(controller.element);
controller.mount();

const terminal = (controller as unknown as TerminalControllerInternals).terminal;
terminal.onSelectionChange(() => {
  selectionChanges++;
  renderState();
});

for (const type of ['mousedown', 'mousemove', 'mouseup'] as const) {
  document.addEventListener(type, event => {
    events.push({ type, button: event.button, buttons: event.buttons });
    renderState();
  }, { capture: true });
}

function state(): SelectionState {
  return {
    hasSelection: terminal.hasSelection(),
    selection: terminal.getSelection(),
    selectionChanges,
    events: [...events]
  };
}

function reset(): void {
  terminal.clearSelection();
  events.length = 0;
  selectionChanges = 0;
  renderState();
}

function geometry(): { start: { x: number; y: number }; end: { x: number; y: number } } {
  const screen = document.querySelector('.xterm-screen')?.getBoundingClientRect();
  const row = document.querySelector('.xterm-rows > div')?.getBoundingClientRect();
  if (!screen || !row || screen.width === 0 || row.height === 0) {
    throw new Error('The xterm screen has not been laid out.');
  }
  return {
    start: { x: screen.left + 16, y: row.top + row.height / 2 },
    end: { x: Math.min(screen.right - 16, screen.left + 480), y: row.top + row.height / 2 }
  };
}

function renderState(): void {
  document.getElementById('result')!.textContent = JSON.stringify(state(), null, 2);
}

declare global {
  interface Window {
    selectionLossPoc: {
      state: () => SelectionState;
      reset: () => void;
      geometry: () => ReturnType<typeof geometry>;
    };
  }
}

window.selectionLossPoc = { state, reset, geometry };
controller.write(
  '0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ selection must remain visible after drag\r\n' +
  'second line for deterministic terminal geometry\r\n',
  () => {
    controller.fitImmediately();
    controller.focus();
    renderState();
  }
);
