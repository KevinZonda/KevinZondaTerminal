const baseUrl = process.argv[2] ?? 'http://127.0.0.1:7132';
const socketUrl = new URL('/ws', baseUrl);
socketUrl.protocol = socketUrl.protocol === 'https:' ? 'wss:' : 'ws:';

const response = await fetch(baseUrl);
if (!response.ok || !(await response.text()).includes('id="app"')) {
  throw new Error(`KTerm frontend request failed with HTTP ${response.status}.`);
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
  while (!inputAcknowledged || !beforeOutput.includes('KTERM_BEFORE_RECONNECT')) {
    message = await first.nextMessage();
    if (message.type === 'session.inputAck' && message.sessionId === sessionId) {
      inputAcknowledged = message.payload.inputSeq >= 1;
    }
    if (message.type === 'session.output' && message.sessionId === sessionId) {
      beforeOutput += message.payload.data;
    }
    if (message.type === 'session.error') {
      throw new Error(message.payload.message);
    }
  }

  first.socket.close();
  stage = 'closing first socket';
  await new Promise(resolve => first.socket.addEventListener('close', resolve, { once: true }));

  stage = 'attaching second socket';
  const second = await connect([{ sessionId, lastAppliedOutputSeq: 0 }]);
  const resumed = second.attached.payload.sessions.find(session => session.sessionId === sessionId);
  if (!resumed || resumed.processId !== processId) {
    throw new Error(`Reconnect did not preserve Shell PID ${processId}.`);
  }

  let replayedOutput = '';
  stage = 'waiting for replay';
  while (!replayedOutput.includes('KTERM_BEFORE_RECONNECT')) {
    message = await second.nextMessage();
    if (message.type === 'session.output' && message.sessionId === sessionId) {
      replayedOutput += message.payload.data;
    }
    if (message.type === 'session.error') {
      throw new Error(message.payload.message);
    }
  }

  send(second.socket, 'session.input', {
    data: 'echo KTERM_AFTER_RECONNECT\r',
    inputSeq: 2
  }, sessionId);
  stage = 'waiting for second input and output';
  let secondInputAcknowledged = false;
  let afterOutput = '';
  let latestOutputSeq = 0;
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
  send(second.socket, 'session.outputAck', { outputSeq: latestOutputSeq }, sessionId);
  send(second.socket, 'session.close', { operationId: 'close-1' }, sessionId, 'close-1');
  stage = 'closing Shell';
  do {
    message = await second.nextMessage();
  } while (message.type !== 'session.closed');
  second.socket.close();
})(), () => `Timed out testing resumable kterm-server Shell I/O (stage: ${stage}).`);

console.log('kterm-server HTTP, resumable WebSocket, replay, and Shell identity checks passed.');
