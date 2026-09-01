import { FitAddon } from '@xterm/addon-fit';
import type { LigaturesAddon } from '@xterm/addon-ligatures';
import { SerializeAddon } from '@xterm/addon-serialize';
import { WebLinksAddon } from '@xterm/addon-web-links';
import { WebglAddon } from '@xterm/addon-webgl';
import { Terminal } from '@xterm/xterm';
import type { IDisposable } from '@xterm/xterm';
import type { CursorSettings, FontSettings, NativeBridge, SessionCreated, ThemeSettings } from './bridge';
import type { TerminalCheckpoint } from './resume-store';
import { resolveTerminalTheme } from './themes';

export interface TerminalCallbacks {
  onControlModifierChanged(sessionId: string, active: boolean): void;
  onFocus(sessionId: string): void;
  onFontSizeChanged(sessionId: string, fontSize: number): void;
  onTitle(sessionId: string, title: string): void;
  onTerminalCheckpoint(checkpoint: TerminalCheckpoint): Promise<void>;
}

export type MobileToolbarKey =
  | 'escape'
  | 'tab'
  | 'arrowLeft'
  | 'arrowUp'
  | 'arrowDown'
  | 'arrowRight';

interface WebglAddonInternals {
  _renderer?: {
    _gl?: WebGL2RenderingContext;
  };
}

export class TerminalController {
  private static readonly MIN_FONT_SIZE = 8;
  private static readonly MAX_FONT_SIZE = 72;
  // Chromium reports pixel wheel deltas; 40px per line matches the normal
  // buffer scroll feel (a typical 120px notch scrolls 3 lines).
  private static readonly ALT_SCROLL_PIXELS_PER_LINE = 40;
  // Grace period before a hidden pane's WebGL context is reclaimed.
  private static readonly WEBGL_RECLAIM_DELAY_MS = 30_000;

  public readonly sessionId: string;
  public readonly element: HTMLDivElement;

  private readonly bridge: NativeBridge;
  private readonly callbacks: TerminalCallbacks;
  private readonly terminal: Terminal;
  private readonly fitAddon = new FitAddon();
  private readonly serializeAddon = new SerializeAddon();
  private readonly host = document.createElement('div');
  private readonly resizeObserver: ResizeObserver;
  private readonly disposables: IDisposable[] = [];
  // Writes go straight to the parser even before open(): query answers (DA,
  // DSR) must return while the app is still waiting, or they land at the shell
  // prompt as garbage when the answer finally arrives. xterm.js parses fine
  // headless; only OSC color reports are skipped pre-open (its theme service
  // does not exist yet), which apps handle with a timeout fallback.
  private webglAddon?: WebglAddon;
  private ligaturesAddon?: LigaturesAddon;
  private ligaturesEnabled: boolean;
  private ligaturesRevision = 0;
  private webglFailed = false;
  private webglReclaimTimer?: number;
  private opened = false;
  private exited = false;
  private fitTimer?: number;
  private lastCols = 0;
  private lastRows = 0;
  private altScrollRemainder = 0;
  private altScrollWasAltBuffer = false;
  private controlModifierActive = false;
  private readonly pinchTouches = new Map<number, { x: number; y: number }>();
  private pinchStartDistance?: number;
  private pinchStartFontSize = 0;
  private suppressXtermTouchGestures = false;
  private touchGestureResetTimer?: number;
  private checkpointTimer?: number;
  private checkpointInFlight?: Promise<void>;
  private lastRenderedOutputSeq = 0;
  private lastCheckpointOutputSeq = 0;
  private suppressSessionResize = false;
  private disposed = false;

