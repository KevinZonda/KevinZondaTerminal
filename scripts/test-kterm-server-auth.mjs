import net from 'node:net';
import { randomBytes } from 'node:crypto';

const baseUrl = new URL(process.argv[2] ?? 'http://127.0.0.1:7132');
const password = process.env.KTERM_TEST_PASSWORD;
if (!password) {
  throw new Error('KTERM_TEST_PASSWORD is required.');
}

function equal(expected, actual, message) {
  if (expected !== actual) {
    throw new Error(`${message}: expected ${expected}, got ${actual}.`);
  }
}

function basic(userName, value) {
  return `Basic ${Buffer.from(`${userName}:${value}`, 'utf8').toString('base64')}`;
}

const health = await fetch(new URL('/healthz', baseUrl), { redirect: 'manual' });
equal(200, health.status, 'health endpoint status');

const anonymousPage = await fetch(baseUrl, { redirect: 'manual' });
equal(302, anonymousPage.status, 'anonymous page status');
const loginLocation = anonymousPage.headers.get('location');
if (!loginLocation || !new URL(loginLocation, baseUrl).pathname.startsWith('/auth/login')) {
  throw new Error(`Anonymous page did not redirect to /auth/login: ${loginLocation}`);
}

const loginUrl = new URL(loginLocation, baseUrl);
const challenge = await fetch(loginUrl, { redirect: 'manual' });
equal(401, challenge.status, 'Basic challenge status');
if (!challenge.headers.get('www-authenticate')?.startsWith('Basic realm="KTerm"')) {
  throw new Error('Basic challenge header is missing or invalid.');
}

const wrongPassword = await fetch(loginUrl, {
  redirect: 'manual',
  headers: { Authorization: basic('kterm', `${password}-wrong`) }
});
equal(401, wrongPassword.status, 'wrong password status');

const wrongUser = await fetch(loginUrl, {
  redirect: 'manual',
  headers: { Authorization: basic('someone-else', password) }
});
equal(401, wrongUser.status, 'wrong user status');

const login = await fetch(loginUrl, {
  redirect: 'manual',
  headers: { Authorization: basic('kterm', password) }
});
equal(302, login.status, 'successful login status');
const setCookie = login.headers.get('set-cookie');
if (!setCookie) {
  throw new Error('Successful Basic authentication did not issue a cookie.');
}
const cookie = setCookie.split(';', 1)[0];
if (!cookie.startsWith('kterm.auth=')) {
  throw new Error(`Unexpected authentication cookie: ${cookie}`);
}

const page = await fetch(baseUrl, {
  redirect: 'manual',
  headers: { Cookie: cookie }
});
equal(200, page.status, 'cookie-authenticated page status');
const html = await page.text();
if (!html.includes('id="app"')) {
  throw new Error('Authenticated page did not return the KTerm frontend.');
}

const assetPath = html.match(/(?:src|href)="([^"]*assets\/[^"]+)"/)?.[1];
if (!assetPath) {
  throw new Error('Unable to find a frontend asset in the KTerm page.');
}
const assetUrl = new URL(assetPath, baseUrl);
const anonymousAsset = await fetch(assetUrl, { redirect: 'manual' });
equal(302, anonymousAsset.status, 'anonymous asset status');
const authenticatedAsset = await fetch(assetUrl, {
  redirect: 'manual',
  headers: { Cookie: cookie }
});
equal(200, authenticatedAsset.status, 'cookie-authenticated asset status');

const anonymousDashboard = await fetch(new URL('/dashboard/', baseUrl), { redirect: 'manual' });
equal(302, anonymousDashboard.status, 'anonymous Dashboard status');
const dashboard = await fetch(new URL('/dashboard/', baseUrl), {
  redirect: 'manual',
  headers: { Cookie: cookie }
});
equal(200, dashboard.status, 'cookie-authenticated Dashboard status');
const dashboardHtml = await dashboard.text();
if (!dashboardHtml.includes('id="dashboard-app"')) {
  throw new Error('Authenticated Dashboard did not return the Dashboard frontend.');
}

