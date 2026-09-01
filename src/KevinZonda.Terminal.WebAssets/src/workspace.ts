import { DEFAULT_SETTINGS } from './bridge';
import type {
  AgentProviderUsage,
  AgentUsageStatus,
  AppSettings,
  AttachedSession,
  BridgeEvent,
  NativeBridge,
  SessionCreated,
  SystemMetricsStatus
} from './bridge';
import { BellPlayer } from './bell-player';
import { TerminalController } from './terminal-controller';
import type { MobileToolbarKey, TerminalCallbacks } from './terminal-controller';
import { createId } from './id';
import type {
  BrowserResumeStore,
  ResumeLayoutNode,
  ResumeWorkspaceSnapshot,
  ResumeWorkspaceRecord,
  TerminalCheckpoint
} from './resume-store';
import { applyTerminalThemeToDocument } from './themes';

type SplitDirection = 'columns' | 'rows';
type SidebarMode = 'hidden' | 'peek' | 'expanded';

type LayoutNode =
  | { type: 'pane'; paneId: string }
  | {
      type: 'split';
      direction: SplitDirection;
      ratio: number;
      first: LayoutNode;
      second: LayoutNode;
    };

interface TerminalTabState {
  sessionId: string;
  title: string;
  processInfo: string;
}

interface PaneState {
  id: string;
  tabs: TerminalTabState[];
  activeSessionId: string;
}

interface WorkspaceState {
  id: string;
  name: string;
  panes: Map<string, PaneState>;
  root?: LayoutNode;
  focusedPaneId?: string;
}

interface TabDragState {
  workspaceId: string;
  paneId: string;
  sessionId: string;
  pointerId: number;
  startX: number;
  startY: number;
  tabElement: HTMLElement;
  dragging: boolean;
  targetPaneId?: string;
  targetIndex?: number;
}

interface SidebarSwipeState {
  identifier: number;
  action: 'open' | 'close';
  startX: number;
  startY: number;
  lastX: number;
  lastY: number;
  claimed: boolean;
  cancelled: boolean;
}

export class Workspace implements TerminalCallbacks {
  private static readonly EDGE_TRIGGER_WIDTH = 4;
  private static readonly SIDEBAR_SWIPE_CLAIM_DISTANCE = 12;
  private static readonly SIDEBAR_SWIPE_TRIGGER_DISTANCE = 60;
  private static readonly SIDEBAR_SWIPE_AXIS_RATIO = 1.2;
  private static readonly MOBILE_KEYBOARD_THRESHOLD = 100;
  private static readonly TAB_DRAG_THRESHOLD = 6;
  private static readonly MAX_WORKSPACE_NAME_LENGTH = 64;
  private static readonly PEEK_OPEN_DELAY = 100;
  private static readonly PEEK_CLOSE_DELAY = 250;
  private static readonly USAGE_TOOLTIP_OPEN_DELAY = 160;
  private static readonly USAGE_TOOLTIP_CLOSE_DELAY = 180;
  private static readonly BELL_FLASH_DURATION_MS = 1200;
  private static readonly USE_META_APPLICATION_SHORTCUTS =
    navigator.platform.startsWith('Mac') || navigator.userAgent.includes('Macintosh');

  private readonly bridge: NativeBridge;
  private readonly app: HTMLElement;
  private readonly workspace: HTMLElement;
  private readonly systemStatus: HTMLElement;
  private readonly workspaceIndicator: HTMLElement;
  private readonly agentStatusBar: HTMLElement;
  private readonly agentUsageTooltip: HTMLElement;
  private readonly peekRail: HTMLElement;
  private readonly peekList: HTMLElement;
  private readonly sidebar: HTMLElement;
  private readonly workspaceList: HTMLElement;
  private readonly status: HTMLElement;
  private readonly mobileInputToolbar: HTMLElement;
  private readonly mobileControlButton: HTMLButtonElement;
  private readonly coarsePointer = window.matchMedia('(pointer: coarse)');
  private readonly bellPlayer = new BellPlayer();
  private mobileViewportBaselineHeight = window.visualViewport?.height ?? window.innerHeight;
  private mobileViewportBaselineWidth = window.visualViewport?.width ?? window.innerWidth;
  private readonly terminals = new Map<string, TerminalController>();
  private readonly earlyOutput = new Map<string, Array<{ data: string; outputSeq: number }>>();
  private readonly closedSessionIds = new Set<string>();
  private readonly pendingExitedSessionIds = new Set<string>();
  private readonly ringingBellSessionIds = new Set<string>();
  private readonly unviewedBellSessionIds = new Set<string>();
  private readonly bellFlashTimers = new Map<string, number>();
  private readonly workspaces: WorkspaceState[] = [];
  private readonly paneElements = new Map<string, HTMLElement>();
  private agentUsageStatus: AgentUsageStatus = { providers: [] };
  private activeWorkspaceId?: string;
  private editingWorkspaceId?: string;
  private nextWorkspaceNumber = 1;
  private sidebarMode: SidebarMode = 'hidden';
  private settings: AppSettings = structuredClone(DEFAULT_SETTINGS);
  private operationPending = false;
  private fontSaveTimer?: number;
  private peekOpenTimer?: number;
  private peekCloseTimer?: number;
  // Prevent a delayed edge peek from opening after the pointer has already
  // crossed out of the WebView, which is common between adjacent displays.
  private pointerInsideViewport = false;
  private lastPointerClientX = Number.POSITIVE_INFINITY;
  private usageTooltipOpenTimer?: number;
  private usageTooltipCloseTimer?: number;
  private activeUsageAnchor?: HTMLElement;
  private activeUsageProvider?: 'codex' | 'kimi';
  private tabDrag?: TabDragState;
  private sidebarSwipe?: SidebarSwipeState;
  private suppressXtermTouchGestures = false;
  private touchGestureResetTimer?: number;
  private restoringResumeState = false;
  private checkpointStorageWarningShown = false;

  public constructor(bridge: NativeBridge, private readonly resumeStore?: BrowserResumeStore) {
    this.bridge = bridge;
    this.app = this.requireElement('app');
    this.workspace = this.requireElement('workspace');
    this.systemStatus = this.requireElement('system-status');
    this.workspaceIndicator = this.requireElement('workspace-indicator');
    this.agentStatusBar = this.requireElement('agent-status-bar');
    this.agentUsageTooltip = document.createElement('div');
    this.agentUsageTooltip.id = 'agent-usage-tooltip';
    this.agentUsageTooltip.className = 'agent-usage-tooltip';
    this.agentUsageTooltip.role = 'dialog';
    this.agentUsageTooltip.hidden = true;
    this.app.append(this.agentUsageTooltip);
    this.peekRail = this.requireElement('workspace-peek');
    this.peekList = this.requireElement('workspace-peek-list');
    this.sidebar = this.requireElement('workspace-sidebar');
    this.workspaceList = this.requireElement('workspace-list');
    this.status = this.requireElement('status');
    this.mobileInputToolbar = this.requireElement('mobile-input-toolbar');
    const mobileControlButton = this.requireElement('mobile-input-control');
    if (!(mobileControlButton instanceof HTMLButtonElement)) {
      throw new Error("Application element '#mobile-input-control' must be a button.");
    }
    this.mobileControlButton = mobileControlButton;
    const newWorkspaceButton = this.requireElement('new-workspace');
    newWorkspaceButton.title = `New workspace (${this.applicationShortcutLabel('N')})`;
    newWorkspaceButton.addEventListener('click', () => void this.createWorkspace());
    this.peekRail.addEventListener('pointerenter', () => this.cancelPeekClose());
    this.peekRail.addEventListener('pointerleave', () => this.schedulePeekClose());
    this.peekRail.addEventListener('click', this.handlePeekBackgroundClick);
    this.sidebar.addEventListener('click', this.handleSidebarBackgroundClick);
    this.agentUsageTooltip.addEventListener('pointerenter', () => this.cancelUsageTooltipClose());
    this.agentUsageTooltip.addEventListener('pointerleave', () => this.scheduleUsageTooltipClose());
    this.mobileInputToolbar.addEventListener('pointerdown', this.handleMobileToolbarPointerDown);

    this.bridge.on('session.output', event => this.handleOutput(event));
    this.bridge.on('session.exited', event => this.handleExit(event));
    this.bridge.on('workspace.command', event => this.executeCommand(this.payloadString(event, 'command')));
    this.bridge.on('app.settingsChanged', event => this.applySettings(this.bridge.settingsFrom(event)));
    this.bridge.on('agentUsage.changed', event => {
      this.renderAgentUsage(this.bridge.agentUsageFrom(event));
    });
    this.bridge.on('systemMetrics.changed', event => {
      this.renderSystemMetrics(this.bridge.systemMetricsFrom(event));
    });
    this.bridge.on('server.connectionChanged', event => {
      const state = this.payloadString(event, 'state');
      if (state === 'reconnecting') {
        this.setStatus('Connection lost. Reconnecting...');
      } else if (state === 'replaced') {
        this.setStatus('This terminal is open in another page. Reload to take control.', true);
      } else if (state === 'connected' && event.payload.reconnected === true) {
        this.setStatus('');
      }
    });
    this.bridge.on('app.runtimeFailed', event => {
      this.setStatus(`WebView2 process failed: ${this.payloadString(event, 'kind')}`, true);
    });

    window.addEventListener('keydown', this.handleKeyboard, { capture: true });
    window.addEventListener('pointermove', this.handleEdgePointerMove, { passive: true });
    this.app.addEventListener('touchstart', this.handleSidebarTouchStart, { capture: true, passive: true });
    this.app.addEventListener('touchmove', this.handleSidebarTouchMove, { capture: true, passive: false });
    this.app.addEventListener('touchend', this.handleSidebarTouchEnd, { capture: true, passive: false });
    this.app.addEventListener('touchcancel', this.handleSidebarTouchCancel, { capture: true, passive: false });
    this.app.addEventListener('-xterm-gesturechange', this.handleSuppressedXtermTouchGesture, { capture: true });
    this.app.addEventListener('-xterm-gesturetap', this.handleSuppressedXtermTouchGesture, { capture: true });
    this.app.addEventListener('-xterm-gesturecontextmenu', this.handleSuppressedXtermTouchGesture, { capture: true });
    document.documentElement.addEventListener('pointerleave', this.handleViewportPointerLeave);
    window.addEventListener('blur', this.handleWindowBlur);
    window.addEventListener('focus', this.handleWindowFocus);
    window.addEventListener('resize', () => {
      this.hideAgentUsageTooltip();
      this.updateMobileInputToolbar();
    });
    window.visualViewport?.addEventListener('resize', this.updateMobileInputToolbar);
    window.visualViewport?.addEventListener('scroll', this.updateMobileInputToolbar);
    this.coarsePointer.addEventListener('change', this.updateMobileInputToolbar);
    window.addEventListener('pagehide', this.handlePageHide);
    window.addEventListener('hashchange', this.handleRuntimeUrlChange);
    document.addEventListener('visibilitychange', this.handleVisibilityChange);
    this.updateMobileInputToolbar();
  }

  public async initialize(): Promise<void> {
    this.setStatus('Starting KevinZonda Terminal...');
    const initialState = await this.bridge.ready();
    this.applySettings(initialState.settings);
    this.renderSystemMetrics(initialState.systemMetrics);
    this.renderAgentUsage(initialState.agentUsage);
    const restored = this.resumeStore !== undefined &&
      (this.resumeStore.isResuming || this.bridge.getAttachedSessions().length > 0) &&
      await this.restoreWorkspaceState();
    if (!restored) {
      await this.createWorkspace();
    }
    this.setStatus('');
  }

