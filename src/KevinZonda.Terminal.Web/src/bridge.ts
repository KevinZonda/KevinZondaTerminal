import { createId } from './id';
import { DEFAULT_THEME_NAME, normalizeTerminalThemeName } from './themes';

export interface SessionCreated {
  sessionId: string;
  shellName: string;
  processId: number;
}

export interface FontSettings {
  family: string;
  size: number;
  lineHeight: number;
  enableLigatures: boolean;
}

export interface AppSettings {
  font: FontSettings;
  theme: ThemeSettings;
  cursor: CursorSettings;
  indicators: IndicatorSettings;
  shell: ShellSettings;
}

export interface ThemeSettings {
  name: string;
}

export interface CursorSettings {
  shape: 'block' | 'underline' | 'bar';
  blink: boolean;
}

export interface IndicatorSettings {
  showWorkspaceIndicator: boolean;
  showRemainingUsage: boolean;
  autoRenewKimiToken: boolean;
}

export interface ShellSettings {
  exitBehavior: 'KeepTab' | 'CloseTab';
}

export interface AgentUsageWindow {
  name: string;
  label: string;
  usedPercent: number;
  resetsAt?: string;
  used?: number;
  limit?: number;
}

export interface AgentUsageCredits {
  remaining?: number;
  isUnlimited: boolean;
  total?: number;
  currency?: string;
}

export interface AgentUsageBudget {
  name: string;
  limit: number;
  used: number;
  remainingPercent: number;
  resetsAt?: string;
  isUnlimited: boolean;
  currency?: string;
}

export interface AgentProviderUsage {
  provider: 'codex' | 'kimi';
  state: 'loading' | 'ready' | 'stale' | 'error';
  refreshing: boolean;
  source?: string;
  plan?: string;
  windows: AgentUsageWindow[];
  credits?: AgentUsageCredits;
  budget?: AgentUsageBudget;
  updatedAt?: string;
  nextRefreshAt?: string;
  error?: string;
}

export interface AgentUsageStatus {
  providers: AgentProviderUsage[];
}

export interface SystemMetricsStatus {
  cpuPercent?: number;
  usedMemoryBytes: number;
  availableMemoryBytes: number;
  totalMemoryBytes: number;
  updatedAt?: string;
}

export interface AppInitialState {
  settings: AppSettings;
  agentUsage: AgentUsageStatus;
  systemMetrics: SystemMetricsStatus;
}

export const DEFAULT_SETTINGS: AppSettings = {
  font: {
    family: 'Cascadia Mono, Cascadia Code, Consolas, Microsoft YaHei, monospace',
    size: 14,
    lineHeight: 1.12,
    enableLigatures: false
  },
  theme: {
    name: DEFAULT_THEME_NAME
  },
  cursor: {
    shape: 'bar',
    blink: true
  },
  indicators: {
    showWorkspaceIndicator: true,
    showRemainingUsage: false,
    autoRenewKimiToken: false
  },
  shell: {
    exitBehavior: 'KeepTab'
  }
};

export interface BridgeEvent {
  version: number;
  type: string;
  requestId?: string;
  sessionId?: string;
  payload: Record<string, unknown>;
}

type BridgeEventHandler = (event: BridgeEvent) => void;

interface PendingRequest {
  resolve: (event: BridgeEvent) => void;
  reject: (error: Error) => void;
}

export class NativeBridge {
  private readonly handlers = new Map<string, Set<BridgeEventHandler>>();
  private readonly pending = new Map<string, PendingRequest>();
  private readonly webView = window.chrome?.webview;
  private readonly socket?: WebSocket;
  private readonly connectionReady: Promise<void>;
  private socketClosed = false;

  public constructor() {
    if (this.webView) {
      this.webView.addEventListener('message', this.handleMessage);
      this.connectionReady = Promise.resolve();
      return;
    }

    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    this.socket = new WebSocket(`${protocol}//${window.location.host}/ws`);
    this.connectionReady = new Promise<void>((resolve, reject) => {
      this.socket!.addEventListener('open', () => resolve(), { once: true });
      this.socket!.addEventListener('error', () => reject(new Error('Unable to connect to kterm-server.')), {
        once: true
      });
    });
    this.socket.addEventListener('message', event => {
      if (typeof event.data !== 'string') {
        return;
      }
      try {
        this.acceptMessage(JSON.parse(event.data));
      } catch {
        // Ignore malformed server messages; valid bridge traffic is JSON.
      }
    });
    this.socket.addEventListener('close', () => this.handleSocketClosed());
  }