  public constructor(
    session: SessionCreated,
    bridge: NativeBridge,
    callbacks: TerminalCallbacks,
    font: FontSettings,
    theme: ThemeSettings,
    cursor: CursorSettings
  ) {
    this.sessionId = session.sessionId;
    this.bridge = bridge;
    this.callbacks = callbacks;
    this.ligaturesEnabled = font.enableLigatures;
    this.element = document.createElement('div');
    this.element.className = 'terminal-pane';
    this.element.dataset.sessionId = session.sessionId;

    this.host.className = 'terminal-host';
    this.element.append(this.host);

    this.terminal = new Terminal({
      allowProposedApi: false,
      convertEol: false,
      cursorBlink: cursor.blink,
      cursorStyle: cursor.shape,
      fontFamily: font.family,
      fontSize: font.size,
      lineHeight: font.lineHeight,
      linkHandler: {
        activate: (_event, uri) => this.bridge.openExternal(uri)
      },
      rightClickSelectsWord: false,
      scrollback: 5000,
      theme: resolveTerminalTheme(theme.name),
      // We always sit behind ConPTY (OpenConsole passthrough), so adopt its
      // buffer semantics on resize instead of vanilla xterm behavior:
      // growing rows pads empty lines at the bottom of the viewport rather
      // than pulling scrollback back in, and a buildNumber below 21376
      // disables xterm's own reflow so the screen always follows the pty's
      // repaint instead of a second, diverging reflow.
      windowsPty: { backend: 'conpty', buildNumber: 19045 }
    });
    this.terminal.loadAddon(this.fitAddon);
    this.terminal.loadAddon(this.serializeAddon);
    this.terminal.loadAddon(new WebLinksAddon((_event, uri) => this.bridge.openExternal(uri)));

    this.disposables.push(
      this.terminal.onData(data => this.handleTerminalData(data)),
      this.terminal.onBinary(data => this.bridge.sendBinaryInput(this.sessionId, data)),
      this.terminal.onTitleChange(title => this.callbacks.onTitle(this.sessionId, title)),
      this.terminal.onResize(size => {
        if (size.cols === this.lastCols && size.rows === this.lastRows) {
          return;
        }

        this.lastCols = size.cols;
        this.lastRows = size.rows;
        if (this.suppressSessionResize) {
          return;
        }
        this.bridge.resize(this.sessionId, size.cols, size.rows);
      })
    );

    this.element.addEventListener('pointerdown', () => this.focus());
    this.element.addEventListener('focusin', () => this.callbacks.onFocus(this.sessionId));
    this.host.addEventListener('contextmenu', this.handleContextMenu, { capture: true });
    this.host.addEventListener('wheel', this.handleWheel, { capture: true, passive: false });
    this.host.addEventListener('touchstart', this.handleTouchStart, { capture: true, passive: false });
    this.host.addEventListener('touchmove', this.handleTouchMove, { capture: true, passive: false });
    this.host.addEventListener('touchend', this.handleTouchEnd, { capture: true, passive: false });
    this.host.addEventListener('touchcancel', this.handleTouchEnd, { capture: true, passive: false });
    this.host.addEventListener('-xterm-gesturestart', this.handleXtermTouchGesture, { capture: true });
    this.host.addEventListener('-xterm-gesturechange', this.handleXtermTouchGesture, { capture: true });
    this.host.addEventListener('-xterm-gesturetap', this.handleXtermTouchGesture, { capture: true });
    this.host.addEventListener('-xterm-gesturecontextmenu', this.handleXtermTouchGesture, { capture: true });
    this.resizeObserver = new ResizeObserver(() => this.scheduleFit());
    this.resizeObserver.observe(this.element);
  }

  public mount(): void {
    if (!this.element.isConnected) {
      return;
    }

    if (!this.opened) {
      this.terminal.open(this.host);
      this.opened = true;
      void this.syncLigaturesAddon();
      if (!this.fitNow()) {
        this.scheduleFit();
      }
      this.enableWebgl();
      return;
    }

    this.scheduleFit();
  }

  // Keeps the GPU renderer aligned with visibility: panes that stay hidden for
  // a while release their WebGL context (xterm.js seamlessly uses its built-in
  // renderer), visible panes get one back. Reclaiming is deferred so quick tab
  // switches don't churn context creation.
  public setVisible(visible: boolean): void {
    if (!this.opened) {
      return;
    }

    if (visible) {
      this.cancelWebglReclaim();
      if (!this.webglAddon && !this.webglFailed) {
        this.enableWebgl();
      }
      return;
    }

    if (this.webglAddon && this.webglReclaimTimer === undefined) {
      this.webglReclaimTimer = window.setTimeout(() => {
        this.webglReclaimTimer = undefined;
        this.disposeWebgl();
      }, TerminalController.WEBGL_RECLAIM_DELAY_MS);
    }
  }

  private cancelWebglReclaim(): void {
    if (this.webglReclaimTimer !== undefined) {
      window.clearTimeout(this.webglReclaimTimer);
      this.webglReclaimTimer = undefined;
    }
  }

  private disposeWebgl(): void {
    const addon = this.webglAddon;
    if (!addon) {
      return;
    }

    this.webglAddon = undefined;
    this.releaseWebglAddon(addon);
    this.element.classList.remove('renderer-webgl');
    this.element.classList.add('renderer-fallback');
  }

