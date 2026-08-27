const baseUrl = process.argv[2] ?? 'http://127.0.0.1:7132';
const shellIoOnly = process.argv.includes('--shell-io-only');
const socketUrl = new URL('/ws', baseUrl);
socketUrl.protocol = socketUrl.protocol === 'https:' ? 'wss:' : 'ws:';

const response = await fetch(baseUrl);
const html = await response.text();
if (!response.ok || !html.includes('id="app"')) {
  throw new Error(`KTerm frontend request failed with HTTP ${response.status}.`);
}

if (!html.includes('rel="manifest"') || !html.includes('crossorigin="use-credentials"') ||
    !html.includes('rel="apple-touch-icon"')) {
  throw new Error('KTerm frontend is missing its Web App metadata.');
}

const manifestResponse = await fetch(new URL('/manifest.webmanifest', baseUrl));
if (!manifestResponse.ok ||
    !manifestResponse.headers.get('content-type')?.startsWith('application/manifest+json')) {
  throw new Error(`KTerm Web App manifest request failed with HTTP ${manifestResponse.status}.`);
}
const manifest = await manifestResponse.json();
if (manifest.name !== 'KevinZonda Terminal' || manifest.display !== 'standalone' ||
    manifest.start_url !== '/' || !manifest.icons?.some(icon => icon.sizes === '512x512') ||
    !manifest.shortcuts?.some(shortcut => shortcut.url === '/dashboard/')) {
  throw new Error('KTerm Web App manifest is missing required installation metadata.');
}

for (const iconPath of ['/icons/kterm-192.png', '/icons/kterm-512.png']) {
  const iconResponse = await fetch(new URL(iconPath, baseUrl));
  if (!iconResponse.ok || iconResponse.headers.get('content-type') !== 'image/png') {
    throw new Error(`KTerm Web App icon request failed: ${iconPath}.`);
  }
}

const serviceWorkerResponse = await fetch(new URL('/sw.js', baseUrl));
const serviceWorker = await serviceWorkerResponse.text();
if (!serviceWorkerResponse.ok ||
    !serviceWorker.includes("pathname.startsWith('/assets/')") ||
    serviceWorker.includes("pathname.startsWith('/api/')") ||
    serviceWorker.includes("pathname.startsWith('/auth/')")) {
  throw new Error('KTerm service worker has an invalid static-cache boundary.');
}

const runtimeId = `smoke-${Date.now()}-${Math.random().toString(16).slice(2)}`;
let stage = 'connecting first socket';

function send(socket, type, payload = {}, sessionId, requestId) {
  socket.send(JSON.stringify({ version: 1, type, requestId, sessionId, payload }));
}

async function connect(sessions = []) {
  const socket = new WebSocket(socketUrl);
  const inbox = [];
  let wake;
  socket.addEventListener('message', event => {
    inbox.push(JSON.parse(event.data));
    wake?.();
    wake = undefined;
  });

  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', () => reject(new Error('WebSocket connection failed.')), { once: true });
  });
  send(socket, 'runtime.attach', { runtimeId, sessions }, undefined, `attach-${Date.now()}`);

  async function nextMessage() {
    while (inbox.length === 0) {
      await new Promise(resolve => { wake = resolve; });
    }
    return inbox.shift();
  }

  let attached;
  while (!attached) {
    const message = await nextMessage();
    if (message.type === 'session.error') {
      throw new Error(message.payload.message);
    }
    if (message.type === 'runtime.attached') {
      attached = message;
    }
  }
  return { socket, attached, nextMessage };
}

function withTimeout(task, message) {
  let timeout;
  return Promise.race([
    task.finally(() => clearTimeout(timeout)),
    new Promise((_, reject) => {
      timeout = setTimeout(() => reject(new Error(message())), 20_000);
    })
  ]);
}

