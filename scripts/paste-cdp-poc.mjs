// Starts the WebAssets dev server, runs the paste PoC in headless Chrome and prints JSON results.
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
const profile = await mkdtemp(join(tmpdir(), 'kterm-paste-poc-'));
const port = await getAvailablePort();
const vite = spawn('pnpm', ['exec', 'vite', '--host', '127.0.0.1', '--port', String(port), '--strictPort'], {
  cwd: webAssets,
  stdio: ['ignore', 'pipe', 'pipe']
});
const child = spawn(chrome, [
  '--headless', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'
], { stdio: 'ignore' });

try {
  await waitForUrl(`http://127.0.0.1:${port}/paste-poc.html`);
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
  await call('Page.enable');
  await call('Page.navigate', { url: `http://127.0.0.1:${port}/paste-poc.html` });
  await waitForExpression(call, 'Boolean(window.pastePoc)');
  const response = await call('Runtime.evaluate', {
    expression: 'window.pastePoc.runMatrix()',
    awaitPromise: true,
    returnByValue: true
  });
  if (response.exceptionDetails) {
    throw new Error(response.exceptionDetails.text ?? 'PoC failed in the browser.');
  }
  console.log(JSON.stringify(response.result.value, null, 2));
  socket.close();
} finally {
  vite.kill();
  child.kill();
  await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
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

async function waitForDebugPort(profile) {
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      return Number((await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]);
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

async function waitForExpression(call, expression) {
  for (let attempt = 0; attempt < 100; attempt++) {
    const response = await call('Runtime.evaluate', { expression, returnByValue: true });
    if (response.result.value) return;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Expression did not become true: ${expression}`);
}