  private releaseWebglAddon(addon: WebglAddon): void {
    // addon-webgl currently removes its canvas on dispose without releasing
    // the underlying context. Keep this localized compatibility shim until
    // https://github.com/xtermjs/xterm.js/pull/6069 ships in our pinned build.
    const context = (addon as unknown as WebglAddonInternals)._renderer?._gl;
    addon.dispose();
    context?.getExtension('WEBGL_lose_context')?.loseContext();
  }

  public write(data: string, callback?: () => void): void {
    this.terminal.write(data, callback);
  }

  public writeOutput(data: string, outputSeq: number): void {
    this.terminal.write(data, () => {
      if (!Number.isSafeInteger(outputSeq) || outputSeq <= 0 || this.disposed) {
        return;
      }
      this.lastRenderedOutputSeq = Math.max(this.lastRenderedOutputSeq, outputSeq);
      this.bridge.acknowledgeOutput(this.sessionId, outputSeq);
      this.scheduleCheckpoint();
    });
  }

  public async restoreCheckpoint(checkpoint: TerminalCheckpoint | undefined): Promise<void> {
    if (!checkpoint || checkpoint.sessionId !== this.sessionId || checkpoint.outputSeq <= 0) {
      return;
    }
    this.suppressSessionResize = true;
    try {
      if (checkpoint.cols > 0 && checkpoint.rows > 0) {
        this.terminal.resize(checkpoint.cols, checkpoint.rows);
      }
      await new Promise<void>(resolve => this.terminal.write(checkpoint.data, resolve));
    } finally {
      this.suppressSessionResize = false;
    }
    this.lastRenderedOutputSeq = checkpoint.outputSeq;
    this.lastCheckpointOutputSeq = checkpoint.outputSeq;
  }

  public checkpointNow(): Promise<void> {
    if (this.checkpointTimer !== undefined) {
      window.clearTimeout(this.checkpointTimer);
      this.checkpointTimer = undefined;
    }
    if (!this.checkpointInFlight && !this.disposed &&
        this.lastRenderedOutputSeq > this.lastCheckpointOutputSeq) {
      this.checkpointInFlight = this.runCheckpointLoop().finally(() => {
        this.checkpointInFlight = undefined;
        if (this.lastRenderedOutputSeq > this.lastCheckpointOutputSeq) {
          this.scheduleCheckpoint();
        }
      });
    }
    return this.checkpointInFlight ?? Promise.resolve();
  }

  private async runCheckpointLoop(): Promise<void> {
    while (!this.disposed && this.lastRenderedOutputSeq > this.lastCheckpointOutputSeq) {
      const outputSeq = this.lastRenderedOutputSeq;
      const checkpoint: TerminalCheckpoint = {
        sessionId: this.sessionId,
        outputSeq,
        data: this.serializeAddon.serialize(),
        cols: this.terminal.cols,
        rows: this.terminal.rows,
        updatedAt: new Date().toISOString()
      };
      await this.callbacks.onTerminalCheckpoint(checkpoint);
      this.lastCheckpointOutputSeq = Math.max(this.lastCheckpointOutputSeq, outputSeq);
    }
  }

  private scheduleCheckpoint(): void {
    if (this.checkpointTimer !== undefined || this.disposed) {
      return;
    }
    this.checkpointTimer = window.setTimeout(() => {
      this.checkpointTimer = undefined;
      void this.checkpointNow().catch(error => {
        console.error('Unable to checkpoint terminal state.', error);
      });
    }, 1500);
  }

  public markExited(exitCode: number, failure?: string): void {
    if (this.exited) {
      return;
    }

    this.exited = true;
    this.element.classList.add('exited');
    const message = failure ?? `process exited with code ${exitCode}`;
    const color = failure ? '\x1b[91m' : '\x1b[90m';
    this.write(`\r\n${color}[${message}]\x1b[0m\r\n`);
  }

  public focus(): void {
    this.callbacks.onFocus(this.sessionId);
    this.terminal.focus();
  }

  public setFocused(focused: boolean): void {
    this.element.classList.toggle('focused', focused);
  }

  public get isControlModifierActive(): boolean {
    return this.controlModifierActive;
  }

  public toggleControlModifier(): void {
    this.setControlModifier(!this.controlModifierActive);
    this.focus();
  }