const dashboardAssetPath = dashboardHtml.match(/(?:src|href)="([^"]*assets\/[^"]+)"/)?.[1];
if (!dashboardAssetPath) {
  throw new Error('Unable to find a Dashboard frontend asset.');
}
const dashboardAsset = await fetch(new URL(dashboardAssetPath, baseUrl), {
  redirect: 'manual',
  headers: { Cookie: cookie }
});
equal(200, dashboardAsset.status, 'cookie-authenticated Dashboard asset status');

const anonymousDashboardApi = await fetch(new URL('/api/dashboard/status', baseUrl), {
  redirect: 'manual'
});
equal(401, anonymousDashboardApi.status, 'anonymous Dashboard API status');
const dashboardStatus = await fetch(new URL('/api/dashboard/status', baseUrl), {
  redirect: 'manual',
  headers: { Cookie: cookie }
});
equal(200, dashboardStatus.status, 'authenticated Dashboard API status');
const csrfCookie = dashboardStatus.headers.get('set-cookie')?.split(';', 1)[0];
const dashboardState = await dashboardStatus.json();
if (!dashboardState.enabled || !dashboardState.csrfToken || !csrfCookie) {
  throw new Error('Dashboard API did not issue its enabled state and CSRF credentials.');
}

const missingCsrf = await fetch(new URL('/api/dashboard/runtimes/not-found', baseUrl), {
  method: 'DELETE',
  redirect: 'manual',
  headers: { Cookie: cookie }
});
equal(400, missingCsrf.status, 'Dashboard mutation without CSRF status');
const missingRuntime = await fetch(new URL('/api/dashboard/runtimes/not-found', baseUrl), {
  method: 'DELETE',
  redirect: 'manual',
  headers: {
    Cookie: `${cookie}; ${csrfCookie}`,
    'X-KTerm-CSRF': dashboardState.csrfToken
  }
});
equal(404, missingRuntime.status, 'Dashboard missing runtime status');

equal(401, await websocketStatus(baseUrl), 'anonymous WebSocket status');
equal(101, await websocketStatus(baseUrl, cookie), 'cookie-authenticated WebSocket status');

const managedRuntimeId = `dashboard-${Date.now()}-${Math.random().toString(16).slice(2)}`;
const managedSocket = await openWebsocket(baseUrl, cookie);
managedSocket.send('runtime.attach', { runtimeId: managedRuntimeId }, undefined, 'dashboard-attach');
await managedSocket.next(message => message.type === 'runtime.attached');
managedSocket.send('session.create', {
  cols: 80,
  rows: 24,
  operationId: 'dashboard-create'
}, undefined, 'dashboard-create');
const createdSession = await managedSocket.next(message => message.type === 'session.created');
const managedSessionId = createdSession.sessionId;
if (!managedSessionId) {
  throw new Error('Dashboard lifecycle test did not create a terminal session.');
}

const populatedStatus = await fetch(new URL('/api/dashboard/status', baseUrl), {
  headers: { Cookie: `${cookie}; ${csrfCookie}` }
});
const populatedState = await populatedStatus.json();
const managedRuntime = populatedState.runtimes?.find(runtime => runtime.runtimeId === managedRuntimeId);
if (!managedRuntime?.sessions.some(session => session.sessionId === managedSessionId)) {
  throw new Error('Dashboard did not report the managed runtime and session.');
}

const closeSession = await fetch(new URL(
  `/api/dashboard/runtimes/${encodeURIComponent(managedRuntimeId)}/sessions/${encodeURIComponent(managedSessionId)}`,
  baseUrl), {
  method: 'DELETE',
  headers: {
    Cookie: `${cookie}; ${csrfCookie}`,
    'X-KTerm-CSRF': populatedState.csrfToken
  }
});
equal(204, closeSession.status, 'Dashboard close session status');
const managedExit = await managedSocket.next(
  message => message.type === 'session.exited' && message.sessionId === managedSessionId);
