import '@xterm/xterm/css/xterm.css';
import './styles.css';
import { NativeBridge } from './bridge';
import { BrowserResumeStore } from './resume-store';
import { Workspace } from './workspace';

const resumeStore = window.chrome?.webview ? undefined : new BrowserResumeStore();
const bridge = new NativeBridge(resumeStore);
const workspace = new Workspace(bridge, resumeStore);

void workspace.initialize().catch(error => {
  const status = document.getElementById('status');
  if (status) {
    status.textContent = error instanceof Error ? error.message : String(error);
    status.classList.add('visible', 'error');
  }
});