  private async restoreWorkspaceState(): Promise<boolean> {
    if (!this.resumeStore) {
      return false;
    }

    this.restoringResumeState = true;
    try {
      const snapshot: ResumeWorkspaceSnapshot = this.resumeStore.getWorkspaceSnapshot();
      const attachedSessions = new Map(
        this.bridge.getAttachedSessions().map(session => [session.sessionId, session])
      );
      const closingSessions = new Set(
        this.resumeStore.getSessions()
          .filter(session => session.pendingCloseOperationId)
          .map(session => session.sessionId)
      );
      const referencedSessions = new Set<string>();
      const restoredWorkspaces: WorkspaceState[] = [];

      for (const storedWorkspace of snapshot.workspaces) {
        const panes = new Map<string, PaneState>();
        for (const storedPane of storedWorkspace.panes) {
          const tabs = storedPane.tabs
            .map(tab => {
              const session = attachedSessions.get(tab.sessionId);
              if (!session) {
                this.resumeStore?.completeSession(tab.sessionId);
                return undefined;
              }
              return {
                sessionId: tab.sessionId,
                title: tab.title || session.shellName,
                processInfo: `${session.shellName} · PID ${session.processId}`
              } satisfies TerminalTabState;
            })
            .filter((tab): tab is TerminalTabState => tab !== undefined);
          if (tabs.length === 0) {
            continue;
          }
          panes.set(storedPane.id, {
            id: storedPane.id,
            tabs,
            activeSessionId: tabs.some(tab => tab.sessionId === storedPane.activeSessionId)
              ? storedPane.activeSessionId
              : tabs[0]!.sessionId
          });
        }

        if (panes.size === 0) {
          continue;
        }
        const root = this.restoreLayoutNode(storedWorkspace.root, new Set(panes.keys()))
          ?? { type: 'pane' as const, paneId: panes.keys().next().value as string };
        const paneIds = new Set(this.collectPaneIds(root));
        for (const paneId of [...panes.keys()]) {
          if (!paneIds.has(paneId)) {
            panes.delete(paneId);
          }
        }
        if (panes.size === 0) {
          continue;
        }

        restoredWorkspaces.push({
          id: storedWorkspace.id,
          name: storedWorkspace.name || `Workspace ${restoredWorkspaces.length + 1}`,
          panes,
          root,
          focusedPaneId: panes.has(storedWorkspace.focusedPaneId ?? '')
            ? storedWorkspace.focusedPaneId
            : panes.keys().next().value as string
        });
      }

      for (const workspace of restoredWorkspaces) {
        for (const pane of workspace.panes.values()) {
          for (const tab of pane.tabs) {
            referencedSessions.add(tab.sessionId);
          }
        }
      }

      const orphanSessions = [...attachedSessions.values()].filter(
        session => !referencedSessions.has(session.sessionId) &&
          !closingSessions.has(session.sessionId)
      );
      if (orphanSessions.length > 0) {
        const paneId = createId();
        const requestedSessionId = this.resumeStore.requestedSessionId;
        const activeSessionId = orphanSessions.some(
          session => session.sessionId === requestedSessionId
        )
          ? requestedSessionId!
          : orphanSessions[0]!.sessionId;
        const pane: PaneState = {
          id: paneId,
          tabs: orphanSessions.map(session => ({
            sessionId: session.sessionId,
            title: session.shellName,
            processInfo: `${session.shellName} · PID ${session.processId}`
          })),
          activeSessionId
        };
        restoredWorkspaces.push({
          id: createId(),
          name: restoredWorkspaces.length === 0 ? 'Workspace 1' : 'Recovered',
          panes: new Map([[paneId, pane]]),
          root: { type: 'pane', paneId },
          focusedPaneId: paneId
        });
        orphanSessions.forEach(session => referencedSessions.add(session.sessionId));
      }

      if (restoredWorkspaces.length === 0) {
        return false;
      }

      this.workspaces.push(...restoredWorkspaces);
      this.nextWorkspaceNumber = Math.max(
        restoredWorkspaces.length + 1,
        snapshot.nextWorkspaceNumber
      );
      let preferredWorkspaceId = restoredWorkspaces.some(
        workspace => workspace.id === snapshot.activeWorkspaceId
      )
        ? snapshot.activeWorkspaceId
        : restoredWorkspaces[0]!.id;
      const requestedSessionId = this.resumeStore.requestedSessionId;
      if (requestedSessionId) {
        for (const workspace of restoredWorkspaces) {
          for (const pane of workspace.panes.values()) {
            if (!pane.tabs.some(tab => tab.sessionId === requestedSessionId)) {
              continue;
            }
            pane.activeSessionId = requestedSessionId;
            workspace.focusedPaneId = pane.id;
            preferredWorkspaceId = workspace.id;
          }
        }
      }
      this.activeWorkspaceId = preferredWorkspaceId;

      for (const sessionId of referencedSessions) {
        const session = attachedSessions.get(sessionId)!;
        await this.restoreTerminal(session);
      }
      for (const session of attachedSessions.values()) {
        if (!referencedSessions.has(session.sessionId) &&
            !closingSessions.has(session.sessionId)) {
          this.bridge.closeSession(session.sessionId);
        }
      }

      this.renderSidebar();
      this.render();
      const focused = this.focusedPane;
      if (focused) {
        this.focusSession(focused.activeSessionId);
      }
      return true;
    } finally {
      this.restoringResumeState = false;
      this.persistResumeState();
    }
  }

  private restoreLayoutNode(
    node: ResumeLayoutNode | undefined,
    validPaneIds: ReadonlySet<string>
  ): LayoutNode | null {
    if (!node) {
      return null;
    }
    if (node.type === 'pane') {
      return validPaneIds.has(node.paneId) ? { type: 'pane', paneId: node.paneId } : null;
    }

    const first = this.restoreLayoutNode(node.first, validPaneIds);
    const second = this.restoreLayoutNode(node.second, validPaneIds);
    if (!first) {
      return second;
    }
    if (!second) {
      return first;
    }
    return {
      type: 'split',
      direction: node.direction,
      ratio: Math.min(0.9, Math.max(0.1, node.ratio)),
      first,
      second
    };
  }

  private async restoreTerminal(session: AttachedSession): Promise<void> {
    const terminal = this.createTerminalController(session);
    let checkpoint: TerminalCheckpoint | undefined;
    try {
      checkpoint = await this.resumeStore?.loadCheckpoint(session.sessionId);
    } catch (error) {
      console.error('Unable to restore terminal checkpoint.', error);
    }
    await terminal.restoreCheckpoint(checkpoint);
    this.closedSessionIds.delete(session.sessionId);
    this.terminals.set(session.sessionId, terminal);
    this.drainEarlyOutput(session.sessionId, terminal);
    if (session.exited) {
      terminal.markExited(session.exitCode, session.failure);
    }
  }

  public async createWorkspace(activate = false): Promise<void> {
    await this.runExclusive(() => this.createWorkspaceCore(activate));
  }

  public async createTab(paneId = this.focusedPaneId): Promise<void> {
    await this.runExclusive(() => this.createTabInPane(paneId));
  }

  public async splitFocused(direction: SplitDirection): Promise<void> {
    const pane = this.focusedPane;
    const root = this.root;
    if (!pane || !root) {
      return;
    }

    await this.runExclusive(async () => {
      this.setStatus('Starting split shell...');
      // Estimate the new pane's size from the focused terminal so the ConPTY
      // starts close to its final dimensions and fullscreen apps do not redraw twice.
      const size = this.terminalSizeFor(pane);
      const session = await this.bridge.createSession(
        direction === 'columns' ? Math.max(2, Math.floor(size.cols / 2)) : size.cols,
        direction === 'rows' ? Math.max(2, Math.floor(size.rows / 2)) : size.rows
      );
      this.addTerminal(session);

      const newPane: PaneState = {
        id: createId(),
        tabs: [this.createTerminalTab(session)],
        activeSessionId: session.sessionId
      };
      this.panes.set(newPane.id, newPane);
      this.root = this.replacePaneLeaf(root, pane.id, {
        type: 'split',
        direction,
        ratio: 0.5,
        first: { type: 'pane', paneId: pane.id },
        second: { type: 'pane', paneId: newPane.id }
      });
      this.focusedPaneId = newPane.id;
      this.render();
      this.focusSession(session.sessionId);
      this.setStatus('');
    });
  }

  public onFocus(sessionId: string): void {
    const match = this.findWorkspacePaneBySession(sessionId);
    if (!match || match.workspace.id !== this.activeWorkspaceId) {
      return;
    }

    const { pane, workspace } = match;
    pane.activeSessionId = sessionId;
    workspace.focusedPaneId = pane.id;
    this.updateFocusState();
    this.clearViewedBell(sessionId);
  }

  public onBell(sessionId: string): void {
    if (this.settings.bell.sound !== 'None') {
      this.bellPlayer.play();
    }
    this.showBellVisualFeedback(sessionId);
  }

  private showBellVisualFeedback(sessionId: string): void {
    const mode = this.settings.bell.visualFeedback;
    if (mode === 'None') {
      return;
    }

    this.ringingBellSessionIds.add(sessionId);
    if (mode === 'UntilViewed' && !this.isSessionViewed(sessionId)) {
      this.unviewedBellSessionIds.add(sessionId);
    } else {
      this.unviewedBellSessionIds.delete(sessionId);
    }

    const existingTimer = this.bellFlashTimers.get(sessionId);
    if (existingTimer !== undefined) {
      window.clearTimeout(existingTimer);
    }
    this.bellFlashTimers.set(sessionId, window.setTimeout(() => {
      this.bellFlashTimers.delete(sessionId);
      this.ringingBellSessionIds.delete(sessionId);
      this.refreshBellTab(sessionId);
    }, Workspace.BELL_FLASH_DURATION_MS));
    this.refreshBellTab(sessionId);
  }

  private isSessionViewed(sessionId: string): boolean {
    const match = this.findWorkspacePaneBySession(sessionId);
    return Boolean(
      match &&
      match.workspace.id === this.activeWorkspaceId &&
      match.workspace.focusedPaneId === match.pane.id &&
      match.pane.activeSessionId === sessionId &&
      document.visibilityState === 'visible' &&
      document.hasFocus()
    );
  }

  private clearViewedBell(sessionId: string): void {
    if (!this.unviewedBellSessionIds.has(sessionId) || !this.isSessionViewed(sessionId)) {
      return;
    }
    this.unviewedBellSessionIds.delete(sessionId);
    this.refreshBellTab(sessionId);
  }

  private clearAllBellVisualFeedback(): void {
    this.bellFlashTimers.forEach(timer => window.clearTimeout(timer));
    this.bellFlashTimers.clear();
    this.ringingBellSessionIds.clear();
    this.unviewedBellSessionIds.clear();
    this.activeWorkspace?.panes.forEach(pane => this.refreshPaneTabs(pane));
  }

  private refreshBellTab(sessionId: string): void {
    const match = this.findWorkspacePaneBySession(sessionId);
    if (match && match.workspace.id === this.activeWorkspaceId) {
      this.refreshPaneTabs(match.pane);
    }
  }

  public onControlModifierChanged(sessionId: string, _active: boolean): void {
    if (sessionId === this.focusedPane?.activeSessionId) {
      this.updateMobileInputToolbar();
    }
  }

  public onFontSizeChanged(sessionId: string, fontSize: number): void {
    if (this.fontSaveTimer !== undefined) {
      window.clearTimeout(this.fontSaveTimer);
      this.fontSaveTimer = undefined;
    }
    if (!this.isOnlyTerminal(sessionId)) {
      return;
    }

    this.fontSaveTimer = window.setTimeout(() => {
      this.fontSaveTimer = undefined;
      if (!this.isOnlyTerminal(sessionId)) {
        return;
      }

      void this.bridge.saveFontSize(fontSize)
        .then(settings => { this.settings = settings; })
        .catch(error => this.setStatus(`Unable to save font size: ${String(error)}`, true));
    }, 400);
  }

  public onTitle(sessionId: string, title: string): void {
    const match = this.findWorkspacePaneBySession(sessionId);
    const pane = match?.pane;
    const tab = pane?.tabs.find(candidate => candidate.sessionId === sessionId);
    if (!pane || !tab || !title.trim()) {
      return;
    }

    tab.title = title.trim();
    this.refreshPaneTabs(pane);
    this.syncWindowTitle();
    this.persistResumeState();
  }

  public async onTerminalCheckpoint(checkpoint: TerminalCheckpoint): Promise<void> {
    if (!this.resumeStore) {
      return;
    }
    try {
      await this.resumeStore.saveCheckpoint(checkpoint);
    } catch (error) {
      if (!this.checkpointStorageWarningShown) {
        this.checkpointStorageWarningShown = true;
        this.setStatus('Terminal history cannot be saved for page refresh.', true);
        console.error('Unable to persist terminal checkpoint.', error);
      }
    } finally {
      // Keep the live terminal usable even when browser persistence is denied.
      // In that fallback case only its persisted history cannot be restored.
      this.bridge.acknowledgeCheckpoint(checkpoint.sessionId, checkpoint.outputSeq);
    }
  }

  private readonly handlePageHide = (): void => {
    this.persistResumeState();
    this.checkpointAllTerminals();
  };

  private readonly handleVisibilityChange = (): void => {
    if (document.visibilityState === 'hidden') {
      this.persistResumeState();
      this.checkpointAllTerminals();
      return;
    }
    const sessionId = this.focusedPane?.activeSessionId;
    if (sessionId) {
      this.clearViewedBell(sessionId);
    }
  };

  private readonly handleRuntimeUrlChange = (): void => {
    if (!this.resumeStore) {
      return;
    }
    const linkedRuntimeId = this.resumeStore.linkedRuntimeId;
    if (!linkedRuntimeId) {
      this.resumeStore.setFocusedSession(this.focusedPane?.activeSessionId);
      return;
    }
    if (linkedRuntimeId !== this.resumeStore.runtimeId) {
      window.location.reload();
      return;
    }

    const sessionId = this.resumeStore.linkedSessionId;
    if (!sessionId) {
      return;
    }
    const match = this.findWorkspacePaneBySession(sessionId);
    if (!match) {
      return;
    }
    match.pane.activeSessionId = sessionId;
    match.workspace.focusedPaneId = match.pane.id;
    this.activeWorkspaceId = match.workspace.id;
    this.renderSidebar();
    this.render();
    this.focusSession(sessionId);
  };

