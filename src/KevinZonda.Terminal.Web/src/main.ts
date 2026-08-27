import '@xterm/xterm/css/xterm.css';
import './styles.css';
import { NativeBridge } from './bridge';
import { BrowserResumeStore } from './resume-store';
import { Workspace } from './workspace';

async function start(): Promise<void> {
  registerServiceWorker();
  const resumeStore = window.chrome?.webview ? undefined : await BrowserResumeStore.create();
  const bridge = new NativeBridge(resumeStore);
  const workspace = new Workspace(bridge, resumeStore);
  await workspace.initialize();
}

function registerServiceWorker(): void {
  if (window.chrome?.webview || !('serviceWorker' in navigator)) {
    return;
  }

  const register = () => {
    void navigator.serviceWorker.register('/sw.js', { scope: '/' }).catch(error => {
      console.warn('Unable to register the KTerm service worker.', error);
    });
  };

  if (document.readyState === 'complete') {
    register();
    return;
  }

  window.addEventListener('load', register, { once: true });
}

void start().catch(error => {
  const status = document.getElementById('status');
  if (status) {
    status.textContent = error instanceof Error ? error.message : String(error);
    status.classList.add('visible', 'error');
  }
});
