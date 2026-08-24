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

equal(401, await websocketStatus(baseUrl), 'anonymous WebSocket status');
equal(101, await websocketStatus(baseUrl, cookie), 'cookie-authenticated WebSocket status');

console.log('kterm-server Basic login, cookie, asset, health, and WebSocket authentication checks passed.');

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
