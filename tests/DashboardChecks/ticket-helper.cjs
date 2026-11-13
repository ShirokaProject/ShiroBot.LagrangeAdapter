const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');
async function run(){
 const html=fs.readFileSync(path.join(__dirname,'../../captcha-helper/index.html'),'utf8');
 const script=html.match(/<script>([\s\S]*?)<\/script>/)[1];
 const elements=Object.fromEntries([...html.matchAll(/\bid="([^"]+)"/g)].map(([,id])=>[id,{value:'',hidden:false,textContent:'',replaceChildren(){},focus(){},select(){}}]));
 const instances=[],clipboard=[];
 function SDK(container,aid,callback,options){this.callback=callback;this.destroy=()=>{};this.show=()=>{};instances.push({container,aid,callback,options})}
 const context=vm.createContext({URL,URLSearchParams,console,setTimeout,clearTimeout,
  location:{search:'?aid=16&uin=123456789&sid=123&cap_cd=abc%2Bdef%2A%2A',hostname:'127.0.0.1',pathname:'/'},
  history:{replaceState(){}},navigator:{clipboard:{async writeText(t){clipboard.push(t)}}},
  document:{getElementById(id){assert.ok(elements[id],id);return elements[id]}},window:{TencentCaptcha:SDK}});
 vm.runInContext(script,context);await new Promise(setImmediate);
 assert.equal(instances.length,1);assert.equal(instances[0].aid,'16');
 assert.equal(instances[0].options.sid,'123');assert.equal(instances[0].options.uin,'123456789');
 assert.equal(instances[0].options.cap_cd,'abc+def**');
 instances[0].callback({ret:0,ticket:'terror_1006_synthetic',randstr:'@x'});
 assert.equal(elements.result.hidden,true);assert.ok(elements.error.textContent);
 elements.retry.onclick();await new Promise(setImmediate);
 instances[0].callback({ret:0,ticket:'stale-ticket'});assert.equal(elements.result.hidden,true);
 instances[1].callback({ret:0,ticket:'synthetic-valid-ticket',randstr:'synthetic-randstr'});
 assert.equal(elements.result.hidden,false);assert.equal(elements.ticket.value,'synthetic-valid-ticket');
 await elements.copy.onclick();await elements['copy-json'].onclick();
 assert.equal(clipboard[0],'synthetic-valid-ticket');assert.deepEqual(JSON.parse(clipboard[1]),{ticket:'synthetic-valid-ticket',randstr:'synthetic-randstr'});
 elements.source.value='https://evil.example/?sid=123';elements['source-form'].onsubmit({preventDefault(){}});
 assert.ok(elements.error.textContent.includes('验证链接'));assert.equal(instances.length,2);
 assert.ok(!/localStorage|sessionStorage|fetch\s*\(/.test(script));
 console.log('Standalone Ticket helper: challenge parameters, genuine success, load errors, stale callbacks, copy and URL checks passed (synthetic SDK, no CAPTCHA performed).');
}
run().catch(e=>{console.error(e);process.exitCode=1});
