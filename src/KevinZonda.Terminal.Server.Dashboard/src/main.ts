import './styles.css';

interface SessionSnapshot {
  sessionId: string;
  shellName: string;
  processId: number;
  columns: number;
  rows: number;
  exited: boolean;
  exitCode: number | null;
  failure: string | null;
  bufferedOutputBytes: number;
}

interface RuntimeSnapshot {
  runtimeId: string;
  createdAtUtc: string;
  connected: boolean;
  lastConnectedAtUtc: string | null;
  lastDisconnectedAtUtc: string | null;
  expiresAtUtc: string | null;
  bufferedOutputBytes: number;
  sessions: SessionSnapshot[];
}

interface DashboardSnapshot {
  enabled: boolean;
  reason?: string;
  version?: string;
  startedAtUtc?: string;
  generatedAtUtc?: string;
  startingDirectory?: string;
  runtimeRetentionMinutes?: number;
  runtimeCount?: number;
  connectedRuntimeCount?: number;
  sessionCount?: number;
  csrfToken?: string;
  runtimes?: RuntimeSnapshot[];
}

const content = requireElement<HTMLElement>('dashboard-content');
const notice = requireElement<HTMLElement>('notice');
const summary = requireElement<HTMLElement>('summary');
const runtimeList = requireElement<HTMLElement>('runtime-list');
const updated = requireElement<HTMLElement>('last-updated');
const refreshButton = requireElement<HTMLButtonElement>('refresh');
let csrfToken = '';
let refreshTimer: number | undefined;
let requestInFlight = false;

refreshButton.addEventListener('click', () => void refresh());
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) {
    void refresh();
  }
});

void refresh();
refreshTimer = window.setInterval(() => void refresh(), 2_000);
window.addEventListener('pagehide', () => window.clearInterval(refreshTimer));

async function refresh(): Promise<void> {
  if (requestInFlight) {
    return;
  }
  requestInFlight = true;
  refreshButton.disabled = true;
  try {
    const response = await fetch('/api/dashboard/status', {
      credentials: 'same-origin',
      cache: 'no-store'
    });
    if (!response.ok) {
      throw new Error(`Dashboard API returned HTTP ${response.status}.`);
    }
    const snapshot = await response.json() as DashboardSnapshot;
    if (!snapshot.enabled) {
      showDisabled(snapshot.reason ?? 'Password authentication is required to enable server management.');
      return;
    }
    csrfToken = snapshot.csrfToken ?? csrfToken;
    render(snapshot);
    updated.textContent = `Updated ${new Date().toLocaleTimeString()}`;
  } catch (error) {
    showNotice(error instanceof Error ? error.message : 'Unable to load the dashboard.', 'error');
    updated.textContent = 'Update failed';
  } finally {
    requestInFlight = false;
    refreshButton.disabled = false;
  }
}

function showDisabled(message: string): void {
  content.hidden = true;
  showNotice(message, 'warning');
  updated.textContent = 'Management disabled';
}

function showNotice(message: string, kind: 'warning' | 'error'): void {
  notice.hidden = false;
  notice.className = `notice ${kind}`;
  notice.textContent = message;
}

function render(snapshot: DashboardSnapshot): void {
  notice.hidden = true;
  content.hidden = false;
  const runtimes = snapshot.runtimes ?? [];
  summary.replaceChildren(
    summaryCard('Runtimes', String(snapshot.runtimeCount ?? runtimes.length), `${snapshot.connectedRuntimeCount ?? 0} connected`),
    summaryCard('Sessions', String(snapshot.sessionCount ?? 0), 'Shell processes'),
    summaryCard('Uptime', formatDurationSince(snapshot.startedAtUtc), `v${snapshot.version ?? 'unknown'}`),
    summaryCard('Retention', `${formatNumber(snapshot.runtimeRetentionMinutes ?? 0)} min`, 'After disconnect')
  );

  runtimeList.replaceChildren();
  if (runtimes.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'empty-state';
    empty.innerHTML = '<strong>No browser runtimes</strong><span>Open the terminal to create one.</span>';
    runtimeList.append(empty);
    return;
  }
  runtimes.forEach(runtime => runtimeList.append(renderRuntime(runtime)));
}

function summaryCard(label: string, value: string, detail: string): HTMLElement {
  const card = document.createElement('article');
  card.className = 'summary-card';
  card.innerHTML = `<span>${escapeHtml(label)}</span><strong>${escapeHtml(value)}</strong><small>${escapeHtml(detail)}</small>`;
  return card;
}