  public sendMobileToolbarKey(key: MobileToolbarKey): void {
    let data: string;
    switch (key) {
      case 'escape':
        data = '\x1b';
        break;
      case 'tab':
        data = '\t';
        break;
      default: {
        const final = key === 'arrowLeft'
          ? 'D'
          : key === 'arrowUp'
            ? 'A'
            : key === 'arrowDown'
              ? 'B'
              : 'C';
        data = this.controlModifierActive
          ? `\x1b[1;5${final}`
          : this.terminal.modes.applicationCursorKeysMode
            ? `\x1bO${final}`
            : `\x1b[${final}`;
        break;
      }
    }

    this.setControlModifier(false);
    this.bridge.sendInput(this.sessionId, data);
    this.focus();
  }

  public get cols(): number {
    return this.terminal.cols;
  }

  public get rows(): number {
    return this.terminal.rows;
  }

  public applyFontSettings(font: FontSettings): void {
    this.terminal.options.fontFamily = font.family;
    this.terminal.options.fontSize = font.size;
    this.terminal.options.lineHeight = font.lineHeight;
    this.ligaturesEnabled = font.enableLigatures;
    void this.syncLigaturesAddon();
    if (this.opened) {
      this.scheduleFit();
    }
  }

  public applyThemeSettings(theme: ThemeSettings): void {
    this.terminal.options.theme = resolveTerminalTheme(theme.name);
  }

  public applyCursorSettings(cursor: CursorSettings): void {
    this.terminal.options.cursorStyle = cursor.shape;
    this.terminal.options.cursorBlink = cursor.blink;
  }

  public scheduleFit(): void {
    if (this.fitTimer !== undefined) {
      window.clearTimeout(this.fitTimer);
    }

    this.fitTimer = window.setTimeout(() => {
      this.fitNow();
    }, 40);
  }

  public fitImmediately(): void {
    this.fitNow();
  }

  public copySelection(): boolean {
    if (!this.terminal.hasSelection()) {
      return false;
    }

    this.bridge.writeClipboard(this.terminal.getSelection());
    return true;
  }

  public paste(text: string): void {
    if (text) {
      this.terminal.paste(text);
    }
  }

  public dispose(): void {
    this.disposed = true;
    this.ligaturesRevision++;
    if (this.fitTimer !== undefined) {
      window.clearTimeout(this.fitTimer);
    }
    if (this.touchGestureResetTimer !== undefined) {
      window.clearTimeout(this.touchGestureResetTimer);
    }
    if (this.checkpointTimer !== undefined) {
      window.clearTimeout(this.checkpointTimer);
    }
    this.cancelWebglReclaim();
    this.host.removeEventListener('wheel', this.handleWheel, { capture: true });
    this.host.removeEventListener('touchstart', this.handleTouchStart, { capture: true });
    this.host.removeEventListener('touchmove', this.handleTouchMove, { capture: true });
    this.host.removeEventListener('touchend', this.handleTouchEnd, { capture: true });
    this.host.removeEventListener('touchcancel', this.handleTouchEnd, { capture: true });
    this.host.removeEventListener('-xterm-gesturestart', this.handleXtermTouchGesture, { capture: true });
    this.host.removeEventListener('-xterm-gesturechange', this.handleXtermTouchGesture, { capture: true });
    this.host.removeEventListener('-xterm-gesturetap', this.handleXtermTouchGesture, { capture: true });
    this.host.removeEventListener('-xterm-gesturecontextmenu', this.handleXtermTouchGesture, { capture: true });
    this.resizeObserver.disconnect();
    this.disposables.forEach(disposable => disposable.dispose());
    this.disposeWebgl();
    this.terminal.dispose();
    this.element.remove();
  }

  private enableWebgl(): void {
    let addon: WebglAddon | undefined;
    try {
      addon = new WebglAddon();
      const activeAddon = addon;
      activeAddon.onContextLoss(() => {
        if (this.webglAddon !== activeAddon) {
          activeAddon.dispose();
          return;
        }

        this.webglAddon = undefined;
        this.webglFailed = true;
        activeAddon.dispose();
        this.element.classList.remove('renderer-webgl');
        this.element.classList.add('renderer-fallback');
      });
      this.terminal.loadAddon(activeAddon);
      this.webglAddon = activeAddon;
      this.element.classList.remove('renderer-fallback');
      this.element.classList.add('renderer-webgl');
    } catch {
      if (addon) {
        this.releaseWebglAddon(addon);
      }
      this.webglFailed = true;
      this.element.classList.remove('renderer-webgl');
      this.element.classList.add('renderer-fallback');
    }
  }