  private checkpointAllTerminals(): void {
    this.terminals.forEach(terminal => {
      void terminal.checkpointNow().catch(error => {
        console.error('Unable to checkpoint terminal before the page was hidden.', error);
      });
    });
  }

  private persistResumeState(): void {
    if (!this.resumeStore || this.restoringResumeState) {
      return;
    }

    const workspaces: ResumeWorkspaceRecord[] = this.workspaces.map(workspace => ({
      id: workspace.id,
      name: workspace.name,
      panes: [...workspace.panes.values()].map(pane => ({
        id: pane.id,
        activeSessionId: pane.activeSessionId,
        tabs: pane.tabs.map(tab => ({
          sessionId: tab.sessionId,
          title: tab.title,
          processInfo: tab.processInfo
        }))
      })),
      root: workspace.root ? structuredClone(workspace.root) : undefined,
      focusedPaneId: workspace.focusedPaneId
    }));
    this.resumeStore.saveWorkspace({
      activeWorkspaceId: this.activeWorkspaceId,
      nextWorkspaceNumber: this.nextWorkspaceNumber,
      workspaces
    });
    this.resumeStore.setFocusedSession(this.focusedPane?.activeSessionId);
  }

  private readonly handleKeyboard = (event: KeyboardEvent): void => {
    const newInstanceModifier = Workspace.USE_META_APPLICATION_SHORTCUTS
      ? event.metaKey && !event.ctrlKey
      : event.ctrlKey && !event.metaKey;
    if (newInstanceModifier && event.shiftKey && !event.altKey && event.code === 'KeyN') {
      event.preventDefault();
      event.stopImmediatePropagation();
      if (!event.repeat) {
        this.bridge.openNewInstance();
      }
      return;
    }

    if (event.repeat) {
      return;
    }

    if (event.key === 'Escape' && this.activeUsageAnchor === document.activeElement) {
      event.preventDefault();
      event.stopImmediatePropagation();
      this.hideAgentUsageTooltip();
      return;
    }

    if (event.target instanceof HTMLInputElement) {
      return;
    }

    if (event.code === 'F2' && !event.altKey && !event.ctrlKey &&
        !event.shiftKey && !event.metaKey && this.sidebarMode === 'expanded' &&
        this.activeWorkspaceId) {
      event.preventDefault();
      event.stopImmediatePropagation();
      this.startWorkspaceRename(this.activeWorkspaceId);
      return;
    }

    if (this.hasApplicationShortcutModifier(event)) {
      let handled = true;
      switch (event.code) {
        case 'KeyT':
          this.executeCommand('newTab');
          break;
        case 'Backslash':
          this.executeCommand('splitColumns');
          break;
        case 'Minus':
          this.executeCommand('splitRows');
          break;
        case 'KeyS':
          this.bridge.openSettings();
          break;
        case 'KeyB':
          this.executeCommand('toggleSidebar');
          break;
        case 'KeyN':
          this.executeCommand('newWorkspace');
          break;
        case 'KeyW':
          this.executeCommand('closeTab');
          break;
        default:
          handled = false;
      }

      if (handled) {
        event.preventDefault();
        event.stopImmediatePropagation();
        return;
      }
    }

    if (event.ctrlKey && event.shiftKey && !event.altKey && !event.metaKey) {
      const terminal = this.focusedTerminal;
      if (event.code === 'KeyC' && terminal?.copySelection()) {
        event.preventDefault();
        event.stopImmediatePropagation();
      } else if (event.code === 'KeyV' && terminal) {
        event.preventDefault();
        event.stopImmediatePropagation();
        void this.bridge.readClipboard().then(text => terminal.paste(text));
      }
    }
  };

  private hasApplicationShortcutModifier(event: KeyboardEvent): boolean {
    if (event.ctrlKey || event.shiftKey) {
      return false;
    }
    return Workspace.USE_META_APPLICATION_SHORTCUTS
      ? event.metaKey && !event.altKey
      : event.altKey && !event.metaKey;
  }

  private applicationShortcutLabel(key: string): string {
    return Workspace.USE_META_APPLICATION_SHORTCUTS ? `⌘${key}` : `Alt+${key}`;
  }

  private executeCommand(command: string): void {
    switch (command) {
      case 'newTab':
        void this.createTab();
        break;
      case 'splitColumns':
        void this.splitFocused('columns');
        break;
      case 'splitRows':
        void this.splitFocused('rows');
        break;
      case 'toggleSidebar':
        this.setSidebarMode(this.sidebarMode === 'expanded' ? 'hidden' : 'expanded');
        break;
      case 'newWorkspace':
        void this.createWorkspace(true);
        break;
      case 'closeTab':
        this.closeFocusedTab();
        break;
    }
  }

  private async createWorkspaceCore(activate = false): Promise<void> {
    this.setStatus('Starting workspace…');
    const session = await this.bridge.createSession();
    this.addTerminal(session);

    const pane: PaneState = {
      id: createId(),
      tabs: [this.createTerminalTab(session)],
      activeSessionId: session.sessionId
    };
    const workspace: WorkspaceState = {
      id: createId(),
      name: `Workspace ${this.nextWorkspaceNumber++}`,
      panes: new Map([[pane.id, pane]]),
      root: { type: 'pane', paneId: pane.id },
      focusedPaneId: pane.id
    };

    this.workspaces.push(workspace);
    // Shortcut-created workspaces activate immediately. Other entry points
    // keep the current workspace active unless there is none (startup, or
    // after the last workspace was closed).
    if (activate || !this.activeWorkspaceId) {
      this.activeWorkspaceId = workspace.id;
      this.renderSidebar();
      this.render();
      this.focusSession(session.sessionId);
    } else {
      this.renderSidebar();
    }
    this.setStatus('');
  }

  private activateWorkspace(workspaceId: string): void {
    if (this.operationPending || workspaceId === this.activeWorkspaceId ||
        !this.workspaces.some(workspace => workspace.id === workspaceId)) {
      return;
    }

    this.activeWorkspaceId = workspaceId;
    this.updateSidebarSelection();
    this.render();
    const focused = this.focusedPane;
    if (focused) {
      this.focusSession(focused.activeSessionId);
    }
  }

  private closeWorkspace(workspaceId: string): void {
    void this.runExclusive(() => this.closeWorkspaceCore(workspaceId, true));
  }

  private async closeWorkspaceCore(workspaceId: string, closeSessions: boolean): Promise<void> {
    const index = this.workspaces.findIndex(workspace => workspace.id === workspaceId);
    if (index < 0) {
      return;
    }

    const workspace = this.workspaces[index]!;
    if (this.editingWorkspaceId === workspace.id) {
      this.editingWorkspaceId = undefined;
    }
    const wasActive = workspace.id === this.activeWorkspaceId;
    if (closeSessions) {
      for (const pane of workspace.panes.values()) {
        for (const tab of pane.tabs) {
          this.destroyTerminal(tab.sessionId);
          this.bridge.closeSession(tab.sessionId);
        }
      }
    }

    this.workspaces.splice(index, 1);
    if (wasActive) {
      this.activeWorkspaceId = this.workspaces[Math.min(index, this.workspaces.length - 1)]?.id;
    }

    if (this.workspaces.length === 0) {
      this.renderSidebar();
      this.render();
      if (this.settings.workspace.lastWorkspaceClosedBehavior === 'QuitApplication' &&
          this.bridge.quitApplication()) {
        return;
      }
      await this.createWorkspaceCore();
      return;
    }

    this.renderSidebar();
    if (wasActive) {
      this.render();
      const focused = this.focusedPane;
      if (focused) {
        this.focusSession(focused.activeSessionId);
      }
    }
  }

  private setSidebarMode(mode: SidebarMode): void {
    if (mode === this.sidebarMode) {
      return;
    }

    const layoutChanged = mode === 'expanded' || this.sidebarMode === 'expanded';
    this.cancelPeekOpen();
    this.cancelPeekClose();
    this.sidebarMode = mode;
    this.app.classList.toggle('sidebar-peek', mode === 'peek');
    this.app.classList.toggle('sidebar-visible', mode === 'expanded');
    this.peekRail.setAttribute('aria-hidden', String(mode !== 'peek'));
    this.sidebar.setAttribute('aria-hidden', String(mode !== 'expanded'));
    if (mode !== 'expanded' && this.editingWorkspaceId) {
      this.editingWorkspaceId = undefined;
      this.renderSidebar();
    }
    if (layoutChanged) {
      this.fitVisibleTerminals(true);
    }
  }

  private renderSidebar(): void {
    const sidebarFragment = document.createDocumentFragment();
    const peekFragment = document.createDocumentFragment();
    const indicatorFragment = document.createDocumentFragment();
    for (const workspace of this.workspaces) {
      const item = document.createElement('div');
      item.className = 'workspace-item';
      item.dataset.workspaceId = workspace.id;
      item.classList.toggle('active', workspace.id === this.activeWorkspaceId);

      if (workspace.id === this.editingWorkspaceId) {
        item.append(this.createWorkspaceNameEditor(workspace));
      } else {
        const activate = document.createElement('button');
        activate.type = 'button';
        activate.className = 'workspace-activate';
        activate.textContent = workspace.name;
        activate.title = workspace.name;
        activate.setAttribute('aria-label', workspace.name);
        activate.addEventListener('click', () => this.activateWorkspace(workspace.id));
        activate.addEventListener('dblclick', event => {
          event.preventDefault();
          this.startWorkspaceRename(workspace.id);
        });
        item.append(activate);
      }

      const close = document.createElement('button');
      close.type = 'button';
      close.className = 'workspace-close';
      close.textContent = '×';
      close.title = `Close ${workspace.name}`;
      close.setAttribute('aria-label', `Close ${workspace.name}`);
      close.addEventListener('click', event => {
        event.stopPropagation();
        this.closeWorkspace(workspace.id);
      });
      item.append(close);
      sidebarFragment.append(item);

      const peekItem = document.createElement('div');
      peekItem.className = 'workspace-peek-item';
      peekItem.dataset.workspaceId = workspace.id;
      peekItem.classList.toggle('active', workspace.id === this.activeWorkspaceId);
      const peekActivate = document.createElement('button');
      peekActivate.type = 'button';
      peekActivate.className = 'workspace-peek-activate';
      peekActivate.title = workspace.name;
      peekActivate.setAttribute('aria-label', workspace.name);
      peekActivate.addEventListener('click', () => this.activateWorkspace(workspace.id));
      peekItem.append(peekActivate);
      peekFragment.append(peekItem);

      const indicator = document.createElement('button');
      indicator.type = 'button';
      indicator.className = 'workspace-indicator-item';
      indicator.dataset.workspaceId = workspace.id;
      indicator.classList.toggle('active', workspace.id === this.activeWorkspaceId);
      indicator.title = workspace.name;
      indicator.setAttribute('aria-label', workspace.name);
      if (workspace.id === this.activeWorkspaceId) {
        indicator.setAttribute('aria-current', 'page');
      }
      indicator.addEventListener('click', () => this.activateWorkspace(workspace.id));
      indicatorFragment.append(indicator);
    }
    const peekAdd = document.createElement('div');
    peekAdd.className = 'workspace-peek-item workspace-peek-add';
    const peekAddButton = document.createElement('button');
    peekAddButton.type = 'button';
    peekAddButton.className = 'workspace-peek-activate';
    peekAddButton.textContent = '+';
    peekAddButton.title = `New workspace (${this.applicationShortcutLabel('N')})`;
    peekAddButton.setAttribute('aria-label', 'New workspace');
    peekAddButton.addEventListener('click', () => void this.createWorkspace());
    peekAdd.append(peekAddButton);
    peekFragment.append(peekAdd);
    this.workspaceList.replaceChildren(sidebarFragment);
    this.peekList.replaceChildren(peekFragment);
    this.workspaceIndicator.replaceChildren(indicatorFragment);
    this.revealActiveWorkspaceIndicator();
    this.persistResumeState();
  }

  private updateSidebarSelection(): void {
    [this.workspaceList, this.peekList].forEach(list => {
      list.querySelectorAll<HTMLElement>('.workspace-item, .workspace-peek-item').forEach(item => {
        item.classList.toggle('active', item.dataset.workspaceId === this.activeWorkspaceId);
      });
    });
    this.workspaceIndicator.querySelectorAll<HTMLElement>('.workspace-indicator-item').forEach(item => {
      const isActive = item.dataset.workspaceId === this.activeWorkspaceId;
      item.classList.toggle('active', isActive);
      if (isActive) {
        item.setAttribute('aria-current', 'page');
      } else {
        item.removeAttribute('aria-current');
      }
    });
    this.revealActiveWorkspaceIndicator();
  }