if (!managedExit.payload.failure?.includes('dashboard')) {
  throw new Error('Dashboard session closure did not notify the browser runtime.');
}

const afterSessionClose = await fetch(new URL('/api/dashboard/status', baseUrl), {
  headers: { Cookie: `${cookie}; ${csrfCookie}` }
});
const afterSessionState = await afterSessionClose.json();
const afterSessionRuntime = afterSessionState.runtimes?.find(runtime => runtime.runtimeId === managedRuntimeId);
if (!afterSessionRuntime || afterSessionRuntime.sessions.length !== 0) {
  throw new Error('Dashboard session closure did not update the runtime snapshot.');
}

const closeRuntime = await fetch(new URL(
  `/api/dashboard/runtimes/${encodeURIComponent(managedRuntimeId)}`,
  baseUrl), {
  method: 'DELETE',
  headers: {
    Cookie: `${cookie}; ${csrfCookie}`,
    'X-KTerm-CSRF': afterSessionState.csrfToken
  }
});
equal(204, closeRuntime.status, 'Dashboard close runtime status');
await managedSocket.next(message => message.type === 'runtime.replaced');
managedSocket.close();

const afterRuntimeClose = await fetch(new URL('/api/dashboard/status', baseUrl), {
  headers: { Cookie: `${cookie}; ${csrfCookie}` }
});
const finalDashboardState = await afterRuntimeClose.json();
if (finalDashboardState.runtimes?.some(runtime => runtime.runtimeId === managedRuntimeId)) {
  throw new Error('Dashboard runtime closure did not remove the runtime snapshot.');
}

console.log('kterm-server authentication and Dashboard management checks passed.');

function websocketStatus(url, cookieHeader) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: url.hostname, port: Number(url.port) });
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(new Error('Timed out waiting for the WebSocket handshake.'));
    }, 10_000);
    let response = '';

    socket.once('connect', () => {
      const headers = [
        'GET /ws HTTP/1.1',
        `Host: ${url.host}`,
        'Connection: Upgrade',
        'Upgrade: websocket',
        `Sec-WebSocket-Key: ${randomBytes(16).toString('base64')}`,
        'Sec-WebSocket-Version: 13',
        `Origin: ${url.origin}`
      ];
      if (cookieHeader) {
        headers.push(`Cookie: ${cookieHeader}`);
      }
      socket.write(`${headers.join('\r\n')}\r\n\r\n`);
    });
    socket.on('data', chunk => {
      response += chunk.toString('latin1');
      const lineEnd = response.indexOf('\r\n');
      if (lineEnd < 0) {
        return;
      }
      clearTimeout(timeout);
      socket.destroy();
      const match = /^HTTP\/1\.1 (\d{3})/.exec(response.slice(0, lineEnd));
      if (!match) {
        reject(new Error(`Invalid WebSocket handshake response: ${response.slice(0, lineEnd)}`));
        return;
      }
      resolve(Number(match[1]));
    });
    socket.once('error', error => {
      clearTimeout(timeout);
      reject(error);
    });
  });
}

function openWebsocket(url, cookieHeader) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: url.hostname, port: Number(url.port) });
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(new Error('Timed out opening the authenticated Dashboard WebSocket.'));
    }, 10_000);
    let response = Buffer.alloc(0);

    const handleHandshake = chunk => {
      response = Buffer.concat([response, chunk]);
      const headerEnd = response.indexOf('\r\n\r\n');
      if (headerEnd < 0) {
        return;
      }
      clearTimeout(timeout);
      socket.off('data', handleHandshake);
      const statusLine = response.subarray(0, response.indexOf('\r\n')).toString('latin1');
      if (!statusLine.includes(' 101 ')) {
        socket.destroy();
        reject(new Error(`Authenticated Dashboard WebSocket failed: ${statusLine}`));
        return;
      }
      const client = createWebsocketClient(socket);
      const remaining = response.subarray(headerEnd + 4);
      if (remaining.length > 0) {
        client.accept(remaining);
      }
      resolve(client);
    };

    socket.once('connect', () => {
      socket.write([
        'GET /ws HTTP/1.1',
        `Host: ${url.host}`,
        'Connection: Upgrade',
        'Upgrade: websocket',
        `Sec-WebSocket-Key: ${randomBytes(16).toString('base64')}`,
        'Sec-WebSocket-Version: 13',
        `Origin: ${url.origin}`,
        `Cookie: ${cookieHeader}`,
        '', ''
      ].join('\r\n'));
    });
    socket.on('data', handleHandshake);
    socket.once('error', error => {
      clearTimeout(timeout);
      reject(error);
    });
  });
}

