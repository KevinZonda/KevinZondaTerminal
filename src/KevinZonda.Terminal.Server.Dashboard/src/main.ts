import './styles.css';

const FONT_FAMILY_STORAGE_KEY = 'kterm.fontFamily';
const FONT_SIZE_STORAGE_KEY = 'kterm.fontSize';
const THEME_STORAGE_KEY = 'kterm.theme';
const TERMINAL_THEME_NAMES = [
  'KevinZonda Terminal Dark',
  'Pro',
  'Ubuntu',
  'Campbell Powershell',
  'Builtin Tango Dark',
  'Campbell',
  'IBM 5153'
] as const;

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
const logoutButton = requireElement<HTMLButtonElement>('logout');
const sessionsTab = requireElement<HTMLButtonElement>('sessions-tab');
const localConfigurationTab = requireElement<HTMLButtonElement>('local-configuration-tab');
const sessionsPanel = requireElement<HTMLElement>('sessions-panel');
const localConfigurationPanel = requireElement<HTMLElement>('local-configuration-panel');
const localConfigurationForm = requireElement<HTMLFormElement>('local-configuration-form');
const localFontFamily = requireElement<HTMLInputElement>('local-font-family');
const localFontSize = requireElement<HTMLInputElement>('local-font-size');
const localTheme = requireElement<HTMLSelectElement>('local-theme');
const localOrigin = requireElement<HTMLElement>('local-origin');
const localConfigurationStatus = requireElement<HTMLElement>('local-configuration-status');
const resetLocalConfiguration = requireElement<HTMLButtonElement>('reset-local-configuration');
let csrfToken = '';
let refreshTimer: number | undefined;
let requestInFlight = false;

refreshButton.addEventListener('click', () => void refresh());
logoutButton.addEventListener('click', () => void logout());
sessionsTab.addEventListener('click', () => activateTab('sessions'));
localConfigurationTab.addEventListener('click', () => activateTab('local-configuration'));
localConfigurationForm.addEventListener('submit', saveLocalConfiguration);
resetLocalConfiguration.addEventListener('click', clearLocalConfiguration);
window.addEventListener('storage', event => {
  if (event.key === FONT_FAMILY_STORAGE_KEY ||
      event.key === FONT_SIZE_STORAGE_KEY ||
      event.key === THEME_STORAGE_KEY) {
    loadLocalConfiguration();
    showLocalConfigurationStatus('Updated in another page.');
  }
});
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) {
    void refresh();
  }
});

TERMINAL_THEME_NAMES.forEach(themeName => {
  const option = document.createElement('option');
  option.value = themeName;
  option.textContent = themeName;
  localTheme.append(option);
});
localOrigin.textContent = window.location.origin;
loadLocalConfiguration();
activateTab(window.location.hash === '#local-configuration' ? 'local-configuration' : 'sessions', false);
void refresh();
refreshTimer = window.setInterval(() => void refresh(), 2_000);
window.addEventListener('pagehide', () => window.clearInterval(refreshTimer));

type DashboardTab = 'sessions' | 'local-configuration';

function activateTab(tab: DashboardTab, updateHash = true): void {
  const localActive = tab === 'local-configuration';
  sessionsTab.classList.toggle('active', !localActive);
  sessionsTab.setAttribute('aria-selected', String(!localActive));
  localConfigurationTab.classList.toggle('active', localActive);
  localConfigurationTab.setAttribute('aria-selected', String(localActive));
  sessionsPanel.hidden = localActive;
  localConfigurationPanel.hidden = !localActive;
  if (updateHash) {
    window.history.replaceState(
      null,
      '',
      localActive
        ? `${window.location.pathname}${window.location.search}#local-configuration`
        : `${window.location.pathname}${window.location.search}`
    );
  }
}

function loadLocalConfiguration(): void {
  try {
    localFontFamily.value = window.localStorage.getItem(FONT_FAMILY_STORAGE_KEY)?.trim() ?? '';
    const storedFontSize = window.localStorage.getItem(FONT_SIZE_STORAGE_KEY)?.trim() ?? '';
    const fontSize = Number(storedFontSize);
    localFontSize.value = storedFontSize && Number.isFinite(fontSize) && fontSize >= 8 && fontSize <= 72
      ? String(fontSize)
      : '';

    const storedTheme = window.localStorage.getItem(THEME_STORAGE_KEY)?.trim() ?? '';
    const themeName = TERMINAL_THEME_NAMES.find(
      candidate => candidate.toLowerCase() === storedTheme.toLowerCase()
    );
    localTheme.value = themeName ?? '';
  } catch {
    showLocalConfigurationStatus('Browser storage is unavailable.', true);
  }
}

function saveLocalConfiguration(event: SubmitEvent): void {
  event.preventDefault();
  if (!localConfigurationForm.reportValidity()) {
    return;
  }

  const rawFontSize = localFontSize.value.trim();
  const fontSize = Number(rawFontSize);
  if (rawFontSize && (!Number.isFinite(fontSize) || fontSize < 8 || fontSize > 72)) {
    localFontSize.setCustomValidity('Font size must be between 8 and 72.');
    localFontSize.reportValidity();
    localFontSize.setCustomValidity('');
    return;
  }

  try {
    const fontFamily = localFontFamily.value.trim();
    if (fontFamily) {
      window.localStorage.setItem(FONT_FAMILY_STORAGE_KEY, fontFamily);
    } else {
      window.localStorage.removeItem(FONT_FAMILY_STORAGE_KEY);
    }
    if (rawFontSize) {
      window.localStorage.setItem(FONT_SIZE_STORAGE_KEY, String(fontSize));
    } else {
      window.localStorage.removeItem(FONT_SIZE_STORAGE_KEY);
    }
    if (localTheme.value) {
      window.localStorage.setItem(THEME_STORAGE_KEY, localTheme.value);
    } else {
      window.localStorage.removeItem(THEME_STORAGE_KEY);
    }
    showLocalConfigurationStatus('Saved. Open Terminal pages update automatically.');
  } catch {
    showLocalConfigurationStatus('Unable to write browser storage.', true);
  }
}

function clearLocalConfiguration(): void {
  try {
    window.localStorage.removeItem(FONT_FAMILY_STORAGE_KEY);
    window.localStorage.removeItem(FONT_SIZE_STORAGE_KEY);
    window.localStorage.removeItem(THEME_STORAGE_KEY);
    loadLocalConfiguration();
    showLocalConfigurationStatus('Using Server defaults.');
  } catch {
    showLocalConfigurationStatus('Unable to write browser storage.', true);
  }
}

function showLocalConfigurationStatus(message: string, error = false): void {
  localConfigurationStatus.textContent = message;
  localConfigurationStatus.classList.toggle('field-error', error);
}

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
  logoutButton.hidden = true;
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
  logoutButton.hidden = false;
  logoutButton.disabled = false;
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

async function logout(): Promise<void> {
  if (!csrfToken) {
    showNotice('Unable to log out before the dashboard security token is available.', 'error');
    return;
  }

  logoutButton.disabled = true;
  try {
    const response = await fetch('/auth/logout', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'X-KTerm-CSRF': csrfToken }
    });
    if (!response.ok) {
      throw new Error(`Logout returned HTTP ${response.status}.`);
    }

    window.clearInterval(refreshTimer);
    window.location.replace('/auth/logged-out');
  } catch (error) {
    logoutButton.disabled = false;
    showNotice(error instanceof Error ? error.message : 'Unable to log out.', 'error');
  }
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
