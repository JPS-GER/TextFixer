const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
class Element {
  constructor(){this.children=[];this.value='';this.textContent='';this.hidden=false;this.disabled=false;this.listeners={};this.dataset={};this.classList={toggle(){}};}
  append(...items){this.children.push(...items);}
  replaceChildren(){this.children=[];}
  setAttribute(key,value){(this.attributes??={})[key]=value;}
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
const sandbox={document:{getElementById:element,createElement:tag=>Object.assign(new Element(),{tag}),querySelectorAll:selector=>selector.split(',').flatMap(part=>{const match=part.trim().match(/^#([\w-]+) (\w+)$/);return match?element(match[1]).querySelectorAll(match[2]):[];})},window:{chrome:{webview:{postMessage:m=>sent.push(m),addEventListener:(_,handler)=>receive=handler}}}};
vm.createContext(sandbox);
for(const file of ['diff.js','review-model.js','markup.js','app.js'])vm.runInContext(fs.readFileSync(`src/TextFixer/Web/${file}`,'utf8'),sandbox);
const message=m=>receive({data:m});
const selectTab=key=>element('review-tabs').children.find(b=>b.dataset.category===key)?.onclick();
assert.equal(sent[0].action,'ready');
assert.equal(element('route').hidden,true,'no capture label on startup');
assert.ok(!element('settings').open,'setup dialog stays closed during ordinary review');
element('open-settings').onclick();assert.equal(element('settings').open,true,'settings opens explicitly');
element('close-settings').onclick();assert.equal(element('settings').open,false,'settings closes back to review');
message({type:'status',status:{connected:true,message:'Using ChatGPT plan'}});
const original='A tesst.\r\nText <script>evil()</script>';
message({type:'capture',text:original,canReplace:true,hasFormatting:true});
assert.equal(element('route-title').textContent,'Captured with Alt+Q');
assert.equal(element('apply').disabled,true,'clipboard replacement is opt-in to prevent formatting loss');
element('allow-plain-text').checked=true;element('allow-plain-text').onchange();
assert.equal(element('apply').disabled,false,'explicit plain-text choice enables clipboard apply');
assert.equal(sent.filter(m=>m.action==='check').at(-1).text,original,'automatic review preserves CRLF');
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
message({type:'error',message:'Reconnect',code:'test',requiresSignIn:true});
message({type:'result',original:'First paragraph.\r\n\r\nSecond tesst.',result:{correctedText:'First paragraph.\r\nSecond test.',findings:[]},canReplace:true});
assert.ok(element('preview').textContent.includes('\r\n\r\n'),'model cannot remove blank paragraph separator');
element('accept-all').onclick();
assert.ok(element('preview').textContent.includes('\r\n\r\n'),'accept all cannot enable paragraph-layout changes');
message({type:'busy',busy:false});
selectTab('punctuation');
assert.ok(element('inline-text').querySelectorAll('button').some(b=>b.dataset.layoutProtected==='true'&&b.disabled),'layout protection persists across state updates');
message({type:'capture',mode:'chrome',text:original,canReplace:true,hasFormatting:true});
assert.equal(element('apply').textContent,'Apply in Chrome');
assert.equal(element('route-title').textContent,'Captured from Chrome');
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

assert.ok(!fs.readFileSync('src/TextFixer/Web/index.html','utf8').includes('id="formatting-checks"'),'formatting checks need no Settings toggle');
element('allow-plain-text').checked=false;
message({type:'result',original:'word next',result:{correctedText:'word next',findings:[]},canReplace:true,canPreserveFormatting:true,formattingSuggestions:[{id:3,start:4,end:5,description:'Remove underline from trailing space',context:'word.',property:'underline',runStart:0,runEnd:5}]});
assert.equal(element('formatting-results').hidden,false,'formatting opens when it is the first non-empty tab');selectTab('formatting');assert.equal(element('formatting-results').hidden,false);
element('copy').onclick();assert.equal(sent.at(-1).formattingIds.length,0,'formatting fixes are individually opt-in');
const formatInput=element('formatting-findings').querySelectorAll('input')[0];formatInput.checked=true;formatInput.onchange();
element('copy').onclick();assert.equal(sent.at(-1).formattingIds[0],3);
message({type:'capture',text:'other',canReplace:true});
element('copy').onclick();assert.equal(sent.at(-1).formattingIds.length,0,'new capture clears formatting acceptance');

assert.ok(!element('help').open,'Help stays closed during review');
element('open-help').onclick();assert.equal(element('help').open,true);
element('close-help').onclick();assert.equal(element('help').open,false);
element('open-help').onclick();message({type:'capture',text:'new selection',canReplace:true});
assert.equal(element('help').open,false,'capture returns to review');
const html=fs.readFileSync('src/TextFixer/Web/index.html','utf8');
const main=html.slice(html.indexOf('<main>'),html.indexOf('</main>'));
assert.ok(!main.includes('right-click')&&!main.includes('Alt+Q'),'capture instructions live in Help');

assert.equal(element('route').hidden,false,'capture shows its source');
element('source').value='manually edited';element('source').listeners.input();assert.equal(element('route').hidden,true,'manual text no longer claims a captured source');

message({type:'capture',text:'',canReplace:false});assert.equal(element('source-section').hidden,true,'empty startup capture hides text section');
message({type:'capture',text:'selected text',canReplace:true,mode:'clipboard'});assert.equal(element('source-section').hidden,false,'Alt+Q capture shows text section');
message({type:'capture',text:'Chrome text',canReplace:true,mode:'chrome'});assert.equal(element('source-section').hidden,false,'Chrome capture shows text section');

message({type:'busy',busy:false});message({type:'status',status:{connected:true,message:'Using ChatGPT plan'}});
let checkCount=sent.filter(m=>m.action==='check').length;
message({type:'capture',text:'Auto review',canReplace:true,mode:'clipboard'});
assert.equal(sent.filter(m=>m.action==='check').length,checkCount+1,'Alt+Q automatically starts review');
message({type:'status',status:{connected:true,message:'Using ChatGPT plan'}});
assert.equal(sent.filter(m=>m.action==='check').length,checkCount+1,'status refresh does not repeat review');
message({type:'capture',text:'Chrome auto review',canReplace:true,mode:'chrome'});
assert.equal(sent.at(-1).text,'Chrome auto review','Chrome automatically starts review');
message({type:'status',status:{connected:false,message:'Sign in'}});
checkCount=sent.filter(m=>m.action==='check').length;
message({type:'capture',text:'Await connection',canReplace:true});
assert.equal(sent.filter(m=>m.action==='check').length,checkCount,'capture waits for connection');
message({type:'status',status:{connected:true,message:'Using ChatGPT plan'}});
assert.equal(sent.filter(m=>m.action==='check').length,checkCount+1,'capture starts when connection becomes ready');

assert.ok(!fs.readFileSync('src/TextFixer/Web/index.html','utf8').includes('id="check"'),'manual Check button removed');
assert.ok(!fs.readFileSync('src/TextFixer/Web/index.html','utf8').includes('id="scope"'),'redundant review scope removed');

message({type:'capture',text:'Original unmarked text',canReplace:true});
assert.equal(element('original-text').textContent,'Original unmarked text');
assert.equal(element('source-section').open,false,'original text starts collapsed');
const reviewHtml=fs.readFileSync('src/TextFixer/Web/index.html','utf8');
assert.ok(reviewHtml.indexOf('Corrections in context')<reviewHtml.indexOf('Show original text'),'corrections appear before collapsed original');
assert.ok(!reviewHtml.includes('Your text</h2>'),'old source heading removed');

message({type:'notice',message:'Review ready.'});assert.equal(element('notice').textContent,'Review ready.');
message({type:'notice',message:''});assert.equal(element('notice').textContent,'Ready');assert.equal(element('notice').hidden,false);
assert.ok(fs.readFileSync('src/TextFixer/Web/index.html','utf8').includes('<footer class="status-bar"'),'messages use bottom status bar');

message({type:'capture',text:'Chrome selection',canReplace:true,mode:'chrome'});
assert.equal(element('chrome-icon').hidden,false);assert.equal(element('clipboard-icon').hidden,true);
message({type:'capture',text:'AltQ selection',canReplace:true,mode:'clipboard'});
assert.equal(element('chrome-icon').hidden,true);assert.equal(element('clipboard-icon').hidden,false);
message({type:'busy',busy:true,action:'check'});assert.equal(element('cancel').hidden,false);
element('cancel').onclick();assert.equal(sent.at(-1).action,'cancel','Stop cancels current operation');
message({type:'busy',busy:false});assert.equal(element('cancel').hidden,true);

message({type:'busy',busy:false});element('allow-plain-text').checked=false;
function formattingResult(property,original='word next',start=4,end=5,runStart=0,runEnd=5){message({type:'result',mode:'clipboard',original,result:{correctedText:original,findings:[]},canReplace:true,canPreserveFormatting:true,formattingSuggestions:[{id:7,start,end,runStart,runEnd,property,description:'Remove '+property+' from space'}]});selectTab('formatting');}
for(const property of ['underline','strikethrough','link']){
 formattingResult(property);
 let button=element('formatting-findings').querySelectorAll('button')[0];
 assert.ok(button,'formatting has clickable contextual preview');
 const [before,after]=button.children;
 assert.equal(before.children[1].className,'format-decoration format-'+property);
 assert.equal(before.children[1].children[1].textContent,'\u00b7','old space remains inside decoration');
 assert.equal(after.children[2].textContent,'\u00b7','new space is outside decoration');
 element('copy').onclick();assert.equal(sent.at(-1).formattingIds.length,0,'preview starts rejected');
 button.onclick();assert.equal(element('formatting-findings').querySelectorAll('input')[0].checked,true,'preview accepts and syncs checkbox');
 element('copy').onclick();assert.equal(sent.at(-1).formattingIds[0],7);
 element('formatting-findings').querySelectorAll('button')[0].onclick();element('copy').onclick();assert.equal(sent.at(-1).formattingIds.length,0,'preview can reject again');
}
formattingResult('underline','first word',5,6,5,10);
let leadingButton=element('formatting-findings').querySelectorAll('button')[0];
assert.equal(leadingButton.children[1].children[1].textContent,'\u00b7','leading space moves before decoration');
message({type:'busy',busy:true,action:'check'});
assert.equal(leadingButton.disabled,true,'busy state disables formatting preview');leadingButton.onclick();
message({type:'busy',busy:false});element('copy').onclick();assert.equal(sent.at(-1).formattingIds.length,0,'busy click cannot accept');
element('allow-plain-text').checked=true;element('allow-plain-text').onchange();
assert.equal(element('formatting-findings').querySelectorAll('button')[0].disabled,true,'plain text disables formatting preview');
element('allow-plain-text').checked=false;element('allow-plain-text').onchange();
formattingResult('underline','<script>bad()</script> next',21,22,0,22);
assert.ok(element('formatting-findings').querySelectorAll('button')[0],'untrusted text rendered without innerHTML');
console.log('PASS: formatting previews, before/after decoration, leading/trailing spaces, acceptance, and busy/plain guards.');

const previewCss=fs.readFileSync('src/TextFixer/Web/style.css','utf8');
assert.match(previewCss,/\.format-space\s*\{\s*display:inline;\s*white-space:pre;/,'space stays in parent decoration flow');
assert.ok(!/\.format-space\s*\{[^}]*inline-block/.test(previewCss),'space marker is not an atomic inline');

assert.match(previewCss,/\.inline-text\s*\{[^}]*font:calc\(15px \* var\(--text-scale\)\)\/calc\(23\.8px \* var\(--text-scale\)\)/, 'correction preview uses original-text line step with larger 15px font');
assert.match(previewCss,/\.inline-correction\s*\{[^}]*line-height:inherit/, 'correction chips inherit preview line spacing');


// Categorized review, inert full context and scoped bulk actions.
const categoriesOriginal='A tesst. He go there, clear words.';
const categoryChanges=[['tesst','test','spelling'],['go','goes','grammar'],[',',';','punctuation'],['clear','concise','wording']];
let categoriesCorrected=categoriesOriginal;
for(const [before,after] of categoryChanges)categoriesCorrected=categoriesCorrected.replace(before,after);
const corrections=categoryChanges.map(([before,after,category])=>({start:categoriesOriginal.indexOf(before),end:categoriesOriginal.indexOf(before)+before.length,after,category}));
message({type:'result',original:categoriesOriginal,result:{correctedText:categoriesCorrected,corrections,findings:[{kind:'term_misuse',quotes:['go there'],explanation:'Could this be clearer?',suggestedRewrite:'travels there'}]},canReplace:true,canPreserveFormatting:true});
const tabs=()=>element('review-tabs').children;
assert.deepEqual(tabs().map(t=>t.children[0].textContent),['Spelling','Grammar','Punctuation','Wording','Worth checking','Formatting']);
assert.equal(tabs()[0].attributes['aria-selected'],'true','Spelling is default');
assert.equal(tabs()[5].disabled,true,'zero-count tab visible but disabled');
assert.equal(element('worth-results').hidden,true,'finding cards wait for Worth checking tab');
assert.equal(element('inline-text').querySelectorAll('button').length,1,'only active correction is clickable');
assert.ok(element('inline-text').children.filter(c=>c.className==='inactive-correction').length>=3,'other corrections appear faded');
element('reject-all').onclick();
assert.equal(element('preview').textContent,categoriesCorrected.replace('test','tesst'),'reject all affects Spelling only');
selectTab('grammar');element('reject-all').onclick();
assert.equal(element('preview').textContent,categoriesCorrected.replace('test','tesst').replace('goes','go'),'Grammar bulk action leaves other choices');
selectTab('worth');assert.equal(element('worth-results').hidden,false);
let use=element('findings').querySelectorAll('button').find(b=>b.className==='use-wording');assert.equal(use.textContent,'Use this wording');
use.onclick();assert.ok(element('preview').textContent.includes('He travels there'),'finding wording included');
assert.equal(element('inline-text').querySelectorAll('button').length,1,'Worth checking highlights its replacement');
selectTab('grammar');assert.equal(element('changes').querySelectorAll('input')[0].disabled,true,'overlapping grammar correction superseded');
element('accept-all').onclick();assert.ok(element('preview').textContent.includes('travels there'),'superseded correction cannot be reactivated');
selectTab('worth');element('inline-text').querySelectorAll('button')[0].onclick();
assert.ok(element('preview').textContent.includes('He go there'),'reject wording restores previously rejected grammar');
selectTab('grammar');element('accept-all').onclick();
selectTab('worth');use=element('findings').querySelectorAll('button').find(b=>b.className==='use-wording');use.onclick();
const undo=element('findings').querySelectorAll('button').find(b=>b.className==='use-wording');assert.equal(undo.textContent,'Undo wording');undo.onclick();
assert.ok(element('preview').textContent.includes('He goes there'),'undo restores previously accepted grammar');
// Repeated (including overlapping) quotes do not offer a replacement button.
message({type:'result',original:'aaa one one',result:{correctedText:'aaa one one',findings:[{kind:'ambiguity',quotes:['aa'],explanation:'First',suggestedRewrite:'b'},{kind:'logic_gap',quotes:['one'],explanation:'Second',suggestedRewrite:'two'}]},canReplace:true});
selectTab('worth');assert.equal(element('findings').querySelectorAll('button').filter(b=>b.className==='use-wording').length,0);
assert.deepEqual(element('findings').children.map(c=>c.children[0].children[0].textContent),['ambiguity','logic gap'],'all finding kinds retain labels');
message({type:'result',original:'First.\r\nSecond.',result:{correctedText:'First.\r\nSecond.',findings:[{kind:'contradiction',quotes:['First.\r\nSecond.'],explanation:'Question',suggestedRewrite:'One.'}]},canReplace:true});
selectTab('worth');assert.equal(element('findings').querySelectorAll('button').find(b=>b.className==='use-wording').disabled,true,'wording cannot change line breaks');
message({type:'result',original:'word next',result:{correctedText:'Word next',findings:[]},canReplace:true,canPreserveFormatting:true,formattingSuggestions:[{id:7,start:4,end:5,runStart:0,runEnd:5,property:'underline',description:'Remove underline'}]});selectTab('formatting');
element('accept-all').onclick();element('copy').onclick();assert.equal(sent.at(-1).formattingIds[0],7,'Formatting bulk action is explicitly scoped');
selectTab('spelling');element('reject-all').onclick();element('copy').onclick();assert.equal(sent.at(-1).formattingIds[0],7,'inactive category cannot reject formatting');
selectTab('formatting');element('reject-all').onclick();element('copy').onclick();assert.equal(sent.at(-1).formattingIds.length,0);
console.log('PASS: category tabs and counts, inert context, scoped bulk actions, wording acceptance/superseding/undo, unique quote guards.');

const selectedTab=()=>tabs().find(t=>t.attributes['aria-selected']==='true').dataset.category;
const categoryNames=['spelling','grammar','punctuation','wording'];
for(const category of categoryNames){
 message({type:'result',original:'bad',result:{correctedText:'good',corrections:[{start:0,end:3,after:'good',category}],findings:[]},canReplace:true});
 assert.equal(selectedTab(),category,'first non-empty language tab: '+category);
 for(const t of tabs())if(t.dataset.category!==category){assert.equal(t.disabled,true);assert.equal(Number(t.children[1].textContent),0);}
}
message({type:'result',original:'original',result:{correctedText:'original',findings:[{kind:'ambiguity',quotes:['original'],explanation:'Check it',suggestedRewrite:'different'}]},canReplace:true});
assert.equal(selectedTab(),'worth','findings open when language tabs are empty');
formattingResult('underline');assert.equal(selectedTab(),'formatting','formatting opens when earlier tabs are empty');
message({type:'result',original:'fine',result:{correctedText:'fine',findings:[]},canReplace:true});
assert.equal(selectedTab(),'spelling','all-empty review keeps Spelling selected');assert.ok(tabs().every(t=>t.disabled));
message({type:'result',original:'First.\r\nSecond.',result:{correctedText:'First.\r\nSecond.',findings:[{kind:'logic_gap',quotes:['First.\r\nSecond.'],explanation:'Check',suggestedRewrite:'One.'}]},canReplace:true});
const disabledWording=element('findings').querySelectorAll('button').find(b=>b.className==='use-wording');
const visibleReason=element('findings').querySelectorAll('p').find(p=>p.className==='hint wording-reason');
assert.ok(disabledWording.disabled);assert.equal(visibleReason.textContent,'This rewrite changes line breaks and cannot be applied.');
assert.equal(disabledWording.attributes['aria-describedby'],visibleReason.id,'disabled reason is associated with its button');
console.log('PASS: first non-empty tab, all-empty fallback, zero counts and visible wording restriction.');

for(const formatted of [true,false]){
 message({type:'applied',formatted,clipboardRestored:false});
 assert.equal(element('notice').textContent,'Paste sent. TextFixer cannot verify the result in the editor. Please check it there. The revised text remains on the clipboard.');
 assert.equal(element('apply').disabled,true,'paste consumes the capture');
 message({type:'applied',formatted,clipboardRestored:true});
 assert.equal(element('notice').textContent,'Paste sent. TextFixer cannot verify the result in the editor. Please check it there.');
}
assert.ok(!fs.readFileSync('src/TextFixer/ReviewWindow.cs','utf8').includes("target's clipboard read could not be confirmed"),'normal paste status is not overwritten by the old warning');
console.log('PASS: neutral Alt+Q paste status and retained-clipboard information.');
{
 const text='Ein Absatz mit Fehlrn.';
 message({type:'capture',text,canReplace:false,readOnly:true,hasFormatting:true,canPreserveFormatting:true});
 message({type:'result',original:text,result:{correctedText:'Ein Absatz mit Fehlern.',findings:[],corrections:[]},canReplace:false,readOnly:true,canPreserveFormatting:true});
 assert.equal(element('apply').hidden,true,'read-only capture hides the paste button');
 assert.equal(element('readonly-hint').hidden,false,'read-only capture explains copy-only');
 assert.equal(element('route').hidden,false,'read-only capture still shows the Alt+Q route');
 assert.equal(element('copy').disabled,false,'copy stays available');
 assert.equal(element('copy').textContent,'Copy with formatting','formatted read-only capture copies with formatting');
 message({type:'capture',text,canReplace:true,readOnly:false});
 assert.equal(element('apply').hidden,false,'editable capture shows the paste button again');
 assert.equal(element('readonly-hint').hidden,true);
}
console.log('PASS: read-only Alt+Q capture offers copy only.');
{
 // One request per tab: progressive results, tab states, merged edits, decisions kept across updates.
 const original='Leute und frauen die dass wissen.';
 const meta={canReplace:true,readOnly:false,canPreserveFormatting:false,formattingSuggestions:[],model:'m',mode:'clipboard'};
 const all=['spelling','grammar','punctuation','wording','worth'];
 message({type:'capture',text:original,canReplace:true});
 message({type:'busy',busy:true,action:'check'});
 // All requests run at once; answers arrive in any order.
 let open=[...all];
 const step=(i,result,error=null)=>{open=open.filter(k=>k!==all[i]);message({type:'tabResult',original,focus:all[i],result,error,pending:open,...meta});};
 message({type:'tabProgress',original,running:all});
 const states=()=>element('review-tabs').children.map(b=>b.className.replace('review-tab','').replace('selected','').trim());
 assert.deepEqual(states().slice(0,5),['state-running','state-running','state-running','state-running','state-running'],'all tabs run at once');
 step(2,{correctedText:original.replace('frauen die','frauen, die'),findings:[],corrections:[]});
 assert.ok(element('notice').textContent.includes('Checking Spelling, Grammar, Wording and Worth checking.'),'notice lists the tabs still running');
 step(0,{correctedText:original.replace('frauen','Frauen'),findings:[],corrections:[]});
 assert.equal(element('results').hidden,false,'first tab result is shown while the rest run');
 assert.equal(element('copy').disabled,true,'copy waits for the remaining tabs');
 const spelling=element('inline-text').children.find(c=>c.tag==='button');
 assert.equal(spelling.disabled,false,'finished tab can be reviewed while the rest run');
 spelling.onclick();
 step(1,{correctedText:original.replace('dass','das'),findings:[],corrections:[]});
 assert.deepEqual(states().slice(0,5),['state-ready','state-ready','state-ready','state-running','state-running'],'answered tabs ready, the others still running');
 step(3,null,'Network error');
 step(4,{correctedText:original,corrections:[],findings:[{kind:'ambiguity',explanation:'?',quotes:['Leute'],suggestedRewrite:null}]});
 message({type:'busy',busy:false});
 assert.equal(states()[3],'state-failed','failed tab is marked');
 assert.equal(element('preview').textContent,'Leute und frauen, die das wissen.','left tab wins only on real conflicts; rejected spelling stays rejected; comma insertion beside it is kept');
 assert.ok(element('notice').textContent.includes('Not checked: Wording (Network error)'));
}
console.log('PASS: one request per tab shows progressive results with tab states.');
{
 // The same fix from several tabs is applied once ("das" -> "dass" must not become "dasss").
 const original='Ich glaube das wir gewinnen.';
 const meta={canReplace:true,readOnly:false,canPreserveFormatting:false,formattingSuggestions:[],model:'m',mode:'clipboard'};
 const all=['spelling','grammar','punctuation','wording','worth'];
 message({type:'capture',text:original,canReplace:true});message({type:'busy',busy:true,action:'check'});
 const answers=[original.replace('das','dass'),original.replace('das','dass'),original.replace('glaube','glaube,'),original.replace('das','dass'),original];
 message({type:'tabProgress',original,running:all});
 // Arrival order differs from tab order.
 [3,2,0,4,1].forEach((i,n,order)=>message({type:'tabResult',original,focus:all[i],result:{correctedText:answers[i],findings:[],corrections:[]},error:null,pending:order.slice(n+1).map(j=>all[j]),...meta}));
 message({type:'busy',busy:false});
 assert.equal(element('preview').textContent,'Ich glaube, dass wir gewinnen.','duplicate tab fixes are applied once');
}
console.log('PASS: duplicate fixes from several tabs are applied once.');
{
 // Alt+E: the capture is translated, not reviewed; the translation can be copied or pasted over the selection.
 message({type:'status',status:{connected:true}});
 const original='Ich glaube, dass wir gewinnen.';
 sent.length=0;
 message({type:'capture',task:'translate',text:original,canReplace:true,readOnly:false});
 assert.equal(sent.at(-1)?.action,'translate','Alt+E capture starts a translation');
 assert.equal(sent.at(-1).text,original);
 assert.ok(!sent.some(s=>s.action==='check'),'Alt+E capture is not reviewed');
 message({type:'busy',busy:false});
 message({type:'translation',original,translation:'I think we will win.',canReplace:true,readOnly:false,model:'m'});
 assert.equal(element('translation-view').hidden,false);
 assert.equal(element('results').hidden,true);
 assert.equal(element('translation-text').textContent,'I think we will win.');
 assert.equal(element('apply-translation').hidden,false);
 element('apply-translation').onclick();
 assert.deepEqual({action:sent.at(-1).action,text:sent.at(-1).text,original:sent.at(-1).original},{action:'applyTranslation',text:'I think we will win.',original});
 element('copy-translation').onclick();
 assert.equal(sent.at(-1).action,'copyTranslation');
 message({type:'translation',original,translation:'I think we will win.',canReplace:false,readOnly:true,model:'m'});
 assert.equal(element('apply-translation').hidden,true,'read-only translation offers copy only');
 assert.equal(element('translation-readonly').hidden,false);
 message({type:'capture',text:original,canReplace:true,readOnly:false});
 assert.equal(element('translation-view').hidden,true,'Alt+Q capture hides the translation');
 assert.equal(sent.at(-1).action,'check');
}
console.log('PASS: Alt+E translates the capture.');
{
 // Punctuation style: introduced typographic quotes and dashes become straight; the user's own stay.
 const keepAll={quotes:'keep',dashes:'keep',ellipsis:'keep',spaces:'keep',multiply:'keep',arrows:'keep',primes:'keep'};
 message({type:'reviewMode',mode:'single',punctuationStyle:keepAll});
 const original='Er sagte "hallo" - und ging. Sie las „Faust“.';
 message({type:'capture',text:original,canReplace:true});
 message({type:'result',original,result:{correctedText:'Er sagte „Hallo“ – und ging. Sie las „Faust“.',findings:[{kind:'ambiguity',explanation:'?',quotes:['und ging'],suggestedRewrite:'und ging – sofort'}],corrections:[]},canReplace:true,model:'m',mode:'clipboard'});
 assert.equal(element('preview').textContent,'Er sagte "Hallo" - und ging. Sie las „Faust“.','typographic quotes and dash are not introduced; existing ones stay');
 assert.equal(element('inline-text').children.filter(c=>c.tag==='button').length,1,'only the real fix remains a correction');
 message({type:'busy',busy:false});
 message({type:'translation',original:'He said "yes".',translation:'Er sagte „ja“.',canReplace:true,readOnly:false,model:'m'});
 assert.equal(element('translation-text').textContent,'Er sagte "ja".');
 {
  // Make plain: the user's own typographic characters become Punctuation corrections; Allow typographic leaves answers alone.
  message({type:'reviewMode',mode:'single',punctuationStyle:{...keepAll,quotes:'plain',spaces:'plain',ellipsis:'plain',arrows:'typographic'}});
  const own='Sie las \u201EFaust\u201C\u2026 15\u00A0% A \u2192 B';
  message({type:'capture',text:own,canReplace:true});
  message({type:'result',original:own,result:{correctedText:own.replace('A \u2192 B','A \u2192 B \u2192 C'),findings:[],corrections:[]},canReplace:true,model:'m',mode:'clipboard'});
  assert.equal(element('preview').textContent,'Sie las "Faust"... 15 % A \u2192 B \u2192 C','plain groups convert the user text; typographic arrows stay');
  const punctuation=element('review-tabs').children.find(b=>b.dataset.category==='punctuation');
  assert.ok(Number(punctuation.children[1].textContent)>=2,'style conversions are Punctuation corrections');
  // One request per tab: style conversions come once, from the Punctuation tab.
  const all=['spelling','grammar','punctuation','wording','worth'];
  message({type:'reviewMode',mode:'focused',punctuationStyle:{...keepAll,quotes:'plain'}});
  const text='Er sagte \u201Ehallo\u201C.';
  message({type:'capture',text,canReplace:true});message({type:'busy',busy:true,action:'check'});
  message({type:'tabProgress',original:text,running:all});
  all.forEach((focus,i)=>message({type:'tabResult',original:text,focus,result:{correctedText:focus==='spelling'?text.replace('hallo','Hallo'):text,findings:[],corrections:[]},error:null,pending:all.slice(i+1),canReplace:true,readOnly:false,canPreserveFormatting:false,formattingSuggestions:[],model:'m',mode:'clipboard'}));
  message({type:'busy',busy:false});
  assert.equal(element('preview').textContent,'Er sagte "Hallo".');
 }
 message({type:'reviewMode',mode:'single',punctuationStyle:keepAll});
}
console.log('PASS: punctuation style keeps, converts or allows each group.');
{
 // Translation change request: sends source, current translation and the wish; the answer replaces the translation.
 const original='Kannst du mir die Datei schicken?';
 message({type:'capture',task:'translate',text:original,canReplace:true,readOnly:false});
 message({type:'busy',busy:false});
 message({type:'translation',original,translation:'Can you send me the file?',canReplace:true,readOnly:false,model:'m'});
 element('translation-request').value='  more formal ';
 element('revise-translation').onsubmit({preventDefault(){}});
 assert.deepEqual({...sent.at(-1)},{action:'reviseTranslation',text:original,translation:'Can you send me the file?',request:'more formal',captureId:0});
 message({type:'busy',busy:true,action:'reviseTranslation'});
 assert.equal(element('revise-translation-button').disabled,true,'revise waits while busy');
 message({type:'busy',busy:false});
 message({type:'translation',original,translation:'Could you please send me the file?',revised:true,canReplace:true,readOnly:false,model:'m'});
 assert.equal(element('translation-text').textContent,'Could you please send me the file?');
 assert.equal(element('translation-request').value,'','request field is cleared after a revision');
 element('apply-translation').onclick();
 assert.equal(sent.at(-1).text,'Could you please send me the file?','paste uses the revised translation');
 const before=sent.length;element('translation-request').value='  ';element('revise-translation').onsubmit({preventDefault(){}});
 assert.equal(sent.length,before,'empty request is not sent');
}
console.log('PASS: translation change requests revise the translation.');
{
 // Settings > Model: Automatic plus each listed model; choosing one saves its slug.
 const models=[{slug:'gpt-a',displayName:'GPT A'},{slug:'gpt-b',displayName:'GPT B'}];
 message({type:'models',connected:true,models,selected:'gpt-b'});
 const options=element('model-list').children;
 assert.deepEqual(options.map(o=>o.children[1].textContent),['Automatic','GPT A','GPT B']);
 assert.equal(options[0].children[2].textContent,'Uses GPT A');
 assert.deepEqual(options.map(o=>o.children[0].checked),[false,false,true],'saved model is selected');
 options[1].children[0].checked=true;options[1].children[0].onchange();
 assert.deepEqual({...sent.at(-1)},{action:'model',slug:'gpt-a',task:'review'});
 options[0].children[0].checked=true;options[0].children[0].onchange();
 assert.deepEqual({...sent.at(-1)},{action:'model',slug:null,task:'review'},'Automatic clears the choice');
 const translation=element('translation-model-list').children;
 assert.deepEqual(translation.map(o=>o.children[1].textContent),['Same as review','GPT A','GPT B']);
 assert.equal(translation[0].children[2].textContent,'Uses GPT B','follows the chosen review model');
 assert.equal(translation[0].children[0].checked,true);
 translation[1].children[0].checked=true;translation[1].children[0].onchange();
 assert.deepEqual({...sent.at(-1)},{action:'model',slug:'gpt-a',task:'translate'});
 message({type:'models',connected:true,models,selected:'gone'});
 assert.equal(element('model-list').children[0].children[0].checked,true,'missing model falls back to Automatic');
 assert.ok(element('model-status').textContent.includes('not available'));
 message({type:'models',connected:false,models:[],selected:null});
 assert.equal(element('model-list').children.length,0);
 assert.equal(element('model-status').textContent,'Sign in to ChatGPT to choose a model.');
}
console.log('PASS: model choice lists models and saves the selection.');
{
 // Fact check (Alt+W): verdicts, sources, exact-range corrections, capture generations, task cache, apply invalidation.
 message({type:'status',status:{connected:true,message:'ok'}});
 const original='Der Eiffelturm steht in Rom. Er wurde 1889 fertig. Rom und Rom.';
 const meta={canReplace:true,readOnly:false,canPreserveFormatting:true,mode:'clipboard'};
 message({type:'busy',busy:false});
 sent.length=0;
 message({type:'capture',captureId:7,task:'factcheck',text:original,canReplace:true,readOnly:false,canPreserveFormatting:true});
 assert.deepEqual(JSON.parse(JSON.stringify(sent.at(-1))),{action:'factCheck',text:original,context:'',clarifications:[],captureId:7},'a Fact check shortcut checks right away'); message({type:'busy',busy:false}); sandbox.runTask('factcheck','context');assert.equal(element('factcheck-setup').hidden,false,'Add context opens the field'); element('fact-context').value='  Artikel vom Mai  ';element('fact-start').onclick(); assert.deepEqual(JSON.parse(JSON.stringify(sent.at(-1))),{action:'factCheck',text:original,context:'Artikel vom Mai',clarifications:[],captureId:7},'Start sends the context');
 const claim=(quote,verdict,extra={})=>({quote,verdict,explanation:'Erklärung.',correction:null,sources:[{url:'https://a.example/x',title:'A',domain:'a.example'}],noSource:false,...extra});
 const result={claims:[claim('Der Eiffelturm steht in Rom','wrong',{correction:'Der Eiffelturm steht in Paris'}),claim('Er wurde 1889 fertig','supported'),claim('Rom','wrong',{correction:'Paris'}),claim('Er wurde 1889 fertig','unverifiable',{sources:[],noSource:true})],model:'m1',elapsedMs:12000,searches:2};
 message({type:'factCheck',captureId:6,original,result,...meta});
 assert.equal(element('factcheck-view').hidden,true,'an answer for an older capture is dropped');
 message({type:'factCheck',captureId:7,original,result,...meta});
 assert.equal(element('factcheck-view').hidden,false);
 assert.equal(element('fact-summary').textContent,"4 claims: 1 supported by the sources found, 2 wrong, 1 can't verify.");
 assert.equal(element('fact-meta').textContent,'Researched in 12 s with m1');
 const cards=element('fact-claims').children;
 assert.equal(cards[1].children[0].textContent,'Supported by the sources found');
 const fix=cards[0].children.find(c=>c.className==='fact-fix');
 assert.equal(fix.children[0].checked,false,'corrections start unchecked');
 assert.equal(element('fact-apply').disabled,true,'nothing to paste before a correction is chosen');
 fix.children[0].checked=true;fix.children[0].onchange();
 assert.equal(element('fact-preview').textContent,'Der Eiffelturm steht in Paris. Er wurde 1889 fertig. Rom und Rom.');
 assert.ok(!cards[2].children.some(c=>c.className==='fact-fix'),'a correction for a repeated quote is not applicable');
 assert.ok(cards[3].children.some(c=>c.className==='hint'&&c.textContent.includes('No source was cited')));
 assert.equal(element('fact-apply').textContent,'Paste with formatting','corrections keep formatting');
 element('fact-apply').onclick();
 const applied=sent.at(-1);
 assert.equal(applied.action,'apply');
 assert.deepEqual(JSON.parse(JSON.stringify(applied.edits)),[{start:24,end:27,before:'Rom',after:'Paris'}],'only the exact range changes');
 assert.equal(applied.allowPlainText,false);
 cards[1].children.find(c=>c.className==='fact-sources').children[0].children[0].onclick();
 assert.deepEqual({...sent.at(-1)},{action:'openSource',url:'https://a.example/x'});
 // Task row: switching runs the other task once and reuses cached results.
 sandbox.runTask('translate');
 assert.deepEqual({...sent.at(-1)},{action:'translate',text:original,captureId:7});
 message({type:'translation',captureId:7,original,translation:'The Eiffel Tower is in Rome.',canReplace:true,readOnly:false,model:'m'});
 const count=sent.length;
 sandbox.runTask('factcheck');
 assert.equal(sent.length,count,'the cached fact check is shown without a new request');
 assert.equal(element('factcheck-view').hidden,false);assert.equal(element('translation-view').hidden,true);
 assert.equal(element('fact-preview').textContent,'Der Eiffelturm steht in Paris. Er wurde 1889 fertig. Rom und Rom.','accepted corrections are kept');
 // After a paste no task can replace the selection again.
 message({type:'applied',clipboardRestored:true,formatted:true});
 assert.equal(element('fact-apply').disabled,true);
 sandbox.runTask('translate');
 assert.equal(element('apply-translation').disabled,true);
 // A new capture clears every cached task.
 message({type:'capture',captureId:8,task:'review',text:'Neu.',canReplace:true});
 assert.deepEqual({...sent.at(-1)},{action:'check',text:'Neu.',captureId:8});
 message({type:'result',captureId:8,original:'Neu.',result:{correctedText:'Neu.',findings:[],corrections:[]},canReplace:true,model:'m',mode:'clipboard'});
 sandbox.runTask('factcheck');
 assert.deepEqual(JSON.parse(JSON.stringify(sent.at(-1))),{action:'factCheck',text:'Neu.',context:'',clarifications:[],captureId:8});
 message({type:'busy',busy:false});
 // Settings: third model list.
 message({type:'models',connected:true,models:[{slug:'a',displayName:'A'},{slug:'b',displayName:'B'}],selected:null,translationSelected:null,factCheckSelected:'b'});
 const facts=element('fact-model-list').children;
 assert.deepEqual(facts.map(o=>o.children[1].textContent),['Same as review','A','B']);
 assert.equal(facts[2].children[0].checked,true);
 facts[1].children[0].checked=true;facts[1].children[0].onchange();
 assert.deepEqual({...sent.at(-1)},{action:'model',slug:'a',task:'factcheck'});
}
console.log('PASS: fact check verdicts, exact corrections, capture generations, task cache and model choice.');
{
 // Read-only fact check: Copy only.
 message({type:'busy',busy:false});
 message({type:'capture',captureId:20,task:'factcheck',text:'Rom ist in Italien.',canReplace:false,readOnly:true,canPreserveFormatting:false});
 message({type:'factCheck',captureId:20,original:'Rom ist in Italien.',result:{claims:[],model:'m',elapsedMs:3000,searches:0},canReplace:false,readOnly:true,canPreserveFormatting:false,mode:'clipboard'});
 assert.equal(element('fact-apply').hidden,true,'read-only offers no paste');
 assert.equal(element('fact-readonly').hidden,false);
}
console.log('PASS: read-only fact check offers copy only.');
{
 // Overlapping fact corrections: accepting one releases the other.
 message({type:'busy',busy:false});
 const text='Berlin hat 4 Millionen Einwohner.';
 message({type:'capture',captureId:30,task:'factcheck',text,canReplace:true,readOnly:false,canPreserveFormatting:true});
 const src=[{url:'https://s.example/',title:'S',domain:'s.example'}];
 message({type:'factCheck',captureId:30,original:text,result:{claims:[
  {quote:'Berlin hat 4 Millionen',verdict:'wrong',explanation:'.',correction:'Berlin hat 3,8 Millionen',sources:src,noSource:false},
  {quote:'4 Millionen Einwohner',verdict:'wrong',explanation:'.',correction:'knapp 4 Millionen Einwohner',sources:src,noSource:false}],model:'m',elapsedMs:1,searches:1},canReplace:true,readOnly:false,canPreserveFormatting:true,mode:'clipboard'});
 const box=i=>element('fact-claims').children[i].children.find(c=>c.className==='fact-fix').children[0];
 box(0).checked=true;box(0).onchange();
 box(1).checked=true;box(1).onchange();
 assert.equal(box(0).checked,false,'the overlapping first correction is released');
 assert.equal(element('fact-preview').textContent,'Berlin hat knapp 4 Millionen Einwohner.');
}
console.log('PASS: overlapping fact corrections cannot both be accepted.');
{
 // A needs_context claim asks a question; the answer re-checks only that claim and replaces its card.
 message({type:'busy',busy:false});
 const text='Die Studie zeigt 20 Prozent. Rom liegt in Italien.';
 message({type:'capture',captureId:31,task:'factcheck',text,canReplace:false,readOnly:true,canPreserveFormatting:false});
 const src=[{url:'https://s.example/',title:'S',domain:'s.example'}];
 message({type:'factCheck',captureId:31,original:text,only:[],result:{claims:[
  {quote:'Die Studie zeigt 20 Prozent',verdict:'needs_context',explanation:'Unklar.',correction:null,sources:[],noSource:false,question:'Welche Studie ist gemeint?'},
  {quote:'Rom liegt in Italien',verdict:'supported',explanation:'.',correction:null,sources:src,noSource:false}],model:'m',elapsedMs:1,searches:1},canReplace:false,readOnly:true,canPreserveFormatting:false,mode:'clipboard'});
 message({type:'busy',busy:false});
 const card=element('fact-claims').children[0];
 assert.equal(card.className,'fact-card verdict-needs_context');
 const question=card.children.find(c=>c.className==='fact-question');assert.equal(question.textContent,'Welche Studie ist gemeint?');
 const form=card.children.find(c=>c.className==='revise-translation fact-answer');
 form.children[0].value='PISA 2022';form.onsubmit({preventDefault(){}});
 assert.deepEqual(JSON.parse(JSON.stringify(sent.at(-1))),{action:'factCheck',text,context:'',clarifications:[{quote:'Die Studie zeigt 20 Prozent',question:'Welche Studie ist gemeint?',answer:'PISA 2022'}],only:['Die Studie zeigt 20 Prozent'],captureId:31});
 message({type:'factCheck',captureId:31,original:text,only:['Die Studie zeigt 20 Prozent'],result:{claims:[
  {quote:'Die Studie zeigt 20 Prozent',verdict:'supported',explanation:'PISA 2022.',correction:null,sources:src,noSource:false}],model:'m',elapsedMs:1,searches:1},canReplace:false,readOnly:true,canPreserveFormatting:false,mode:'clipboard'});
 message({type:'busy',busy:false});
 assert.equal(element('fact-claims').children.length,2);
 assert.equal(element('fact-claims').children[0].className,'fact-card verdict-supported');
 assert.equal(element('fact-claims').children[1].className,'fact-card verdict-supported');
 // Checking again with more context sends the whole text, the context and the answers.
 element('fact-context-more').value='Bildungsbericht';element('fact-recheck').onclick();
 const again=JSON.parse(JSON.stringify(sent.at(-1)));
 assert.equal(again.context,'Bildungsbericht');assert.equal(again.clarifications.length,1);assert.equal(again.only,undefined);
}
console.log('PASS: fact-check context, questions on claims and single-claim re-checks.');
{
 // Card options send their variant; each option has its own result, and the card shows which one is selected.
 message({type:'busy',busy:false});
 const text='Bitte bis Freitag den Bericht schicken.';
 message({type:'capture',captureId:50,task:'open',text,canReplace:false,readOnly:true});
 sandbox.runTask('translate','en');
 assert.deepEqual({...sent.at(-1)},{action:'translate',text,captureId:50,target:'en'});
 message({type:'translation',captureId:50,original:text,translation:'Please send the report by Friday.',target:'en',canReplace:false,readOnly:true,model:'m'});
 message({type:'busy',busy:false});
 assert.equal(element('translation-view').hidden,false);assert.equal(element('translation-hint').textContent,'Translated into English.');
 const before=sent.length;sandbox.runTask('translate','en');assert.equal(sent.length,before,'same option shows the cached result');
 sandbox.runTask('translate','');assert.equal(element('translation-view').hidden,true,'another option never shows the cached result');
 assert.deepEqual({...sent.at(-1)},{action:'translate',text,captureId:50});
 message({type:'busy',busy:false});
 sandbox.runTask('summary','actions');
 assert.deepEqual({...sent.at(-1)},{action:'write',kind:'summary',text,original:text,captureId:50,variant:'actions'});
 message({type:'writing',captureId:50,kind:'summary',variant:'actions',original:text,result:'- Bericht bis Freitag schicken',canReplace:false,readOnly:true,model:'m'});
 message({type:'busy',busy:false});
 assert.equal(element('answer-title').textContent,'Summary: Action items');
 const option=element('task-choices').children[3].children[3];assert.equal(option.className,'group-row selected');assert.equal(element('task-choices').children[3].children[0].className,'group-head');
 sandbox.runTask('summary','');assert.equal(element('answer-view').hidden,true);assert.equal(sent.at(-1).variant,undefined);
 message({type:'busy',busy:false});
 sandbox.runTask('review','errors');
 assert.deepEqual(JSON.parse(JSON.stringify(sent.at(-1))),{action:'check',text,captureId:50,scope:'basics'});
 message({type:'result',captureId:50,original:text,result:{correctedText:text,findings:[],corrections:[]},canReplace:false,readOnly:true,model:'m',mode:'clipboard'});
 const tab=key=>element('review-tabs').children.find(b=>b.dataset.category===key);
 assert.equal(tab('wording').children[1].textContent,'Not checked');assert.equal(tab('worth').children[1].textContent,'Not checked');assert.equal(tab('spelling').children[1].textContent,0);
}
console.log('PASS: task menu options, per-option results and selection.');
{
 // A result becomes the captured text; the window starts over with the task choice.
 message({type:'busy',busy:false});
 message({type:'capture',captureId:60,task:'translate',text:'Hallo Welt.',canReplace:true,readOnly:false});
 message({type:'translation',captureId:60,original:'Hallo Welt.',translation:'Hello world.',target:'auto',canReplace:true,readOnly:false,model:'m'});
 message({type:'busy',busy:false});
 element('use-translation').onclick();
 assert.deepEqual({...sent.at(-1)},{action:'useAsCapture',text:'Hello world.'});
 message({type:'capture',captureId:61,task:'open',text:'Hello world.',canReplace:true,readOnly:false,hasFormatting:false,canPreserveFormatting:false,mode:'clipboard',reused:true});
 assert.equal(element('choose-text').textContent,'Hello world.');assert.equal(element('translation-view').hidden,true);
 assert.equal(element('notice').textContent,'The result is the captured text. Choose what TextFixer should do with it.');
}
console.log('PASS: a result can become the captured text.');
{
 // Settings > Shortcuts: Open required, optional task shortcuts, recording, key mapping, cancel, reset/remove and errors.
 const defaults={open:'Alt+E',review:'Alt+Q',translate:'',factcheck:'',rewrite:'',summary:'',explain:''};
 message({type:'shortcuts',shortcuts:{...defaults,translate:'Ctrl+Alt+T'},defaults,registered:{open:true,review:true,translate:true,factcheck:true,rewrite:true,summary:true,explain:true}});
 const rows=()=>element('shortcut-list').children;
 const keys=i=>rows()[i].children[1];
 assert.deepEqual(rows().map(r=>r.children[0].textContent),['Open','Review','Translate','Fact check','Summary','Explain','Rewrite']);
 assert.deepEqual(keys(2).children.map(k=>k.textContent),['Ctrl','Alt','T']);
 assert.equal(keys(3).children[0].textContent,'Not set');
 assert.equal(rows()[0].children[2].hidden,true,'Open cannot be removed');
 assert.equal(rows()[1].children[2].textContent,'Remove');assert.equal(rows()[1].children[2].hidden,false);
 assert.equal(rows()[3].children[2].hidden,true,'nothing to remove for an unset task');
 sent.length=0;
 rows()[2].children[2].onclick();
 assert.deepEqual({...sent.at(-1)},{action:'shortcut',task:'translate',keys:''},'Remove clears an optional shortcut');
 keys(4).onclick();
 assert.deepEqual({...sent.at(-1)},{action:'shortcutRecording',active:true});
 assert.equal(keys(4).children[0].textContent,'Press the new keys');
 const press=(i,props)=>{let prevented=false;keys(i).onkeydown({ctrlKey:false,altKey:false,shiftKey:false,metaKey:false,preventDefault(){prevented=true;},stopPropagation(){},...props});return prevented;};
 const count=sent.length;
 assert.equal(press(4,{key:'Control',code:'ControlLeft',ctrlKey:true}),true);
 assert.equal(sent.length,count,'a modifier alone keeps recording');
 press(4,{key:'s',code:'KeyS',ctrlKey:true,altKey:true});
 assert.deepEqual({...sent.at(-1)},{action:'shortcut',task:'summary',keys:'Ctrl+Alt+S'});
 keys(0).onclick();press(0,{key:'F5',code:'F5',altKey:true,shiftKey:true});
 assert.deepEqual({...sent.at(-1)},{action:'shortcut',task:'open',keys:'Alt+Shift+F5'});
 keys(1).onclick();press(1,{key:'Escape',code:'Escape'});
 assert.deepEqual({...sent.at(-1)},{action:'shortcutRecording',active:false},'Esc cancels');
 message({type:'shortcuts',shortcuts:{...defaults,open:'Ctrl+Alt+O',review:''},defaults,registered:{open:false},errorTask:'summary',error:'Ctrl+Alt+Q types @ with your keyboard layout. Choose another combination.'});
 assert.equal(rows()[0].children[2].textContent,'Reset to Alt+E');
 rows()[0].children[2].onclick();assert.deepEqual({...sent.at(-1)},{action:'shortcut',task:'open',keys:'Alt+E'});
 assert.equal(rows()[1].children[2].textContent,'Reset to Alt+Q');
 assert.equal(rows()[0].children[3].textContent,'Another app uses this shortcut, so it does not work. Choose another one.');
 assert.equal(rows()[4].children[3].className,'shortcut-message error');
 element('settings').listeners.close?.();
 message({type:'shortcuts',shortcuts:defaults,defaults,registered:{}});
}
{
 // Markdown subset: paragraphs, lists, one nesting level, bold and italic; everything else stays text.
 const md='Intro mit **fett** und *kursiv*.\nZweite Zeile.\n\n- **Stufen:** Booster\n  - Unterpunkt\n- <b>kein HTML</b>\n\n1. Erst\n2. Dann\n\n## Titel\n5 * 3 = 15';
 assert.equal(sandbox.markupPlain(md),'Intro mit fett und kursiv.\nZweite Zeile.\n\n\u2022 Stufen: Booster\n   \u2022 Unterpunkt\n\u2022 <b>kein HTML</b>\n\n1. Erst\n2. Dann\n\nTitel\n\n5 * 3 = 15');
 const box=sandbox.document.createElement('div');sandbox.renderMarkup(box,md);
 assert.deepEqual(box.children.map(c=>c.tag),['p','ul','ol','p','p']);
 assert.deepEqual(box.children[0].children.map(c=>c.tag),['span','strong','span','em','span','br','span']);
 const first=box.children[1].children[0];assert.equal(first.children[0].tag,'strong');assert.equal(first.children[0].textContent,'Stufen:');assert.equal(first.children[2].tag,'ul');
 assert.equal(box.children[1].children[1].children[0].textContent,'<b>kein HTML</b>','HTML stays text');
 assert.equal(box.children[4].children[0].textContent,'5 * 3 = 15','a lone asterisk stays text');
}
console.log('PASS: Markdown subset rendering and plain text.');
{
 // Text size: 16 px by default, steps of 1 px between 12 and 24, Reset goes back to 16.
 element('open-text-size').onclick();assert.equal(element('text-size').hidden,false);assert.equal(element('text-size-value').textContent,'16 px');assert.equal(element('text-reset').disabled,true);
 element('text-larger').onclick();assert.equal(element('text-size-value').textContent,'17 px');assert.equal(element('text-reset').disabled,false);
 for(let i=0;i<20;i++)element('text-smaller').onclick();assert.equal(element('text-size-value').textContent,'12 px');assert.equal(element('text-smaller').disabled,true);
 element('text-reset').onclick();assert.equal(element('text-size-value').textContent,'16 px');
 element('open-text-size').onclick();assert.equal(element('text-size').hidden,true);
}
console.log('PASS: text size steps, limits and reset.');
console.log('PASS: shortcut recording, optional task shortcuts, cancel, reset, remove and errors.');
{
 // Open: the task choice appears and nothing runs until a task is chosen.
 message({type:'status',status:{connected:true,message:'ok'}});
 message({type:'busy',busy:false});
 sent.length=0;
 const text='Wir treffen uns am Montag. Bitte schick mir die E-Mail.';
 message({type:'capture',captureId:40,task:'open',text,canReplace:true,readOnly:false,canPreserveFormatting:true});
 assert.equal(sent.filter(m=>['check','translate','factCheck','write'].includes(m.action)).length,0,'Open makes no request');
 assert.equal(element('choose-view').hidden,false);
 assert.deepEqual(element('task-choices').children.map(c=>c.dataset.task),['review','translate','factcheck','summary','explain','rewrite']);
 assert.equal(element('choose-text').textContent,text);
 // The Rewrite card improves the text right away; More... opens all presets; a preset sends one.
 sandbox.runTask('rewrite');
 assert.equal(sent.at(-1).request,'Improve: clearer and smoother wording; keep the meaning, tone, form of address and length');
 assert.equal(element('task-choices').children[5].className,'task-group active','the sidebar marks the running task');assert.equal(element('task-choices').children[5].children[0].className,'group-head selected');
 message({type:'busy',busy:false});
 sandbox.runTask('rewrite','more');
 assert.equal(element('rewrite-view').hidden,false);assert.equal(element('choose-view').hidden,false,'the captured text stays at the top');
 const presets=element('rewrite-presets').children;
 assert.deepEqual(presets.map(b=>b.textContent),['Improve','More formal','Friendlier','Shorter','Simpler','Clearer structure']);
 presets[3].onclick();
 assert.deepEqual({...sent.at(-1)},{action:'write',kind:'rewrite',text,original:text,request:'Shorter: remove repetition and filler, keep all facts',captureId:40});
 message({type:'writing',captureId:40,kind:'rewrite',original:text,result:'Treffen am Montag. Schick mir die E-Mail.',request:'Shorter: remove repetition and filler, keep all facts',canReplace:true,readOnly:false,model:'m'});
 // A free-text follow-up changes the latest rewrite; the diff compares with the captured original.
 element('rewrite-request').value='mehr Ausrufezeichen';
 element('rewrite-form').onsubmit({preventDefault(){}});
 assert.deepEqual({...sent.at(-1)},{action:'write',kind:'rewrite',text:'Treffen am Montag. Schick mir die E-Mail.',original:text,request:'mehr Ausrufezeichen',captureId:40});
 message({type:'writing',captureId:40,kind:'rewrite',original:text,result:'Treffen am Montag! Schick mir die E-Mail.',request:'mehr Ausrufezeichen',canReplace:true,readOnly:false,model:'m'});
 assert.equal(element('rewrite-text').textContent,'Treffen am Montag! Schick mir die E-Mail.');
 assert.equal(element('rewrite-steps').textContent,'Changes requested: Shorter, then mehr Ausrufezeichen');
 assert.ok(element('rewrite-changes').children.filter(c=>c.tag==='del').map(c=>c.textContent).join('|').includes('Wir'),'diff against the original');
 element('apply-rewrite').onclick();
 assert.deepEqual({...sent.at(-1)},{action:'applyRewrite',text:'Treffen am Montag! Schick mir die E-Mail.',original:text});
 element('rewrite-reset').onclick();
 assert.equal(element('rewrite-result').hidden,true);
 // Summary runs once and is cached; a late answer for an older capture is dropped.
 sandbox.runTask('summary');
 assert.deepEqual({...sent.at(-1)},{action:'write',kind:'summary',text,original:text,captureId:40});
 message({type:'writing',captureId:39,kind:'summary',original:text,result:'old',canReplace:true,readOnly:false,model:'m'});
 assert.equal(element('answer-view').hidden,true);
 message({type:'writing',captureId:40,kind:'summary',original:text,result:'- Treffen am Montag',canReplace:true,readOnly:false,model:'m'});
 assert.equal(element('answer-title').textContent,'Summary');const list=element('dialog').children[0].children[0].children[0];assert.equal(list.tag,'ul');assert.equal(list.children[0].children[0].textContent,'Treffen am Montag','the summary renders as a list');
 const before=sent.length;
 sandbox.runTask('rewrite','more');sandbox.runTask('summary');
 assert.equal(sent.length,before,'cached summary');
 element('dialog').children[0].children[1].children[1].onclick();
 assert.deepEqual({...sent.at(-1)},{action:'copyResult',kind:'summary',text:'\u2022 Treffen am Montag'});
 // Follow-up questions carry the conversation; the answer shows its sources; a failed question returns to the input.
 element('dialog-question').value='Stimmt das?';element('dialog-form').onsubmit({preventDefault(){}});
 assert.deepEqual(JSON.parse(JSON.stringify(sent.at(-1))),{action:'discuss',kind:'summary',text,turns:[{role:'assistant',text:'- Treffen am Montag'},{role:'user',text:'Stimmt das?'}],captureId:40});
 assert.equal(element('dialog').children.length,3);
 message({type:'dialog',captureId:40,kind:'summary',question:'Stimmt das?',answer:{text:'Ja.',sources:[{url:'https://a.example/x',title:'A',domain:'a.example'}],searches:1,model:'m'}});
 message({type:'busy',busy:false});
 assert.equal(element('dialog').children.length,3);assert.equal(element('dialog').children[2].children[0].children[0].children[0].textContent,'Ja.');
 element('dialog').children[2].children[1].children[0].children[0].onclick();
 assert.deepEqual({...sent.at(-1)},{action:'openSource',url:'https://a.example/x'});
 element('dialog-question').value='Und warum?';element('dialog-form').onsubmit({preventDefault(){}});
 assert.equal(JSON.parse(JSON.stringify(sent.at(-1))).turns.length,4);
 message({type:'busy',busy:false});
 assert.equal(element('dialog-question').value,'Und warum?');assert.equal(element('dialog').children.length,3);
 element('copy-conversation').onclick();
 assert.equal(sent.at(-1).text,'Summary:\n\u2022 Treffen am Montag\n\nYou: Stimmt das?\n\nTextFixer:\nJa.\nSources:\n- A: https://a.example/x');
 element('dialog-question').value='';
 // A capture from a task shortcut starts that task.
 message({type:'capture',captureId:41,task:'explain',text:'API',canReplace:false,readOnly:true});
 assert.deepEqual({...sent.at(-1)},{action:'write',kind:'explain',text:'API',original:'API',captureId:41});
 message({type:'busy',busy:false});
 message({type:'reviewMode',mode:'single',emailSpelling:'E-Mail',addressForm:'Du'});
}
console.log('PASS: Open chooser, Rewrite presets and chaining, Summary cache, task shortcuts.');
{
 // Clipboard history cards: only the two copy buttons copy an entry.
 sandbox.setTimeout=()=>0;sandbox.clearTimeout=()=>{};
 message({type:'history',enabled:true,entries:[{id:'a',preview:'Plain entry',copiedAt:Date.now(),hasFormatting:false},{id:'b',preview:'Bold entry',copiedAt:Date.now(),hasFormatting:true}]});
 const cards=()=>element('history-list').children;
 const buttons=i=>cards()[i].children[2].children[0].children;
 assert.equal(cards()[0].children[1].onclick,undefined,'the entry text does not copy');
 assert.deepEqual(buttons(0).map(b=>b.textContent),['Copy plain']);
 assert.equal(cards()[0].children[0].children[1].textContent,'×');
 cards()[0].children[0].children[1].onclick();assert.deepEqual({...sent.at(-1)},{action:'historyDelete',id:'a'});
 assert.deepEqual(buttons(1).map(b=>b.textContent),['Copy plain','Copy formatted']);
 sent.length=0;
 buttons(1)[0].onclick();assert.deepEqual({...sent.at(-1)},{action:'historyRestore',id:'b',plain:true});
 buttons(1)[1].onclick();assert.deepEqual({...sent.at(-1)},{action:'historyRestore',id:'b',plain:false});
 message({type:'historyCopied',id:'b',plain:false});
 assert.deepEqual(buttons(1).map(b=>b.textContent),['Copy plain','Copied'],'only the used button confirms');
}
console.log('PASS: history cards copy only through plain or formatted copy buttons.');
