const baseUrl = process.argv[2] ?? 'http://127.0.0.1:7132';
const socketUrl = new URL('/ws', baseUrl);
socketUrl.protocol = socketUrl.protocol === 'https:' ? 'wss:' : 'ws:';

const response = await fetch(baseUrl);
if (!response.ok || !(await response.text()).includes('id="app"')) {
  throw new Error(`KTerm frontend request failed with HTTP ${response.status}.`);
}

await new Promise((resolve, reject) => {
  const socket = new WebSocket(socketUrl);
  let output = '';
  const timeout = setTimeout(() => {
    socket.close();
    reject(new Error(`Timed out waiting for shell output. Received: ${JSON.stringify(output)}`));
  }, 15_000);
  const send = (type, payload = {}, sessionId, requestId) => socket.send(JSON.stringify({
    version: 1,
    type,
    requestId,
    sessionId,
    payload
  }));

  socket.addEventListener('open', () => send('app.ready', {}, undefined, 'ready-1'));
  socket.addEventListener('error', () => reject(new Error('WebSocket connection failed.')));
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    switch (message.type) {
      case 'app.initialState':
        send('session.create', { cols: 80, rows: 24 }, undefined, 'create-1');
        break;
      case 'session.created':
        send('session.input', { data: 'echo KTERM_SERVER_E2E\r' }, message.sessionId);
        break;
      case 'session.output':
        output += message.payload.data;
        if (output.includes('KTERM_SERVER_E2E')) {
          clearTimeout(timeout);
          socket.close();
          resolve();
        }
        break;
      case 'session.error':
        clearTimeout(timeout);
        socket.close();
        reject(new Error(message.payload.message));
        break;
    }
  });
});

console.log('kterm-server HTTP, WebSocket, and shell checks passed.');
