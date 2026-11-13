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
for(const file of ['diff.js','review-model.js','app.js'])vm.runInContext(fs.readFileSync(`src/TextFixer/Web/${file}`,'utf8'),sandbox);
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

assert.match(previewCss,/\.inline-text\s*\{[^}]*font:15px\/23\.8px/, 'correction preview uses original-text line step with larger 15px font');
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
 assert.equal(element('notice').textContent,'Paste sent. Alt+Q cannot verify the result in the editor. Please check it there. The revised text remains on the clipboard.');
 assert.equal(element('apply').disabled,true,'paste consumes the capture');
 message({type:'applied',formatted,clipboardRestored:true});
 assert.equal(element('notice').textContent,'Paste sent. Alt+Q cannot verify the result in the editor. Please check it there.');
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
 const step=(i,result,error=null)=>{message({type:'tabProgress',original,running:all[i],done:all.slice(0,i),pending:all.slice(i+1)});message({type:'tabResult',original,focus:all[i],result,error,pending:all.slice(i+1),...meta});};
 message({type:'tabProgress',original,running:'spelling',done:[],pending:all.slice(1)});
 const states=()=>element('review-tabs').children.map(b=>b.className.replace('review-tab','').replace('selected','').trim());
 assert.deepEqual(states().slice(0,5),['state-running','state-queued','state-queued','state-queued','state-queued'],'first tab running, rest queued');
 step(0,{correctedText:original.replace('frauen','Frauen'),findings:[],corrections:[]});
 assert.equal(element('results').hidden,false,'first tab result is shown while the rest run');
 assert.equal(element('copy').disabled,true,'copy waits for the remaining tabs');
 const spelling=element('inline-text').children.find(c=>c.tag==='button');
 assert.equal(spelling.disabled,false,'finished tab can be reviewed while the rest run');
 spelling.onclick();
 step(1,{correctedText:original.replace('dass','das'),findings:[],corrections:[]});
 step(2,{correctedText:original.replace('frauen die','frauen, die'),findings:[],corrections:[]});
 assert.deepEqual(states().slice(0,5),['state-ready','state-ready','state-ready','state-queued','state-queued'],'answered tabs ready, later tabs queued until their request starts');
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
 answers.forEach((correctedText,i)=>{message({type:'tabProgress',original,running:all[i],done:all.slice(0,i),pending:all.slice(i+1)});message({type:'tabResult',original,focus:all[i],result:{correctedText,findings:[],corrections:[]},error:null,pending:all.slice(i+1),...meta});});
 message({type:'busy',busy:false});
 assert.equal(element('preview').textContent,'Ich glaube, dass wir gewinnen.','duplicate tab fixes are applied once');
}
console.log('PASS: duplicate fixes from several tabs are applied once.');