  private revealActiveWorkspaceIndicator(): void {
    const active = this.workspaceIndicator.querySelector<HTMLElement>('.workspace-indicator-item.active');
    if (!active) {
      return;
    }

    const left = active.offsetLeft;
    const right = left + active.offsetWidth;
    if (left < this.workspaceIndicator.scrollLeft) {
      this.workspaceIndicator.scrollLeft = left;
    } else if (right > this.workspaceIndicator.scrollLeft + this.workspaceIndicator.clientWidth) {
      this.workspaceIndicator.scrollLeft = right - this.workspaceIndicator.clientWidth;
    }
  }

  private startWorkspaceRename(workspaceId: string): void {
    if (this.sidebarMode !== 'expanded' ||
        !this.workspaces.some(workspace => workspace.id === workspaceId)) {
      return;
    }

    this.editingWorkspaceId = workspaceId;
    this.renderSidebar();
    window.requestAnimationFrame(() => {
      const editor = this.workspaceList.querySelector<HTMLInputElement>(
        `.workspace-name-editor[data-workspace-id="${workspaceId}"]`
      );
      editor?.focus();
      editor?.select();
    });
  }

  private createWorkspaceNameEditor(workspace: WorkspaceState): HTMLInputElement {
    const editor = document.createElement('input');
    editor.type = 'text';
    editor.className = 'workspace-name-editor';
    editor.dataset.workspaceId = workspace.id;
    editor.value = workspace.name;
    editor.maxLength = Workspace.MAX_WORKSPACE_NAME_LENGTH;
    editor.setAttribute('aria-label', `Rename ${workspace.name}`);

    let finished = false;
    const finish = (save: boolean): void => {
      if (finished) {
        return;
      }
      finished = true;

      if (save) {
        const name = editor.value.trim();
        if (name) {
          workspace.name = name;
        }
      }

      if (this.editingWorkspaceId === workspace.id) {
        this.editingWorkspaceId = undefined;
      }
      this.renderSidebar();
    };

    editor.addEventListener('keydown', event => {
      event.stopPropagation();
      if (event.key === 'Enter') {
        event.preventDefault();
        finish(true);
      } else if (event.key === 'Escape') {
        event.preventDefault();
        finish(false);
      }
    });
    editor.addEventListener('blur', () => window.setTimeout(() => finish(true), 0));
    return editor;
  }

  private readonly handleEdgePointerMove = (event: PointerEvent): void => {
    this.pointerInsideViewport = true;
    this.lastPointerClientX = event.clientX;

    if (this.sidebarMode !== 'hidden') {
      return;
    }

    if (event.buttons !== 0 || event.clientX > Workspace.EDGE_TRIGGER_WIDTH) {
      this.cancelPeekOpen();
      return;
    }

    if (this.peekOpenTimer === undefined) {
      this.peekOpenTimer = window.setTimeout(() => {
        this.peekOpenTimer = undefined;
        if (this.sidebarMode === 'hidden' &&
            this.pointerInsideViewport &&
            this.lastPointerClientX <= Workspace.EDGE_TRIGGER_WIDTH) {
          this.setSidebarMode('peek');
        }
      }, Workspace.PEEK_OPEN_DELAY);
    }
  };

  private readonly handlePeekBackgroundClick = (event: MouseEvent): void => {
    if (this.sidebarMode === 'peek' &&
        (event.target === this.peekRail || event.target === this.peekList)) {
      this.setSidebarMode('expanded');
    }
  };
  private readonly handleSidebarBackgroundClick = (event: MouseEvent): void => {
    if (this.sidebarMode === 'expanded' &&
        (event.target === this.sidebar || event.target === this.workspaceList)) {
      this.setSidebarMode('hidden');
    }
  };

  private readonly handleSidebarTouchStart = (event: TouchEvent): void => {
    if (event.touches.length !== 1) {
      this.sidebarSwipe = undefined;
      this.suppressXtermTouchGestures = false;
      if (this.touchGestureResetTimer !== undefined) {
        window.clearTimeout(this.touchGestureResetTimer);
        this.touchGestureResetTimer = undefined;
      }
      return;
    }

    const touch = event.changedTouches.item(0);
    const target = event.target;
    if (!touch || (target instanceof Node && this.mobileInputToolbar.contains(target))) {
      return;
    }

    const action: SidebarSwipeState['action'] = this.sidebarMode === 'expanded'
      ? 'close'
      : 'open';

    this.sidebarSwipe = {
      identifier: touch.identifier,
      action,
      startX: touch.clientX,
      startY: touch.clientY,
      lastX: touch.clientX,
      lastY: touch.clientY,
      claimed: false,
      cancelled: false
    };
  };

  private readonly handleSidebarTouchMove = (event: TouchEvent): void => {
    const swipe = this.sidebarSwipe;
    const touch = swipe && this.findTouch(event.touches, swipe.identifier);
    if (!swipe || !touch || swipe.cancelled) {
      return;
    }

    swipe.lastX = touch.clientX;
    swipe.lastY = touch.clientY;
    const deltaX = swipe.lastX - swipe.startX;
    const deltaY = swipe.lastY - swipe.startY;
    const horizontalDistance = Math.abs(deltaX);
    const verticalDistance = Math.abs(deltaY);

    if (!swipe.claimed) {
      if (verticalDistance >= Workspace.SIDEBAR_SWIPE_CLAIM_DISTANCE &&
          verticalDistance > horizontalDistance) {
        swipe.cancelled = true;
        return;
      }

      const correctDirection = swipe.action === 'open' ? deltaX > 0 : deltaX < 0;
      if (horizontalDistance >= Workspace.SIDEBAR_SWIPE_CLAIM_DISTANCE && !correctDirection) {
        swipe.cancelled = true;
        return;
      }
      if (!correctDirection ||
          horizontalDistance < Workspace.SIDEBAR_SWIPE_CLAIM_DISTANCE ||
          horizontalDistance <= verticalDistance * Workspace.SIDEBAR_SWIPE_AXIS_RATIO) {
        return;
      }

      swipe.claimed = true;
      this.suppressXtermTouchGestures = true;
      this.cancelPeekOpen();
      this.cancelPeekClose();
    }

    event.preventDefault();
    event.stopPropagation();
  };

  private readonly handleSidebarTouchEnd = (event: TouchEvent): void => {
    const swipe = this.sidebarSwipe;
    const touch = swipe && this.findTouch(event.changedTouches, swipe.identifier);
    if (!swipe || !touch) {
      return;
    }

    swipe.lastX = touch.clientX;
    swipe.lastY = touch.clientY;
    this.finishSidebarSwipe(event, true);
  };

  private readonly handleSidebarTouchCancel = (event: TouchEvent): void => {
    if (!this.sidebarSwipe) {
      return;
    }

    this.finishSidebarSwipe(event, false);
  };

  private finishSidebarSwipe(event: TouchEvent, allowAction: boolean): void {
    const swipe = this.sidebarSwipe;
    this.sidebarSwipe = undefined;
    if (!swipe?.claimed) {
      return;
    }

    event.preventDefault();
    const deltaX = swipe.lastX - swipe.startX;
    const deltaY = swipe.lastY - swipe.startY;
    const correctDirection = swipe.action === 'open' ? deltaX > 0 : deltaX < 0;
    const triggered = allowAction && correctDirection &&
      Math.abs(deltaX) >= Workspace.SIDEBAR_SWIPE_TRIGGER_DISTANCE &&
      Math.abs(deltaX) > Math.abs(deltaY) * Workspace.SIDEBAR_SWIPE_AXIS_RATIO;
    if (triggered) {
      this.setSidebarMode(swipe.action === 'open' ? 'expanded' : 'hidden');
    }

    if (this.touchGestureResetTimer !== undefined) {
      window.clearTimeout(this.touchGestureResetTimer);
    }
    // xterm's document-level touchend listener runs after this capture handler.
    // Keep suppression through that dispatch so it cannot synthesize a tap.
    this.touchGestureResetTimer = window.setTimeout(() => {
      this.touchGestureResetTimer = undefined;
      this.suppressXtermTouchGestures = false;
    }, 0);
  }

