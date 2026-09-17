// node scripts/poc-ligatures.mjs
// Exercise the production controller in an isolated Edge profile.
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { build } from '../src/KevinZonda.Terminal.WebAssets/node_modules/vite/dist/node/index.js';

const root = fileURLToPath(new URL('../', import.meta.url));
const webRoot = path.join(root, 'src/KevinZonda.Terminal.WebAssets');
const outDir = path.join(root, 'artifacts/ligatures');
const fixture = `
import { TerminalController } from '${webRoot.replaceAll('\\', '/')}/src/terminal-controller.ts';
import '@xterm/xterm/css/xterm.css';
const font = {family:'CaskaydiaCove NF, Cascadia Mono, Cascadia Code, Consolas, Microsoft YaHei, monospace',size:20,lineHeight:1.05,enableLigatures:true};
const bridge = {openExternal(){},sendInput(){},sendBinaryInput(){},resize(){},acknowledgeOutput(){}};
const callbacks = {onBell(){},onControlModifierChanged(){},onFocus(){},onFontSizeChanged(){},onTitle(){},async onTerminalCheckpoint(){}};
window.errors = [];
window.addEventListener('error', e => errors.push(e.message));
window.addEventListener('unhandledrejection', e => errors.push(String(e.reason)));
const query = window.queryLocalFonts?.bind(window);
window.fontQueryState = 'not called';
window.queryLocalFonts = async () => {
  window.fontQueryState = 'waiting';
  await new Promise(r => window.resolveFontQuery = r);
  const fonts = await query();
  window.fontMetadata = [...new Set(fonts.filter(f => /Cask|Cascadia/i.test(f.family)).map(f => f.family))];
  window.fontQueryState = 'resolved';
  return fonts.map(f => ({family:f.family,fullName:f.fullName,postscriptName:f.postscriptName,blob:()=>{window.loadedFont=f.fullName;return f.blob();}}));
};
window.controller = new TerminalController({sessionId:'fixture'}, bridge, callbacks, font, {name:'Default'}, {shape:'block',blink:false});
controller.element.style.cssText='width:1100px;height:300px';
document.body.style.cssText='background:#101010;color:white;margin:20px';
document.body.append(controller.element);
controller.mount();
await new Promise(r => controller.write('== != === !== => -> <= >= <=>\\r\\n', r));
window.snapshot = () => {
  const c = controller, t = c.terminal, renderer = t._core._renderService._renderer.value;
  return {classes:c.element.className,addon:!!c.ligaturesAddon,failed:c.webglFailed,
    font:t.options.fontFamily,features:t.element.style.fontFeatureSettings,
    joined:t._core._characterJoinerService.getJoinedCharacters(0),
    fontQueryState,errors,renderer:renderer?.constructor.name,
    canvases:[...c.element.querySelectorAll('canvas')].map(el=>({width:el.width,height:el.height,display:getComputedStyle(el).display,features:getComputedStyle(el).fontFeatureSettings})),
    loadedFont:window.loadedFont,metadata:window.fontMetadata};
};
window.ready = true;
`;
await build({root:webRoot,configFile:path.join(webRoot,'vite.config.ts'),
  plugins:[{name:'fixture',resolveId(id){if(id==='ligature-fixture')return '\0ligature-fixture';},load(id){if(id==='\0ligature-fixture')return fixture;}}],
  build:{outDir,emptyOutDir:true,rollupOptions:{input:'ligature-fixture',output:{entryFileNames:'fixture.js',chunkFileNames:'[name]-[hash].js',assetFileNames:'[name][extname]'}}}});