await withTimeout((async () => {
  const first = await connect();
  stage = 'reading initial state';
  send(first.socket, 'app.ready', {}, undefined, 'ready-1');
  let message;
  do {
    message = await first.nextMessage();
  } while (message.type !== 'app.initialState');

  send(first.socket, 'session.create', {
    cols: 80,
    rows: 24,
    operationId: 'create-1'
  }, undefined, 'create-1');
  stage = 'creating Shell';
  do {
    message = await first.nextMessage();
  } while (message.type !== 'session.created');

  const sessionId = message.sessionId;
  const processId = message.payload.processId;
  send(first.socket, 'session.input', {
    data: 'echo KTERM_BEFORE_RECONNECT\r',
    inputSeq: 1
  }, sessionId);
  stage = 'waiting for first input and output';

  let inputAcknowledged = false;
  let beforeOutput = '';
  let checkpointOutputSeq = 0;
  while (!inputAcknowledged ||
         beforeOutput.split('KTERM_BEFORE_RECONNECT').length - 1 < 2) {
    message = await first.nextMessage();
    if (message.type === 'session.inputAck' && message.sessionId === sessionId) {
      inputAcknowledged = message.payload.inputSeq >= 1;
    }
    if (message.type === 'session.output' && message.sessionId === sessionId) {
      beforeOutput += message.payload.data;
      checkpointOutputSeq = Math.max(checkpointOutputSeq, message.payload.outputSeq ?? 0);
    }
    if (message.type === 'session.error') {
      throw new Error(message.payload.message);
    }
  }

  if (shellIoOnly) {
    send(first.socket, 'session.close', { operationId: 'close-shell-io' }, sessionId, 'close-shell-io');
    stage = 'closing Shell after I/O validation';
    do {
      message = await first.nextMessage();
    } while (message.type !== 'session.closed');
    first.socket.close();
    return;
  }

  send(first.socket, 'session.outputAck', { outputSeq: checkpointOutputSeq }, sessionId);
  send(first.socket, 'session.checkpointAck', { outputSeq: checkpointOutputSeq }, sessionId);

  stage = 'taking over the runtime with a second socket';
  const second = await connect([{
    sessionId,
    lastAppliedOutputSeq: checkpointOutputSeq,
    checkpointOutputSeq
  }]);
  const resumed = second.attached.payload.sessions.find(session => session.sessionId === sessionId);
  if (!resumed || resumed.processId !== processId) {
    throw new Error(`Reconnect did not preserve Shell PID ${processId}.`);
  }
  if (resumed.checkpointOutputSeq !== checkpointOutputSeq) {
    throw new Error(`Reconnect did not retain checkpoint ${checkpointOutputSeq}.`);
  }

  stage = 'notifying the replaced socket';
  do {
    message = await first.nextMessage();
  } while (message.type !== 'runtime.replaced');
  await new Promise(resolve => {
    if (first.socket.readyState === WebSocket.CLOSED) {
      resolve();
      return;
    }
    first.socket.addEventListener('close', resolve, { once: true });
  });

  send(second.socket, 'session.input', {
    data: 'echo KTERM_AFTER_RECONNECT\r',
    inputSeq: 2
  }, sessionId);
  stage = 'waiting for second input and output';
  let secondInputAcknowledged = false;
  let afterOutput = '';
  let latestOutputSeq = checkpointOutputSeq;
  while (!secondInputAcknowledged || !afterOutput.includes('KTERM_AFTER_RECONNECT')) {
    message = await second.nextMessage();
    if (message.type === 'session.inputAck' && message.sessionId === sessionId) {
      secondInputAcknowledged = message.payload.inputSeq >= 2;
    }
    if (message.type === 'session.output' && message.sessionId === sessionId) {
      afterOutput += message.payload.data;
      latestOutputSeq = Math.max(latestOutputSeq, message.payload.outputSeq ?? 0);
    }
    if (message.type === 'session.error') {
      throw new Error(message.payload.message);
    }
  }
  second.socket.close();
  stage = 'closing second socket before checkpoint';
  await new Promise(resolve => second.socket.addEventListener('close', resolve, { once: true }));

  stage = 'attaching third socket from durable checkpoint';
  const third = await connect([{
    sessionId,
    lastAppliedOutputSeq: checkpointOutputSeq,
    checkpointOutputSeq
  }]);
  const resumedAgain = third.attached.payload.sessions.find(session => session.sessionId === sessionId);
  if (!resumedAgain || resumedAgain.processId !== processId) {
    throw new Error(`Checkpoint reconnect did not preserve Shell PID ${processId}.`);
  }

  let replayedOutput = '';
  stage = 'replaying output newer than checkpoint';
  while (!replayedOutput.includes('KTERM_AFTER_RECONNECT')) {
    message = await third.nextMessage();
    if (message.type === 'session.output' && message.sessionId === sessionId) {
      replayedOutput += message.payload.data;
      latestOutputSeq = Math.max(latestOutputSeq, message.payload.outputSeq ?? 0);
    }
    if (message.type === 'session.error') {
      throw new Error(message.payload.message);
    }
  }
  if (replayedOutput.includes('KTERM_BEFORE_RECONNECT')) {
    throw new Error('Output already covered by the checkpoint was replayed.');
  }

  send(third.socket, 'session.checkpointAck', { outputSeq: latestOutputSeq }, sessionId);
  send(third.socket, 'session.close', { operationId: 'close-1' }, sessionId, 'close-1');
  stage = 'closing Shell';
  do {
    message = await third.nextMessage();
  } while (message.type !== 'session.closed');
  third.socket.close();
})(), () => `Timed out testing resumable kterm-server Shell I/O (stage: ${stage}).`);

console.log(shellIoOnly
  ? 'kterm-server HTTP, WebSocket, and Shell input/output checks passed.'
  : 'kterm-server HTTP, page checkpoint, URL takeover, replay, and Shell identity checks passed.');
