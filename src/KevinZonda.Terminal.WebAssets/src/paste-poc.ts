import { Terminal } from '@xterm/xterm';
import '@xterm/xterm/css/xterm.css';

type PasteSource = 'direct' | 'clipboard-plain' | 'clipboard-rich';
type BridgeSink = 'current' | 'chunked' | 'none';

interface Scenario {
  size: number;
  source: PasteSource;
  sink: BridgeSink;
}

interface ScenarioResult extends Scenario {
  richPayloadSize: number;
  pasteSyncMs: number;
  totalMs: number;
  maxBlockingMs: number;
  dataEvents: number;
  largestMessage: number;
  outputChars: number;
  clipboardTypes: string[];
}

const CHUNK_SIZE = 16 * 1024;
const host = document.getElementById('terminal')!;
const status = document.getElementById('status')!;
const resultsBody = document.getElementById('results')!;
const terminal = new Terminal({ cols: 110, rows: 16, scrollback: 1000 });
const encoder = new TextEncoder();
const results: ScenarioResult[] = [];

let activeSink: BridgeSink = 'none';
let dataEvents = 0;
let largestMessage = 0;
let outputChars = 0;
let maxBlockingMs = 0;
let drain = Promise.resolve();
let clipboardTypes: string[] = [];

terminal.open(host);
terminal.write('KTerm paste PoC ready. Bracketed paste mode is enabled.\r\n\x1b[?2004h', () => {
  terminal.focus();
});

terminal.onData(data => {
  dataEvents++;
  largestMessage = Math.max(largestMessage, data.length);
  outputChars += data.length;

  if (activeSink === 'none') {
    return;
  }
  if (activeSink === 'current') {
    const started = performance.now();
    serializeBridgeMessage(data);
    maxBlockingMs = Math.max(maxBlockingMs, performance.now() - started);
    return;
  }

  drain = drain.then(() => sendChunked(data));
});

document.addEventListener('paste', event => {
  if (event.target === terminal.textarea) {
    clipboardTypes = [...(event.clipboardData?.types ?? [])];
  }
}, { capture: true });

function serializeBridgeMessage(data: string): number {
  const json = JSON.stringify({
    version: 1,
    type: 'session.input',
    sessionId: 'paste-poc',
    payload: { data }
  });
  return encoder.encode(json).byteLength;
}

async function sendChunked(data: string): Promise<void> {
  for (let offset = 0; offset < data.length; offset += CHUNK_SIZE) {
    const started = performance.now();
    serializeBridgeMessage(data.slice(offset, offset + CHUNK_SIZE));
    maxBlockingMs = Math.max(maxBlockingMs, performance.now() - started);
    await new Promise<void>(resolve => window.setTimeout(resolve, 0));
  }
}

function makeText(size: number): string {
  const line = '0123456789 abcdefghijklmnopqrstuvwxyz KTerm paste probe 0123456789\n';
  return line.repeat(Math.ceil(size / line.length)).slice(0, size);
}

function makeRichPayload(text: string): { html: string; rtf: string } {
  const html = `<article><h1>KTerm paste probe</h1><pre>${text}</pre></article>`;
  const rtf = `{\\rtf1\\ansi\\deff0 {\\b KTerm paste probe}\\line ${text.replaceAll('\n', '\\line ')}}`;
  return { html, rtf };
}

function dispatchClipboardPaste(text: string, rich: boolean): number {
  const transfer = new DataTransfer();
  transfer.setData('text/plain', text);
  let richPayloadSize = 0;
  if (rich) {
    const payload = makeRichPayload(text);
    transfer.setData('text/html', payload.html);
    transfer.setData('text/rtf', payload.rtf);
    richPayloadSize = payload.html.length + payload.rtf.length;
  }
  terminal.textarea!.dispatchEvent(new ClipboardEvent('paste', {
    bubbles: true,
    cancelable: true,
    clipboardData: transfer
  }));
  return richPayloadSize;
}