const server = http.createServer(async (req,res) => {
  try {
    if(req.url==='/'){res.setHeader('Content-Type','text/html');res.end('<link rel="stylesheet" href="/_ligature-fixture.css"><script type="module" src="/fixture.js"></script>');return;}
    const name = req.url.slice(1);
    if(name.includes('..') || name.includes('/')){res.writeHead(404).end();return;}
    res.setHeader('Content-Type',name.endsWith('.css')?'text/css':'text/javascript');
    res.end(await fs.readFile(path.join(outDir,name)));
  } catch(e){res.writeHead(500).end(String(e));}
});
await new Promise(r=>server.listen(0,'127.0.0.1',r));
const origin = `http://127.0.0.1:${server.address().port}`;
const profile = await fs.mkdtemp(path.join(os.tmpdir(),'kterm-ligatures-poc-'));
const edge = spawn(process.env.POC_BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  ['--headless=new','--no-first-run','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
const delay = ms=>new Promise(r=>setTimeout(r,ms));
let socket;
try {
  let port;
  for(let i=0;i<100;i++){try{port=(await fs.readFile(path.join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0];break;}catch{await delay(100);}}
  assert.ok(port,'Browser did not start');
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  socket = new WebSocket(targets.find(t=>t.type==='page').webSocketDebuggerUrl);
  await new Promise((resolve,reject)=>{socket.onopen=resolve;socket.onerror=reject;});
  let id=0;const pending=new Map();
  socket.onmessage=e=>{const m=JSON.parse(e.data),p=pending.get(m.id);if(!p)return;pending.delete(m.id);clearTimeout(p.timer);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);};
  const send=(method,params={})=>new Promise((resolve,reject)=>{const n=++id,timer=setTimeout(()=>reject(new Error('Timeout '+method)),20000);pending.set(n,{resolve,reject,timer});socket.send(JSON.stringify({id:n,method,params}));});
  const evaluate=async expression=>{const r=await send('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;};
  await send('Browser.setPermission',{permission:{name:'local-fonts'},setting:'granted',origin});
  await send('Emulation.setDeviceMetricsOverride',{width:1200,height:600,deviceScaleFactor:1,mobile:false});
  await send('Page.navigate',{url:origin+'/'});
  for(let i=0;i<100;i++){if(await evaluate('window.ready && !!controller.ligaturesAddon'))break;await delay(100);}
  const report=[];
  async function capture(name){
    const snapshot=await evaluate('snapshot()');report.push({name,...snapshot});console.log(JSON.stringify(report.at(-1)));
    const shot=await send('Page.captureScreenshot',{format:'png'});await fs.writeFile(path.join(outDir,name+'.png'),Buffer.from(shot.data,'base64'));
  }
  await capture('before-font-access');
  await delay(10000);
  await capture('after-10s');
  await evaluate('window.resolveFontQuery?.()');
  for(let i=0;i<100;i++){if(await evaluate('window.fontQueryState === "resolved"'))break;await delay(100);}
  await delay(1000);
  await capture('after-font-access');
  await evaluate('controller.applyFontSettings({...controller.terminal.options, family:controller.terminal.options.fontFamily,size:20,lineHeight:1.05,enableLigatures:false})');
  await delay(100);
  await capture('disabled');
  await evaluate('controller.applyFontSettings({family:controller.terminal.options.fontFamily,size:20,lineHeight:1.05,enableLigatures:true})');
  await delay(1000);
  await capture('reenabled');
  const before = report.find(r=>r.name==='before-font-access');
  const after = report.find(r=>r.name==='after-font-access');
  assert.equal(before.joined.length,9,'Initial fallback ligatures');
  assert.equal(after.fontQueryState,'resolved','Local Font Access must complete');
  assert.ok(after.loadedFont,'Exercise a successfully parsed fallback font');
  assert.deepEqual(after.joined,before.joined,'Font loading must preserve common ligatures');
  assert.ok(after.classes.includes('renderer-webgl'),'WebGL must remain active');
  assert.equal(after.failed,false,'No WebGL failure');
  assert.deepEqual(report.find(r=>r.name==='disabled').joined,[],'Disabling removes both joiners');
  assert.deepEqual(report.find(r=>r.name==='reenabled').joined,before.joined,'Reenabling restores both joiners');
  assert.ok(report.every(r=>r.errors.length===0),'No browser errors');
  await fs.writeFile(path.join(outDir,'report.json'),JSON.stringify(report,null,2));
  await send('Browser.close');
} finally {socket?.close();if(edge.exitCode===null)edge.kill();server.close();}
