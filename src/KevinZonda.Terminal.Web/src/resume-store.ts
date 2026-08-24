import { createId } from './id';

export type ResumeLayoutNode =
  | { type: 'pane'; paneId: string }
  | {
      type: 'split';
      direction: 'columns' | 'rows';
      ratio: number;
      first: ResumeLayoutNode;
      second: ResumeLayoutNode;
    };

export interface ResumeTabRecord {
  sessionId: string;
  title: string;
  processInfo: string;
}

export interface ResumePaneRecord {
  id: string;
  tabs: ResumeTabRecord[];
  activeSessionId: string;
}

export interface ResumeWorkspaceRecord {
  id: string;
  name: string;
  panes: ResumePaneRecord[];
  root?: ResumeLayoutNode;
  focusedPaneId?: string;
}

export interface ResumeWorkspaceSnapshot {
  activeWorkspaceId?: string;
  nextWorkspaceNumber: number;
  workspaces: ResumeWorkspaceRecord[];
}

export interface ResumeInputRecord {
  type: 'session.input' | 'session.binaryInput';
  inputSeq: number;
  data: string;
}

export interface ResumeSessionRecord {
  sessionId: string;
  shellName: string;
  processId: number;
  checkpointOutputSeq: number;
  cols: number;
  rows: number;
  nextInputSeq: number;
  pendingInputs: ResumeInputRecord[];
  pendingCloseOperationId?: string;
}

export interface TerminalCheckpoint {
  sessionId: string;
  outputSeq: number;
  data: string;
  cols: number;
  rows: number;
  updatedAt: string;
}

interface ResumeManifest extends ResumeWorkspaceSnapshot {
  version: 1;
  runtimeId: string;
  sessions: ResumeSessionRecord[];
  updatedAt: string;
}

interface CheckpointRecord extends TerminalCheckpoint {
  key: string;
  runtimeId: string;
}

export class BrowserResumeStore {
  private static readonly STORAGE_KEY = 'kterm.serverResume.v1';
  private static readonly DATABASE_NAME = 'kterm-resume';
  private static readonly DATABASE_VERSION = 1;
  private static readonly CHECKPOINT_STORE = 'terminal-checkpoints';

  private readonly database: Promise<IDBDatabase>;
  private manifest: ResumeManifest;

  public constructor() {
    const previous = this.readManifest();
    const navigation = performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming | undefined;
    const resumesPreviousPage = navigation?.type === 'reload' && previous !== undefined;
    this.manifest = resumesPreviousPage
      ? previous
      : {
          version: 1,
          runtimeId: createId(),
          activeWorkspaceId: undefined,
          nextWorkspaceNumber: 1,
          workspaces: [],
          sessions: [],
          updatedAt: new Date().toISOString()
        };
    this.database = this.openDatabase();
    this.writeManifest();
  }

  public get runtimeId(): string {
    return this.manifest.runtimeId;
  }

  public get isResuming(): boolean {
    return this.manifest.workspaces.length > 0 && this.manifest.sessions.length > 0;
  }

  public getWorkspaceSnapshot(): ResumeWorkspaceSnapshot {
    return structuredClone({
      activeWorkspaceId: this.manifest.activeWorkspaceId,
      nextWorkspaceNumber: this.manifest.nextWorkspaceNumber,
      workspaces: this.manifest.workspaces
    });
  }

  public getSessions(): ResumeSessionRecord[] {
    return structuredClone(this.manifest.sessions);
  }

  public saveWorkspace(snapshot: ResumeWorkspaceSnapshot): void {
    this.manifest.activeWorkspaceId = snapshot.activeWorkspaceId;
    this.manifest.nextWorkspaceNumber = snapshot.nextWorkspaceNumber;
    this.manifest.workspaces = structuredClone(snapshot.workspaces);
    this.writeManifest();
  }

  public registerSession(
    sessionId: string,
    shellName: string,
    processId: number,
    cols: number,
    rows: number
  ): void {
    const existing = this.findSession(sessionId);
    if (existing) {
      existing.shellName = shellName;
      existing.processId = processId;
      existing.cols = cols;
      existing.rows = rows;
    } else {
      this.manifest.sessions.push({
        sessionId,
        shellName,
        processId,
        checkpointOutputSeq: 0,
        cols,
        rows,
        nextInputSeq: 1,
        pendingInputs: []
      });
    }
    this.writeManifest();
  }

  public updateResize(sessionId: string, cols: number, rows: number): void {
    const session = this.findSession(sessionId);
    if (!session) {
      return;
    }
    session.cols = cols;
    session.rows = rows;
    this.writeManifest();
  }

  public saveInputState(
    sessionId: string,
    nextInputSeq: number,
    pendingInputs: ResumeInputRecord[]
  ): void {
    const session = this.findSession(sessionId);
    if (!session) {
      return;
    }
    session.nextInputSeq = nextInputSeq;
    session.pendingInputs = structuredClone(pendingInputs);
    this.writeManifest();
  }

  public markSessionClosing(sessionId: string, operationId: string): void {
    const session = this.findSession(sessionId);
    if (!session) {
      return;
    }
    session.pendingCloseOperationId = operationId;
    this.writeManifest();
  }

  public completeSession(sessionId: string): void {
    const index = this.manifest.sessions.findIndex(session => session.sessionId === sessionId);
    if (index >= 0) {
      this.manifest.sessions.splice(index, 1);
      this.writeManifest();
    }
    void this.deleteCheckpoint(sessionId);
  }