  public async ready(): Promise<AppInitialState> {
    await this.connectionReady;
    const event = await this.request('app.ready', {});
    return {
      settings: this.settingsFrom(event),
      agentUsage: this.agentUsageFrom(event),
      systemMetrics: this.systemMetricsFrom(event)
    };
  }

  public async createSession(cols = 80, rows = 24): Promise<SessionCreated> {
    const event = await this.request('session.create', { cols, rows });
    if (!event.sessionId) {
      throw new Error('The native host did not return a session ID.');
    }

    return {
      sessionId: event.sessionId,
      shellName: this.payloadString(event, 'shellName') || 'shell',
      processId: this.payloadNumber(event, 'processId')
    };
  }

  public sendInput(sessionId: string, data: string): void {
    this.send('session.input', { data }, sessionId);
  }

  public sendBinaryInput(sessionId: string, data: string): void {
    this.send('session.binaryInput', { data: btoa(data) }, sessionId);
  }

  public resize(sessionId: string, cols: number, rows: number): void {
    this.send('session.resize', { cols, rows }, sessionId);
  }

  public closeSession(sessionId: string): void {
    this.send('session.close', {}, sessionId);
  }

  public openSettings(): void {
    if (this.webView) {
      this.send('window.settings', {});
    }
  }

  public openNewInstance(): void {
    if (this.webView) {
      this.send('window.newInstance', {});
      return;
    }
    window.open(window.location.href, '_blank', 'noopener');
  }

  public openExternal(uri: string): void {
    if (this.webView) {
      this.send('window.openExternal', { uri });
      return;
    }
    window.open(uri, '_blank', 'noopener');
  }

  public async saveFontSize(size: number): Promise<AppSettings> {
    return this.settingsFrom(await this.request('settings.fontSize', { size }));
  }

  public async refreshAgentUsage(provider: 'codex' | 'kimi'): Promise<boolean> {
    const event = await this.request('agentUsage.refresh', { provider });
    return event.payload.started === true;
  }

  public settingsFrom(event: BridgeEvent): AppSettings {
    const settings = event.payload.settings;
    if (typeof settings !== 'object' || settings === null) {
      return structuredClone(DEFAULT_SETTINGS);
    }

    const partialSettings = settings as Partial<AppSettings>;
    const font = typeof partialSettings.font === 'object' && partialSettings.font !== null
      ? partialSettings.font
      : DEFAULT_SETTINGS.font;
    const theme = typeof partialSettings.theme === 'object' && partialSettings.theme !== null
      ? partialSettings.theme
      : DEFAULT_SETTINGS.theme;
    const cursor = typeof partialSettings.cursor === 'object' && partialSettings.cursor !== null
      ? partialSettings.cursor
      : DEFAULT_SETTINGS.cursor;
    const indicators = typeof partialSettings.indicators === 'object' && partialSettings.indicators !== null
      ? partialSettings.indicators
      : DEFAULT_SETTINGS.indicators;
    const shell = typeof partialSettings.shell === 'object' && partialSettings.shell !== null
      ? partialSettings.shell
      : DEFAULT_SETTINGS.shell;

    const family = typeof font.family === 'string' && font.family.trim()
      ? font.family.trim()
      : DEFAULT_SETTINGS.font.family;
    const size = typeof font.size === 'number' && Number.isFinite(font.size)
      ? Math.min(72, Math.max(8, font.size))
      : DEFAULT_SETTINGS.font.size;
    const lineHeight = typeof font.lineHeight === 'number' && Number.isFinite(font.lineHeight)
      ? Math.min(2, Math.max(0.8, font.lineHeight))
      : DEFAULT_SETTINGS.font.lineHeight;

    return {
      font: {
        family,
        size,
        lineHeight,
        enableLigatures: font.enableLigatures === true
      },
      theme: { name: normalizeTerminalThemeName(theme.name) },
      cursor: {
        shape: cursor.shape === 'block' || cursor.shape === 'underline'
          ? cursor.shape
          : 'bar',
        blink: cursor.blink !== false
      },
      indicators: {
        showWorkspaceIndicator: indicators.showWorkspaceIndicator !== false,
        showRemainingUsage: indicators.showRemainingUsage === true,
        autoRenewKimiToken: indicators.autoRenewKimiToken === true
      },
      shell: {
        exitBehavior: shell.exitBehavior === 'CloseTab' ? 'CloseTab' : 'KeepTab'
      }
    };
  }