  private async syncLigaturesAddon(): Promise<void> {
    const revision = ++this.ligaturesRevision;
    if (!this.opened || this.disposed || this.ligaturesEnabled === Boolean(this.ligaturesAddon)) {
      return;
    }

    let addon: LigaturesAddon | undefined;
    if (this.ligaturesEnabled) {
      try {
        const module = await import('@xterm/addon-ligatures');
        if (revision !== this.ligaturesRevision || this.disposed || !this.ligaturesEnabled) {
          return;
        }
        addon = new module.LigaturesAddon();
      } catch (error) {
        console.error('Could not load the xterm ligatures addon.', error);
        return;
      }
    }

    // WebGL captures font-feature-settings when its texture atlas is created,
    // so rebuild it after changing ligature support.
    const restoreWebgl = this.webglAddon !== undefined;
    if (restoreWebgl) {
      this.disposeWebgl();
    }

    if (addon) {
      this.terminal.loadAddon(addon);
      this.ligaturesAddon = addon;
    } else {
      this.ligaturesAddon?.dispose();
      this.ligaturesAddon = undefined;
    }

    if (restoreWebgl && !this.webglFailed) {
      this.enableWebgl();
    }
    this.terminal.refresh(0, this.terminal.rows - 1);
  }

  private fitNow(): boolean {
    if (this.fitTimer !== undefined) {
      window.clearTimeout(this.fitTimer);
      this.fitTimer = undefined;
    }
    if (!this.opened || !this.element.isConnected ||
        this.element.clientWidth < 20 || this.element.clientHeight < 20) {
      return false;
    }

    try {
      this.fitAddon.fit();
      return true;
    } catch {
      // A detached/transitioning pane will be fitted on its next ResizeObserver event.
      return false;
    }
  }

