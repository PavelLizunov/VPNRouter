// Dependency-free Chromium DevTools smoke check. Use only isolated catalog browser.
import assert from 'node:assert/strict';
const endpoint=process.argv[2] || 'http://127.0.0.1:9331';
const url=process.argv[3] || 'http://127.0.0.1:8289/opendesign/mockups/ui-icon-catalog/icon-catalog.html';
const tabs=await(await fetch(endpoint+'/json')).json();
const tab=tabs.find(t=>t.type==='page');
assert(tab,'No isolated browser page');
const ws=new WebSocket(tab.webSocketDebuggerUrl);
await new Promise((resolve,reject)=>{ws.addEventListener('open',resolve,{once:true});ws.addEventListener('error',reject,{once:true});});
let id=0;const pending=new Map();
ws.addEventListener('message',event=>{const message=JSON.parse(event.data);if(message.id){const item=pending.get(message.id);if(item){clearTimeout(item.timer);pending.delete(message.id);message.error?item.reject(new Error(JSON.stringify(message.error))):item.resolve(message.result);}}});
function send(method,params={}){return new Promise((resolve,reject)=>{const key=++id;const timer=setTimeout(()=>{pending.delete(key);reject(new Error('CDP timeout '+method));},30000);pending.set(key,{resolve,reject,timer});ws.send(JSON.stringify({id:key,method,params}));});}
async function evaluate(expression){const result=await send('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});assert(!result.exceptionDetails,JSON.stringify(result.exceptionDetails));return result.result.value;}
try{
 await send('Page.enable');await send('Page.navigate',{url});
 await evaluate(`new Promise((resolve,reject)=>{const end=Date.now()+15000;function check(){if(document.querySelector('#count')?.classList.contains('error'))return reject(new Error(document.querySelector('#count').textContent));if(document.querySelectorAll('.card').length)return resolve(true);if(Date.now()>end)return reject(new Error('Catalog did not render'));requestAnimationFrame(check);}check();})`);
 const total=await evaluate(`document.querySelectorAll('.card').length`);assert(total>0);
 assert.equal(await evaluate(`document.querySelectorAll('.card svg').length`)>total*4,true);
 for(const theme of ['light','dark'])for(const size of ['16','20','24','32'])for(const state of ['default','hover','active','disabled']){
  const result=await evaluate(`(()=>{for(const [id,value]of ${JSON.stringify([['theme',theme],['size',size],['state',state]])}){const e=document.getElementById(id);e.value=value;e.dispatchEvent(new Event('change'));}const card=document.querySelector('.card');const g=card.querySelector('.well:last-child .glyph');return {theme:document.documentElement.dataset.theme,size:g.querySelector('svg').getBoundingClientRect().width,state:document.querySelector('button.glyph')?.dataset.state,disabled:document.querySelector('button.glyph')?.disabled,count:document.querySelectorAll('.card').length};})()`);
  assert.equal(result.theme,theme);assert.equal(result.size,Number(size));assert.equal(result.count,total);assert.equal(result.state,state);assert.equal(result.disabled,state==='disabled');
 }
 for(const name of ['screen','purpose']){
  const result=await evaluate(`(()=>{const select=document.getElementById('${name}');select.selectedIndex=1;select.dispatchEvent(new Event('change'));const count=document.querySelectorAll('.card').length;document.getElementById('reset').click();return count;})()`);assert(result>0&&result<=total);
 }
 assert.equal(await evaluate(`(()=>{const s=document.getElementById('search');s.value='__no_such_pictogram__';s.dispatchEvent(new Event('input'));return document.querySelectorAll('.card').length===0&&!document.getElementById('empty').hidden;})()`),true);
 await evaluate(`document.getElementById('reset').click()`);
 for(const width of [360,768,1440]){
  await send('Emulation.setDeviceMetricsOverride',{width,height:1000,deviceScaleFactor:1,mobile:false});
  const overflow=await evaluate(`({ok:document.documentElement.scrollWidth<=innerWidth,items:[...document.querySelectorAll('body *')].filter(e=>e.getBoundingClientRect().right>innerWidth+1).slice(0,12).map(e=>({tag:e.tagName,id:e.id,cls:e.className,right:e.getBoundingClientRect().right}))})`);
  assert.equal(overflow.ok,true,'Horizontal overflow at '+width+': '+JSON.stringify(overflow.items));
 }
 console.log(`PASS browser: ${total} cards, 32 theme/size/state combinations, filters/reset/empty state, 360/768/1440 widths`);
}finally{ws.close();}