  public async loadCheckpoint(sessionId: string): Promise<TerminalCheckpoint | undefined> {
    const session = this.findSession(sessionId);
    if (!session || session.checkpointOutputSeq <= 0) {
      return undefined;
    }

    const database = await this.database;
    const record = await new Promise<CheckpointRecord | undefined>((resolve, reject) => {
      const request = database
        .transaction(BrowserResumeStore.CHECKPOINT_STORE, 'readonly')
        .objectStore(BrowserResumeStore.CHECKPOINT_STORE)
        .get(this.checkpointKey(sessionId));
      request.onsuccess = () => resolve(request.result as CheckpointRecord | undefined);
      request.onerror = () => reject(request.error ?? new Error('Unable to read terminal checkpoint.'));
    });
    if (!record || record.runtimeId !== this.runtimeId || record.sessionId !== sessionId ||
        record.outputSeq !== session.checkpointOutputSeq || typeof record.data !== 'string') {
      return undefined;
    }

    return {
      sessionId: record.sessionId,
      outputSeq: record.outputSeq,
      data: record.data,
      cols: record.cols,
      rows: record.rows,
      updatedAt: record.updatedAt
    };
  }

  public async saveCheckpoint(checkpoint: TerminalCheckpoint): Promise<void> {
    const session = this.findSession(checkpoint.sessionId);
    if (!session || checkpoint.outputSeq <= session.checkpointOutputSeq) {
      return;
    }

    const record: CheckpointRecord = {
      ...checkpoint,
      key: this.checkpointKey(checkpoint.sessionId),
      runtimeId: this.runtimeId
    };
    const database = await this.database;
    const committedRecord = await new Promise<CheckpointRecord>((resolve, reject) => {
      const transaction = database.transaction(BrowserResumeStore.CHECKPOINT_STORE, 'readwrite');
      const store = transaction.objectStore(BrowserResumeStore.CHECKPOINT_STORE);
      const request = store.get(record.key);
      let newestRecord = record;
      request.onsuccess = () => {
        const existing = request.result as CheckpointRecord | undefined;
        if (existing?.runtimeId === this.runtimeId && existing.sessionId === checkpoint.sessionId &&
            existing.outputSeq >= checkpoint.outputSeq) {
          newestRecord = existing;
          return;
        }
        store.put(record);
      };
      transaction.oncomplete = () => resolve(newestRecord);
      transaction.onabort = () => reject(transaction.error ?? new Error('Unable to save terminal checkpoint.'));
      transaction.onerror = () => reject(transaction.error ?? new Error('Unable to save terminal checkpoint.'));
    });

    const current = this.findSession(checkpoint.sessionId);
    if (!current) {
      await this.deleteCheckpoint(checkpoint.sessionId);
      return;
    }
    if (committedRecord.outputSeq <= current.checkpointOutputSeq) {
      return;
    }
    current.checkpointOutputSeq = committedRecord.outputSeq;
    current.cols = committedRecord.cols;
    current.rows = committedRecord.rows;
    this.writeManifest(true);
  }

  private async deleteCheckpoint(sessionId: string): Promise<void> {
    try {
      const database = await this.database;
      await new Promise<void>((resolve, reject) => {
        const transaction = database.transaction(BrowserResumeStore.CHECKPOINT_STORE, 'readwrite');
        transaction.objectStore(BrowserResumeStore.CHECKPOINT_STORE).delete(this.checkpointKey(sessionId));
        transaction.oncomplete = () => resolve();
        transaction.onabort = () => reject(transaction.error ?? new Error('Unable to delete terminal checkpoint.'));
        transaction.onerror = () => reject(transaction.error ?? new Error('Unable to delete terminal checkpoint.'));
      });
    } catch {
      // Expired checkpoints are harmless and are overwritten if the ID is reused.
    }
  }

  private checkpointKey(sessionId: string): string {
    return `${this.runtimeId}:${sessionId}`;
  }

  private findSession(sessionId: string): ResumeSessionRecord | undefined {
    return this.manifest.sessions.find(session => session.sessionId === sessionId);
  }

  private readManifest(): ResumeManifest | undefined {
    try {
      const raw = window.sessionStorage.getItem(BrowserResumeStore.STORAGE_KEY);
      if (!raw) {
        return undefined;
      }
      const candidate = JSON.parse(raw) as Partial<ResumeManifest>;
      if (candidate.version !== 1 || typeof candidate.runtimeId !== 'string' ||
          !Array.isArray(candidate.workspaces) || !Array.isArray(candidate.sessions) ||
          typeof candidate.nextWorkspaceNumber !== 'number') {
        return undefined;
      }
      return candidate as ResumeManifest;
    } catch {
      return undefined;
    }
  }

  private writeManifest(throwOnFailure = false): void {
    this.manifest.updatedAt = new Date().toISOString();
    try {
      window.sessionStorage.setItem(
        BrowserResumeStore.STORAGE_KEY,
        JSON.stringify(this.manifest)
      );
    } catch (error) {
      if (throwOnFailure) {
        throw error;
      }
      console.warn('Unable to persist the KTerm page resume manifest.', error);
    }
  }

  private openDatabase(): Promise<IDBDatabase> {
    return new Promise((resolve, reject) => {
      const request = window.indexedDB.open(
        BrowserResumeStore.DATABASE_NAME,
        BrowserResumeStore.DATABASE_VERSION
      );
      request.onupgradeneeded = () => {
        const database = request.result;
        if (!database.objectStoreNames.contains(BrowserResumeStore.CHECKPOINT_STORE)) {
          database.createObjectStore(BrowserResumeStore.CHECKPOINT_STORE, { keyPath: 'key' });
        }
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error ?? new Error('Unable to open terminal checkpoint storage.'));
    });
  }
}