function createWebsocketClient(socket) {
  let receiveBuffer = Buffer.alloc(0);
  const inbox = [];
  const waiters = [];

  function accept(chunk) {
    receiveBuffer = Buffer.concat([receiveBuffer, chunk]);
    while (receiveBuffer.length >= 2) {
      const opcode = receiveBuffer[0] & 0x0f;
      let payloadLength = receiveBuffer[1] & 0x7f;
      let headerLength = 2;
      if (payloadLength === 126) {
        if (receiveBuffer.length < 4) return;
        payloadLength = receiveBuffer.readUInt16BE(2);
        headerLength = 4;
      } else if (payloadLength === 127) {
        if (receiveBuffer.length < 10) return;
        const length = receiveBuffer.readBigUInt64BE(2);
        if (length > BigInt(Number.MAX_SAFE_INTEGER)) {
          throw new Error('Dashboard WebSocket frame is too large.');
        }
        payloadLength = Number(length);
        headerLength = 10;
      }
      if (receiveBuffer.length < headerLength + payloadLength) return;
      const payload = receiveBuffer.subarray(headerLength, headerLength + payloadLength);
      receiveBuffer = receiveBuffer.subarray(headerLength + payloadLength);
      if (opcode === 1) {
        inbox.push(JSON.parse(payload.toString('utf8')));
        waiters.splice(0).forEach(wake => wake());
      } else if (opcode === 8) {
        socket.end();
      } else if (opcode === 9) {
        sendFrame(payload, 10);
      }
    }
  }

  function sendFrame(payload, opcode = 1) {
    const mask = randomBytes(4);
    let header;
    if (payload.length < 126) {
      header = Buffer.from([0x80 | opcode, 0x80 | payload.length]);
    } else if (payload.length <= 0xffff) {
      header = Buffer.alloc(4);
      header[0] = 0x80 | opcode;
      header[1] = 0x80 | 126;
      header.writeUInt16BE(payload.length, 2);
    } else {
      header = Buffer.alloc(10);
      header[0] = 0x80 | opcode;
      header[1] = 0x80 | 127;
      header.writeBigUInt64BE(BigInt(payload.length), 2);
    }
    const masked = Buffer.alloc(payload.length);
    for (let index = 0; index < payload.length; index += 1) {
      masked[index] = payload[index] ^ mask[index % 4];
    }
    socket.write(Buffer.concat([header, mask, masked]));
  }

  socket.on('data', accept);
  return {
    accept,
    send(type, payload = {}, sessionId, requestId) {
      sendFrame(Buffer.from(JSON.stringify({ version: 1, type, requestId, sessionId, payload })));
    },
    async next(predicate) {
      const deadline = Date.now() + 20_000;
      while (Date.now() < deadline) {
        const index = inbox.findIndex(predicate);
        if (index >= 0) {
          return inbox.splice(index, 1)[0];
        }
        await new Promise((resolveWait, rejectWait) => {
          const timer = setTimeout(
            () => rejectWait(new Error('Timed out waiting for a Dashboard WebSocket message.')),
            Math.max(1, deadline - Date.now()));
          waiters.push(() => {
            clearTimeout(timer);
            resolveWait();
          });
        });
      }
      throw new Error('Timed out waiting for a Dashboard WebSocket message.');
    },
    close() {
      if (!socket.destroyed) {
        sendFrame(Buffer.alloc(0), 8);
        socket.end();
      }
    }
  };
}