  public agentUsageFrom(event: BridgeEvent): AgentUsageStatus {
    const value = event.payload.agentUsage;
    if (typeof value !== 'object' || value === null) {
      return { providers: [] };
    }

    const rawProviders = (value as { providers?: unknown }).providers;
    if (!Array.isArray(rawProviders)) {
      return { providers: [] };
    }

    const providers: AgentProviderUsage[] = [];
    for (const rawProvider of rawProviders) {
      if (typeof rawProvider !== 'object' || rawProvider === null) {
        continue;
      }

      const candidate = rawProvider as Record<string, unknown>;
      if ((candidate.provider !== 'codex' && candidate.provider !== 'kimi') ||
          (candidate.state !== 'loading' && candidate.state !== 'ready' &&
           candidate.state !== 'stale' && candidate.state !== 'error')) {
        continue;
      }

      const windows: AgentUsageWindow[] = [];
      if (Array.isArray(candidate.windows)) {
        for (const rawWindow of candidate.windows) {
          if (typeof rawWindow !== 'object' || rawWindow === null) {
            continue;
          }
          const window = rawWindow as Record<string, unknown>;
          if (typeof window.name !== 'string' || typeof window.label !== 'string' ||
              typeof window.usedPercent !== 'number' ||
              !Number.isFinite(window.usedPercent)) {
            continue;
          }
          windows.push({
            name: window.name,
            label: window.label,
            usedPercent: Math.min(100, Math.max(0, window.usedPercent)),
            resetsAt: typeof window.resetsAt === 'string' ? window.resetsAt : undefined,
            used: this.finiteNumber(window.used),
            limit: this.finiteNumber(window.limit)
          });
        }
      }

      const rawCredits = candidate.credits;
      const credits = typeof rawCredits === 'object' && rawCredits !== null
        ? rawCredits as Record<string, unknown>
        : undefined;
      const rawBudget = candidate.budget;
      const budget = typeof rawBudget === 'object' && rawBudget !== null
        ? rawBudget as Record<string, unknown>
        : undefined;

      providers.push({
        provider: candidate.provider,
        state: candidate.state,
        refreshing: candidate.refreshing === true,
        source: typeof candidate.source === 'string' ? candidate.source : undefined,
        plan: typeof candidate.plan === 'string' ? candidate.plan : undefined,
        windows,
        credits: credits
          ? {
              remaining: this.finiteNumber(credits.remaining),
              isUnlimited: credits.isUnlimited === true,
              total: this.finiteNumber(credits.total),
              currency: typeof credits.currency === 'string' ? credits.currency : undefined
            }
          : undefined,
        budget: budget && typeof budget.name === 'string' &&
          this.finiteNumber(budget.limit) !== undefined &&
          this.finiteNumber(budget.used) !== undefined &&
          this.finiteNumber(budget.remainingPercent) !== undefined
          ? {
              name: budget.name,
              limit: this.finiteNumber(budget.limit)!,
              used: this.finiteNumber(budget.used)!,
              remainingPercent: Math.min(100, Math.max(0, this.finiteNumber(budget.remainingPercent)!)),
              resetsAt: typeof budget.resetsAt === 'string' ? budget.resetsAt : undefined,
              isUnlimited: budget.isUnlimited === true,
              currency: typeof budget.currency === 'string' ? budget.currency : undefined
            }
          : undefined,
        updatedAt: typeof candidate.updatedAt === 'string' ? candidate.updatedAt : undefined,
        nextRefreshAt: typeof candidate.nextRefreshAt === 'string' ? candidate.nextRefreshAt : undefined,
        error: typeof candidate.error === 'string' ? candidate.error : undefined
      });
    }

    return { providers };
  }