  private readonly handleSuppressedXtermTouchGesture = (event: Event): void => {
    if (!this.suppressXtermTouchGestures) {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
  };

  private findTouch(touches: TouchList, identifier: number): Touch | undefined {
    for (let index = 0; index < touches.length; index++) {
      const touch = touches.item(index);
      if (touch?.identifier === identifier) {
        return touch;
      }
    }
    return undefined;
  }

  private readonly handleWindowBlur = (): void => {
    this.pointerInsideViewport = false;
    this.lastPointerClientX = Number.POSITIVE_INFINITY;
    this.cancelPeekOpen();
    this.hideAgentUsageTooltip();
    this.mobileInputToolbar.hidden = true;
    if (this.sidebarMode === 'peek') {
      this.setSidebarMode('hidden');
    }
  };

  private readonly handleWindowFocus = (): void => {
    this.updateMobileInputToolbar();
    const sessionId = this.focusedPane?.activeSessionId;
    if (sessionId) {
      this.clearViewedBell(sessionId);
    }
  };

  private readonly handleViewportPointerLeave = (): void => {
    this.pointerInsideViewport = false;
    this.lastPointerClientX = Number.POSITIVE_INFINITY;
    this.cancelPeekOpen();
    this.schedulePeekClose();
  };

  private schedulePeekClose(): void {
    if (this.sidebarMode !== 'peek' || this.peekCloseTimer !== undefined) {
      return;
    }

    this.peekCloseTimer = window.setTimeout(() => {
      this.peekCloseTimer = undefined;
      if (this.sidebarMode === 'peek') {
        this.setSidebarMode('hidden');
      }
    }, Workspace.PEEK_CLOSE_DELAY);
  }

  private cancelPeekOpen(): void {
    if (this.peekOpenTimer !== undefined) {
      window.clearTimeout(this.peekOpenTimer);
      this.peekOpenTimer = undefined;
    }
  }

  private cancelPeekClose(): void {
    if (this.peekCloseTimer !== undefined) {
      window.clearTimeout(this.peekCloseTimer);
      this.peekCloseTimer = undefined;
    }
  }

  private async createTabInPane(paneId?: string): Promise<void> {
    const workspace = this.activeWorkspace;
    if (!workspace) {
      throw new Error('No active workspace is available.');
    }

    await this.createTabInWorkspace(workspace, paneId);
  }

  private async createTabInWorkspace(workspace: WorkspaceState, paneId?: string): Promise<void> {
    const isActiveWorkspace = workspace.id === this.activeWorkspaceId;
    if (isActiveWorkspace) {
      this.setStatus('Starting shell…');
    }

    // A new tab fills the pane, so start the ConPTY at the current terminal's size.
    const anchor = paneId
      ? workspace.panes.get(paneId)
      : workspace.focusedPaneId
        ? workspace.panes.get(workspace.focusedPaneId)
        : undefined;
    const size = this.terminalSizeFor(anchor);
    const session = await this.bridge.createSession(size.cols, size.rows);
    this.addTerminal(session);

    let pane = paneId
      ? workspace.panes.get(paneId)
      : workspace.focusedPaneId
        ? workspace.panes.get(workspace.focusedPaneId)
        : undefined;
    if (!pane) {
      pane = {
        id: createId(),
        tabs: [],
        activeSessionId: session.sessionId
      };
      workspace.panes.set(pane.id, pane);
      workspace.root = { type: 'pane', paneId: pane.id };
    }

    pane.tabs.push(this.createTerminalTab(session));
    pane.activeSessionId = session.sessionId;
    workspace.focusedPaneId = pane.id;
    if (isActiveWorkspace) {
      this.render();
      this.focusSession(session.sessionId);
      this.setStatus('');
    }
    this.persistResumeState();
  }

  private createTerminalTab(session: SessionCreated): TerminalTabState {
    return {
      sessionId: session.sessionId,
      title: session.shellName,
      processInfo: `${session.shellName} · PID ${session.processId}`
    };
  }

  private addTerminal(session: SessionCreated): void {
    const terminal = this.createTerminalController(session);
    this.closedSessionIds.delete(session.sessionId);
    this.terminals.set(session.sessionId, terminal);
    this.drainEarlyOutput(session.sessionId, terminal);
  }

  private createTerminalController(session: SessionCreated): TerminalController {
    return new TerminalController(
      session,
      this.bridge,
      this,
      this.settings.font,
      this.settings.theme,
      this.settings.cursor
    );
  }

  private drainEarlyOutput(sessionId: string, terminal: TerminalController): void {
    const pending = this.earlyOutput.get(sessionId);
    if (pending) {
      pending.forEach(({ data, outputSeq }) => {
        terminal.writeOutput(data, outputSeq);
      });
      this.earlyOutput.delete(sessionId);
    }
  }

  private isOnlyTerminal(sessionId: string): boolean {
    if (this.terminals.size !== 1 || this.panes.size !== 1) {
      return false;
    }

    const pane = this.panes.values().next().value as PaneState | undefined;
    return pane?.tabs.length === 1 && pane.activeSessionId === sessionId;
  }

  private applySettings(settings: AppSettings): void {
    const usageDisplayChanged =
      this.settings.indicators.showRemainingUsage !== settings.indicators.showRemainingUsage;
    const visualBellChanged =
      this.settings.bell.visualFeedback !== settings.bell.visualFeedback;
    this.settings = settings;
    if (visualBellChanged) {
      this.clearAllBellVisualFeedback();
    }
    this.workspaceIndicator.hidden = !settings.indicators.showWorkspaceIndicator;
    if (settings.indicators.showWorkspaceIndicator) {
      this.revealActiveWorkspaceIndicator();
    }
    applyTerminalThemeToDocument(settings.theme.name);
    this.terminals.forEach(terminal => {
      terminal.applyFontSettings(settings.font);
      terminal.applyThemeSettings(settings.theme);
      terminal.applyCursorSettings(settings.cursor);
    });
    if (usageDisplayChanged) {
      this.renderAgentUsage(this.agentUsageStatus);
    }
  }

  private handleOutput(event: BridgeEvent): void {
    if (!event.sessionId || this.closedSessionIds.has(event.sessionId)) {
      return;
    }

    const data = this.payloadString(event, 'data');
    const outputSeq = this.payloadNumber(event, 'outputSeq');
    const terminal = this.terminals.get(event.sessionId);
    if (terminal) {
      terminal.writeOutput(data, outputSeq);
    } else {
      const pending = this.earlyOutput.get(event.sessionId) ?? [];
      pending.push({ data, outputSeq });
      this.earlyOutput.set(event.sessionId, pending);
    }
  }

  private handleExit(event: BridgeEvent): void {
    const sessionId = event.sessionId;
    if (!sessionId) {
      return;
    }

    const failure = this.payloadString(event, 'failure');
    if (!failure && this.settings.shell.exitBehavior === 'CloseTab') {
      this.pendingExitedSessionIds.add(sessionId);
      this.drainExitedSessionClosures();
      return;
    }

    this.terminals.get(sessionId)?.markExited(
      this.payloadNumber(event, 'exitCode'),
      failure || undefined);
  }

  private render(): void {
    this.paneElements.clear();
    if (!this.root) {
      this.workspace.replaceChildren(this.emptyState());
    } else {
      this.workspace.replaceChildren(this.renderNode(this.root));
      this.updateFocusState();

      for (const paneId of this.collectPaneIds(this.root)) {
        const pane = this.panes.get(paneId);
        if (pane) {
          this.terminals.get(pane.activeSessionId)?.mount();
        }
      }
    }

    // WebGL contexts follow visibility (reclaimed after a grace period).
    for (const terminal of this.terminals.values()) {
      terminal.setVisible(terminal.element.isConnected);
    }
    this.persistResumeState();
  }

  private terminalSizeFor(pane: PaneState | undefined): { cols: number; rows: number } {
    const current = pane ? this.terminals.get(pane.activeSessionId) : undefined;
    return {
      cols: current && current.cols > 0 ? current.cols : 80,
      rows: current && current.rows > 0 ? current.rows : 24
    };
  }

  private renderNode(node: LayoutNode): HTMLElement {
    if (node.type === 'pane') {
      const pane = this.panes.get(node.paneId);
      return pane ? this.renderPane(pane) : this.missingPane();
    }

    const split = document.createElement('div');
    split.className = `split split-${node.direction}`;
    const first = this.renderNode(node.first);
    const divider = document.createElement('div');
    divider.className = 'split-divider';
    divider.setAttribute('role', 'separator');
    const second = this.renderNode(node.second);
    split.append(first, divider, second);

    const applyRatio = (): void => {
      if (node.direction === 'columns') {
        split.style.gridTemplateColumns = `${node.ratio}fr 5px ${1 - node.ratio}fr`;
      } else {
        split.style.gridTemplateRows = `${node.ratio}fr 5px ${1 - node.ratio}fr`;
      }
    };
    applyRatio();

    divider.addEventListener('pointerdown', event => {
      event.preventDefault();
      divider.setPointerCapture(event.pointerId);
      divider.classList.add('dragging');
    });
    divider.addEventListener('pointermove', event => {
      if (!divider.hasPointerCapture(event.pointerId)) {
        return;
      }
      const rect = split.getBoundingClientRect();
      const ratio = node.direction === 'columns'
        ? (event.clientX - rect.left) / rect.width
        : (event.clientY - rect.top) / rect.height;
      node.ratio = Math.min(0.9, Math.max(0.1, ratio));
      applyRatio();
    });
    const finishDrag = (event: PointerEvent): void => {
      if (divider.hasPointerCapture(event.pointerId)) {
        divider.releasePointerCapture(event.pointerId);
      }
      divider.classList.remove('dragging');
      this.fitVisibleTerminals();
      this.persistResumeState();
    };
    divider.addEventListener('pointerup', finishDrag);
    divider.addEventListener('pointercancel', finishDrag);
    return split;
  }

  private renderPane(pane: PaneState): HTMLElement {
    const element = document.createElement('section');
    element.className = 'pane';
    element.classList.toggle('compact', this.panes.size === 1 && pane.tabs.length === 1);
    element.dataset.paneId = pane.id;
    element.addEventListener('pointerdown', () => {
      this.focusedPaneId = pane.id;
      this.updateFocusState();
    });

    const tabStrip = document.createElement('header');
    tabStrip.className = 'pane-tab-strip';
    tabStrip.setAttribute('aria-label', 'Pane terminal tabs');
    element.append(tabStrip);
    this.renderPaneTabs(pane, tabStrip);
    this.watchTabStripOverflow(tabStrip);

    const content = document.createElement('div');
    content.className = 'pane-content';
    const terminal = this.terminals.get(pane.activeSessionId);
    content.append(terminal?.element ?? this.missingTerminal());
    element.append(content);

    this.paneElements.set(pane.id, element);
    return element;
  }

  private renderPaneTabs(pane: PaneState, tabStrip: HTMLElement): void {
    const fragment = document.createDocumentFragment();
    for (const tab of pane.tabs) {
      const tabElement = document.createElement('div');
      tabElement.className = 'pane-tab';
      tabElement.dataset.sessionId = tab.sessionId;
      tabElement.classList.toggle('active', tab.sessionId === pane.activeSessionId);
      const bellRinging = this.ringingBellSessionIds.has(tab.sessionId);
      const bellUnviewed = this.unviewedBellSessionIds.has(tab.sessionId);
      const hasBell = bellRinging || bellUnviewed;
      tabElement.classList.toggle('has-bell', hasBell);
      tabElement.classList.toggle('bell-ringing', bellRinging);
      tabElement.classList.toggle('bell-unviewed', bellUnviewed);
      tabElement.addEventListener('pointerdown', event => {
        if (event.button === 1) {
          event.preventDefault();
          return;
        }
        if (event.button === 0 &&
            !(event.target as HTMLElement | null)?.closest('.pane-tab-close')) {
          this.handleTabPointerDown(event, pane.id, tab.sessionId, tabElement);
        }
      });
      tabElement.addEventListener('pointermove', event => this.handleTabPointerMove(event));
      tabElement.addEventListener('pointerup', event => this.handleTabPointerUp(event));
      tabElement.addEventListener('pointercancel', event => this.handleTabPointerCancel(event));
      tabElement.addEventListener('lostpointercapture', event => this.handleTabPointerCancel(event));
      tabElement.addEventListener('auxclick', event => {
        if (event.button !== 1) {
          return;
        }

        event.preventDefault();
        event.stopPropagation();
        this.closeTerminalTab(pane.id, tab.sessionId);
      });

      const activate = document.createElement('button');
      activate.type = 'button';
      activate.className = 'pane-tab-activate';
      activate.title = `${tab.title}\n${tab.processInfo}${hasBell ? '\nBell rang' : ''}`;
      if (hasBell) {
        activate.setAttribute('aria-label', `${tab.title || 'Terminal'}, bell notification`);
      }
      activate.textContent = tab.title || 'Terminal';
      activate.addEventListener('click', event => {
        // Pointer activation is handled on pointerup so a small hand movement
        // cannot turn a click into a swallowed native drag. Keep click for
        // keyboard and accessibility activation.
        if (event.detail === 0) {
          this.activateTab(pane.id, tab.sessionId);
        }
      });

      const close = document.createElement('button');
      close.type = 'button';
      close.className = 'pane-tab-close';
      close.title = 'Close tab';
      close.setAttribute('aria-label', `Close ${tab.title}`);
      close.textContent = '×';
      close.addEventListener('click', event => {
        event.stopPropagation();
        this.closeTerminalTab(pane.id, tab.sessionId);
      });
      tabElement.append(activate);
      if (hasBell) {
        const bell = document.createElement('span');
        bell.className = 'pane-tab-bell';
        bell.title = 'Bell rang';
        bell.setAttribute('aria-hidden', 'true');
        bell.innerHTML = '<svg viewBox="0 0 24 24"><path d="M12 22a2 2 0 0 0 2-2h-4a2 2 0 0 0 2 2m6-6v-5c0-3.1-1.6-5.6-4.5-6.3V4a1.5 1.5 0 0 0-3 0v.7C7.6 5.4 6 7.9 6 11v5l-2 2v1h16v-1z"/></svg>';
        tabElement.append(bell);
      }
      tabElement.append(close);
      fragment.append(tabElement);
    }

    const add = document.createElement('button');
    add.type = 'button';
    add.className = 'pane-new-tab';
    add.title = `New tab in this pane (${this.applicationShortcutLabel('T')})`;
    add.setAttribute('aria-label', 'New terminal tab in this pane');
    add.textContent = '+';
    add.addEventListener('click', () => void this.createTab(pane.id));
    fragment.append(add);
    tabStrip.replaceChildren(fragment);
  }

  private handleTabPointerDown(
    event: PointerEvent,
    paneId: string,
    sessionId: string,
    tabElement: HTMLElement
  ): void {
    const workspaceId = this.activeWorkspaceId;
    if (!workspaceId) {
      return;
    }

    if (this.tabDrag) {
      this.releaseTabPointerCapture(this.tabDrag);
      this.clearTabDragState();
    }
    this.tabDrag = {
      workspaceId,
      paneId,
      sessionId,
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      tabElement,
      dragging: false
    };
    tabElement.setPointerCapture(event.pointerId);
    event.preventDefault();
  }

  private handleTabPointerMove(event: PointerEvent): void {
    const drag = this.tabDrag;
    if (!drag || drag.pointerId !== event.pointerId || drag.tabElement !== event.currentTarget) {
      return;
    }

    if (!drag.dragging) {
      const distance = Math.hypot(event.clientX - drag.startX, event.clientY - drag.startY);
      if (distance < Workspace.TAB_DRAG_THRESHOLD || this.operationPending) {
        return;
      }
      drag.dragging = true;
      drag.tabElement.classList.add('dragging');
    }

    event.preventDefault();
    this.updateTabDropTarget(event.clientX, event.clientY);
  }

  private handleTabPointerUp(event: PointerEvent): void {
    const drag = this.tabDrag;
    if (!drag || drag.pointerId !== event.pointerId || drag.tabElement !== event.currentTarget) {
      return;
    }

    if (drag.dragging) {
      this.updateTabDropTarget(event.clientX, event.clientY);
    }

    const wasDragging = drag.dragging;
    const targetPaneId = drag.targetPaneId;
    const targetIndex = drag.targetIndex;
    const sourcePaneId = drag.paneId;
    const sessionId = drag.sessionId;
    this.releaseTabPointerCapture(drag);
    this.clearTabDragState();
    event.preventDefault();
    event.stopPropagation();

    if (wasDragging) {
      if (targetPaneId !== undefined && targetIndex !== undefined) {
        this.moveTerminalTab(sourcePaneId, targetPaneId, sessionId, targetIndex);
      }
      return;
    }

    this.activateTab(sourcePaneId, sessionId);
  }

  private handleTabPointerCancel(event: PointerEvent): void {
    const drag = this.tabDrag;
    if (!drag || drag.pointerId !== event.pointerId || drag.tabElement !== event.currentTarget) {
      return;
    }

    this.releaseTabPointerCapture(drag);
    this.clearTabDragState();
  }

  private releaseTabPointerCapture(drag: TabDragState): void {
    if (drag.tabElement.hasPointerCapture(drag.pointerId)) {
      drag.tabElement.releasePointerCapture(drag.pointerId);
    }
  }

  private updateTabDropTarget(clientX: number, clientY: number): void {
    const drag = this.tabDrag;
    if (!drag) {
      return;
    }

    this.clearTabDropPositions();
    drag.targetPaneId = undefined;
    drag.targetIndex = undefined;

    const element = document.elementFromPoint(clientX, clientY);
    const tabStrip = element?.closest<HTMLElement>('.pane-tab-strip');
    const paneId = tabStrip?.closest<HTMLElement>('.pane')?.dataset.paneId;
    if (!tabStrip || !paneId || !this.canDropTab(paneId)) {
      return;
    }

    const index = this.tabDropIndex(tabStrip, clientX);
    drag.targetPaneId = paneId;
    drag.targetIndex = index;
    this.showTabDropPosition(tabStrip, index);
  }

  private canDropTab(paneId: string): boolean {
    return Boolean(
      this.tabDrag &&
      this.tabDrag.workspaceId === this.activeWorkspaceId &&
      this.panes.has(this.tabDrag.paneId) &&
      this.panes.has(paneId)
    );
  }

  private tabDropIndex(tabStrip: HTMLElement, clientX: number): number {
    const tabs = [...tabStrip.querySelectorAll<HTMLElement>('.pane-tab')];
    const index = tabs.findIndex(tab => {
      const bounds = tab.getBoundingClientRect();
      return clientX < bounds.left + bounds.width / 2;
    });
    return index < 0 ? tabs.length : index;
  }

  private showTabDropPosition(tabStrip: HTMLElement, index: number): void {
    this.workspace.querySelectorAll('.tab-drop-before').forEach(element => {
      element.classList.remove('tab-drop-before');
    });
    const tabs = [...tabStrip.querySelectorAll<HTMLElement>('.pane-tab')];
    (tabs[index] ?? tabStrip.querySelector('.pane-new-tab'))?.classList.add('tab-drop-before');
  }

  private clearTabDropPositions(): void {
    this.workspace.querySelectorAll('.tab-drop-before').forEach(element => {
      element.classList.remove('tab-drop-before');
    });
  }

  private clearTabDragState(): void {
    this.tabDrag = undefined;
    this.workspace.querySelectorAll('.dragging, .tab-drop-before').forEach(element => {
      element.classList.remove('dragging', 'tab-drop-before');
    });
  }

  private moveTerminalTab(
    sourcePaneId: string,
    targetPaneId: string,
    sessionId: string,
    requestedTargetIndex: number
  ): void {
    const source = this.panes.get(sourcePaneId);
    const target = this.panes.get(targetPaneId);
    const sourceIndex = source?.tabs.findIndex(tab => tab.sessionId === sessionId) ?? -1;
    if (!source || !target || sourceIndex < 0) {
      return;
    }

    let targetIndex = Math.min(Math.max(requestedTargetIndex, 0), target.tabs.length);
    if (source === target && targetIndex > sourceIndex) {
      targetIndex--;
    }
    if (source === target && targetIndex === sourceIndex) {
      return;
    }

    const [tab] = source.tabs.splice(sourceIndex, 1);
    if (!tab) {
      return;
    }
    target.tabs.splice(targetIndex, 0, tab);

    if (source !== target && source.activeSessionId === sessionId && source.tabs.length > 0) {
      source.activeSessionId = source.tabs[Math.min(sourceIndex, source.tabs.length - 1)]!.sessionId;
    }
    target.activeSessionId = sessionId;

    if (source.tabs.length === 0) {
      this.panes.delete(source.id);
      this.root = this.root ? this.removePaneLeaf(this.root, source.id) ?? undefined : undefined;
    }

    this.focusedPaneId = target.id;
    this.render();
    this.focusSession(sessionId);
  }

  private refreshPaneTabs(pane: PaneState): void {
    const tabStrip = this.paneElements.get(pane.id)?.querySelector<HTMLElement>('.pane-tab-strip');
    if (tabStrip) {
      this.renderPaneTabs(pane, tabStrip);
      this.updateTabStripOverflow(tabStrip);
    }
  }

  private watchTabStripOverflow(tabStrip: HTMLElement): void {
    const update = () => this.updateTabStripOverflow(tabStrip);
    tabStrip.addEventListener('scroll', update, { passive: true });
    new ResizeObserver(update).observe(tabStrip);
    update();
  }

  private updateTabStripOverflow(tabStrip: HTMLElement): void {
    tabStrip.classList.toggle('scroll-left', tabStrip.scrollLeft > 1);
    tabStrip.classList.toggle(
      'scroll-right',
      tabStrip.scrollLeft + tabStrip.clientWidth < tabStrip.scrollWidth - 1
    );
  }

  private activateTab(paneId: string, sessionId: string): void {
    const pane = this.panes.get(paneId);
    if (!pane || !pane.tabs.some(tab => tab.sessionId === sessionId)) {
      return;
    }

    pane.activeSessionId = sessionId;
    this.focusedPaneId = pane.id;
    this.render();
    this.focusSession(sessionId);
  }

  private closeTerminalTab(paneId: string, sessionId: string, focusPane = true): void {
    void this.runExclusive(async () => {
      const match = this.findWorkspacePaneBySession(sessionId);
      const workspace = match?.workspace;
      const pane = match?.pane;
      const index = pane?.tabs.findIndex(tab => tab.sessionId === sessionId) ?? -1;
      if (!workspace || !pane || pane.id !== paneId || index < 0) {
        return;
      }

      const isActiveWorkspace = workspace.id === this.activeWorkspaceId;
      const paneWasFocused = workspace.focusedPaneId === pane.id;
      const wasActive = pane.activeSessionId === sessionId;
      pane.tabs.splice(index, 1);
      this.destroyTerminal(sessionId);
      this.bridge.closeSession(sessionId);

      if (pane.tabs.length > 0) {
        if (wasActive) {
          pane.activeSessionId = pane.tabs[Math.min(index, pane.tabs.length - 1)]!.sessionId;
        }
        if (focusPane) {
          workspace.focusedPaneId = pane.id;
        }
        if (isActiveWorkspace) {
          this.render();
          if (focusPane || paneWasFocused) {
            this.focusSession(pane.activeSessionId);
          }
        }
        this.persistResumeState();
        return;
      }

      const nextPaneId = workspace.root
        ? this.findClosestSiblingPaneId(workspace.root, pane.id)
        : undefined;
      workspace.panes.delete(pane.id);
      workspace.root = workspace.root
        ? this.removePaneLeaf(workspace.root, pane.id) ?? undefined
        : undefined;
      if (focusPane || paneWasFocused) {
        workspace.focusedPaneId = nextPaneId
          ?? (workspace.root ? this.firstPaneId(workspace.root) : undefined);
      }
      if (!workspace.root) {
        if (this.settings.workspace.lastTabClosedBehavior === 'OpenNewTab') {
          await this.createTabInWorkspace(workspace);
          return;
        }

        await this.closeWorkspaceCore(workspace.id, false);
        return;
      }

      if (isActiveWorkspace) {
        this.render();
        const focused = workspace.focusedPaneId
          ? workspace.panes.get(workspace.focusedPaneId)
          : undefined;
        if (focused && (focusPane || paneWasFocused)) {
          this.focusSession(focused.activeSessionId);
        }
      }
      this.persistResumeState();
    });
  }

  private closeFocusedTab(): void {
    const pane = this.focusedPane;
    if (!pane) {
      return;
    }

    this.closeTerminalTab(pane.id, pane.activeSessionId);
  }

  private destroyTerminal(sessionId: string): void {
    const bellTimer = this.bellFlashTimers.get(sessionId);
    if (bellTimer !== undefined) {
      window.clearTimeout(bellTimer);
      this.bellFlashTimers.delete(sessionId);
    }
    this.ringingBellSessionIds.delete(sessionId);
    this.unviewedBellSessionIds.delete(sessionId);
    this.terminals.get(sessionId)?.dispose();
    this.terminals.delete(sessionId);
    this.earlyOutput.delete(sessionId);
    this.closedSessionIds.add(sessionId);
  }

  private drainExitedSessionClosures(): void {
    if (this.operationPending) {
      return;
    }

    while (this.pendingExitedSessionIds.size > 0) {
      const sessionId = this.pendingExitedSessionIds.values().next().value as string;
      this.pendingExitedSessionIds.delete(sessionId);
      const match = this.findWorkspacePaneBySession(sessionId);
      if (match) {
        this.closeTerminalTab(match.pane.id, sessionId, false);
        return;
      }
    }
  }

  private focusSession(sessionId: string): void {
    this.terminals.get(sessionId)?.focus();
  }

  private updateFocusState(): void {
    this.paneElements.forEach((element, paneId) => {
      element.classList.toggle('focused', paneId === this.focusedPaneId);
    });

    const focusedSessionId = this.focusedPane?.activeSessionId;
    this.terminals.forEach((terminal, sessionId) => {
      terminal.setFocused(sessionId === focusedSessionId);
    });

    this.updateMobileInputToolbar();
    this.syncWindowTitle();
    this.persistResumeState();
  }

  private readonly handleMobileToolbarPointerDown = (event: PointerEvent): void => {
    if (event.button !== 0) {
      return;
    }

    const button = (event.target as Element | null)?.closest<HTMLButtonElement>(
      '.mobile-input-key'
    );
    const terminal = this.focusedTerminal;
    if (!button || !this.mobileInputToolbar.contains(button) || !terminal) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    const key = button.dataset.key;
    if (key === 'control') {
      terminal.toggleControlModifier();
      return;
    }
    if (this.isMobileToolbarKey(key)) {
      terminal.sendMobileToolbarKey(key);
    }
  };

  private readonly updateMobileInputToolbar = (): void => {
    const terminal = this.focusedTerminal;
    const viewport = window.visualViewport;
    const viewportHeight = viewport?.height ?? window.innerHeight;
    const viewportWidth = viewport?.width ?? window.innerWidth;
    if (Math.abs(viewportWidth - this.mobileViewportBaselineWidth) > 40) {
      // A substantial width change is normally an orientation change, not the
      // software keyboard. Establish a fresh unoccluded height baseline.
      this.mobileViewportBaselineWidth = viewportWidth;
      this.mobileViewportBaselineHeight = viewportHeight;
    } else {
      this.mobileViewportBaselineHeight = Math.max(
        this.mobileViewportBaselineHeight,
        viewportHeight
      );
    }

    const keyboardVisible = !viewport ||
      this.mobileViewportBaselineHeight - viewportHeight >= Workspace.MOBILE_KEYBOARD_THRESHOLD;
    const visible = Boolean(terminal) && keyboardVisible &&
      (navigator.maxTouchPoints > 0 || this.coarsePointer.matches);
    this.mobileInputToolbar.hidden = !visible;
    if (!visible || !terminal) {
      this.mobileControlButton.classList.remove('active');
      this.mobileControlButton.setAttribute('aria-pressed', 'false');
      return;
    }

    const controlActive = terminal.isControlModifierActive;
    this.mobileControlButton.classList.toggle('active', controlActive);
    this.mobileControlButton.setAttribute('aria-pressed', String(controlActive));

    const viewportBottom = viewport
      ? viewport.offsetTop + viewport.height
      : window.innerHeight;
    const keyboardInset = Math.max(0, window.innerHeight - viewportBottom);
    const viewportLeft = viewport?.offsetLeft ?? 0;
    const viewportRight = viewport
      ? Math.max(0, window.innerWidth - viewport.offsetLeft - viewport.width)
      : 0;
    this.mobileInputToolbar.style.bottom = `${Math.max(31, keyboardInset + 6)}px`;
    this.mobileInputToolbar.style.left = `${viewportLeft + 6}px`;
    this.mobileInputToolbar.style.right = `${viewportRight + 6}px`;
  };

  private isMobileToolbarKey(value: string | undefined): value is MobileToolbarKey {
    return value === 'escape' ||
      value === 'tab' ||
      value === 'arrowLeft' ||
      value === 'arrowUp' ||
      value === 'arrowDown' ||
      value === 'arrowRight';
  }

  private syncWindowTitle(): void {
    const pane = this.focusedPane;
    const title = pane?.tabs.find(tab => tab.sessionId === pane.activeSessionId)?.title.trim();
    document.title = title ? `${title} - KevinZonda Terminal` : 'KevinZonda Terminal';
  }

  private fitVisibleTerminals(immediate = false): void {
    this.activeWorkspace?.panes.forEach(pane => {
      const terminal = this.terminals.get(pane.activeSessionId);
      if (immediate) {
        terminal?.fitImmediately();
      } else {
        terminal?.scheduleFit();
      }
    });
  }

  private replacePaneLeaf(node: LayoutNode, paneId: string, replacement: LayoutNode): LayoutNode {
    if (node.type === 'pane') {
      return node.paneId === paneId ? replacement : node;
    }
    return {
      ...node,
      first: this.replacePaneLeaf(node.first, paneId, replacement),
      second: this.replacePaneLeaf(node.second, paneId, replacement)
    };
  }

  private removePaneLeaf(node: LayoutNode, paneId: string): LayoutNode | null {
    if (node.type === 'pane') {
      return node.paneId === paneId ? null : node;
    }

    const first = this.removePaneLeaf(node.first, paneId);
    const second = this.removePaneLeaf(node.second, paneId);
    if (!first) {
      return second;
    }
    if (!second) {
      return first;
    }
    return { ...node, first, second };
  }

  private collectPaneIds(node: LayoutNode): string[] {
    return node.type === 'pane'
      ? [node.paneId]
      : [...this.collectPaneIds(node.first), ...this.collectPaneIds(node.second)];
  }

  private firstPaneId(node: LayoutNode): string {
    return node.type === 'pane' ? node.paneId : this.firstPaneId(node.first);
  }

  private findClosestSiblingPaneId(node: LayoutNode, paneId: string): string | undefined {
    if (node.type === 'pane') {
      return undefined;
    }

    if (this.containsPane(node.first, paneId)) {
      return this.findClosestSiblingPaneId(node.first, paneId) ?? this.firstPaneId(node.second);
    }
    if (this.containsPane(node.second, paneId)) {
      return this.findClosestSiblingPaneId(node.second, paneId) ?? this.firstPaneId(node.first);
    }
    return undefined;
  }

  private containsPane(node: LayoutNode, paneId: string): boolean {
    return node.type === 'pane'
      ? node.paneId === paneId
      : this.containsPane(node.first, paneId) || this.containsPane(node.second, paneId);
  }

  private findWorkspacePaneBySession(
    sessionId: string
  ): { workspace: WorkspaceState; pane: PaneState } | undefined {
    for (const workspace of this.workspaces) {
      for (const pane of workspace.panes.values()) {
        if (pane.tabs.some(tab => tab.sessionId === sessionId)) {
          return { workspace, pane };
        }
      }
    }
    return undefined;
  }

  private async runExclusive(operation: () => Promise<void>): Promise<void> {
    if (this.operationPending) {
      return;
    }
    this.operationPending = true;
    try {
      await operation();
    } catch (error) {
      this.setStatus(error instanceof Error ? error.message : String(error), true);
    } finally {
      this.operationPending = false;
      this.drainExitedSessionClosures();
    }
  }

  private emptyState(): HTMLElement {
    const element = document.createElement('div');
    element.className = 'empty-state';
    element.textContent = 'No terminal panes are open.';
    return element;
  }

  private missingPane(): HTMLElement {
    const element = document.createElement('div');
    element.className = 'pane-missing';
    element.textContent = 'Terminal pane is unavailable.';
    return element;
  }

  private missingTerminal(): HTMLElement {
    const element = document.createElement('div');
    element.className = 'terminal-missing';
    element.textContent = 'Terminal session is unavailable.';
    return element;
  }

  private requireElement(id: string): HTMLElement {
    const element = document.getElementById(id);
    if (!element) {
      throw new Error(`Missing application element '#${id}'.`);
    }
    return element;
  }

  private setStatus(message: string, error = false): void {
    this.status.textContent = message;
    this.status.classList.toggle('visible', Boolean(message));
    this.status.classList.toggle('error', error);
  }

  private renderAgentUsage(status: AgentUsageStatus): void {
    this.agentUsageStatus = status;
    const openProvider = this.agentUsageTooltip.hidden ? undefined : this.activeUsageProvider;
    const restoreAnchorFocus = this.activeUsageAnchor === document.activeElement;
    const restoreRefreshFocus = document.activeElement instanceof HTMLElement &&
      document.activeElement.classList.contains('agent-usage-tooltip-refresh');
    this.hideAgentUsageTooltip();
    if (status.providers.length === 0) {
      const idle = document.createElement('span');
      idle.className = 'agent-status-idle';
      idle.textContent = 'Ready';
      this.agentStatusBar.replaceChildren(idle);
      return;
    }

    const fragment = document.createDocumentFragment();
    let tooltipTarget: { anchor: HTMLElement; provider: AgentProviderUsage } | undefined;
    for (const provider of status.providers) {
      const item = this.renderAgentProviderUsage(provider);
      fragment.append(item);
      if (provider.provider === openProvider) {
        tooltipTarget = { anchor: item, provider };
      }
    }
    this.agentStatusBar.replaceChildren(fragment);
    if (tooltipTarget) {
      this.showAgentUsageTooltip(tooltipTarget.anchor, tooltipTarget.provider);
      if (restoreAnchorFocus) {
        tooltipTarget.anchor.focus({ preventScroll: true });
      } else if (restoreRefreshFocus) {
        this.agentUsageTooltip.querySelector<HTMLButtonElement>('.agent-usage-tooltip-refresh')
          ?.focus({ preventScroll: true });
      }
    }
  }

  private renderSystemMetrics(status: SystemMetricsStatus): void {
    const cpu = document.createElement('span');
    cpu.className = 'system-metric system-metric-cpu';
    const roundedCpu = status.cpuPercent === undefined ? undefined : Math.round(status.cpuPercent);
    cpu.textContent = `CPU ${roundedCpu === undefined ? '--' : roundedCpu}%`;
    if (status.cpuPercent !== undefined) {
      cpu.classList.add(this.systemMetricSeverity(status.cpuPercent, 80, 95));
    }

    const memory = document.createElement('span');
    memory.className = 'system-metric system-metric-memory';
    const hasMemory = status.totalMemoryBytes > 0;
    const memoryPercent = hasMemory ? status.usedMemoryBytes / status.totalMemoryBytes * 100 : 0;
    memory.textContent = hasMemory
      ? `RAM ${this.formatMemoryValue(status.usedMemoryBytes)}/${this.formatMemory(status.totalMemoryBytes)}`
      : 'RAM --';
    if (hasMemory) {
      memory.classList.add(this.systemMetricSeverity(memoryPercent, 85, 95));
    }

    const details: string[] = [];
    if (status.cpuPercent !== undefined) {
      details.push(`CPU ${this.formatUsagePercent(status.cpuPercent)}%`);
    }
    if (hasMemory) {
      details.push(
        `Memory ${this.formatMemory(status.usedMemoryBytes, 2)} used ` +
        `(${this.formatUsagePercent(memoryPercent)}%)`,
        `${this.formatMemory(status.availableMemoryBytes, 2)} available of ` +
        `${this.formatMemory(status.totalMemoryBytes, 2)}`);
    }
    const updated = this.parseUsageDate(status.updatedAt);
    if (updated) {
      details.push(`Updated ${updated.toLocaleString()}`);
    }
    this.systemStatus.title = details.join('\n');
    this.systemStatus.setAttribute('aria-label', details.join('; ') || 'System resource usage unavailable');
    this.systemStatus.replaceChildren(cpu, memory);
  }

  private renderAgentProviderUsage(provider: AgentProviderUsage): HTMLElement {
    const item = document.createElement('button');
    item.type = 'button';
    item.className = `agent-usage agent-usage-${provider.state}`;
    item.setAttribute(
      'aria-label',
      `${provider.provider === 'codex' ? 'Codex' : 'Kimi'} usage details; click to refresh`);
    item.setAttribute('aria-controls', this.agentUsageTooltip.id);
    item.setAttribute('aria-expanded', 'false');
    item.setAttribute('aria-haspopup', 'dialog');
    item.addEventListener('pointerenter', () => this.scheduleUsageTooltipOpen(item, provider));
    item.addEventListener('pointerleave', () => this.scheduleUsageTooltipClose());
    item.addEventListener('focus', () => this.showAgentUsageTooltip(item, provider));
    item.addEventListener('blur', () => this.scheduleUsageTooltipClose());
    item.addEventListener('click', () => this.requestAgentUsageRefresh(provider.provider, provider.refreshing));

    const name = document.createElement('span');
    name.className = 'agent-usage-name';
    name.textContent = provider.provider === 'codex' ? 'Codex' : 'Kimi';
    item.append(name);

    if (provider.state === 'loading') {
      item.append(this.agentUsageText('Loading usage…', 'agent-usage-message'));
    } else if (provider.windows.length === 0) {
      item.append(this.agentUsageText('Usage unavailable', 'agent-usage-message'));
    } else {
      for (const window of provider.windows) {
        const value = Math.round(this.displayedUsagePercent(window.usedPercent));
        const suffix = this.settings.indicators.showRemainingUsage ? '% left' : '%';
        const usage = this.agentUsageText(`${window.label} ${value}${suffix}`, 'agent-usage-window');
        usage.classList.add(this.usageSeverity(window.usedPercent));
        item.append(usage);
      }
    }

    if (provider.state === 'stale') {
      item.append(this.agentUsageText('stale', 'agent-usage-stale-label'));
    }

    return item;
  }

  private scheduleUsageTooltipOpen(anchor: HTMLElement, provider: AgentProviderUsage): void {
    this.cancelUsageTooltipClose();
    if (this.usageTooltipOpenTimer !== undefined) {
      window.clearTimeout(this.usageTooltipOpenTimer);
    }
    const delay = this.agentUsageTooltip.hidden ? Workspace.USAGE_TOOLTIP_OPEN_DELAY : 0;
    this.usageTooltipOpenTimer = window.setTimeout(() => {
      this.usageTooltipOpenTimer = undefined;
      if (anchor.isConnected) {
        this.showAgentUsageTooltip(anchor, provider);
      }
    }, delay);
  }

  private showAgentUsageTooltip(anchor: HTMLElement, provider: AgentProviderUsage): void {
    if (this.usageTooltipOpenTimer !== undefined) {
      window.clearTimeout(this.usageTooltipOpenTimer);
      this.usageTooltipOpenTimer = undefined;
    }
    this.cancelUsageTooltipClose();
    this.activeUsageAnchor?.setAttribute('aria-expanded', 'false');
    this.activeUsageAnchor = anchor;
    this.activeUsageProvider = provider.provider;
    anchor.setAttribute('aria-expanded', 'true');
    this.agentUsageTooltip.setAttribute(
      'aria-label',
      `${provider.provider === 'codex' ? 'Codex' : 'Kimi'} usage details`);
    this.renderAgentUsageTooltip(provider);
    this.agentUsageTooltip.hidden = false;
    this.positionAgentUsageTooltip(anchor);
  }

  private renderAgentUsageTooltip(provider: AgentProviderUsage): void {
    const content = document.createDocumentFragment();
    const header = document.createElement('div');
    header.className = 'agent-usage-tooltip-header';

    const heading = document.createElement('div');
    heading.className = 'agent-usage-tooltip-heading';
    heading.textContent = provider.provider === 'codex' ? 'Codex usage' : 'Kimi usage';
    header.append(heading);

    const badges = document.createElement('div');
    badges.className = 'agent-usage-tooltip-badges';
    if (provider.plan) {
      badges.append(this.agentUsageText(provider.plan, 'agent-usage-tooltip-badge'));
    }
    if (provider.refreshing) {
      badges.append(this.agentUsageText('Refreshing', 'agent-usage-tooltip-badge refreshing'));
    } else if (provider.state === 'stale') {
      badges.append(this.agentUsageText('Stale', 'agent-usage-tooltip-badge stale'));
    } else if (provider.state === 'error') {
      badges.append(this.agentUsageText('Unavailable', 'agent-usage-tooltip-badge error'));
    }
    const actions = document.createElement('div');
    actions.className = 'agent-usage-tooltip-actions';
    actions.append(badges);
    const refresh = document.createElement('button');
    refresh.type = 'button';
    refresh.className = 'agent-usage-tooltip-refresh';
    refresh.textContent = '↻';
    refresh.title = provider.refreshing ? 'Refreshing usage' : 'Refresh usage';
    refresh.setAttribute(
      'aria-label',
      `Refresh ${provider.provider === 'codex' ? 'Codex' : 'Kimi'} usage`);
    refresh.setAttribute('aria-disabled', String(provider.refreshing));
    refresh.addEventListener('focus', () => this.cancelUsageTooltipClose());
    refresh.addEventListener('blur', () => this.scheduleUsageTooltipClose());
    refresh.addEventListener('click', event => {
      event.stopPropagation();
      this.requestAgentUsageRefresh(provider.provider, provider.refreshing);
    });
    actions.append(refresh);
    header.append(actions);
    content.append(header);

    if (provider.source) {
      const source = document.createElement('div');
      source.className = 'agent-usage-tooltip-source';
      source.textContent = `Source: ${provider.source}`;
      content.append(source);
    }

    if (provider.windows.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'agent-usage-tooltip-empty';
      empty.textContent = provider.state === 'loading' ? 'Loading usage…' : 'Usage details unavailable';
      content.append(empty);
    } else {
      const meters = document.createElement('div');
      meters.className = 'agent-usage-tooltip-meters';
      for (const usageWindow of provider.windows) {
        const amount = usageWindow.used !== undefined && usageWindow.limit !== undefined
          ? this.formatUsageAmountPair(usageWindow.used, usageWindow.limit)
          : undefined;
        this.appendUsageMeter(
          meters,
          usageWindow.name,
          usageWindow.label,
          usageWindow.usedPercent,
          usageWindow.resetsAt,
          amount);
      }
      content.append(meters);
    }

    if (provider.credits || provider.budget) {
      const extras = document.createElement('div');
      extras.className = 'agent-usage-tooltip-extras';
      if (provider.credits) {
        const credits = document.createElement('div');
        credits.className = 'agent-usage-tooltip-extra';
        credits.append(this.agentUsageText('Credits', 'agent-usage-tooltip-extra-name'));
        const value = provider.credits.isUnlimited
          ? 'Unlimited'
          : provider.credits.remaining === undefined
            ? 'Unknown'
            : provider.credits.total === undefined
              ? `${this.formatUsageAmount(provider.credits.remaining, provider.credits.currency)} remaining`
              : `${this.formatUsageAmount(provider.credits.remaining, provider.credits.currency)} / ` +
                `${this.formatUsageAmount(provider.credits.total, provider.credits.currency)} remaining`;
        credits.append(this.agentUsageText(value, 'agent-usage-tooltip-extra-value'));
        extras.append(credits);
      }
      if (provider.budget) {
        if (provider.budget.isUnlimited) {
          const budget = document.createElement('div');
          budget.className = 'agent-usage-tooltip-extra';
          budget.append(this.agentUsageText(provider.budget.name, 'agent-usage-tooltip-extra-name'));
          budget.append(this.agentUsageText(
            `${this.formatUsageAmount(provider.budget.used, provider.budget.currency)} used · Unlimited`,
            'agent-usage-tooltip-extra-value'));
          extras.append(budget);
        } else {
          this.appendUsageMeter(
            extras,
            provider.budget.name,
            'Budget',
            100 - provider.budget.remainingPercent,
            provider.budget.resetsAt,
            this.formatUsageAmountPair(
              provider.budget.used,
              provider.budget.limit,
              provider.budget.currency));
        }
      }
      content.append(extras);
    }

    if (provider.error) {
      const error = document.createElement('div');
      error.className = 'agent-usage-tooltip-error';
      error.textContent = provider.error;
      content.append(error);
    }

    const timing = document.createElement('div');
    timing.className = 'agent-usage-tooltip-timing';
    const updated = this.parseUsageDate(provider.updatedAt);
    if (updated) {
      timing.append(this.agentUsageText(
        `Updated ${this.formatPastTime(updated)} · ${updated.toLocaleString()}`,
        'agent-usage-tooltip-time'));
    }
    const nextRefresh = this.parseUsageDate(provider.nextRefreshAt);
    if (provider.refreshing) {
      timing.append(this.agentUsageText('Refreshing now', 'agent-usage-tooltip-time'));
    } else if (nextRefresh) {
      timing.append(this.agentUsageText(
        `Next refresh ${this.formatFutureTime(nextRefresh)}`,
        'agent-usage-tooltip-time'));
    }
    if (timing.childElementCount > 0) {
      content.append(timing);
    }

    const scrollContainer = document.createElement('div');
    scrollContainer.className = 'agent-usage-tooltip-content';
    scrollContainer.append(content);
    this.agentUsageTooltip.replaceChildren(scrollContainer);
  }

  private appendUsageMeter(
    parent: HTMLElement,
    name: string,
    label: string,
    usedPercent: number,
    resetsAt?: string,
    amount?: string
  ): void {
    const displayedPercent = this.displayedUsagePercent(usedPercent);
    const meter = document.createElement('section');
    meter.className = 'agent-usage-tooltip-meter';

    const heading = document.createElement('div');
    heading.className = 'agent-usage-tooltip-meter-heading';
    const title = document.createElement('div');
    title.className = 'agent-usage-tooltip-meter-title';
    title.append(this.agentUsageText(name, 'agent-usage-tooltip-meter-name'));
    if (label !== name) {
      title.append(this.agentUsageText(label, 'agent-usage-tooltip-meter-label'));
    }
    heading.append(title);
    heading.append(this.agentUsageText(
      this.formatDisplayedUsagePercent(usedPercent),
      `agent-usage-tooltip-percent ${this.usageSeverity(usedPercent)}`));
    meter.append(heading);

    const track = document.createElement('div');
    track.className = 'agent-usage-tooltip-track';
    track.role = 'progressbar';
    track.setAttribute(
      'aria-label',
      `${name} ${this.settings.indicators.showRemainingUsage ? 'remaining' : 'used'}`);
    track.setAttribute('aria-valuemin', '0');
    track.setAttribute('aria-valuemax', '100');
    track.setAttribute('aria-valuenow', String(displayedPercent));
    track.setAttribute('aria-valuetext', this.formatDisplayedUsagePercent(usedPercent));
    const fill = document.createElement('div');
    fill.className = `agent-usage-tooltip-fill ${this.usageSeverity(usedPercent)}`;
    fill.style.width = `${displayedPercent}%`;
    track.append(fill);
    meter.append(track);

    const details = document.createElement('div');
    details.className = 'agent-usage-tooltip-meter-details';
    if (amount) {
      details.append(this.agentUsageText(amount, 'agent-usage-tooltip-amount'));
    }
    const reset = this.parseUsageDate(resetsAt);
    if (reset) {
      details.append(this.agentUsageText(
        `Resets ${this.formatFutureTime(reset)} · ${reset.toLocaleString()}`,
        'agent-usage-tooltip-reset'));
    }
    if (details.childElementCount > 0) {
      meter.append(details);
    }
    parent.append(meter);
  }

  private positionAgentUsageTooltip(anchor: HTMLElement): void {
    const margin = 8;
    const gap = 9;
    const anchorRect = anchor.getBoundingClientRect();
    const tooltipRect = this.agentUsageTooltip.getBoundingClientRect();
    const anchorCenter = anchorRect.left + anchorRect.width / 2;
    const left = Math.min(
      window.innerWidth - tooltipRect.width - margin,
      Math.max(margin, anchorCenter - tooltipRect.width / 2));
    const top = Math.max(margin, anchorRect.top - tooltipRect.height - gap);
    const arrowLeft = Math.min(tooltipRect.width - 18, Math.max(18, anchorCenter - left));
    this.agentUsageTooltip.style.left = `${left}px`;
    this.agentUsageTooltip.style.top = `${top}px`;
    this.agentUsageTooltip.style.setProperty('--agent-usage-arrow-left', `${arrowLeft}px`);
  }

  private scheduleUsageTooltipClose(): void {
    if (this.usageTooltipCloseTimer !== undefined) {
      return;
    }
    this.usageTooltipCloseTimer = window.setTimeout(() => {
      this.usageTooltipCloseTimer = undefined;
      this.hideAgentUsageTooltip();
    }, Workspace.USAGE_TOOLTIP_CLOSE_DELAY);
  }

  private cancelUsageTooltipClose(): void {
    if (this.usageTooltipCloseTimer !== undefined) {
      window.clearTimeout(this.usageTooltipCloseTimer);
      this.usageTooltipCloseTimer = undefined;
    }
  }

  private hideAgentUsageTooltip(): void {
    if (this.usageTooltipOpenTimer !== undefined) {
      window.clearTimeout(this.usageTooltipOpenTimer);
      this.usageTooltipOpenTimer = undefined;
    }
    this.cancelUsageTooltipClose();
    this.activeUsageAnchor?.setAttribute('aria-expanded', 'false');
    this.activeUsageAnchor = undefined;
    this.activeUsageProvider = undefined;
    this.agentUsageTooltip.hidden = true;
  }

  private requestAgentUsageRefresh(provider: 'codex' | 'kimi', refreshing: boolean): void {
    if (refreshing) {
      return;
    }
    void this.bridge.refreshAgentUsage(provider)
      .catch(error => this.setStatus(`Unable to refresh usage: ${String(error)}`, true));
  }

  private parseUsageDate(value?: string): Date | undefined {
    if (!value) {
      return undefined;
    }
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? undefined : date;
  }

  private formatUsagePercent(value: number): string {
    return value.toLocaleString(undefined, { maximumFractionDigits: 1 });
  }

  private formatUsageAmount(value: number, currency?: string): string {
    if (currency) {
      try {
        return value.toLocaleString(undefined, {
          style: 'currency',
          currency,
          maximumFractionDigits: 2
        });
      } catch {
        return `${value.toLocaleString(undefined, { maximumFractionDigits: 2 })} ${currency}`;
      }
    }
    return value.toLocaleString(undefined, { maximumFractionDigits: 2 });
  }

  private displayedUsagePercent(usedPercent: number): number {
    return this.settings.indicators.showRemainingUsage ? 100 - usedPercent : usedPercent;
  }

  private formatDisplayedUsagePercent(usedPercent: number): string {
    const suffix = this.settings.indicators.showRemainingUsage ? ' remaining' : '';
    return `${this.formatUsagePercent(this.displayedUsagePercent(usedPercent))}%${suffix}`;
  }

  private formatUsageAmountPair(used: number, limit: number, currency?: string): string {
    if (this.settings.indicators.showRemainingUsage) {
      const remaining = Math.max(0, limit - used);
      return `${this.formatUsageAmount(remaining, currency)} / ${this.formatUsageAmount(limit, currency)} remaining`;
    }
    return `${this.formatUsageAmount(used, currency)} / ${this.formatUsageAmount(limit, currency)}`;
  }

  private formatMemory(bytes: number, fractionDigits = 1): string {
    return `${this.formatMemoryValue(bytes, fractionDigits)} GB`;
  }

  private formatMemoryValue(bytes: number, fractionDigits = 1): string {
    const gibibytes = bytes / 1024 ** 3;
    return gibibytes.toLocaleString(undefined, {
      minimumFractionDigits: fractionDigits,
      maximumFractionDigits: fractionDigits
    });
  }

  private systemMetricSeverity(
    value: number,
    warningThreshold: number,
    criticalThreshold: number
  ): 'normal' | 'warning' | 'critical' {
    return value >= criticalThreshold ? 'critical' : value >= warningThreshold ? 'warning' : 'normal';
  }

  private usageSeverity(value: number): 'normal' | 'warning' | 'critical' {
    return value >= 90 ? 'critical' : value >= 70 ? 'warning' : 'normal';
  }

  private formatPastTime(date: Date): string {
    const seconds = Math.max(0, Math.floor((Date.now() - date.getTime()) / 1000));
    if (seconds < 45) {
      return 'just now';
    }
    if (seconds < 3600) {
      return `${Math.floor(seconds / 60)}m ago`;
    }
    if (seconds < 86400) {
      return `${Math.floor(seconds / 3600)}h ago`;
    }
    return `${Math.floor(seconds / 86400)}d ago`;
  }

  private formatFutureTime(date: Date): string {
    const seconds = Math.max(0, Math.floor((date.getTime() - Date.now()) / 1000));
    if (seconds < 60) {
      return seconds === 0 ? 'now' : 'in less than a minute';
    }
    if (seconds < 3600) {
      return `in ${Math.ceil(seconds / 60)}m`;
    }
    if (seconds < 86400) {
      const hours = Math.floor(seconds / 3600);
      const minutes = Math.floor(seconds % 3600 / 60);
      return `in ${hours}h${minutes > 0 ? ` ${minutes}m` : ''}`;
    }
    const days = Math.floor(seconds / 86400);
    const hours = Math.floor(seconds % 86400 / 3600);
    return `in ${days}d${hours > 0 ? ` ${hours}h` : ''}`;
  }

  private agentUsageText(text: string, className: string): HTMLSpanElement {
    const element = document.createElement('span');
    element.className = className;
    element.textContent = text;
    return element;
  }

  private payloadString(event: BridgeEvent, name: string): string {
    const value = event.payload[name];
    return typeof value === 'string' ? value : '';
  }

  private payloadNumber(event: BridgeEvent, name: string): number {
    const value = event.payload[name];
    return typeof value === 'number' ? value : 0;
  }

  private get focusedPane(): PaneState | undefined {
    return this.focusedPaneId ? this.panes.get(this.focusedPaneId) : undefined;
  }

  private get focusedTerminal(): TerminalController | undefined {
    const pane = this.focusedPane;
    return pane ? this.terminals.get(pane.activeSessionId) : undefined;
  }

  private get activeWorkspace(): WorkspaceState | undefined {
    return this.activeWorkspaceId
      ? this.workspaces.find(workspace => workspace.id === this.activeWorkspaceId)
      : undefined;
  }

  private get panes(): Map<string, PaneState> {
    const workspace = this.activeWorkspace;
    if (!workspace) {
      throw new Error('No active workspace is available.');
    }
    return workspace.panes;
  }

  private get root(): LayoutNode | undefined {
    return this.activeWorkspace?.root;
  }

  private set root(root: LayoutNode | undefined) {
    const workspace = this.activeWorkspace;
    if (!workspace) {
      throw new Error('No active workspace is available.');
    }
    workspace.root = root;
  }

  private get focusedPaneId(): string | undefined {
    return this.activeWorkspace?.focusedPaneId;
  }

  private set focusedPaneId(paneId: string | undefined) {
    const workspace = this.activeWorkspace;
    if (!workspace) {
      throw new Error('No active workspace is available.');
    }
    workspace.focusedPaneId = paneId;
  }
}