function renderRuntime(runtime: RuntimeSnapshot): HTMLElement {
  const card = document.createElement('article');
  card.className = 'runtime-card';

  const header = document.createElement('header');
  header.className = 'runtime-header';
  const identity = document.createElement('div');
  identity.className = 'runtime-identity';
  identity.innerHTML = `
    <div class="status-dot ${runtime.connected ? 'connected' : ''}" aria-hidden="true"></div>
    <div>
      <h3 title="${escapeHtml(runtime.runtimeId)}">${escapeHtml(shortId(runtime.runtimeId))}</h3>
      <p>${runtime.connected ? 'Connected' : expirationLabel(runtime.expiresAtUtc)}</p>
    </div>`;

  const actions = document.createElement('div');
  actions.className = 'runtime-actions';
  const metadata = document.createElement('span');
  metadata.className = 'muted';
  metadata.textContent = `${runtime.sessions.length} session${runtime.sessions.length === 1 ? '' : 's'} · ${formatBytes(runtime.bufferedOutputBytes)} buffered`;
  const close = actionButton('Close runtime', 'danger', async () => {
    if (!window.confirm(`Close runtime ${shortId(runtime.runtimeId)} and all of its sessions?`)) {
      return;
    }
    await remove(`/api/dashboard/runtimes/${encodeURIComponent(runtime.runtimeId)}`);
  });
  actions.append(metadata, close);
  header.append(identity, actions);
  card.append(header);

  if (runtime.sessions.length === 0) {
    const empty = document.createElement('p');
    empty.className = 'runtime-empty';
    empty.textContent = 'No announced sessions in this runtime.';
    card.append(empty);
    return card;
  }

  const tableWrap = document.createElement('div');
  tableWrap.className = 'table-wrap';
  const table = document.createElement('table');
  table.innerHTML = '<thead><tr><th>Session</th><th>Shell</th><th>PID</th><th>Size</th><th>Status</th><th>Buffer</th><th></th></tr></thead>';
  const body = document.createElement('tbody');
  runtime.sessions.forEach(session => body.append(renderSession(runtime.runtimeId, session)));
  table.append(body);
  tableWrap.append(table);
  card.append(tableWrap);
  return card;
}

function renderSession(runtimeId: string, session: SessionSnapshot): HTMLTableRowElement {
  const row = document.createElement('tr');
  const status = session.exited
    ? `Exited${session.exitCode === null ? '' : ` (${session.exitCode})`}`
    : 'Running';
  row.innerHTML = `
    <td><code title="${escapeHtml(session.sessionId)}">${escapeHtml(shortId(session.sessionId))}</code></td>
    <td>${escapeHtml(session.shellName)}</td>
    <td>${session.processId}</td>
    <td>${session.columns} × ${session.rows}</td>
    <td><span class="session-status ${session.exited ? 'exited' : ''}" title="${escapeHtml(session.failure ?? '')}">${escapeHtml(status)}</span></td>
    <td>${formatBytes(session.bufferedOutputBytes)}</td>`;
  const actionCell = document.createElement('td');
  actionCell.className = 'row-action';
  actionCell.append(actionButton('Close', 'danger subtle', async () => {
    if (!window.confirm(`Close ${session.shellName} session ${shortId(session.sessionId)}?`)) {
      return;
    }
    await remove(`/api/dashboard/runtimes/${encodeURIComponent(runtimeId)}/sessions/${encodeURIComponent(session.sessionId)}`);
  }));
  row.append(actionCell);
  return row;
}

function actionButton(label: string, className: string, action: () => Promise<void>): HTMLButtonElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = `button ${className}`;
  button.textContent = label;
  button.addEventListener('click', async () => {
    button.disabled = true;
    try {
      await action();
    } catch (error) {
      showNotice(error instanceof Error ? error.message : 'The operation failed.', 'error');
    } finally {
      button.disabled = false;
    }
  });
  return button;
}

async function remove(path: string): Promise<void> {
  const response = await fetch(path, {
    method: 'DELETE',
    credentials: 'same-origin',
    headers: { 'X-KTerm-CSRF': csrfToken }
  });
  if (!response.ok && response.status !== 404) {
    throw new Error(`Management operation returned HTTP ${response.status}.`);
  }
  await refresh();
}

function expirationLabel(value: string | null): string {
  if (!value) {
    return 'Disconnected';
  }
  const remaining = new Date(value).getTime() - Date.now();
  return remaining <= 0 ? 'Awaiting cleanup' : `Expires in ${formatDuration(remaining)}`;
}

function formatDurationSince(value?: string): string {
  if (!value) {
    return '—';
  }
  return formatDuration(Math.max(0, Date.now() - new Date(value).getTime()));
}

function formatDuration(milliseconds: number): string {
  const seconds = Math.floor(milliseconds / 1_000);
  const days = Math.floor(seconds / 86_400);
  const hours = Math.floor((seconds % 86_400) / 3_600);
  const minutes = Math.floor((seconds % 3_600) / 60);
  if (days > 0) return `${days}d ${hours}h`;
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m`;
  return `${seconds}s`;
}

function formatBytes(value: number): string {
  if (value < 1_024) return `${value} B`;
  if (value < 1_048_576) return `${(value / 1_024).toFixed(1)} KiB`;
  return `${(value / 1_048_576).toFixed(1)} MiB`;
}

function formatNumber(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}

function shortId(value: string): string {
  return value.length <= 12 ? value : `${value.slice(0, 8)}…${value.slice(-4)}`;
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>'"]/g, character => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;'
  })[character] ?? character);
}

function requireElement<T extends HTMLElement>(id: string): T {
  const element = document.getElementById(id);
  if (!element) {
    throw new Error(`Missing dashboard element #${id}.`);
  }
  return element as T;
}