async function runScenario(scenario: Scenario): Promise<ScenarioResult> {
  const text = makeText(scenario.size);
  activeSink = scenario.sink;
  dataEvents = 0;
  largestMessage = 0;
  outputChars = 0;
  maxBlockingMs = 0;
  drain = Promise.resolve();
  clipboardTypes = [];
  status.textContent = `Running ${scenario.source} / ${scenario.sink} / ${formatBytes(scenario.size)}...`;

  await nextFrame();
  const totalStarted = performance.now();
  const pasteStarted = performance.now();
  let richPayloadSize = 0;
  if (scenario.source === 'direct') {
    terminal.paste(text);
  } else {
    richPayloadSize = dispatchClipboardPaste(text, scenario.source === 'clipboard-rich');
  }
  const pasteSyncMs = performance.now() - pasteStarted;
  await drain;
  await nextFrame();

  const result: ScenarioResult = {
    ...scenario,
    richPayloadSize,
    pasteSyncMs,
    totalMs: performance.now() - totalStarted,
    maxBlockingMs,
    dataEvents,
    largestMessage,
    outputChars,
    clipboardTypes: [...clipboardTypes]
  };
  results.push(result);
  appendResult(result);
  status.textContent = `Done: ${scenario.source} / ${scenario.sink} / ${formatBytes(scenario.size)}`;
  return result;
}

async function runMatrix(): Promise<ScenarioResult[]> {
  const scenarios: Scenario[] = [
    { size: 128 * 1024, source: 'direct', sink: 'current' },
    { size: 1024 * 1024, source: 'direct', sink: 'current' },
    { size: 8 * 1024 * 1024, source: 'direct', sink: 'current' },
    { size: 1024 * 1024, source: 'clipboard-plain', sink: 'current' },
    { size: 1024 * 1024, source: 'clipboard-rich', sink: 'current' },
    { size: 8 * 1024 * 1024, source: 'clipboard-rich', sink: 'current' },
    { size: 8 * 1024 * 1024, source: 'direct', sink: 'chunked' }
  ];
  const runResults: ScenarioResult[] = [];
  for (const scenario of scenarios) {
    runResults.push(await runScenario(scenario));
  }
  return runResults;
}

function nextFrame(): Promise<void> {
  return new Promise(resolve => requestAnimationFrame(() => resolve()));
}

function appendResult(result: ScenarioResult): void {
  const row = document.createElement('tr');
  const values = [
    result.source,
    result.sink,
    formatBytes(result.size),
    result.richPayloadSize ? formatBytes(result.richPayloadSize) : '-',
    formatMs(result.pasteSyncMs),
    formatMs(result.totalMs),
    formatMs(result.maxBlockingMs),
    String(result.dataEvents),
    formatBytes(result.largestMessage),
    formatBytes(result.outputChars)
  ];
  values.forEach((value, index) => {
    const cell = document.createElement('td');
    cell.textContent = value;
    if (index >= 4 && index <= 6) {
      const milliseconds = index === 4 ? result.pasteSyncMs :
        index === 5 ? result.totalMs : result.maxBlockingMs;
      cell.className = milliseconds >= 100 ? 'bad' : milliseconds >= 50 ? 'warn' : 'good';
    }
    row.append(cell);
  });
  resultsBody.append(row);
}

function formatBytes(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(2)} MiB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${bytes} B`;
}

function formatMs(milliseconds: number): string {
  return `${milliseconds.toFixed(1)} ms`;
}

document.getElementById('run')!.addEventListener('click', () => {
  const size = Number((document.getElementById('size') as HTMLSelectElement).value);
  const source = (document.getElementById('source') as HTMLSelectElement).value as PasteSource;
  const sink = (document.getElementById('sink') as HTMLSelectElement).value as BridgeSink;
  void runScenario({ size, source, sink });
});
document.getElementById('matrix')!.addEventListener('click', () => void runMatrix());
document.getElementById('clear')!.addEventListener('click', () => {
  results.length = 0;
  resultsBody.replaceChildren();
  status.textContent = '';
});

declare global {
  interface Window {
    pastePoc: {
      results: ScenarioResult[];
      runScenario: (scenario: Scenario) => Promise<ScenarioResult>;
      runMatrix: () => Promise<ScenarioResult[]>;
    };
  }
}

window.pastePoc = { results, runScenario, runMatrix };
