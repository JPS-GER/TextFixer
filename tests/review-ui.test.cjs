const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
class Element {
  constructor(){this.children=[];this.value='';this.textContent='';this.hidden=false;this.disabled=false;this.listeners={};this.dataset={};this.classList={toggle(){}};}
  append(...items){this.children.push(...items);}
  replaceChildren(){this.children=[];}
  setAttribute(){}
  addEventListener(event,handler){this.listeners[event]=handler;}
  querySelectorAll(tag){return this.children.flatMap(child=>[...(child.tag===tag?[child]:[]),...child.querySelectorAll(tag)]);}
  focus(){}
  showModal(){this.open=true;}
  close(){this.open=false;}
  set innerHTML(value){throw new Error('Untrusted review text must never be assigned to innerHTML');}
}
const elements=new Map(),sent=[];
const element=id=>{if(!elements.has(id))elements.set(id,new Element());return elements.get(id);};
let receive;
const sandbox={document:{getElementById:element,createElement:tag=>Object.assign(new Element(),{tag}),querySelectorAll:()=>[...element('inline-text').querySelectorAll('button'),...element('changes').querySelectorAll('input')]},window:{chrome:{webview:{postMessage:m=>sent.push(m),addEventListener:(_,handler)=>receive=handler}}}};
vm.createContext(sandbox);
for(const file of ['diff.js','app.js'])vm.runInContext(fs.readFileSync(`src/TextFixer/Web/${file}`,'utf8'),sandbox);
const message=m=>receive({data:m});
assert.equal(sent[0].action,'ready');
assert.ok(!element('settings').open,'setup dialog stays closed during ordinary review');
element('open-settings').onclick();assert.equal(element('settings').open,true,'settings opens explicitly');
element('close-settings').onclick();assert.equal(element('settings').open,false,'settings closes back to review');
message({type:'status',status:{connected:true,message:'Using ChatGPT plan'}});
const original='A tesst.\r\nText <script>evil()</script>';
message({type:'capture',text:original,canReplace:true,hasFormatting:true});
assert.equal(element('route-title').textContent,'CLIPBOARD MODE');
assert.equal(element('apply').disabled,true,'clipboard replacement is opt-in to prevent formatting loss');
element('allow-plain-text').checked=true;element('allow-plain-text').onchange();
assert.equal(element('apply').disabled,false,'explicit plain-text choice enables clipboard apply');
element('check').onclick();assert.equal(sent.at(-1).text,original,'CRLF must survive textarea display');
message({type:'result',original,result:{correctedText:original.replace('tesst','test'),findings:[{kind:'ambiguity',quotes:['<script>evil()</script>'],explanation:'A question?',suggestedRewrite:null}]},canReplace:true,model:'test'});
assert.equal(element('results').hidden,false);
assert.equal(element('preview').textContent,original.replace('tesst','test'));
assert.equal(element('inline-text').children.map(p=>p.tag==='span'?p.textContent:p.children.find(c=>c.tag==='del')?.textContent||'').join(''),original,'inline review preserves original context and CRLF');
const correction=element('inline-text').querySelectorAll('button')[0];
assert.equal(correction.children.find(c=>c.tag==='del').textContent,'tesst');
assert.equal(correction.children.find(c=>c.tag==='ins').textContent,'test');
correction.onclick();assert.equal(element('preview').textContent,original,'click rejects inline correction');
assert.equal(element('changes').querySelectorAll('input')[0].checked,false,'inline rejection synchronizes correction list');
element('inline-text').querySelectorAll('button')[0].onclick();
assert.equal(element('preview').textContent,original.replace('tesst','test'),'click accepts inline correction');
element('apply').onclick();assert.equal(sent.at(-1).original,original,'apply carries exact reviewed original');
assert.equal(sent.at(-1).text,original.replace('tesst','test'));
assert.equal(sent.at(-1).edits.length,1,'direct editor gets locally calculated corrections');
assert.equal(sent.at(-1).edits[0].before,'s');
assert.equal(sent.at(-1).edits[0].after,'');
element('reject-all').onclick();assert.equal(element('preview').textContent,original);
element('accept-all').onclick();assert.equal(element('preview').textContent,original.replace('tesst','test'));
message({type:'busy',busy:true,action:'check'});assert.equal(element('apply').disabled,true);assert.equal(element('source').disabled,true);
assert.equal(element('inline-text').querySelectorAll('button')[0].disabled,true,'inline controls are disabled during operations');
message({type:'busy',busy:false});
element('source').value='edited';element('source').listeners.input();
assert.equal(element('apply').disabled,true,'editing source invalidates replacement');
assert.equal(element('results').hidden,true,'editing source invalidates results');
message({type:'error',message:'Reconnect',code:'test',requiresSignIn:true});assert.equal(element('check').disabled,true);
message({type:'result',original:'First paragraph.\r\n\r\nSecond tesst.',result:{correctedText:'First paragraph.\r\nSecond test.',findings:[]},canReplace:true});
assert.ok(element('preview').textContent.includes('\r\n\r\n'),'model cannot remove blank paragraph separator');
element('accept-all').onclick();
assert.ok(element('preview').textContent.includes('\r\n\r\n'),'accept all cannot enable paragraph-layout changes');
message({type:'busy',busy:false});
assert.ok(element('inline-text').querySelectorAll('button').some(b=>b.dataset.layoutProtected==='true'&&b.disabled),'layout protection persists across state updates');
message({type:'capture',mode:'chrome',text:original,canReplace:true,hasFormatting:true});
assert.equal(element('apply').textContent,'Apply in Chrome');
assert.equal(element('route-title').textContent,'DIRECT CHROME EDITING');
assert.equal(element('allow-plain-text').checked,false,'new capture resets clipboard replacement permission');
assert.equal(element('formatting').hidden,true,'direct Chrome capture does not show plain-paste formatting warning');
message({type:'targetLost'});assert.equal(element('apply').disabled,true,'bridge disconnect invalidates apply');
const nonbreaking='One\u00a0\u00a0two';
message({type:'result',mode:'chrome',original:nonbreaking,result:{correctedText:'One  two',findings:[]},canReplace:true});
assert.equal(element('preview').textContent,nonbreaking,'space representation alone does not rewrite NBSP');
element('apply').onclick();assert.equal(sent.at(-1).edits.length,0,'NBSP-only suggestions do not become editor writes');
message({type:'chromeStatus',connected:false,registered:false,folder:'C:/example/chrome-extension'});
assert.equal(element('setup-details').open,true,'missing registration exposes setup');
element('register-chrome').onclick();assert.equal(sent.at(-1).action,'registerChrome','registration requires explicit setup action');
message({type:'chromeStatus',connected:true,registered:true});
assert.equal(element('setup-details').open,false,'connected bridge collapses setup');
message({type:'capture',mode:'clipboard',text:original,canReplace:true,hasFormatting:true,canPreserveFormatting:true});
assert.equal(element('apply').textContent,'Paste with formatting');
assert.equal(element('apply').disabled,false,'rich clipboard replacement does not require plain-text opt-in');
assert.equal(element('copy').textContent,'Copy with formatting');
message({type:'result',mode:'clipboard',original,result:{correctedText:original.replace('tesst','test'),findings:[]},canReplace:true,canPreserveFormatting:true});
element('copy').onclick();assert.equal(sent.at(-1).original,original,'rich copy binds to original selection');
assert.equal(sent.at(-1).edits[0].before,'s','rich copy sends minimal exact local patch');
element('allow-plain-text').checked=true;element('allow-plain-text').onchange();
assert.equal(element('apply').textContent,'Paste plain text','plain-text fallback remains explicit');
console.log('PASS: review UI message contract, CRLF, source invalidation, busy guards, accept/reject, and safe text rendering.');
