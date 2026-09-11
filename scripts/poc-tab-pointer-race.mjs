// Run: node scripts/poc-tab-pointer-race.mjs
// Uses the repository's real event handlers, with terminal/backend effects stubbed.
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { stripTypeScriptTypes } from 'node:module';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('../', import.meta.url));
const source = await fs.readFile(path.join(root, 'src/KevinZonda.Terminal.WebAssets/src/workspace.ts'), 'utf8');
// Keep the entire production class; skip its application constructor and imports.
const compiled = stripTypeScriptTypes(source, {mode:'transform'})
  .replace(/^import\s[^;]+;\s*/gm, '').replace('export class Workspace', 'class Workspace');
const profile = await fs.mkdtemp(path.join(os.tmpdir(), 'kterm-pointer-poc-'));
const edge = spawn(process.env.POC_BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  ['--headless=new', '--disable-gpu', '--no-first-run', '--remote-debugging-port=0',
    `--user-data-dir=${profile}`, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let socket;
try {
  let port;
  for (let i = 0; i < 100; i++) {
    try { port = (await fs.readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; break; }
    catch { await delay(100); }
  }
  assert.ok(port, 'Browser debugging port did not start');
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  socket = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  let nextId = 0;
  const pending = new Map();
  socket.onmessage = e => {
    const m = JSON.parse(e.data);
    if (!m.id) return;
    const p = pending.get(m.id);
    if (!p) return;
    pending.delete(m.id);
    clearTimeout(p.timer);
    m.error ? p.reject(new Error(JSON.stringify(m.error))) : p.resolve(m.result);
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++nextId;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Timed out: ${method}`)); }, 10000);
    pending.set(id, { resolve, reject, timer });
    socket.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async expression => {
    const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails));
    return r.result.value;
  };
  const css = await fs.readFile(path.join(root, 'src/KevinZonda.Terminal.WebAssets/src/styles.css'), 'utf8');
  await send('Emulation.setDeviceMetricsOverride',{width:1200,height:600,deviceScaleFactor:1,mobile:false});
  await evaluate(`document.head.innerHTML = '<style></style>'; document.querySelector('style').textContent = ${JSON.stringify(css)};
    document.body.innerHTML = '<main id="fixture" style="display:flex;width:1000px;height:300px"></main>';
    ${compiled}
    window.h = Object.create(Workspace.prototype);
    for(const key of ['panes','focusedPaneId','activeWorkspaceId'])
      Object.defineProperty(h,key,{value:undefined,writable:true});
    Object.assign(h, { workspace: document.querySelector('#fixture'), activeWorkspaceId: 'w',
      settings: {bell:{tabVisualFeedback:'None'}}, ringingBellSessionIds:new Set(),
      unviewedTabBellSessionIds:new Set(), paneElements:new Map(), panes:new Map(), operationPending:false,
      applicationShortcutLabel:k=>k, updateTabStripOverflow:()=>{}, syncWindowTitle:()=>{}, persistResumeState:()=>{},
      activateTab:(p,s)=>{h.panes.get(p).activeSessionId=s; h.actions.push(['activate',p,s]);},
      createTab:p=>h.actions.push(['create',p]), closeTerminalTab:()=>{},
      moveTerminalTab:(...a)=>h.actions.push(['move',...a]), actions:[], trace:[] });
    h.workspaces=[{id:'w',panes:h.panes}];
    for (const p of ['p1','p2']) {
      const pane={id:p,activeSessionId:p+'a',tabs:['a','b'].map(s=>({sessionId:p+s,title:p+s,processInfo:''}))};
      h.panes.set(p,pane);
      const el=document.createElement('section'); el.className='pane'; el.dataset.paneId=p;
      el.style.width='500px'; el.innerHTML='<header class="pane-tab-strip"></header>';
      el.addEventListener('pointerdown',()=>{h.focusedPaneId=p;});
      h.workspace.append(el); h.paneElements.set(p,el); h.renderPaneTabs(pane,el.firstElementChild);
    }
    for(const type of ['pointerdown','pointerup','pointercancel','gotpointercapture','lostpointercapture','click'])
      document.addEventListener(type,e=>h.trace.push({type,target:e.target.className||e.target.nodeName,
        connected:e.target.isConnected, trusted:e.isTrusted}),true);
    window.reset=()=>{h.tabDrag=undefined;h.actions=[];h.trace=[];h.focusedPaneId='p2';
      for(const p of h.panes.values()){p.activeSessionId=p.id+'a';h.refreshPaneTabs(p);}};
  `);
  const point = selector => evaluate(`(()=>{const r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()`);
  const mouse = (type, pos) => send('Input.dispatchMouseEvent', {type, ...pos, button:'left', buttons:type==='mouseReleased'?0:1, clickCount:1});
  const refresh = () => evaluate("h.onTitle('p1a', h.panes.get('p1').tabs[0].title === 'p1a' ? 'p1c' : 'p1a')");
  const results=[];
  async function trial(name, selector, replacement, drag=false) {
    await evaluate('reset()');
    await evaluate('window.originalNodes=[...h.workspace.querySelectorAll(".pane-tab,.pane-tab-activate,.pane-tab-close,.pane-new-tab")];');
    const pos=await point(selector);
    await mouse('mousePressed',pos);
    if(typeof replacement === 'function') await replacement();
    else if(replacement) await refresh();
    assert.equal(await evaluate('originalNodes.every(node=>node.isConnected)'),true,'Refresh detached a gesture target');
    if(drag) await mouse('mouseMoved',{x:pos.x-170,y:pos.y});
    await mouse('mouseReleased',drag?{x:pos.x-170,y:pos.y}:pos);
    const result=await evaluate('({actions:h.actions,focus:h.focusedPaneId,staleDrag:!!h.tabDrag&&!h.tabDrag.tabElement.isConnected,trace:h.trace})');
    results.push({name,...result});
    return result;
  }
  const tab=p=>`[data-pane-id="${p}"] .pane-tab:nth-child(2) .pane-tab-activate`;
  const add=p=>`[data-pane-id="${p}"] .pane-new-tab`;
  assert.equal((await trial('baseline tab',tab('p1'),false)).actions[0][0],'activate');
  assert.equal((await trial('baseline plus',add('p1'),false)).actions[0][0],'create');
  assert.equal((await trial('baseline drag',tab('p1'),false,true)).actions[0][0],'move');
  const updated=await trial('changed title during tab press',tab('p1'),true);
  assert.equal(updated.focus,'p1'); assert.equal(updated.actions.length,1);
  assert.equal(updated.staleDrag,false);
  // A second gesture must neither be lost nor activate twice.
  const recovery=await point(tab('p1'));
  await mouse('mousePressed',recovery); await mouse('mouseReleased',recovery);
  results.push({name:'next click without reset',...await evaluate('({actions:h.actions,staleDrag:!!h.tabDrag})')});
  assert.equal(await evaluate('h.actions.length'),2);
  assert.equal((await trial('changed title during plus press',add('p1'),true)).actions[0][0],'create');
  assert.equal((await trial('changed title during drag',tab('p1'),true,true)).actions[0][0],'move');
  const sameTitle=()=>evaluate("h.onTitle('p1a',h.panes.get('p1').tabs[0].title)");
  assert.equal((await trial('identical title during click',tab('p1'),sameTitle)).actions.length,1);
  for(const mode of ['None','Briefly','UntilViewed']) {
    for(const ringing of [true,false]) {
      const bell=()=>evaluate(`h.settings.bell.tabVisualFeedback=${JSON.stringify(mode)};
        h.ringingBellSessionIds.${ringing?'add':'delete'}('p1b');
        h.unviewedTabBellSessionIds.${ringing?'add':'delete'}('p1b');
        h.refreshPaneTabs(h.panes.get('p1'));`);
      for(const [label,selector,drag,action] of [['tab',tab('p1'),false,'activate'],['plus',add('p1'),false,'create'],['drag',tab('p1'),true,'move']]) {
        assert.equal((await trial(`Bell ${mode} ${ringing}: ${label}`,selector,bell,drag)).actions[0][0],action);
      }
    }
  }
  assert.equal((await trial('Pane2 tab while Pane1 updates',tab('p2'),true)).actions[0][0],'activate');
  assert.equal((await trial('Pane2 plus while Pane1 updates',add('p2'),true)).actions[0][0],'create');
  // Independent page timer: not triggered by a click. Compare press duration
  // separately from spacing; rapid clicking often changes both in real usage.
  async function periodic(name, selector, holdMs, gapMs, count) {
    await evaluate('reset(); window.updates=0; window.ticker=setInterval(()=>{updates++; h.onTitle("p1a",updates%2?"p1a":"p1c");},20)');
    try {
      for(let i=0;i<count;i++) {
        const pos=await point(selector);
        await mouse('mousePressed',pos);
        if(holdMs) await delay(holdMs);
        await mouse('mouseReleased',pos);
        if(gapMs) await delay(gapMs);
      }
    } finally { await evaluate('clearInterval(ticker)'); }
    const outcome=await evaluate('({successes:h.actions.length,updates})');
    results.push({name,holdMs,gapMs,attempts:count,...outcome});
    return outcome;
  }
  assert.equal((await periodic('Pane1 plus: 20ms title stream, slow clicks',add('p1'),60,1000,3)).successes,3);
  await periodic('Pane1 plus: same stream, short rapid clicks',add('p1'),0,20,20);
  assert.equal((await periodic('Pane1 plus: rapid spacing but 60ms hold',add('p1'),60,20,3)).successes,3);
  await periodic('Pane1 plus: slow spacing but short hold',add('p1'),0,1000,3);
  assert.equal((await periodic('Pane2 plus: same stream, slow clicks',add('p2'),60,1000,3)).successes,3);
  console.log(JSON.stringify({browser:await send('Browser.getVersion'),results},null,2));
  await send('Browser.close');
} finally {
  socket?.close();
  if (edge.exitCode === null) edge.kill();
  // Keep isolated profile for diagnosis; never touch the running application's profile.
  console.error(`Isolated browser profile: ${profile}`);
}
