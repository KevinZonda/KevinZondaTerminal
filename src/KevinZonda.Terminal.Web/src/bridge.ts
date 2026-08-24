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
  event: BridgeEvent;
}

interface PendingInputState {
  nextSequence: number;
  pending: Map<number, BridgeEvent>;
}

export class NativeBridge {
  private static readonly FONT_SIZE_STORAGE_KEY = 'kterm.fontSize';
  private readonly handlers = new Map<string, Set<BridgeEventHandler>>();
  private readonly pending = new Map<string, PendingRequest>();
  private readonly webView = window.chrome?.webview;
  private socket?: WebSocket;
  private readonly connectionReady: Promise<void>;
  private readonly runtimeId = createId();
  private readonly pendingInputs = new Map<string, PendingInputState>();
  private readonly pendingResizes = new Map<string, BridgeEvent>();
  private readonly pendingCloses = new Map<string, BridgeEvent>();
  private readonly outputAcks = new Map<string, number>();
  private readonly receivedOutputSequences = new Map<string, number>();
  private readonly closedSessionIds = new Set<string>();
  private resolveConnectionReady: () => void = () => undefined;
  private reconnectTimer?: number;
  private reconnectAttempt = 0;
  private attached = false;
  private everConnected = false;
  private currentSettings: AppSettings = structuredClone(DEFAULT_SETTINGS);
  private serverFontSize = DEFAULT_SETTINGS.font.size;

  public constructor() {
    if (this.webView) {
      this.webView.addEventListener('message', this.handleMessage);
      this.connectionReady = Promise.resolve();
      return;
    }

    window.addEventListener('storage', this.handleBrowserStorage);
    window.addEventListener('online', () => this.reconnectNow());
    this.connectionReady = new Promise<void>(resolve => {
      this.resolveConnectionReady = resolve;
    });
    this.connectSocket();
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

    const session = {
      sessionId: event.sessionId,
      shellName: this.payloadString(event, 'shellName') || 'shell',
      processId: this.payloadNumber(event, 'processId')
    };
    if (!this.webView) {
      this.closedSessionIds.delete(session.sessionId);
      const inputAck = this.payloadNumber(event, 'inputAck');
      if (!this.pendingInputs.has(session.sessionId)) {
        this.pendingInputs.set(session.sessionId, {
          nextSequence: Math.max(1, inputAck + 1),
          pending: new Map()
        });
      }
      if (!this.outputAcks.has(session.sessionId)) {
        this.outputAcks.set(session.sessionId, 0);
      }
      if (!this.receivedOutputSequences.has(session.sessionId)) {
        this.receivedOutputSequences.set(session.sessionId, 0);
      }
    }
    return session;
  }

  public sendInput(sessionId: string, data: string): void {
    this.queueInput('session.input', sessionId, { data });
  }

  public sendBinaryInput(sessionId: string, data: string): void {
    this.queueInput('session.binaryInput', sessionId, { data: btoa(data) });
  }

  public resize(sessionId: string, cols: number, rows: number): void {
    if (this.webView) {
      this.send('session.resize', { cols, rows }, sessionId);
      return;
    }
    const event = this.createEvent('session.resize', { cols, rows }, sessionId);
    this.pendingResizes.set(sessionId, event);
    this.sendBrowserEvent(event);
  }

  public closeSession(sessionId: string): void {
    if (this.webView) {
      this.send('session.close', {}, sessionId);
      return;
    }
    const operationId = createId();
    const event = this.createEvent('session.close', { operationId }, sessionId, operationId);
    this.closedSessionIds.add(sessionId);
    this.pendingCloses.set(sessionId, event);
    this.sendBrowserEvent(event);
  }

  public acknowledgeOutput(sessionId: string, outputSeq: number): void {
    if (this.webView || this.closedSessionIds.has(sessionId) ||
        !Number.isSafeInteger(outputSeq) || outputSeq <= 0) {
      return;
    }
    const acknowledged = this.outputAcks.get(sessionId) ?? 0;
    if (outputSeq <= acknowledged) {
      return;
    }
    this.outputAcks.set(sessionId, outputSeq);
    this.receivedOutputSequences.set(
      sessionId,
      Math.max(this.receivedOutputSequences.get(sessionId) ?? 0, outputSeq));
    this.sendBrowserEvent(this.createEvent('session.outputAck', { outputSeq }, sessionId));
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
    const normalizedSize = this.normalizeFontSize(size);
    if (!this.webView) {
      this.saveBrowserFontSize(normalizedSize);
      this.currentSettings = {
        ...this.currentSettings,
        font: {
          ...this.currentSettings.font,
          size: normalizedSize
        }
      };
      return structuredClone(this.currentSettings);
    }

    return this.settingsFrom(await this.request('settings.fontSize', { size: normalizedSize }));
  }