  public systemMetricsFrom(event: BridgeEvent): SystemMetricsStatus {
    const value = event.payload.systemMetrics;
    if (typeof value !== 'object' || value === null) {
      return { usedMemoryBytes: 0, availableMemoryBytes: 0, totalMemoryBytes: 0 };
    }

    const candidate = value as Record<string, unknown>;
    return {
      cpuPercent: this.clampedNumber(candidate.cpuPercent, 0, 100),
      usedMemoryBytes: this.nonNegativeNumber(candidate.usedMemoryBytes),
      availableMemoryBytes: this.nonNegativeNumber(candidate.availableMemoryBytes),
      totalMemoryBytes: this.nonNegativeNumber(candidate.totalMemoryBytes),
      updatedAt: typeof candidate.updatedAt === 'string' ? candidate.updatedAt : undefined
    };
  }

  private finiteNumber(value: unknown): number | undefined {
    return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
  }

  private nonNegativeNumber(value: unknown): number {
    const number = this.finiteNumber(value);
    return number === undefined ? 0 : Math.max(0, number);
  }

  private clampedNumber(value: unknown, minimum: number, maximum: number): number | undefined {
    const number = this.finiteNumber(value);
    return number === undefined ? undefined : Math.min(maximum, Math.max(minimum, number));
  }

  public writeClipboard(text: string): void {
    if (this.webView) {
      this.send('clipboard.write', { text });
      return;
    }
    void navigator.clipboard.writeText(text);
  }

  public async readClipboard(): Promise<string> {
    if (!this.webView) {
      return navigator.clipboard.readText();
    }
    const event = await this.request('clipboard.read', {});
    return this.payloadString(event, 'text');
  }

  public on(type: string, handler: BridgeEventHandler): () => void {
    const handlers = this.handlers.get(type) ?? new Set<BridgeEventHandler>();
    handlers.add(handler);
    this.handlers.set(type, handlers);
    return () => handlers.delete(handler);
  }

  private readonly handleMessage = (messageEvent: MessageEvent<unknown>): void => {
    this.acceptMessage(messageEvent.data);
  };

  private acceptMessage(value: unknown): void {
    if (!this.isBridgeEvent(value)) {
      return;
    }

    const event = value;
    if (event.requestId) {
      const request = this.pending.get(event.requestId);
      if (request) {
        this.pending.delete(event.requestId);
        if (event.type === 'session.error') {
          request.reject(new Error(this.payloadString(event, 'message') || 'Native operation failed.'));
        } else {
          request.resolve(event);
        }
      }
    }

    this.handlers.get(event.type)?.forEach(handler => handler(event));
  }

  private handleSocketClosed(): void {
    this.socketClosed = true;
    const error = new Error('The connection to kterm-server was closed.');
    for (const request of this.pending.values()) {
      request.reject(error);
    }
    this.pending.clear();
    this.handlers.get('app.runtimeFailed')?.forEach(handler => handler({
      version: 1,
      type: 'app.runtimeFailed',
      payload: { kind: 'server-connection' }
    }));
  }

  private request(type: string, payload: Record<string, unknown>): Promise<BridgeEvent> {
    const requestId = createId();
    return new Promise<BridgeEvent>((resolve, reject) => {
      this.pending.set(requestId, { resolve, reject });
      this.send(type, payload, undefined, requestId);

      window.setTimeout(() => {
        if (this.pending.delete(requestId)) {
          reject(new Error(`Native request '${type}' timed out.`));
        }
      }, 15_000);
    });
  }

  private send(
    type: string,
    payload: Record<string, unknown>,
    sessionId?: string,
    requestId?: string
  ): void {
    const event: BridgeEvent = {
      version: 1,
      type,
      requestId,
      sessionId,
      payload
    };
    if (this.webView) {
      this.webView.postMessage(event);
      return;
    }
    if (!this.socket || this.socketClosed || this.socket.readyState !== WebSocket.OPEN) {
      throw new Error('The connection to kterm-server is not open.');
    }
    this.socket.send(JSON.stringify(event));
  }

  private isBridgeEvent(value: unknown): value is BridgeEvent {
    if (typeof value !== 'object' || value === null) {
      return false;
    }

    const candidate = value as Partial<BridgeEvent>;
    return candidate.version === 1 &&
      typeof candidate.type === 'string' &&
      typeof candidate.payload === 'object' &&
      candidate.payload !== null;
  }

  private payloadString(event: BridgeEvent, name: string): string {
    const value = event.payload[name];
    return typeof value === 'string' ? value : '';
  }

  private payloadNumber(event: BridgeEvent, name: string): number {
    const value = event.payload[name];
    return typeof value === 'number' ? value : 0;
  }
}