  private readonly handleContextMenu = (event: MouseEvent): void => {
    if (this.copySelection()) {
      event.preventDefault();
      event.stopImmediatePropagation();
      this.terminal.clearSelection();
      return;
    }

    if (this.terminal.modes.mouseTrackingMode !== 'none') {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
    this.focus();
    void this.bridge.readClipboard()
      .then(text => this.paste(text))
      .catch(error => console.error('Unable to paste clipboard text.', error));
  };

  private readonly handleWheel = (event: WheelEvent): void => {
    if (event.deltaY === 0) {
      return;
    }

    if (event.ctrlKey) {
      this.zoomFont(event);
      return;
    }

    // The alternate buffer has no scrollback and xterm.js does not translate
    // the wheel into input, so without mouse reporting the wheel would be a
    // no-op. Send arrow keys like Windows Terminal does, letting fullscreen
    // apps such as codex scroll their own transcript.
    const isAltBuffer = this.terminal.buffer.active.type === 'alternate';
    if (isAltBuffer !== this.altScrollWasAltBuffer) {
      // Don't leak a fractional remainder across a buffer switch.
      this.altScrollWasAltBuffer = isAltBuffer;
      this.altScrollRemainder = 0;
    }
    if (this.terminal.modes.mouseTrackingMode !== 'none' || !isAltBuffer) {
      return;
    }

    const lines = this.consumeAltScrollDelta(event);
    if (lines === 0) {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();

    const applicationMode = this.terminal.modes.applicationCursorKeysMode;
    const sequence = lines < 0
      ? (applicationMode ? '\x1bOA' : '\x1b[A')
      : (applicationMode ? '\x1bOB' : '\x1b[B');
    this.bridge.sendInput(this.sessionId, sequence.repeat(Math.abs(lines)));
  };

  private handleTerminalData(data: string): void {
    if (!this.controlModifierActive) {
      this.bridge.sendInput(this.sessionId, data);
      return;
    }

    this.setControlModifier(false);
    this.bridge.sendInput(this.sessionId, this.controlCharacter(data) ?? data);
  }

  private controlCharacter(data: string): string | undefined {
    if (Array.from(data).length !== 1) {
      return undefined;
    }

    const code = data.charCodeAt(0);
    if (code >= 65 && code <= 90) {
      return String.fromCharCode(code - 64);
    }
    if (code >= 97 && code <= 122) {
      return String.fromCharCode(code - 96);
    }

    const aliases: Record<string, number> = {
      ' ': 0,
      '@': 0,
      '2': 0,
      '[': 27,
      '3': 27,
      '\\': 28,
      '4': 28,
      ']': 29,
      '5': 29,
      '^': 30,
      '6': 30,
      '_': 31,
      '-': 31,
      '7': 31,
      '?': 127,
      '8': 127
    };
    const controlCode = aliases[data];
    return controlCode === undefined ? undefined : String.fromCharCode(controlCode);
  }

  private setControlModifier(active: boolean): void {
    if (active === this.controlModifierActive) {
      return;
    }

    this.controlModifierActive = active;
    this.callbacks.onControlModifierChanged(this.sessionId, active);
  }

  private consumeAltScrollDelta(event: WheelEvent): number {
    let delta: number;
    if (event.deltaMode === WheelEvent.DOM_DELTA_LINE) {
      delta = event.deltaY;
    } else if (event.deltaMode === WheelEvent.DOM_DELTA_PAGE) {
      delta = event.deltaY * this.terminal.rows;
    } else {
      delta = event.deltaY / TerminalController.ALT_SCROLL_PIXELS_PER_LINE;
    }

    if (Math.sign(delta) !== Math.sign(this.altScrollRemainder)) {
      this.altScrollRemainder = 0;
    }
    this.altScrollRemainder += delta;

    const lines = Math.trunc(this.altScrollRemainder);
    this.altScrollRemainder -= lines;
    return lines;
  }

  private zoomFont(event: WheelEvent): void {
    event.preventDefault();
    event.stopImmediatePropagation();

    const currentSize = this.terminal.options.fontSize ?? 14;
    this.setFontSize(currentSize + (event.deltaY < 0 ? 1 : -1));
  }

  private setFontSize(fontSize: number, focus = true): void {
    const nextSize = Math.min(
      TerminalController.MAX_FONT_SIZE,
      Math.max(TerminalController.MIN_FONT_SIZE, Math.round(fontSize))
    );
    if (nextSize === this.terminal.options.fontSize) {
      return;
    }

    this.terminal.options.fontSize = nextSize;
    this.scheduleFit();
    if (focus) {
      this.focus();
    }
    this.callbacks.onFontSizeChanged(this.sessionId, nextSize);
  }

  private readonly handleTouchStart = (event: TouchEvent): void => {
    if (this.touchGestureResetTimer !== undefined) {
      window.clearTimeout(this.touchGestureResetTimer);
      this.touchGestureResetTimer = undefined;
    }

    this.updatePinchTouches(event.changedTouches);
    if (this.pinchTouches.size < 2) {
      return;
    }

    const distance = this.getPinchDistance();
    if (distance === undefined || distance <= 0) {
      return;
    }

    this.pinchStartDistance = distance;
    this.pinchStartFontSize = this.terminal.options.fontSize ?? 14;
    this.suppressXtermTouchGestures = true;
    event.preventDefault();
  };

  private readonly handleTouchMove = (event: TouchEvent): void => {
    this.updatePinchTouches(event.changedTouches);
    if (!this.suppressXtermTouchGestures) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    if (this.pinchStartDistance === undefined || this.pinchTouches.size < 2) {
      return;
    }

    const distance = this.getPinchDistance();
    if (distance === undefined) {
      return;
    }
    this.setFontSize(this.pinchStartFontSize * distance / this.pinchStartDistance, false);
  };

  private readonly handleTouchEnd = (event: TouchEvent): void => {
    for (let index = 0; index < event.changedTouches.length; index++) {
      const touch = event.changedTouches.item(index);
      if (touch) {
        this.pinchTouches.delete(touch.identifier);
      }
    }

    if (!this.suppressXtermTouchGestures) {
      return;
    }

    event.preventDefault();
    if (this.pinchTouches.size < 2) {
      this.pinchStartDistance = undefined;
    }
    if (this.pinchTouches.size !== 0) {
      return;
    }

    // Let xterm observe touchend and clean up its own touch records first. Its
    // synthetic tap/context-menu events are suppressed for the same dispatch.
    this.touchGestureResetTimer = window.setTimeout(() => {
      this.touchGestureResetTimer = undefined;
      this.suppressXtermTouchGestures = false;
    }, 0);
  };

  private readonly handleXtermTouchGesture = (event: Event): void => {
    if (!this.suppressXtermTouchGestures) {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
  };

  private updatePinchTouches(touches: TouchList): void {
    for (let index = 0; index < touches.length; index++) {
      const touch = touches.item(index);
      if (touch) {
        this.pinchTouches.set(touch.identifier, { x: touch.clientX, y: touch.clientY });
      }
    }
  }

  private getPinchDistance(): number | undefined {
    const touches = Array.from(this.pinchTouches.values());
    const first = touches[0];
    const second = touches[1];
    if (!first || !second) {
      return undefined;
    }

    return Math.hypot(second.x - first.x, second.y - first.y);
  }

}
