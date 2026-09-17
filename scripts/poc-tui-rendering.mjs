// node scripts/poc-tui-rendering.mjs [--serve]
// Uses pinned npm assets; isolated browser profile, no running zt processes touched.
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import http from 'node:http';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
const root=fileURLToPath(new URL('../',import.meta.url));
const modules=path.join(root,'src/KevinZonda.Terminal.WebAssets/node_modules');
const routes=new Map([
  ['/',[path.join(root,'scripts/poc-tui-rendering.html'),'text/html']],
  ['/xterm.css',[path.join(modules,'@xterm/xterm/css/xterm.css'),'text/css']],
  ['/xterm.js',[path.join(modules,'@xterm/xterm/lib/xterm.js'),'text/javascript']],
  ['/webgl.js',[path.join(modules,'@xterm/addon-webgl/lib/addon-webgl.js'),'text/javascript']]
]);
const server=http.createServer(async(req,res)=>{try{const route=routes.get(req.url);if(!route){res.writeHead(404).end();return;}res.setHeader('Content-Type',route[1]);res.end(await fs.readFile(route[0]));}catch(e){res.writeHead(500).end(String(e));}});
await new Promise(r=>server.listen(0,'127.0.0.1',r));
const url=`http://127.0.0.1:${server.address().port}/`;
if(process.argv.includes('--serve')){console.log(url);await new Promise(()=>{});}
const profile=await fs.mkdtemp(path.join(os.tmpdir(),'kterm-render-poc-'));
const edge=spawn(process.env.POC_BROWSER??'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  ['--headless=new','--no-first-run','--force-device-scale-factor=1.5','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
const delay=ms=>new Promise(r=>setTimeout(r,ms));
let socket;
try{
  let port;
  for(let i=0;i<100;i++){try{port=(await fs.readFile(path.join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0];break;}catch{await delay(100);}}
  if(!port)throw new Error('Browser did not start');
  const targets=await(await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  socket=new WebSocket(targets.find(t=>t.type==='page').webSocketDebuggerUrl);
  await new Promise((resolve,reject)=>{socket.onopen=resolve;socket.onerror=reject;});
  let id=0;const pending=new Map();
  socket.onmessage=e=>{const m=JSON.parse(e.data);const p=pending.get(m.id);if(!p)return;pending.delete(m.id);clearTimeout(p.timer);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);};
  const send=(method,params={})=>new Promise((resolve,reject)=>{const n=++id;const timer=setTimeout(()=>{pending.delete(n);reject(new Error('Timeout '+method));},60000);pending.set(n,{resolve,reject,timer});socket.send(JSON.stringify({id:n,method,params}));});
  const evaluate=async expression=>{const r=await send('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;};
  await send('Emulation.setDeviceMetricsOverride',{width:2000,height:1100,deviceScaleFactor:0,mobile:false});
  await send('Page.navigate',{url});
  for(let i=0;i<100;i++){if(await evaluate('!!window.done'))break;await delay(100);}
  // Poll progress so individual calls do not wait on a long GPU stress run.
  let report;
  for(let i=0;i<240;i++){
    report=await evaluate('window.report');if(report)break;
    const errors=await evaluate('window.errors');if(errors?.length)throw new Error(JSON.stringify(errors));
    await delay(500);
  }
  if(!report)throw new Error('Stress test did not finish');
  console.log(JSON.stringify(report,null,2));
  await fs.writeFile(path.join(profile,'report.json'),JSON.stringify(report,null,2));
  const shot=await send('Page.captureScreenshot',{format:'png'});
  const artifact=path.join(profile,'result.png');await fs.writeFile(artifact,Buffer.from(shot.data,'base64'));
  console.log('Screenshot: '+artifact);
  await send('Browser.close');
}finally{socket?.close();if(edge.exitCode===null)edge.kill();server.close();console.error('Isolated artifacts: '+profile);}