  public async refreshAgentUsage(provider: 'codex' | 'kimi'): Promise<boolean> {
    const event = await this.request('agentUsage.refresh', { provider });
    return event.payload.started === true;
  }

  public settingsFrom(event: BridgeEvent): AppSettings {
    const settings = event.payload.settings;
    if (typeof settings !== 'object' || settings === null) {
      const defaults = structuredClone(DEFAULT_SETTINGS);
      if (!this.webView) {
        this.serverFontSize = defaults.font.size;
        defaults.font.size = this.loadBrowserFontSize() ?? defaults.font.size;
      }
      this.currentSettings = defaults;
      return structuredClone(defaults);
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
      ? this.normalizeFontSize(font.size)
      : DEFAULT_SETTINGS.font.size;
    const lineHeight = typeof font.lineHeight === 'number' && Number.isFinite(font.lineHeight)
      ? Math.min(2, Math.max(0.8, font.lineHeight))
      : DEFAULT_SETTINGS.font.lineHeight;

    const normalizedSettings: AppSettings = {
      font: {
        family,
        size: this.webView ? size : (this.loadBrowserFontSize() ?? size),
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
    this.serverFontSize = size;
    this.currentSettings = normalizedSettings;
    return structuredClone(normalizedSettings);
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

  private connectSocket(): void {
    if (this.webView || this.socket?.readyState === WebSocket.CONNECTING ||
        this.socket?.readyState === WebSocket.OPEN) {
      return;
    }

    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const socket = new WebSocket(`${protocol}//${window.location.host}/ws`);
    this.socket = socket;
    socket.addEventListener('open', () => {
      if (this.socket !== socket) {
        socket.close();
        return;
      }
      const requestId = createId();
      const sessions = [...this.outputAcks].map(([sessionId, lastAppliedOutputSeq]) => ({
        sessionId,
        lastAppliedOutputSeq
      }));
      socket.send(JSON.stringify(this.createEvent('runtime.attach', {
        runtimeId: this.runtimeId,
        sessions
      }, undefined, requestId)));
    });
    socket.addEventListener('message', event => {
      if (this.socket !== socket || typeof event.data !== 'string') {
        return;
      }
      try {
        this.acceptMessage(JSON.parse(event.data));
      } catch {
        // Ignore malformed server messages; valid bridge traffic is JSON.
      }
    });
    socket.addEventListener('error', () => {
      if (this.socket === socket && socket.readyState !== WebSocket.CLOSED) {
        socket.close();
      }
    });
    socket.addEventListener('close', () => this.handleSocketClosed(socket));
  }

  private acceptMessage(value: unknown): void {
    if (!this.isBridgeEvent(value)) {
      return;
    }

    const event = value;
    if (!this.webView && event.type === 'runtime.attached') {
      this.attached = true;
      this.reconnectAttempt = 0;
      this.reconcileAttachedSessions(event);
      this.flushBrowserQueues();
      this.resolveConnectionReady();
      this.emitConnectionChanged('connected');
      this.everConnected = true;
    }

    if (!this.webView && event.type === 'session.inputAck' && event.sessionId) {
      const acknowledged = this.payloadNumber(event, 'inputSeq');
      const state = this.pendingInputs.get(event.sessionId);
      if (state) {
        for (const sequence of state.pending.keys()) {
          if (sequence <= acknowledged) {
            state.pending.delete(sequence);
          }
        }
        state.nextSequence = Math.max(state.nextSequence, acknowledged + 1);
      }
    }

    if (!this.webView && event.type === 'session.inputNack' && event.sessionId) {
      const expected = this.payloadNumber(event, 'expectedInputSeq');
      const state = this.pendingInputs.get(event.sessionId);
      if (state) {
        [...state.pending]
          .filter(([sequence]) => sequence >= expected)
          .sort(([left], [right]) => left - right)
          .forEach(([, pendingEvent]) => this.sendBrowserEvent(pendingEvent));
      }
    }

    if (!this.webView && event.type === 'session.output' && event.sessionId) {
      const outputSeq = this.payloadNumber(event, 'outputSeq');
      if (outputSeq > 0) {
        const received = this.receivedOutputSequences.get(event.sessionId) ??
          this.outputAcks.get(event.sessionId) ?? 0;
        if (outputSeq <= received) {
          return;
        }
        this.receivedOutputSequences.set(event.sessionId, outputSeq);
      }
    }

    if (!this.webView && event.type === 'session.closed' && event.sessionId) {
      this.pendingCloses.delete(event.sessionId);
      this.pendingResizes.delete(event.sessionId);
      this.pendingInputs.delete(event.sessionId);
      this.outputAcks.delete(event.sessionId);
      this.receivedOutputSequences.delete(event.sessionId);
    }

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

  private handleSocketClosed(socket: WebSocket): void {
    if (this.socket !== socket) {
      return;
    }

    this.socket = undefined;
    this.attached = false;
    for (const [sessionId, acknowledged] of this.outputAcks) {
      this.receivedOutputSequences.set(sessionId, acknowledged);
    }

    const delays = [500, 1000, 2000, 4000, 8000, 15_000];
    const baseDelay = delays[Math.min(this.reconnectAttempt, delays.length - 1)]!;
    this.reconnectAttempt++;
    const delay = Math.round(baseDelay * (0.8 + Math.random() * 0.4));
    this.emitConnectionChanged('reconnecting', delay);
    window.clearTimeout(this.reconnectTimer);
    this.reconnectTimer = window.setTimeout(() => {
      this.reconnectTimer = undefined;
      this.connectSocket();
    }, delay);
  }

  private reconnectNow(): void {
    if (this.webView || this.attached) {
      return;
    }
    window.clearTimeout(this.reconnectTimer);
    this.reconnectTimer = undefined;
    this.connectSocket();
  }

  private reconcileAttachedSessions(event: BridgeEvent): void {
    const rawSessions = event.payload.sessions;
    const attachedSessions = new Map<string, Record<string, unknown>>();
    if (Array.isArray(rawSessions)) {
      for (const value of rawSessions) {
        if (typeof value !== 'object' || value === null) {
          continue;
        }
        const session = value as Record<string, unknown>;
        if (typeof session.sessionId === 'string') {
          attachedSessions.set(session.sessionId, session);
        }
      }
    }

    for (const sessionId of [...this.outputAcks.keys()]) {
      const attached = attachedSessions.get(sessionId);
      if (attached) {
        const inputAck = typeof attached.inputAck === 'number' ? attached.inputAck : 0;
        const input = this.pendingInputs.get(sessionId);
        if (input) {
          for (const sequence of input.pending.keys()) {
            if (sequence <= inputAck) {
              input.pending.delete(sequence);
            }
          }
          input.nextSequence = Math.max(input.nextSequence, inputAck + 1);
        }
        continue;
      }

      const wasClosing = this.pendingCloses.has(sessionId);
      this.pendingCloses.delete(sessionId);
      this.pendingResizes.delete(sessionId);
      this.pendingInputs.delete(sessionId);
      this.outputAcks.delete(sessionId);
      this.receivedOutputSequences.delete(sessionId);
      this.closedSessionIds.add(sessionId);
      const missingEvent: BridgeEvent = wasClosing
        ? this.createEvent('session.closed', {}, sessionId)
        : this.createEvent('session.exited', {
            exitCode: 1,
            failure: 'The server runtime expired before it could reconnect.'
          }, sessionId);
      this.handlers.get(missingEvent.type)?.forEach(handler => handler(missingEvent));
    }
  }

  private emitConnectionChanged(state: 'connected' | 'reconnecting', retryInMs?: number): void {
    this.handlers.get('server.connectionChanged')?.forEach(handler => handler({
      version: 1,
      type: 'server.connectionChanged',
      payload: {
        state,
        retryInMs,
        reconnected: state === 'connected' && this.everConnected
      }
    }));
  }

  private flushBrowserQueues(): void {
    for (const request of this.pending.values()) {
      this.sendBrowserEvent(request.event);
    }
    for (const state of this.pendingInputs.values()) {
      [...state.pending]
        .sort(([left], [right]) => left - right)
        .forEach(([, event]) => this.sendBrowserEvent(event));
    }
    this.pendingResizes.forEach(event => this.sendBrowserEvent(event));
    this.pendingCloses.forEach(event => this.sendBrowserEvent(event));
    this.outputAcks.forEach((outputSeq, sessionId) => {
      if (outputSeq > 0) {
        this.sendBrowserEvent(this.createEvent('session.outputAck', { outputSeq }, sessionId));
      }
    });
  }

  private request(type: string, payload: Record<string, unknown>): Promise<BridgeEvent> {
    const requestId = createId();
    const requestPayload = !this.webView && type === 'session.create'
      ? { ...payload, operationId: requestId }
      : payload;
    const event = this.createEvent(type, requestPayload, undefined, requestId);
    return new Promise<BridgeEvent>((resolve, reject) => {
      this.pending.set(requestId, { resolve, reject, event });
      if (this.webView) {
        this.webView.postMessage(event);
      } else {
        this.sendBrowserEvent(event);
      }

      if (this.webView) {
        window.setTimeout(() => {
          if (this.pending.delete(requestId)) {
            reject(new Error(`Native request '${type}' timed out.`));
          }
        }, 15_000);
      }
    });
  }

  private send(
    type: string,
    payload: Record<string, unknown>,
    sessionId?: string,
    requestId?: string
  ): void {
    const event = this.createEvent(type, payload, sessionId, requestId);
    if (this.webView) {
      this.webView.postMessage(event);
      return;
    }
    this.sendBrowserEvent(event);
  }

  private queueInput(type: 'session.input' | 'session.binaryInput', sessionId: string,
                     payload: Record<string, unknown>): void {
    if (this.webView) {
      this.send(type, payload, sessionId);
      return;
    }

    const state = this.pendingInputs.get(sessionId) ?? {
      nextSequence: 1,
      pending: new Map<number, BridgeEvent>()
    };
    this.pendingInputs.set(sessionId, state);
    const inputSeq = state.nextSequence++;
    const event = this.createEvent(type, { ...payload, inputSeq }, sessionId);
    state.pending.set(inputSeq, event);
    this.sendBrowserEvent(event);
  }

  private createEvent(
    type: string,
    payload: Record<string, unknown>,
    sessionId?: string,
    requestId?: string
  ): BridgeEvent {
    return { version: 1, type, requestId, sessionId, payload };
  }

  private sendBrowserEvent(event: BridgeEvent): boolean {
    if (!this.attached || !this.socket || this.socket.readyState !== WebSocket.OPEN) {
      return false;
    }
    this.socket.send(JSON.stringify(event));
    return true;
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

  private normalizeFontSize(size: number): number {
    return Number.isFinite(size)
      ? Math.min(72, Math.max(8, size))
      : DEFAULT_SETTINGS.font.size;
  }

  private loadBrowserFontSize(): number | undefined {
    try {
      const storedSize = window.localStorage.getItem(NativeBridge.FONT_SIZE_STORAGE_KEY);
      if (storedSize === null || storedSize.trim() === '') {
        return undefined;
      }

      const size = Number(storedSize);
      return Number.isFinite(size) ? this.normalizeFontSize(size) : undefined;
    } catch {
      // Storage can be unavailable in restricted/private browser contexts.
      return undefined;
    }
  }

  private saveBrowserFontSize(size: number): void {
    try {
      window.localStorage.setItem(NativeBridge.FONT_SIZE_STORAGE_KEY, String(size));
    } catch {
      // Keep the in-memory setting usable when persistent storage is blocked.
    }
  }

  private readonly handleBrowserStorage = (event: StorageEvent): void => {
    if (event.key !== NativeBridge.FONT_SIZE_STORAGE_KEY) {
      return;
    }

    const storedSize = event.newValue === null || event.newValue.trim() === ''
      ? Number.NaN
      : Number(event.newValue);
    const size = Number.isFinite(storedSize)
      ? this.normalizeFontSize(storedSize)
      : this.serverFontSize;
    if (size === this.currentSettings.font.size) {
      return;
    }

    this.currentSettings = {
      ...this.currentSettings,
      font: {
        ...this.currentSettings.font,
        size
      }
    };
    const settings = structuredClone(this.currentSettings);
    this.handlers.get('app.settingsChanged')?.forEach(handler => handler({
      version: 1,
      type: 'app.settingsChanged',
      payload: { settings }
    }));
  };
}
