const $=id=>document.getElementById(id);
const post=(action,extra={})=>window.chrome.webview.postMessage({action,...extra});
let connected=false,busy=false,canReplace=false,readOnlyCapture=false,sourceText='',reviewOriginal='',parts=[],captureMode='clipboard',richClipboard=false;
let pendingAutoCheck=false;
// Alt+E captures translate instead of review.
let captureTask='review',translationOriginal='',translationText='';
// Task menu: activeTask is the visible task; taskReady[task] holds the captureId its result belongs to.
// taskOption[task] is the chosen card option ('' = the card's main action); readyOption[task] is the option of the shown result.
// captureId increases with every capture; answers for an older capture are dropped.
let captureClip=null,captureText='';
// Appends text to a node. Paragraph gaps the app mapped for this capture (M! Forum: one paragraph per line, an empty
// paragraph per blank line) become gap elements that keep the original line breaks and take the forum's spacing in CSS.
// offset: where text starts in the captured text; base: the text the offsets refer to.
function appendGapped(node,text,offset=0,base=sourceText){
  const gaps=base===captureText&&captureClip?.gaps||[];let pos=0;
  if(!gaps.length){node.textContent=text;return;}
  const piece=value=>{const span=document.createElement('span');span.textContent=value;return span;};
  for(const [start,length,empty] of gaps){
    const s=start-offset;if(s<pos||s+length>text.length)continue;
    if(s>pos)node.append(piece(text.slice(pos,s)));
    const gap=document.createElement('span');gap.className='line-gap';gap.style?.setProperty?.('--empty',String(empty));gap.textContent=text.slice(s,s+length);
    node.append(gap);pos=s+length;
  }
  if(pos<text.length)node.append(piece(text.slice(pos)));
}
let captureId=0,activeTask='review',taskReady={},taskOption={},readyOption={},requestedOption={},factSetup=false,dialogKey='',menuButtons=[];
// Fact check: claims with their correction wordings (exact quote replacements, applied like review edits).
// factContext and factClarifications (answers to the questions of needs_context claims) belong to the current capture.
let factOriginal='',factClaims=[],factWordings=[],factMeta=null,factContext='',factClarifications=[],factIds=0;
// Rewrite steps for the current capture (each request changes the latest one) and Summary/Explain answers with their dialogs
// (turns: alternating user and assistant messages; pending: the question waiting for an answer).
// A rich capture (quotes, links, mentions, code): its protected items and ranges, and whether results use its markup.
let captureDoc=null,translationStructured=false,translationIntact=true,rewriteStructured=false,rewriteIntact=true;
let rewriteSteps=[],rewriteModel='',answers={},writingPrefs={email:'keep',address:'auto',reviewDu:'keep',appLanguage:'en',resultLanguage:'text'};
let shortcutLabels={open:'Ctrl+Alt+.',review:'Ctrl+Alt+-',translate:'',factcheck:'',rewrite:'',summary:'',explain:''},shortcutDefaults={...shortcutLabels},shortcutRegistered={},shortcutError=null,recordingTask=null,renderingShortcuts=false;
const verdictLabel={supported:'Supported by the sources found',wrong:'Wrong',disputed:'Disputed',unverifiable:"Can't verify",needs_context:'Needs context'};
// Settings > Punctuation style.
let punctuationStyle={quotes:'keep',dashes:'keep',ellipsis:'keep',spaces:'keep',multiply:'keep',arrows:'keep',primes:'keep'};
const styledParts=(original,corrected,corrections,styleAll=true)=>reviewStyleParts(reviewParts(original,styleAll?reviewStyleText(corrected,punctuationStyle):corrected,corrections,punctuationStyle.spaces==='plain'),punctuationStyle);
const styledFindings=list=>(list||[]).map(f=>({...f,suggestedRewrite:reviewPlainStyle(f.suggestedRewrite,f.quotes?.[0]||'',punctuationStyle),dismissed:false}));
// "One request per tab" review: tabState holds ready | running | failed | stopped per requested tab.
let reviewMode='single',partialReview=false,tabState={},tabResults={},tabErrors={},autoTab=true;
const focusOrder=['spelling','grammar','punctuation','wording','worth'];
function locked(){return busy&&!partialReview;}
let activeTab='spelling',reviewFindings=[],wordings=[];
const reviewTabs=[['spelling','Spelling'],['grammar','Grammar'],['punctuation','Punctuation'],['wording','Wording'],['formatting','Formatting']];
// Names of every review part, including Worth checking, which has its own section instead of a tab.
const reviewPartNames=Object.fromEntries([...reviewTabs,['worth','Worth checking']]);
function activeCorrection(p){return p.category===activeTab&&!p.layoutProtected&&(p.category==='worth'||!reviewSuperseded(p,wordings));}
function tabCount(key){
  if(key==='worth')return reviewFindings.filter(f=>!f.dismissed).length;
  if(key==='formatting')return shownFormatting().length;
  return parts.filter(p=>p.changed&&p.category===key).length;
}
function renderTabs(){
  const container=$('review-tabs');container.replaceChildren();
  reviewTabs.forEach(([key,title])=>{
    const button=document.createElement('button');button.type='button';button.id='tab-'+key;button.className='review-tab'+(activeTab===key?' selected':'');
    button.dataset.category=key;button.setAttribute('role','tab');button.setAttribute('aria-selected',String(activeTab===key));
    const state=tabState[key];
    // Formatting fixes cannot be applied with plain-text replacement, so their tab is not selectable then.
    button.disabled=tabCount(key)===0||(state&&state!=='ready')||(key==='formatting'&&plainReplace());if(state)button.className+=' state-'+state;button.tabIndex=activeTab===key||(!tabCount(activeTab)&&reviewTabs.find(([k])=>tabCount(k)>0)?.[0]===key)?0:-1;
    const name=document.createElement('span');name.textContent=title;
    const count=document.createElement('span');count.className='number';count.textContent=tabCount(key);
    if(state&&state!=='ready'){count.className='tab-state';count.textContent=state==='failed'?'!':state==='stopped'?'-':'';button.title=tabStateTitle[state];}
    button.append(name,count);
    // A checked category without corrections leaves the bar and is named in the note next to it.
    button.hidden=tabCount(key)===0&&(!state||state==='ready');
    button.onclick=()=>{if(button.disabled)return;autoTab=false;activeTab=key;showChanges();$('tab-'+key)?.focus();};
    button.onkeydown=e=>{
      if(!['ArrowRight','ArrowLeft','Home','End'].includes(e.key))return;e.preventDefault();
      const available=reviewTabs.filter(([k])=>tabCount(k)>0).map(([k])=>k),i=available.indexOf(key);
      const next=e.key==='Home'?available[0]:e.key==='End'?available.at(-1):available[(i+(e.key==='ArrowRight'?1:-1)+available.length)%available.length];
      if(next){activeTab=next;showChanges();$('tab-'+next)?.focus();}
    };
    container.append(button);
  });
  // Each category without corrections appears as its name and 0, like a tab.
  const note=$('review-tabs-empty');note.replaceChildren();
  if(parts.length)for(const [key,title] of reviewTabs){
    if(tabCount(key)!==0||(tabState[key]&&tabState[key]!=='ready'))continue;
    const item=document.createElement('span');item.className='empty-tab';
    const name=document.createElement('span');name.textContent=title;
    const count=document.createElement('span');count.className='number';count.textContent='0';
    item.append(name,count);note.append(item);
  }
  // Basics only skips Worth checking, so its section is left out; in a tab-by-tab review it appears once its answer is in.
  $('worth-results').hidden=readyOption.review==='errors'||(partialReview&&tabState.worth!=='ready');
}
const tabStateTitle={running:'Checking now',failed:'Could not be checked',stopped:'Stopped'};
function focusParts(original){
  // Each tab answer fixes only its own category. Earlier (left) tabs win where two answers touch the same text.
  const previous=new Map(parts.filter(p=>p.changed).map(p=>[`${p.start}|${p.end}|${p.after}`,p.accepted]));
  const kept=[];
  // Changes may touch at a boundary; two insertions at one position, or any shared character, conflict.
  const clash=(a,b)=>a.start===a.end&&b.start===b.end?a.start===b.start:a.start===a.end?b.start<a.start&&a.start<b.end:b.start===b.end?a.start<b.start&&b.start<a.end:a.start<b.end&&b.start<a.end;
  // A change another tab already made (same text in the same range) is a duplicate, not a second edit.
  const duplicate=p=>{let out='',pos=p.start;for(const k of kept.filter(k=>k.start>=p.start&&k.end<=p.end).sort((a,b)=>a.start-b.start||a.end-b.end)){out+=original.slice(pos,k.start)+k.after;pos=Math.max(pos,k.end);}return out+original.slice(pos,p.end)===p.after;};
  for(const focus of focusOrder){
    const r=tabResults[focus];if(!r||focus==='worth')continue;
    // Style corrections of the user's own text come from the Punctuation tab only, so they are not repeated per tab.
    for(const p of protectParts(styledParts(original,r.correctedText,r.corrections,focus==='punctuation'),original)){
      if(!p.changed||duplicate(p))continue;
      if(!kept.some(k=>clash(k,p))){kept.push({...p,category:p.styleOnly?'punctuation':focus});continue;}
      // Same word changed by two tabs (for example "frauen" -> "Frauen" and "frauen" -> "frauen,"): keep the character-level edit when it fits.
      const e=minimalTextEdit(p.start,p.before,p.after);
      if(e&&!kept.some(k=>clash(k,e)))kept.push({...e,changed:true,accepted:true,category:focus,layoutProtected:/[\r\n]/.test(e.before+e.after)});
    }
  }
  kept.sort((a,b)=>a.start-b.start||a.end-b.end);
  const merged=[];let position=0;
  for(const p of kept){
    if(p.start>position)merged.push({before:original.slice(position,p.start),after:original.slice(position,p.start),changed:false,accepted:true,start:position,end:p.start});
    const key=`${p.start}|${p.end}|${p.after}`;if(previous.has(key)&&!p.layoutProtected)p.accepted=previous.get(key);
    merged.push(p);position=Math.max(position,p.end);
  }
  if(position<original.length)merged.push({before:original.slice(position),after:original.slice(position),changed:false,accepted:true,start:position,end:original.length});
  return merged;
}
function tabProgressNotice(){
  const names=focusOrder.filter(k=>tabState[k]==='running').map(k=>reviewPartNames[k]);
  if(!names.length)return;
  const list=names.length>1?names.slice(0,-1).join(', ')+' and '+names.at(-1):names[0];
  notice(`Checking ${list}. Finished tabs are ready to review.`);
}
function applyReviewMeta(m){captureMode=m.mode||'clipboard';richClipboard=!!m.canPreserveFormatting;canReplace=m.canReplace;readOnlyCapture=!!m.readOnly;showFormatting(m.formattingSuggestions||[]);$('model').textContent=m.model||'';}
function readyNotice(){
  const failed=focusOrder.filter(k=>tabState[k]==='failed');
  if(failed.length){notice('Review ready. Not checked: '+failed.map(k=>(reviewPartNames[k]||k)+' ('+tabErrors[k]+')').join('; '));return;}
  notice(parts.some(p=>p.layoutProtected)?'Review ready. Suggestions that would change line breaks were excluded to preserve your paragraph layout.':(readOnlyCapture?'Review ready. Accept or reject corrections, then copy your text.':'Review ready. Accept or reject corrections, then copy or replace your text.'));
}
function startCapturedReview(){
  if(!pendingAutoCheck||!connected||busy||!sourceText.trim())return;
  pendingAutoCheck=false;
  runTask(activeTask);
}
// Task groups of the sidebar: [task, title, tooltip, options, color]. The colored bar runs the main action; each option
// row runs a variant. Option kind: go (starts right away, the default) or ask (asks for something first); an optional tooltip follows.
const taskCards=[
  ['review','Review','Spelling, grammar, punctuation, wording and points worth checking.',[['errors','Basics only','go','Checks spelling, grammar, punctuation and wrong words only. No points worth checking.']],'#1a7fa0'],
  ['translate','Translate','German into English, other languages into German.',[['de','To German'],['en','To English']],'#2f5fd0'],
  ['factcheck','Fact check','Researches the claims on the web and cites sources.',[['context','Add context','ask']],'#b83555'],
  ['summary','Summary','The main points in a few lines.',[['sentence','One sentence'],['points','Key points'],['actions','Action items']],'#138d75'],
  ['explain','Explain','What the text means, in plain language.',[['beginner','For beginners'],['technical','Technical detail']],'#d35400'],
  ['rewrite','Rewrite','Improves wording and flow and keeps the meaning.',[['formal','More formal'],['friendly','Friendlier'],['short','Shorter'],['more','Own request','ask']],'#5b3fc4'],
];
const optionLabel=(task,option)=>taskCards.find(c=>c[0]===task)?.[3].find(o=>o[0]===option)?.[1]||'';
const answerKey=task=>task+':'+(taskOption[task]||'');
// Line end icons: go (play) starts right away, ask (?) asks for something first, more (dots) opens a view.
function menuIcon(kind){const i=document.createElement('span');i.className='menu-icon icon-'+kind;i.setAttribute('aria-hidden','true');if(kind==='ask')i.textContent='?';if(kind==='more')i.textContent='•••';return i;}
function menuDot(){const d=document.createElement('span');d.className='menu-dot';d.setAttribute('aria-hidden','true');return d;}
function renderTaskMenu(){
  const list=$('task-choices');list.replaceChildren();menuButtons=[];
  for(const [task,title,tooltip,options,color] of taskCards){
    const group=document.createElement('div');group.className='task-group';group.dataset.task=task;group.style?.setProperty('--group',color);
    const head=document.createElement('button');head.type='button';head.className='group-head';head.dataset.task=task;head.title=tooltip;
    const name=document.createElement('strong');name.textContent=title;
    head.append(menuDot(),name,menuIcon('go'));head.onclick=()=>runTask(task,'');
    group.append(head);menuButtons.push({button:head,task,option:'',group});
    for(const [option,label,kind,tip] of options){
      const row=document.createElement('button');row.type='button';row.className='group-row';row.dataset.task=task;row.dataset.option=option;if(tip)row.title=tip;
      const text=document.createElement('span');text.className='row-label';text.textContent=label;
      row.append(menuDot(),text,menuIcon(kind||'go'));row.onclick=()=>runTask(task,option);
      group.append(row);menuButtons.push({button:row,task,option,group:null});
    }
    list.append(group);
  }
}
// Marks the active task and its chosen line; the tasks work once text is captured.
function updateTaskMenu(){
  const noText=!sourceText.trim();
  for(const m of menuButtons){
    m.button.disabled=busy||noText;
    const here=!noText&&activeTask===m.task,chosen=taskOption[m.task]||'';
    const selected=here&&(m.option==='context'?factSetup:m.option===''?chosen===''&&!(m.task==='factcheck'&&factSetup):chosen===m.option);
    if(m.group){m.group.className='task-group'+(here?' active':'')+(noText?' idle':'');m.button.className='group-head'+(selected?' selected':'')+(selected&&busy?' running':'');}
    else m.button.className='group-row'+(selected?' selected':'')+(selected&&busy?' running':'');
  }
}
// Whether the active option's result for this capture is ready to show.
function ready(task){
  if(task==='summary'||task==='explain')return !!answers[answerKey(task)];
  if(task==='rewrite')return taskReady.rewrite===captureId;
  return taskReady[task]===captureId&&(readyOption[task]||'')===(taskOption[task]||'');
}
// Shows the active task's result when it belongs to the current capture.
function showTaskView(){
  $('results').hidden=activeTask!=='review'||!ready('review');
  $('translation-view').hidden=activeTask!=='translate'||!ready('translate');
  $('factcheck-view').hidden=activeTask!=='factcheck'||!ready('factcheck');
  $('factcheck-setup').hidden=activeTask!=='factcheck'||ready('factcheck')||!factSetup;
  $('rewrite-view').hidden=activeTask!=='rewrite'||!ready('rewrite');
  $('choose-view').hidden=!sourceText.trim();$('empty-view').hidden=!!sourceText.trim();$('main').classList.toggle('task-active',!!activeTask);
  $('choose-text').textContent=sourceText;renderBand();
  const answer=['summary','explain'].includes(activeTask)?answers[answerKey(activeTask)]:null;
  $('answer-view').hidden=!answer;
  if(answer){const summary=activeTask==='summary',variant=optionLabel(activeTask,taskOption[activeTask]||'');$('answer-title').textContent=(summary?'Summary':'Explanation')+(variant?': '+variant:'');
    $('answer-hint').textContent='Written from the selected text alone. Ask a follow-up question or object to something below.';
    $('answer-model').textContent=answer.model||'';renderDialog();}
}
// Rewrite card: the main action improves the text; options are presets. Each starts from the captured original.
const rewriteCardRequests={'':'Improve: clearer and smoother wording; keep the meaning, tone, form of address and length',formal:'More formal and professional; keep du or Sie as written',friendly:'Friendlier and warmer, still polite',short:'Shorter: remove repetition and filler, keep all facts'};
// Runs a task option on the captured text, or shows its cached result for this capture.
function runTask(task,option=''){
  if(busy||!sourceText.trim())return;
  activeTask=task;
  if(task==='factcheck'&&option==='context'){
    factSetup=true;showTaskView();
    if(ready('factcheck')){$('fact-more').open=true;$('fact-context-more').focus?.();notice('Add context, then check again.');}
    else{$('fact-context').focus?.();notice('Add context, then start the fact check.');}
    update();return;
  }
  // Own request starts from the original like the other options; only the field under a result builds on it.
  if(task==='rewrite'&&option==='more'){taskOption.rewrite='more';taskReady.rewrite=captureId;rewriteSteps=[];showTaskView();renderRewrite();notice('Describe the change.');update();return;}
  if(task==='factcheck')factSetup=false;
  taskOption[task]=option;showTaskView();
  if(task==='rewrite'){
    const request=rewriteCardRequests[option]??rewriteCardRequests[''];
    if(rewriteSteps.length===1&&rewriteSteps[0].request===request&&ready('rewrite')){update();return;}
    if(!connected){notice('Connect ChatGPT in Settings first.');update();return;}
    rewriteSteps=[];renderRewrite();requestRewrite(request);update();return;
  }
  if(ready(task)){update();return;}
  if(!connected){notice('Connect ChatGPT in Settings first.');update();return;}
  requestedOption[task]=option;
  if(task==='translate'){notice('Translating your text...');post('translate',{text:sourceText,captureId,...(option?{target:option}:{})});}
  else if(task==='summary'||task==='explain'){notice(task==='summary'?'Summarizing your text...':'Explaining your text...');post('write',{kind:task,text:sourceText,original:sourceText,captureId,...(option?{variant:option}:{})});}
  else if(task==='factcheck')startFactCheck('');
  else{notice('Checking your text...');post('check',{text:sourceText,captureId,...(option==='errors'?{scope:'basics'}:{})});}
  update();
}
// Starts a fact check of the whole text; context and earlier answers go along.
function startFactCheck(context){
  if(busy||!sourceText.trim())return;
  if(!connected){notice('Connect ChatGPT in Settings first.');return;}
  factContext=context.trim();$('fact-context').value=factContext;$('fact-context-more').value=factContext;
  notice('Researching the claims on the web. This can take a minute...');
  post('factCheck',{text:sourceText,context:factContext,clarifications:factClarifications,captureId});
}
// Checks one needs_context claim again with the user's answer.
function answerClaim(c,answer){
  if(busy||!answer)return;
  if(!connected){notice('Connect ChatGPT in Settings first.');return;}
  c.draft=answer;
  factClarifications=factClarifications.filter(x=>x.quote!==c.quote).concat({quote:c.quote,question:c.question||'',answer});
  notice('Checking the claim again with your answer...');
  post('factCheck',{text:sourceText,context:factContext,clarifications:factClarifications,only:[c.quote],captureId});
}
function factClaim(c,original){
  const correction=c.correction?reviewPlainStyle(c.correction,c.quote,punctuationStyle):null;
  const wording=correction?reviewWording(original,{quotes:[c.quote],suggestedRewrite:correction},factIds++):null;
  if(wording)wording.category='fact';
  return {...c,explanation:reviewPlainStyle(c.explanation,original,punctuationStyle),correction,wording};
}
function factRevised(){return reviewRevised(factOriginal,[],factWordings);}
function factEdits(){return reviewEdits([],factWordings);}
function renderFacts(){
  const claims=factClaims,count=v=>claims.filter(c=>c.verdict===v).length;
  $('fact-count').textContent=claims.length;
  $('fact-meta').textContent=factMeta?`Researched in ${Math.max(1,Math.round(factMeta.ms/1000))} s with ${factMeta.model}`:'';
  const kinds=[['supported','supported by the sources found'],['wrong','wrong'],['disputed','disputed'],['unverifiable',"can't verify"],['needs_context','need context']].filter(([v])=>count(v));
  const summary=list=>claims.length?`${claims.length} ${claims.length===1?'claim':'claims'}: ${list.join(', ')}.`:'No checkable factual claims found in this text.';
  $('fact-summary').textContent=summary(kinds.map(([v,t])=>`${count(v)} ${t}`));
  factKinds=kinds.map(([v,t])=>[v,t,count(v)]);
  const list=$('fact-claims');list.replaceChildren();
  $('fact-export').replaceChildren(...(claims.length?[exportRow(factResultsBlocks,{label:'Copy results as'})]:[]));
  for(const c of claims){
    const card=document.createElement('article');card.className='fact-card verdict-'+c.verdict;
    const badge=document.createElement('span');badge.className='verdict';badge.textContent=verdictLabel[c.verdict];
    const quote=document.createElement('blockquote');quote.textContent=c.quote;
    card.append(badge,quote);
    if(c.explanation){const p=document.createElement('div');p.className='fact-explanation';renderMarkup(p,c.explanation);card.append(p);}
    const given=factClarifications.find(x=>x.quote===c.quote);
    if(given&&c.verdict!=='needs_context'){const p=document.createElement('p');p.className='hint';p.textContent='Your answer: '+given.answer;card.append(p);}
    if(c.verdict==='needs_context'){
      const ask=document.createElement('p');ask.className='fact-question';ask.textContent=c.question||'What does this claim refer to? Add the details needed to check it.';
      const form=document.createElement('form');form.className='revise-translation fact-answer';
      const input=document.createElement('input');input.type='text';input.maxLength=1000;input.autocomplete='off';input.placeholder='Your answer';input.value=c.draft||'';input.disabled=busy;input.oninput=()=>{c.draft=input.value;};
      const send=document.createElement('button');send.type='submit';send.textContent='Check this claim';send.disabled=busy;
      form.onsubmit=e=>{e.preventDefault();answerClaim(c,input.value.trim());};
      form.append(input,send);card.append(ask,form);
    }
    if(c.noSource){const p=document.createElement('p');p.className='hint';p.textContent='No source was cited for this verdict, so the claim is marked as not verifiable.';card.append(p);}
    if(c.sources.length){
      const sources=document.createElement('ul');sources.className='fact-sources';
      for(const s of c.sources){
        const item=document.createElement('li');const link=document.createElement('button');link.type='button';link.className='link source-link';
        link.textContent=s.title;link.title=s.url;link.onclick=()=>post('openSource',{url:s.url});
        const domain=document.createElement('span');domain.className='hint';domain.textContent=s.domain;
        item.append(link,domain);sources.append(item);
      }
      card.append(sources);
    }
    if(c.correction){
      if(c.wording){
        const label=document.createElement('label');label.className='fact-fix';
        const box=document.createElement('input');box.type='checkbox';box.checked=c.wording.accepted;box.disabled=busy||c.wording.layoutProtected;
        box.onchange=()=>{if(box.checked)reviewAcceptWording(c.wording,factWordings);else c.wording.accepted=false;renderFacts();update();};
        const text=document.createElement('span');text.textContent='Use correction: ';
        const fix=document.createElement('ins');fix.textContent=c.wording.after;
        label.append(box,text,fix);card.append(label);
        if(c.wording.layoutProtected){const p=document.createElement('p');p.className='hint';p.textContent='This correction changes line breaks and cannot be applied.';card.append(p);}
      }else{
        const p=document.createElement('p');p.className='hint';p.textContent='Correction: '+c.correction+' (the quote appears more than once in the text, so it is not applied automatically).';card.append(p);
      }
    }
    list.append(card);
  }
  $('fact-revision').hidden=!factWordings.length;
  $('fact-preview').textContent=factRevised();
}
function notice(message){$('notice').textContent=message||'Ready';$('notice').hidden=false;}
function update(){
  syncAsides();
  // Copy buttons of answers are built while a request may still be running; they follow the busy state from here.
  for(const b of document.querySelectorAll?.('.export-button, .msg-foot .use-capture, #use-answer')||[])b.disabled=busy;
  $('count').textContent=`${$('source').value.length.toLocaleString()} / 30,000`;
  $('signin').disabled=busy;$('signout').disabled=busy;$('source').disabled=busy;
  $('cancel').hidden=!busy;
  for(const id of ['use-review','use-translation','use-fact','use-rewrite'])$(id).disabled=busy;
  for(const id of ['fact-start','fact-recheck','fact-context','fact-context-more'])$(id).disabled=busy;
  $('dialog-send').disabled=busy;$('dialog-question').disabled=busy;$('copy-conversation').disabled=busy;
  const formattedPaste=captureMode!=='chrome'&&richClipboard&&!$('allow-plain-text').checked;
  $('copy').disabled=busy;$('copy').hidden=!formattedPaste;$('apply').disabled=busy||!canReplace||(captureMode!=='chrome'&&!formattedPaste&&!$('allow-plain-text').checked);
  $('allow-plain-text').disabled=busy;$('plain-text-option').hidden=captureMode==='chrome'||!canReplace;
  $('apply').hidden=readOnlyCapture;$('readonly-hint').hidden=!readOnlyCapture;
  $('translation-request').disabled=busy;$('revise-translation-button').disabled=busy;$('apply-translation').disabled=busy||!canReplace||!translationIntact;$('apply-translation').textContent=translationStructured?'Replace selection':'Replace as plain text';$('apply-rewrite').textContent=rewriteStructured?'Replace selection':'Replace as plain text';$('rewrite-plain').hidden=rewriteStructured;$('apply-translation').hidden=readOnlyCapture;$('translation-readonly').hidden=!readOnlyCapture;
  const bulkCount=tabCount(activeTab);
  $('accept-all').disabled=locked()||!bulkCount||(activeTab==='formatting'&&$('allow-plain-text').checked);$('reject-all').disabled=locked()||!bulkCount||(activeTab==='formatting'&&$('allow-plain-text').checked);
  document.querySelectorAll('#inline-text button, #changes input').forEach(control=>control.disabled=locked()||control.dataset.layoutProtected==='true'||control.dataset.inactive==='true');
  document.querySelectorAll('#findings button').forEach(control=>control.disabled=locked()||control.dataset.layoutProtected==='true');
  $('apply').textContent=captureMode==='chrome'?'Apply in Chrome':formattedPaste?'Paste with formatting':'Paste plain text';
  $('route').className='capture-icon';
  const captureLabel=captureMode==='chrome'?'Captured from Chrome':'Captured with '+(shortcutLabels[captureTask]||shortcutLabels.open);
  updateTaskMenu();
  const factFormatted=captureMode!=='chrome'&&richClipboard&&!$('fact-plain').checked;
  const factAccepted=factWordings.some(w=>w.accepted);
  $('fact-copy').disabled=busy;$('fact-copy').hidden=!factFormatted;
  $('fact-apply').hidden=readOnlyCapture;$('fact-apply').disabled=busy||!canReplace||!factAccepted||(captureMode!=='chrome'&&!factFormatted&&!$('fact-plain').checked);
  $('fact-apply').textContent=captureMode==='chrome'?'Apply in Chrome':factFormatted?'Paste with formatting':'Paste plain text';
  $('fact-plain').disabled=busy;$('fact-plain-option').hidden=captureMode==='chrome'||!canReplace;$('fact-readonly').hidden=!readOnlyCapture;
  $('route').title=captureLabel;$('route').setAttribute('aria-label',captureLabel);
  $('clipboard-icon').hidden=captureMode==='chrome';$('chrome-icon').hidden=captureMode!=='chrome';
  $('route').hidden=!canReplace&&!readOnlyCapture;
  $('route-title').textContent=captureMode==='chrome'?'Captured from Chrome':'Captured with '+shortcutLabels.review;
  $('formatting').hidden=richClipboard||captureMode==='chrome'||!canReplace||activeTask!=='review';
  $('formatting').textContent=richClipboard?'':'This selection includes formatting. Plain-text replacement may remove formatting.';
}
let formattingSuggestions=[];
// A formatting fix needs a known decoration and a run around its space inside the original text.
function validFormatting(f){return ['underline','strikethrough','link'].includes(f.property)&&Number.isInteger(f.runStart)&&Number.isInteger(f.runEnd)&&f.runStart>=0&&f.runEnd<=reviewOriginal.length&&f.runStart<=f.start&&f.end<=f.runEnd;}
// Formatting fixes shown in the review. Where a text correction or a used wording touches the run, the text wins and
// the formatting fix is not offered. Chrome captures have no formatting to fix.
function shownFormatting(){
  if(captureMode==='chrome')return [];
  const edits=[...parts.filter(p=>p.changed),...wordings.filter(w=>w.accepted)];
  const touches=(p,f)=>p.start===p.end?p.start>f.runStart&&p.start<f.runEnd:p.start<f.runEnd&&p.end>f.runStart;
  return formattingSuggestions.filter(f=>validFormatting(f)&&!edits.some(p=>touches(p,f)));
}
function selectedFormatting(){return shownFormatting().filter(f=>f.accepted).map(f=>f.id);}
// Formatting fixes start accepted, like text corrections.
function showFormatting(items){formattingSuggestions=items.map(f=>({...f,accepted:true}));}
const plainReplace=()=>!!$('allow-plain-text').checked;
// Old and new run of a formatting fix: the old run with its decoration over the space, the new one without it there.
// Only plain text with known CSS classes, never captured HTML or links.
function formattingVariants(f){
  return [false,true].map(corrected=>{
    const variant=document.createElement(corrected?'ins':'del');
    const run=document.createElement('span');run.className='format-decoration format-'+f.property;
    const space=document.createElement('span');space.className='format-space';space.textContent='\u00b7';space.title=tr('Space (kept in the text)');
    const leading=f.start===f.runStart;
    const content=document.createElement('span');content.textContent=reviewOriginal.slice(leading?f.end:f.runStart,leading?f.runEnd:f.start);
    if(corrected){run.append(content);if(leading)variant.append(space,run);else variant.append(run,space);}
    else {if(leading)run.append(space,content);else run.append(content,space);variant.append(run);}
    return variant;
  });
}
// A formatting fix in the text: clickable in the Formatting tab, gray in the other tabs.
function formattingBox(f,active){
  const box=document.createElement(active?'button':'span');
  box.className=(active?'inline-correction':'inactive-correction')+' formatting-correction'+(f.accepted?'':' rejected');
  box.append(...formattingVariants(f));
  if(active){
    box.type='button';box.dataset.start=String(f.runStart);box.dataset.inactive=String(plainReplace());box.disabled=locked()||plainReplace();
    box.setAttribute('aria-pressed',String(f.accepted));
    box.title=`${tr(f.description)}. ${tr(f.accepted?'Accepted correction. Click to reject.':'Rejected correction. Click to accept.')}`;
    box.onclick=()=>{if(box.disabled)return;f.accepted=!f.accepted;showChanges();};
  }else{
    box.title=`${tr(f.description)}. ${tr('Formatting correction. Click to open its tab.')}`;
    box.onclick=()=>{if(!plainReplace())openCorrection({category:'formatting',start:f.runStart});};
  }
  return box;
}
function revised(){return reviewRevised(reviewOriginal,parts,wordings);}
function acceptedEdits(){return reviewEdits(parts,wordings);}
function preview(){$('preview').textContent=revised();showInline();}
function toggleCorrection(p){
  if(locked()||!activeCorrection(p))return;
  p.accepted=!p.accepted;showChanges();
}
// Sentence ends in the original text: after closing punctuation and its spaces, and after line breaks.
function sentenceEnds(text){
  const ends=[],re=/[.!?\u2026]+["'\u201c\u201d\u00bb\u00ab)\]]*\s+|(?:\r?\n)+/g;let m;
  while((m=re.exec(text)))ends.push(m.index+m[0].length);
  if(ends.at(-1)!==text.length)ends.push(text.length);
  return ends;
}
function sentenceIndex(ends,pos){let i=0;while(i<ends.length-1&&ends[i]<=pos)i++;return i;}
// Opens the tab of a correction shown in gray and points at it there.
function openCorrection(p){
  if(p.category==='worth'){$('worth-results').scrollIntoView?.({block:'start',behavior:'smooth'});return;}
  const state=tabState[p.category];
  if(!reviewTabs.some(([k])=>k===p.category)||(state&&state!=='ready'))return;
  autoTab=false;activeTab=p.category;showChanges();
  const button=[...$('inline-text').querySelectorAll('button')].find(b=>b.dataset.start===String(p.start));
  if(!button)return;
  button.scrollIntoView?.({block:'nearest',behavior:'smooth'});
  button.classList.toggle('flash',true);setTimeout(()=>button.classList.toggle('flash',false),1000);
}
function showInline(){
  const container=$('inline-text');container.replaceChildren();let pos=0;
  // Adjacent unchanged parts form one piece, so a formatting run across a part boundary is found.
  const segments=[];
  for(const p of reviewSegments(reviewOriginal,parts,wordings)){
    const last=segments.at(-1);
    if(last&&!p.changed&&!p.superseded&&!last.changed&&!last.superseded)segments[segments.length-1]={...last,before:last.before+p.before,after:last.after+p.after};
    else segments.push(p);
  }
  const ends=sentenceEnds(reviewOriginal),lit=new Set(),fmts=shownFormatting();
  const light=(start,end)=>{for(let i=sentenceIndex(ends,start),last=sentenceIndex(ends,Math.max(start,end-1));i<=last;i++)lit.add(i);};
  for(const p of segments)if(p.changed&&p.category===activeTab)light(p.start,p.end);
  if(activeTab==='formatting')for(const f of fmts)light(f.runStart,f.runEnd);
  const muted=i=>lit.size>0&&!lit.has(i);
  segments.forEach(p=>{
    const at=pos;pos+=(p.before||'').length;
    if(!p.changed){
      // Unchanged text splits at sentence ends, so each sentence can be muted on its own, and around formatting fixes.
      for(let s=at,end=pos;s<end;){
        const f=fmts.find(f=>f.runStart===s&&f.runEnd<=end);
        if(f){container.append(formattingBox(f,activeTab==='formatting'));s=f.runEnd;continue;}
        const i=sentenceIndex(ends,s);let e=Math.min(end,ends[i]);
        for(const g of fmts)if(g.runStart>s&&g.runStart<e&&g.runEnd<=end)e=g.runStart;
        const span=document.createElement('span');appendGapped(span,reviewOriginal.slice(s,e),s,reviewOriginal);
        span.className=[p.superseded?'superseded-text':'',muted(i)?'muted':''].filter(Boolean).join(' ');
        container.append(span);s=e;
      }
      return;
    }
    if(p.category!==activeTab){
      // Same old and new boxes as in its own tab, in gray, so switching tabs keeps the text in place.
      const span=document.createElement('span');span.className='inactive-correction';if(!p.accepted)span.className+=' rejected';
      if(p.before)span.append(removedText(p.before,!p.after&&p.accepted));
      if(p.after)span.append(addedText(p.after,p.accepted));
      span.title=tr(p.category==='worth'?'Wording from Worth checking. Click to show it.':`${reviewPartNames[p.category]||p.category} correction. Click to open its tab.`);
      span.onclick=()=>openCorrection(p);container.append(span);return;
    }
    const button=document.createElement('button');button.type='button';button.className='inline-correction'+(p.accepted?'':' rejected');button.dataset.start=String(p.start);
    button.disabled=locked()||!!p.layoutProtected;button.dataset.layoutProtected=String(!!p.layoutProtected);button.dataset.category=p.category;
    button.title=tr(p.layoutProtected?'Not applied: this suggestion would change line breaks.':p.accepted?'Accepted correction. Click to reject.':'Rejected correction. Click to accept.');
    button.setAttribute('aria-pressed',String(p.accepted));
    button.setAttribute('aria-label',`${p.before||'insert'} to ${p.after||'delete'}. ${button.title}`);
    if(p.before)button.append(removedText(p.before,!p.after&&p.accepted));
    if(p.after)button.append(addedText(p.after,p.accepted));
    button.onclick=()=>toggleCorrection(p);container.append(button);
  });
}
// A struck-through comma reads like a dash; short punctuation or space removals render as a labelled box instead.
function removedText(text,label){
  const old=document.createElement('del');old.textContent=text;
  if(text.length<=4&&!/[\p{L}\p{N}\r\n]/u.test(text)){
    const space=!text.trim();old.className='punct';
    old.dataset.label=tr(label?(space?'remove space':'remove'):(space?'space':''));
  }
  return old;
}
// Short added punctuation or spaces get a labelled box like removals, so they are easy to spot.
function addedText(text,label){
  const next=document.createElement('ins');next.textContent=text;
  if(text.length<=4&&!/[\p{L}\p{N}\r\n]/u.test(text)){
    const space=!text.trim();next.className='punct';
    next.dataset.label=tr(label?(space?'add space':'add'):(space?'space':''));
  }
  return next;
}
function showChanges(){
  const container=$('changes');container.replaceChildren();
  const changes=[...parts.filter(p=>p.changed&&p.category===activeTab),...wordings.filter(w=>w.created&&activeTab==='worth')];
  const fmtTab=activeTab==='formatting',fmts=fmtTab?shownFormatting():[];
  if(!changes.length&&!fmts.length){const p=document.createElement('p');p.className='empty';p.textContent='No text corrections in this category.';container.append(p);}
  fmts.forEach(f=>{
    const label=document.createElement('label');label.className='change';label.title=tr(f.description);
    const checkbox=document.createElement('input');checkbox.type='checkbox';checkbox.className='switch';checkbox.checked=f.accepted;
    checkbox.disabled=locked()||plainReplace();checkbox.dataset.inactive=String(plainReplace());
    checkbox.onchange=()=>{if(checkbox.disabled)return;f.accepted=checkbox.checked;showChanges();};
    const content=document.createElement('span');content.className='change-content formatting-correction';
    const [old,next]=formattingVariants(f),arrow=document.createElement('span');arrow.className='change-arrow';arrow.textContent='\u2192';
    content.append(old,arrow,next);
    label.append(checkbox,content,changeContext({start:f.runStart,end:f.runEnd}));container.append(label);
  });
  changes.forEach((p,index)=>{
    const inactive=!activeCorrection(p),label=document.createElement('label');label.className='change'+(inactive?' inactive-change':'');
    const checkbox=document.createElement('input');checkbox.type='checkbox';checkbox.checked=p.accepted;checkbox.setAttribute('aria-label',`Accept correction ${index+1}`);
    checkbox.disabled=locked()||inactive;checkbox.dataset.layoutProtected=String(!!p.layoutProtected);checkbox.dataset.inactive=String(inactive);
    checkbox.onchange=()=>{
      if(locked()||!activeCorrection(p))return;
      if(p.category==='worth'&&checkbox.checked)reviewAcceptWording(p,wordings);else p.accepted=checkbox.checked;
      showChanges();
    };
    checkbox.className='switch';
    const content=document.createElement('span');content.className='change-content';
    if(!p.before)content.append(addedText(p.after,true));
    else if(!p.after)content.append(removedText(p.before,true));
    else {
      const arrow=document.createElement('span');arrow.className='change-arrow';arrow.textContent='\u2192';
      const next=document.createElement('ins');next.textContent=p.after;content.append(removedText(p.before,false),arrow,next);
    }
    label.append(checkbox,content,changeContext(p));container.append(label);
  });
  // Progress of the active tab: accepted and rejected corrections.
  const decided=fmtTab?(plainReplace()?[]:fmts):parts.filter(p=>p.changed&&p.category===activeTab&&activeCorrection(p)),yes=decided.filter(p=>p.accepted).length;
  $('decision-progress').hidden=!decided.length;
  $('progress-accepted').style?.setProperty?.('width',decided.length?`${yes/decided.length*100}%`:'0');
  $('progress-rejected').style?.setProperty?.('width',decided.length?`${(decided.length-yes)/decided.length*100}%`:'0');
  $('progress-label').textContent=`${yes} accepted, ${decided.length-yes} rejected`;
  preview();renderTabs();showFindings();update();
}
// The sentence around a correction, shortened, with the changed words in bold.
function changeContext(p){
  const ends=sentenceEnds(reviewOriginal),i=sentenceIndex(ends,p.start),from=i?ends[i-1]:0,to=ends[i];
  const left=reviewOriginal.slice(from,p.start).replace(/^\s+/,''),right=reviewOriginal.slice(p.end,to).replace(/\s+$/,'');
  const box=document.createElement('span');box.className='change-context';
  const mark=document.createElement('b');mark.textContent=reviewOriginal.slice(p.start,p.end)||'\u2038';
  const text=value=>{const span=document.createElement('span');span.textContent=value;return span;};
  // Long sides are cut at a word boundary.
  box.append(text(left.length>28?'\u2026'+left.slice(-28).replace(/^\S*\s/,''):left),mark,text(right.length>28?right.slice(0,28).replace(/\s\S*$/,'')+'\u2026':right));
  return box;
}
function showFindings(){
  const container=$('findings');container.replaceChildren();$('finding-count').textContent=tabCount('worth');
  if(!tabCount('worth')){const p=document.createElement('p');p.className='empty';p.textContent='No specific reasoning or terminology concerns found.';container.append(p);}
  reviewFindings.forEach((f,index)=>{
    if(f.dismissed)return;
    const card=document.createElement('article');card.className='finding';
    const top=document.createElement('div');top.className='finding-top';
    const kind=document.createElement('span');kind.className='kind';kind.textContent=tr(f.kind.replaceAll('_',' '));
    const dismiss=document.createElement('button');dismiss.className='link';dismiss.textContent='Dismiss';dismiss.disabled=locked();
    dismiss.onclick=()=>{if(locked())return;f.dismissed=true;const w=wordings.find(w=>w.findingIndex===index);if(w){w.accepted=false;w.created=false;}showChanges();};
    top.append(kind,dismiss);card.append(top);
    (f.quotes||[]).forEach(q=>{const quote=document.createElement('blockquote');quote.textContent=q;card.append(quote);});
    const explanation=document.createElement('p');explanation.textContent=f.explanation;card.append(explanation);
    if(f.suggestedRewrite){
      const rewrite=document.createElement('p');rewrite.textContent=`Possible wording: ${f.suggestedRewrite}`;card.append(rewrite);
      const w=wordings.find(w=>w.findingIndex===index);
      if(w){
        const use=document.createElement('button');use.type='button';use.className='use-wording';use.textContent=w.accepted?'Undo wording':'Use this wording';
        use.dataset.findingIndex=String(index);use.dataset.layoutProtected=String(w.layoutProtected);use.disabled=locked()||w.layoutProtected;
        use.title=w.layoutProtected?'This rewrite changes line breaks and cannot be applied.':w.accepted?'Restore the overlapping correction choices.':'Replace the uniquely matched quote with this wording.';
        use.onclick=()=>{if(locked()||w.layoutProtected)return;if(w.accepted)w.accepted=false;else reviewAcceptWording(w,wordings);showChanges();};
        card.append(use);
        if(w.layoutProtected){
          const reason=document.createElement('p');reason.className='hint wording-reason';
          reason.id='wording-reason-'+index;reason.textContent='This rewrite changes line breaks and cannot be applied.';
          use.setAttribute('aria-describedby',reason.id);card.append(reason);
        }
      }
      const note=document.createElement('small');note.textContent=w?.accepted?'Included in revised text.':'Not applied automatically.';card.append(note);
    }
    container.append(card);
  });
}
function bulkCorrections(accepted){
  if(locked())return;
  if(activeTab==='formatting'){if(!plainReplace())shownFormatting().forEach(f=>f.accepted=accepted);showChanges();return;}
  parts.filter(p=>p.changed&&activeCorrection(p)).forEach(p=>p.accepted=accepted);
  showChanges();
}
$('source').addEventListener('input',()=>{pendingAutoCheck=false;sourceText=$('source').value;canReplace=false;readOnlyCapture=false;richClipboard=false;$('formatting').hidden=true;$('results').hidden=true;update();});
$('signin').onclick=()=>post('signin');$('signout').onclick=()=>post('signout');$('usage').onclick=()=>post('usage');$('cancel').onclick=()=>post('cancel');
$('open-help').onclick=()=>$('help').showModal();$('close-help').onclick=()=>$('help').close();
$('settings').addEventListener('close',stopRecording);
// Pin: keeps the window in front of other windows. Kept in this WebView profile and applied at start.
const pinWindow=(()=>{
  let on=false;try{on=localStorage.getItem('pinWindow')==='1';}catch{}
  const apply=send=>{const b=$('pin-window');b.classList.toggle('active',on);b.setAttribute('aria-pressed',String(on));b.title=tr(on?'In front of other windows. Click to release.':'Keep TextFixer in front of other windows');if(send)post('topMost',{on});};
  $('pin-window').onclick=()=>{on=!on;try{localStorage.setItem('pinWindow',on?'1':'0');}catch{}apply(true);};
  apply(on);return {apply};
})();
$('open-settings').onclick=()=>{$('settings').showModal();post('chromeStatus');post('models');};$('close-settings').onclick=()=>$('settings').close();
$('copy').onclick=()=>post('copy',{text:revised(),original:reviewOriginal,edits:acceptedEdits(),formattingIds:selectedFormatting(),allowPlainText:$('allow-plain-text').checked});$('apply').onclick=()=>post('apply',{text:revised(),original:reviewOriginal,edits:acceptedEdits(),formattingIds:selectedFormatting(),allowPlainText:$('allow-plain-text').checked});
// With plain-text replacement the formatting fixes stay visible in gray; the Formatting tab is left for the first other tab.
$('allow-plain-text').onchange=()=>{if(plainReplace()&&activeTab==='formatting')activeTab=reviewTabs.find(([k])=>k!=='formatting'&&tabCount(k)>0)?.[0]||'spelling';showChanges();};
document.querySelectorAll('input[data-writing]').forEach(r=>r.onchange=()=>{if(r.checked)post('writing',{[r.dataset.writing]:r.value});});
renderTaskMenu();
$('rewrite-form').onsubmit=e=>{e.preventDefault();requestRewrite($('rewrite-request').value.trim());};
// A result becomes the captured text for the next task.
function useAsCapture(text){if(busy||!text?.trim())return;post('useAsCapture',{text});}
$('use-review').onclick=()=>useAsCapture(revised());$('use-translation').onclick=()=>useAsCapture(resultPlain(translationText,translationStructured));
$('use-fact').onclick=()=>useAsCapture(factRevised());$('use-rewrite').onclick=()=>useAsCapture(resultPlain(rewriteSteps.at(-1)?.text||'',rewriteStructured));
$('apply-rewrite').onclick=()=>{const text=rewriteSteps.at(-1)?.text||'';if(rewriteStructured)applyStructured('applyRewrite',text,sourceText);else post('applyRewrite',{text,original:sourceText});};
$('rewrite-reset').onclick=()=>{rewriteSteps=[];renderRewrite();update();notice('Describe the change.');};
$('dialog-form').onsubmit=e=>{e.preventDefault();askDialog();};
$('dialog-question').onkeydown=e=>{if(e.key==='Enter'&&!e.shiftKey&&!e.isComposing){e.preventDefault();askDialog();}};
$('dialog-question').oninput=sizeQuestion;
$('copy-conversation').onclick=()=>{const a=answers[answerKey(activeTask)];if(a)post('copyResult',{kind:'conversation',text:conversationText(activeTask,a)});};
document.querySelectorAll('input[data-style]').forEach(r=>r.onchange=()=>{if(r.checked)post('typography',{style:{[r.dataset.style]:r.value}});});
$('revise-translation').onsubmit=e=>{e.preventDefault();const request=$('translation-request').value.trim();if(busy||!request||!translationText)return;notice('Revising the translation...');post('reviseTranslation',{text:translationOriginal,translation:translationText,request,captureId});};
$('fact-copy').onclick=()=>post('copy',{text:factRevised(),original:factOriginal,edits:factEdits(),formattingIds:[],allowPlainText:$('fact-plain').checked});
$('fact-apply').onclick=()=>post('apply',{text:factRevised(),original:factOriginal,edits:factEdits(),formattingIds:[],allowPlainText:$('fact-plain').checked});
$('fact-plain').onchange=update;
$('fact-start').onclick=()=>startFactCheck($('fact-context').value);
$('fact-recheck').onclick=()=>startFactCheck($('fact-context-more').value);
$('fact-context').onkeydown=e=>{if(e.key==='Enter'&&e.ctrlKey){e.preventDefault();startFactCheck($('fact-context').value);}};
$('fact-context-more').onkeydown=e=>{if(e.key==='Enter'&&e.ctrlKey){e.preventDefault();startFactCheck($('fact-context-more').value);}};
$('apply-translation').onclick=()=>{if(translationStructured)applyStructured('applyTranslation',translationText,translationOriginal);else post('applyTranslation',{text:translationText,original:translationOriginal});};
$('open-source').onclick=()=>{if(!sourceText)return;activeTask='';showTaskView();update();};
document.querySelectorAll('input[name=review-mode]').forEach(r=>r.onchange=()=>{if(r.checked)post('reviewMode',{mode:r.value});});
$('autostart').onchange=e=>post('autostart',{enabled:e.target.checked});$('register-chrome').onclick=()=>post('registerChrome');$('extension-folder').onclick=()=>post('extensionFolder');$('refresh-chrome').onclick=()=>post('chromeStatus');
$('accept-all').onclick=()=>bulkCorrections(true);$('reject-all').onclick=()=>bulkCorrections(false);
// While a task runs and its result has not appeared yet, the empty main area shows a spinner with the status text.
// The task band stays at the top; the spinner shows below it while none of these has content.
const resultViews=['choose-preview','results','translation-view','rewrite-view','answer-view','factcheck-setup','factcheck-view','empty-view'];
function updateWorking(){
  const w=$('working');if(!w)return;
  const waiting=busy&&resultViews.every(id=>!$(id)||$(id).hidden);
  // Only real changes are written; writing the same value would wake the observer below again.
  if(w.hidden===waiting)w.hidden=!waiting;
  const text=waiting?$('notice').textContent:'';if($('working-text').textContent!==text)$('working-text').textContent=text;
}
if(typeof MutationObserver==='function'){
  const watch=new MutationObserver(updateWorking);
  watch.observe($('main'),{subtree:true,attributes:true,attributeFilter:['hidden','class']});
  watch.observe($('notice'),{childList:true,characterData:true,subtree:true});
}
window.chrome.webview.addEventListener('message',event=>{
  const m=event.data;
  if(['result','tabProgress','tabResult','translation','factCheck','writing','dialog'].includes(m.type)&&m.captureId!==undefined&&m.captureId!==captureId)return;
  if(m.type==='capture'){captureClip=m.clip||null;captureText=m.text||'';formatInfo=detectFormat(m.text||'',m);captureDoc=m.doc&&m.text?{text:m.text,model:m.doc.model||'',title:m.doc.title||'',items:new Map((m.doc.items||[]).map(i=>[String(i.id),i])),ranges:m.doc.ranges||[]}:null;markupSetItems(captureDoc?.items);revisedRows();renderPostInfo(m.text?m.doc:null);translationStructured=false;translationIntact=true;rewriteStructured=false;rewriteIntact=true;captureTask=['open','translate','factcheck','rewrite','summary','explain'].includes(m.task)?m.task:'review';activeTask=captureTask==='open'?'':captureTask;if(m.captureId!==undefined)captureId=m.captureId;taskReady={};taskOption={};readyOption={};requestedOption={};factSetup=false;dialogKey='';rewriteSteps=[];answers={};$('rewrite-request').value='';factClaims=[];factWordings=[];factMeta=null;factContext='';factClarifications=[];$('fact-context').value='';$('fact-context-more').value='';$('fact-more').open=false;$('fact-plain').checked=false;$('factcheck-view').hidden=true;$('translation-view').hidden=true;$('translation-request').value='';activeTab='spelling';partialReview=false;tabState={};tabResults={};parts=[];wordings=[];reviewFindings=[];if($('help').open)$('help').close();showFormatting([]);if($('settings').open)$('settings').close();captureMode=m.mode||'clipboard';richClipboard=!!m.canPreserveFormatting;readOnlyCapture=!!m.readOnly;$('allow-plain-text').checked=false;sourceText=m.text;$('source').value=m.text;canReplace=m.canReplace;renderFormatInfo();$('formatting').hidden=!m.hasFormatting||captureMode==='chrome';$('results').hidden=true;pendingAutoCheck=!!m.text&&!!activeTask;notice(m.text&&!connected?(captureTask==='translate'?'Connect ChatGPT in Settings to translate this selection.':captureTask==='review'?'Connect ChatGPT in Settings to review this selection.':'Connect ChatGPT in Settings to work with this selection.'):'');showTaskView();startCapturedReview();if(m.reused)notice('The result is the captured text. Choose what TextFixer should do with it.');}
  if(m.type==='revisedExport'){
    if(!exportFormats.some(([f])=>f===m.format))return;
    let out;markupSetItems(m.items);
    try{out=markupExport(m.model||'',m.format);}finally{markupSetItems(captureDoc?.items);}
    post('copyAs',{format:m.format,text:out.text,...(out.html?{html:out.html}:{})});return;
  }
  if(m.type==='factCheck'){
    taskReady.factcheck=captureId;readyOption.factcheck='';factSetup=false;factOriginal=m.original;captureMode=m.mode||'clipboard';richClipboard=!!m.canPreserveFormatting;canReplace=m.canReplace;readOnlyCapture=!!m.readOnly;
    const fresh=(m.result.claims||[]).map(c=>factClaim(c,m.original));
    if(m.only?.length&&factClaims.length){
      // A re-check of single claims replaces only those cards.
      let replaced=0;
      for(const q of m.only){const i=factClaims.findIndex(c=>c.quote===q),n=fresh.find(c=>c.quote===q);if(i>=0&&n){factClaims[i]=n;replaced++;}}
      factWordings=factClaims.map(c=>c.wording).filter(Boolean);
      renderFacts();showTaskView();
      const still=fresh.some(c=>c.verdict==='needs_context');
      notice(!replaced?'The claim was not returned. Try again, or add more context.':still?'Checked again. The claim still needs more context.':'Claim checked again with your answer.');
      return;
    }
    factMeta={ms:m.result.elapsedMs,model:m.result.model};
    factClaims=fresh;
    factWordings=factClaims.map(c=>c.wording).filter(Boolean);
    $('fact-context-more').value=factContext;
    renderFacts();showTaskView();
    const wrong=factClaims.filter(c=>c.verdict==='wrong').length,ask=factClaims.filter(c=>c.verdict==='needs_context').length;
    if(ask){notice(`Fact check finished. ${ask} ${ask===1?'claim needs':'claims need'} more context: answer the question on the card.`);return;}
    notice(!factClaims.length?'Fact check finished. No checkable factual claims found.':wrong?`Fact check finished. ${wrong} wrong ${wrong===1?'claim':'claims'} found. Review the sources before using a correction.`:'Fact check finished. Review the verdicts and their sources.');
  }
  if(m.type==='translation'){taskReady.translate=captureId;if(!m.revised)readyOption.translate=m.target&&m.target!=='auto'?m.target:'';$('translation-hint').textContent=readyOption.translate==='de'?'Translated into German.':readyOption.translate==='en'?'Translated into English.':'German text becomes English; text in other languages becomes German.';translationOriginal=m.original;translationText=reviewPlainStyle(m.translation,m.original,punctuationStyle);translationStructured=!!m.structured;translationIntact=m.intact!==false;canReplace=m.canReplace;readOnlyCapture=!!m.readOnly;$('results').hidden=true;$('formatting').hidden=true;showResultText('translation-text',translationText,translationStructured);$('translation-export').replaceChildren();$('translation-export').append(exportRow(()=>translationText));$('translation-original').textContent=m.original;$('translation-model').textContent=m.model||'';$('translation-view').hidden=false;if(m.revised)$('translation-request').value='';notice(!translationIntact?missingNotice('translation'):m.revised?'Translation revised. Request another change, or copy it.':readOnlyCapture?'Translation ready. Copy it and paste it where you need it.':'Translation ready. Copy it or replace your selection.');}
  if(m.type==='autostart')$('autostart').checked=!!m.enabled;
  if(m.type==='chromeStatus'){
    $('chrome-status').textContent=m.connected?'Connected to Chrome. Use the extension context menu to capture text.':m.registered?'Desktop connection registered. Load or reload the extension, then use its right-click review action.':'Chrome connection is not registered for this executable. Complete setup below.';
    $('setup-details').open=!m.connected;$('extension-path').textContent=m.folder||'chrome-extension';$('extension-folder').disabled=!m.folder;
  }
  if(m.type==='models')renderModels(m);
  if(m.type==='connected'&&$('settings').open)post('models');
  if(m.type==='screenChanged'){textSize.rescreen();return;}
  // The tray icon, the tray menu and a normal start choose the view.
  if(m.type==='status'){connected=m.status.connected;$('connection').textContent=m.status.message;$('daily-status').textContent=connected?'Using ChatGPT plan':'Connect ChatGPT in Settings';$('signin').hidden=connected;$('signout').hidden=!connected;startCapturedReview();}
  if(m.type==='dialog'){const a=answers[dialogKey],r=m.answer||{};
    if(a){a.turns.push({role:'user',text:a.pending||m.question||''},{role:'assistant',text:r.text||'',sources:r.sources||[],searches:r.searches||0});a.pending='';}
    if(answerKey(activeTask)===dialogKey&&activeTask===m.kind){renderDialog();$('dialog').lastElementChild?.scrollIntoView?.({block:'start',behavior:'smooth'});}
    notice(r.searches?'Answer ready.':'Answer ready. No web search ran for it.');}
  // A failed or cancelled question goes back into the input field.
  if(m.type==='busy'&&!m.busy)for(const k of Object.keys(answers)){const a=answers[k];if(!a?.pending)continue;if(!$('dialog-question').value)$('dialog-question').value=a.pending;a.pending='';if(answerKey(activeTask)===k)renderDialog();}
  if(m.type==='busy'){busy=m.busy;updateTaskMenu();setTimeout(updateWorking,0);if(factClaims.length)renderFacts();if(!busy&&partialReview){partialReview=false;for(const k of focusOrder)if(tabState[k]==='running')tabState[k]='stopped';if(!$('results').hidden)renderTabs();}}
  if(m.type==='notice')notice(m.message);
  if(m.type==='connected')notice('Using ChatGPT plan. You can manage this connection and its usage in ChatGPT settings.');
  if(m.type==='shortcuts'){shortcutLabels={...shortcutLabels,...m.shortcuts};shortcutDefaults={...shortcutDefaults,...m.defaults};shortcutRegistered=m.registered||{};shortcutError=m.error?{task:m.errorTask,message:m.error}:null;recordingTask=null;renderShortcuts();}
  if(m.type==='writing'){
    const text=reviewPlainStyle(m.result,m.original,punctuationStyle);canReplace=m.canReplace;readOnlyCapture=!!m.readOnly;
    if(m.kind==='rewrite'){rewriteSteps.push({text,request:m.request||''});rewriteStructured=!!m.structured;rewriteIntact=m.intact!==false;rewriteModel=m.model||'';$('rewrite-request').value='';taskReady.rewrite=captureId;renderRewrite();notice(!rewriteIntact?missingNotice('rewrite'):readOnlyCapture?'Rewrite ready. Copy it, or request another change.':'Rewrite ready. Copy it, replace your selection, or request another change.');}
    else{answers[m.kind+':'+(m.variant||'')]={text,model:m.model||'',turns:[],pending:''};taskReady[m.kind]=captureId;notice(m.kind==='summary'?'Summary ready.':'Explanation ready.');}
    showTaskView();
  }
  if(m.type==='reviewMode'){if(m.appLanguage){writingPrefs.appLanguage=m.appLanguage;setAppLanguage(m.appLanguage);}if(m.resultLanguage){writingPrefs.resultLanguage=m.resultLanguage;resultLanguage=m.resultLanguage;}if(m.emailSpelling)writingPrefs.email=m.emailSpelling;if(m.addressForm)writingPrefs.address=m.addressForm;if(m.reviewDu)writingPrefs.reviewDu=m.reviewDu;for(const r of document.querySelectorAll?.('input[data-writing]')||[])r.checked=r.value===writingPrefs[r.dataset.writing];}
  if(m.type==='reviewMode'){reviewMode=m.mode;for(const r of document.querySelectorAll?.('input[name=review-mode]')||[])r.checked=r.value===reviewMode;if(m.punctuationStyle)punctuationStyle={...punctuationStyle,...m.punctuationStyle};for(const r of document.querySelectorAll?.('input[data-style]')||[])r.checked=r.value===(punctuationStyle[r.dataset.style]||'keep');}
  if(m.type==='tabProgress'){taskReady.review=captureId;readyOption.review=requestedOption.review||'';
    if(!partialReview){partialReview=true;tabState={};tabResults={};tabErrors={};autoTab=true;reviewOriginal=m.original;parts=focusParts(m.original);reviewFindings=[];wordings=[];activeTab='spelling';$('results').hidden=false;}
    for(const k of m.running||[])tabState[k]='running';
    showChanges();tabProgressNotice();
  }
  if(m.type==='tabResult'){
    if(m.original!==reviewOriginal)return;
    applyReviewMeta(m);
    if(m.error){tabState[m.focus]='failed';tabErrors[m.focus]=m.error;}
    else{
      tabState[m.focus]='ready';
      if(m.focus==='worth'){reviewFindings=styledFindings(m.result.findings);wordings=reviewFindings.map((f,i)=>reviewWording(reviewOriginal,f,i)).filter(w=>w&&!insideProtected(w,reviewOriginal));}
      else{tabResults[m.focus]=m.result;parts=focusParts(reviewOriginal);}
    }
    if(autoTab)activeTab=reviewTabs.find(([k])=>tabState[k]==='ready'&&tabCount(k)>0)?.[0]||'spelling';
    showChanges();
    if(m.pending?.length)tabProgressNotice();else readyNotice();
  }
  if(m.type==='result'){
    taskReady.review=captureId;readyOption.review=requestedOption.review||'';partialReview=false;tabState={};
    activeTab='spelling';reviewOriginal=m.original;parts=protectParts(styledParts(m.original,m.result.correctedText,m.result.corrections),m.original);
    reviewFindings=styledFindings(m.result.findings);
    wordings=reviewFindings.map((f,i)=>reviewWording(reviewOriginal,f,i)).filter(w=>w&&!insideProtected(w,reviewOriginal));
    captureMode=m.mode||'clipboard';richClipboard=!!m.canPreserveFormatting;canReplace=m.canReplace;readOnlyCapture=!!m.readOnly;showFormatting(m.formattingSuggestions||[]);activeTab=reviewTabs.find(([key])=>tabCount(key)>0)?.[0]||'spelling';showChanges();$('model').textContent=m.model||'';$('results').hidden=false;
    notice(parts.some(p=>p.layoutProtected)?'Review ready. Suggestions that would change line breaks were excluded to preserve your paragraph layout.':(readOnlyCapture?'Review ready. Accept or reject corrections, then copy your text.':'Review ready. Accept or reject corrections, then copy or replace your text.'));
  }
  if(m.type==='applied'){canReplace=false;notice('Paste sent. TextFixer cannot verify the result in the editor. Please check it there.'+(m.clipboardRestored===false?(captureTask==='translate'?' The translation remains on the clipboard.':' The revised text remains on the clipboard.'):''));}
  if(m.type==='chromeApplied'){canReplace=false;notice(m.message);}
  if(m.type==='targetLost'){canReplace=false;}
  if(m.type==='error'){notice(`${tr(m.message)}${m.code?'\nCode: '+m.code:''}${m.param?' ('+m.param+')':''}`);$('usage').classList.toggle('primary',!!m.usageLimit);if(m.requiresSignIn){connected=false;$('signin').hidden=false;}}
  update();
});
// Settings > Model: Automatic uses the first model the account lists; translations can follow the review model.
function renderModels(m){
  const models=m.models||[];
  const listed=slug=>models.some(x=>x.slug===slug)?slug:null;
  const status=$('model-status');
  for(const id of ['model-list','translation-model-list','fact-model-list'])$(id).replaceChildren();
  document.querySelector?.('.model-choice')?.classList.toggle('empty',!models.length);
  if(!m.connected){status.textContent='Sign in to ChatGPT to choose a model.';status.hidden=false;return;}
  if(m.error){status.textContent='Models could not be loaded: '+m.error;status.hidden=false;return;}
  const missing=[m.selected,m.translationSelected,m.factCheckSelected].some(s=>s&&!listed(s));
  status.textContent=missing?'A chosen model is not available for this account. The default is used instead.':'';status.hidden=!missing;
  const render=(list,name,task,chosen,first)=>{
    const option=(slug,title,note)=>{
      const label=document.createElement('label');label.className='model-option';
      const input=document.createElement('input');input.type='radio';input.name=name;input.value=slug??'';input.checked=slug===chosen;
      input.onchange=()=>{if(input.checked)post('model',{slug,task});};
      const text=document.createElement('span');text.textContent=title;label.append(input,text);
      if(note){const hint=document.createElement('small');hint.textContent=note;label.append(hint);}
      list.append(label);
    };
    option(null,first[0],first[1]);
    for(const x of models)option(x.slug,x.displayName,'');
  };
  if(!models.length)return;
  const review=listed(m.selected);
  render($('model-list'),'model','review',review,['Automatic','Uses '+models[0].displayName]);
  const reviewName=models.find(x=>x.slug===review)?.displayName||models[0].displayName;
  render($('translation-model-list'),'translation-model','translate',listed(m.translationSelected),['Same as review','Uses '+reviewName]);
  render($('fact-model-list'),'fact-model','factcheck',listed(m.factCheckSelected),['Same as review','Uses '+reviewName]);
}
showTaskView();update();post('ready');

// Settings > Shortcuts. The window records a combination; C# validates, registers and saves it, then sends 'shortcuts'.
// While recording, C# suspends the global shortcuts so the pressed keys reach this window.
const shortcutTasks=[['open','Open'],['review','Review'],['translate','Translate'],['factcheck','Fact check'],['summary','Summary'],['explain','Explain'],['rewrite','Rewrite']];
function shortcutKeys(e){
  // Period, comma and minus follow the key's label (C# registers them by label), the keypad minus is not one of them.
  const sign=!e.code.startsWith('Numpad')&&['.',',','-'].includes(e.key)?e.key:{Period:'.',Comma:','}[e.code];
  const key=/^Key([A-Z])$/.exec(e.code)?.[1]??/^Digit(\d)$/.exec(e.code)?.[1]??/^(F\d{1,2})$/.exec(e.code)?.[1]??sign??e.code;
  return [e.ctrlKey&&'Ctrl',e.altKey&&'Alt',e.shiftKey&&'Shift',key].filter(Boolean).join('+');
}
function stopRecording(){if(!recordingTask)return;recordingTask=null;post('shortcutRecording',{active:false});renderShortcuts();}
function recordShortcut(task){
  if(recordingTask===task){stopRecording();return;}
  if(recordingTask)post('shortcutRecording',{active:false});
  recordingTask=task;shortcutError=null;post('shortcutRecording',{active:true});renderShortcuts();
  document.querySelector?.(`.shortcut-keys[data-task="${task}"]`)?.focus();
}
function shortcutKeyDown(task,e){
  if(recordingTask!==task||e.key==='Tab')return;
  e.preventDefault();e.stopPropagation();
  if(['Control','Alt','Shift','Meta','AltGraph','OS'].includes(e.key))return;
  if(e.key==='Escape'&&!e.ctrlKey&&!e.altKey&&!e.shiftKey){stopRecording();return;}
  if(e.metaKey){shortcutError={task,message:'Use Ctrl or Alt with the key. The Windows key is reserved for Windows.'};stopRecording();return;}
  const keys=shortcutKeys(e);
  if(keys===shortcutLabels[task]){stopRecording();return;}
  recordingTask=null;post('shortcut',{task,keys});renderShortcuts();
}
function renderShortcuts(){
  for(const el of document.querySelectorAll?.('[data-shortcut]')||[]){const task=el.dataset.shortcut;el.textContent=shortcutLabels[task]||`${shortcutLabels.open}, then ${shortcutTasks.find(t=>t[0]===task)?.[1]||task}`;}
  renderingShortcuts=true;
  const list=$('shortcut-list');list.replaceChildren();
  for(const [task,name] of shortcutTasks){
    const row=document.createElement('div');row.className='shortcut-row';
    const label=document.createElement('span');label.className='shortcut-task';label.textContent=name;
    const button=document.createElement('button');button.type='button';button.className='shortcut-keys';button.dataset.task=task;
    const recording=recordingTask===task;
    button.classList.toggle('recording',recording);
    if(recording){const prompt=document.createElement('span');prompt.className='shortcut-prompt';prompt.textContent='Press the new keys';button.append(prompt);}
    else if(!shortcutLabels[task]){const none=document.createElement('span');none.className='shortcut-none';none.textContent='Not set';button.append(none);}
    else for(const part of shortcutLabels[task].split('+')){const key=document.createElement('kbd');key.textContent=part;button.append(key);}
    button.title=recording?'Press Esc to keep the current shortcut.':(shortcutLabels[task]?'Change the shortcut for ':'Set a shortcut for ')+name;
    button.onclick=()=>recordShortcut(task);
    button.onkeydown=e=>shortcutKeyDown(task,e);
    button.onblur=()=>{if(!renderingShortcuts&&recordingTask===task)stopRecording();};
    // Reset returns to the default; Remove clears an optional shortcut (Open always keeps one).
    const reset=document.createElement('button');reset.type='button';reset.className='link shortcut-reset';
    const toDefault=shortcutDefaults[task]&&shortcutLabels[task]!==shortcutDefaults[task];
    reset.textContent=toDefault?'Reset to '+shortcutDefaults[task]:'Remove';
    reset.hidden=recording||(toDefault?false:task==='open'||!shortcutLabels[task]);
    reset.onclick=()=>{shortcutError=null;post('shortcut',{task,keys:toDefault?shortcutDefaults[task]:''});};
    row.append(label,button,reset);
    const message=document.createElement('p');message.className='shortcut-message';
    if(recording)message.textContent='Esc keeps the current shortcut.';
    else if(shortcutError?.task===task){message.textContent=shortcutError.message;message.className='shortcut-message error';}
    else if(shortcutRegistered[task]===false){message.textContent='Another app uses this shortcut, so it does not work. Choose another one.';message.className='shortcut-message error';}
    message.hidden=!message.textContent;
    row.append(message);
    list.append(row);
  }
  renderingShortcuts=false;
}

// Rewrite: presets or a free-text request; each request changes the latest rewrite. Replace pastes plain text.
const rewritePresets=[['Improve','Improve: clearer and smoother wording; keep the meaning, tone, form of address and length'],['More formal','More formal and professional; keep du or Sie as written'],['Friendlier','Friendlier and warmer, still polite'],['Shorter','Shorter: remove repetition and filler, keep all facts'],['Simpler','Simpler: plain language and short sentences'],['Clearer structure','Clearer structure: logical order, paragraphs or bullet points where they help']];
function requestRewrite(request){
  if(busy||!request||!sourceText.trim())return;
  if(!connected){notice('Connect ChatGPT in Settings first.');return;}
  notice('Rewriting your text...');
  post('write',{kind:'rewrite',text:rewriteSteps.at(-1)?.text??sourceText,original:sourceText,request,captureId});
}
// Window width that keeps the review tabs on one line at a text size, in CSS px: the sidebar, the frame around the
// tab bar and the widest tab bar (German or English names, two-digit counts or running marks). Measured with hidden copies of the real
// elements, so it follows style.css. 0 when the page cannot be measured.
const tabLineNames=[['Rechtschreibung','Grammatik','Satzzeichen','Formulierung','Formatierung'],['Spelling','Grammar','Punctuation','Wording','Formatting']];
function tabLineWidth(px,base){
  const main=$('main'),scroll=$('main-scroll');
  if(!main?.getBoundingClientRect||!scroll||typeof getComputedStyle!=='function')return 0;
  const probe=document.createElement('div');probe.style.cssText=`position:absolute;left:0;top:0;visibility:hidden;width:max-content;--text-scale:${px/base}`;
  const sidebar=document.createElement('div');sidebar.style.cssText='width:calc(var(--sidebar-base) * var(--text-scale));height:0';
  const gutter=document.createElement('div');gutter.className='main-scroll';gutter.style.cssText='width:100px;height:10px;overflow-y:scroll';
  probe.append(sidebar,gutter);
  let bar=0;
  // A running tab shows its mark instead of the count and is wider than one with a two-digit count; both are measured.
  for(const names of tabLineNames)for(const running of [false,true]){
    const frame=document.createElement('div');frame.className='result-layout';frame.style.cssText='display:block;width:max-content';
    let parent=frame;
    for(const [tag,cls] of [['div','result-main'],['section',''],['div','context-box'],['div','review-tab-row'],['div','review-tabs']]){const e=document.createElement(tag);if(cls)e.className=cls;parent.append(e);parent=e;}
    const tabs=parent;tabs.style.flexWrap='nowrap';
    names.forEach((name,i)=>{
      const b=document.createElement('button');b.className='review-tab'+(i?'':' selected')+(running?' state-running':'');
      const n=document.createElement('span');n.textContent=name;const c=document.createElement('span');
      if(running)c.className='tab-state';else{c.className='number';c.textContent='99';}
      b.append(n,c);tabs.append(b);
    });
    probe.append(frame);
  }
  main.append(probe);
  for(const frame of [...probe.children].slice(2))bar=Math.max(bar,frame.getBoundingClientRect().width);
  const css=getComputedStyle(main),width=sidebar.getBoundingClientRect().width+(gutter.offsetWidth-gutter.clientWidth)+parseFloat(css.paddingLeft)+parseFloat(css.paddingRight)+bar;
  probe.remove();
  // 2 px for rounding to device pixels at scaled displays.
  return Math.ceil(width)+2;
}
// Text size for the whole window, kept in this WebView profile. Sizes are the body text in px; every font scales with it.
// The largest size is the one whose one-line tab bar still fits the screen; the window's minimum width keeps the
// tab bar on one line at the chosen size. The saved size stays, so a larger size returns on a larger screen.
const textSize=(()=>{
  const DEFAULT=15,MIN=12,MAX=18,BASE=14;
  let preferred=DEFAULT,size=DEFAULT,open=false,screenKey='';
  try{const saved=Number(localStorage.getItem('textSize'));if(saved)preferred=saved;}catch{}
  const clamp=v=>Math.min(MAX,Math.max(MIN,Math.round(v)));
  preferred=clamp(preferred);
  // Largest size that fits the screen; MIN when even that does not fit (the tab bar then wraps).
  const largest=()=>{
    const avail=globalThis.screen?.availWidth;if(!avail)return MAX;
    for(let px=MAX;px>MIN;px--){const need=tabLineWidth(px,BASE);if(!need||need<=avail)return px;}
    return MIN;
  };
  let fit=MAX;
  const limit=()=>{
    fit=largest();size=Math.min(preferred,fit);
    const need=tabLineWidth(size,BASE);if(need)post('minWidth',{width:need});
  };
  const apply=()=>document.documentElement?.style?.setProperty('--text-scale',String(size/BASE));
  const render=()=>{
    $('open-text-size').title=`Text size ${size} px (click to change, or turn the wheel)`;
    $('open-text-size').toggleAttribute?.('data-open',open);
    $('text-size').hidden=!open;
    $('text-size-value').textContent=`${size} px`;
    $('text-smaller').disabled=size<=MIN;$('text-larger').disabled=size>=Math.min(MAX,fit);$('text-reset').disabled=size===Math.min(DEFAULT,fit);
    $('text-reset').title=`Back to ${DEFAULT} px`;
  };
  const place=()=>{
    const popup=$('text-size'),box=$('open-text-size').getBoundingClientRect?.();if(!box||!popup.style)return;
    popup.style.setProperty('left',`${Math.max(8,Math.min(box.right-popup.offsetWidth,innerWidth-popup.offsetWidth-8))}px`);
    popup.style.setProperty('top',`${box.bottom+6}px`);
  };
  const set=v=>{preferred=Math.min(clamp(v),fit);try{localStorage.setItem('textSize',String(preferred));}catch{}limit();apply();render();if(open)place();sizeQuestion();};
  // A move to another screen or another scaling can change the largest size and the minimum width.
  const rescreen=()=>{const key=`${globalThis.screen?.availWidth}|${globalThis.devicePixelRatio}`;if(key===screenKey)return;screenKey=key;limit();apply();render();if(open)place();sizeQuestion();};
  const step=by=>set(size+by);
  const toggle=()=>{open=!open;render();if(open)place();};
  const close=()=>{if(!open)return;open=false;render();};
  const wheel=e=>{e.preventDefault();step(e.deltaY<0?1:-1);};
  $('open-text-size').onclick=toggle;
  $('open-text-size').addEventListener('wheel',wheel,{passive:false});
  $('text-size').addEventListener('wheel',wheel,{passive:false});
  $('text-smaller').onclick=()=>step(-1);$('text-larger').onclick=()=>step(1);$('text-reset').onclick=()=>set(DEFAULT);
  document.addEventListener?.('pointerdown',e=>{if(open&&!e.target.closest?.('#text-size,#open-text-size'))close();});
  document.addEventListener?.('keydown',e=>{if(open&&e.key==='Escape')close();});
  window.addEventListener?.('resize',()=>{rescreen();if(open)place();});
  screenKey=`${globalThis.screen?.availWidth}|${globalThis.devicePixelRatio}`;limit();apply();render();
  return {get size(){return size;},step,set,close,isOpen:()=>open,rescreen};
})();
// ---- Source format of the captured text and the export buttons (docs/text-formats.md).
const siteProfiles={'maniac-forum.de':['mforum','M! Forum'],'youtube.com':['youtube','YouTube'],'reddit.com':['reddit','Reddit']};
let formatInfo={matches:new Set(),source:'',evidence:'',unknown:'',received:''};
function siteProfile(host){
  const h=(host||'').toLowerCase();
  for(const [site,profile] of Object.entries(siteProfiles))if(h===site||h.endsWith('.'+site))return profile;
  return null;
}
// Measured facts about the text itself: line ends and characters that are easy to miss.
function textFacts(text){
  const count=re=>(text.match(re)||[]).length,facts=[],add=(n,one,many)=>{if(n)facts.push(`${n} ${n===1?one:many}`);};
  add(count(/\r\n/g),'CRLF line end','CRLF line ends');add(count(/(?<!\r)\n/g),'LF line end','LF line ends');add(count(/\r(?!\n)/g),'CR line end','CR line ends');
  add(count(/\u00a0/g),'non-breaking space','non-breaking spaces');add(count(/\t/g),'tab','tabs');
  add(count(/[\u200b-\u200d\u2060\ufeff]/g),'zero-width character','zero-width characters');add(count(/\p{Extended_Pictographic}/gu),'emoji','emoji');
  return facts;
}
// Candidates with evidence, never a guess: plain prose shows "No distinguishing markup", and markers that
// mean different things in different formats are reported as ambiguous.
function detectFormat(text,m){
  const clip=m.clip||null;
  const received=m.mode==='chrome'?'Chrome page, edited directly':m.reused?'Result of a TextFixer task':clip?.formats?.length?clip.formats.join(' + '):'Plain text';
  const info={received:[received,...textFacts(text)].join('; '),matches:new Set(),source:'',evidence:'',unknown:''};
  if(clip?.formats?.some(f=>f==='HTML'||f==='RTF')){
    const site=siteProfile(clip.host),editor=clip.found?.includes('rich editor');
    info.matches.add('formatted');if(site)info.matches.add(site[0]);
    info.source=site?`${site[1]}, strong evidence: copied from ${clip.host}`:`Rich text${editor?' from a rich text editor':''}${clip.host?`, copied from ${clip.host}`:''}`;
    const elements=(clip.found||[]).filter(f=>f!=='rich editor');
    if(elements.length)info.evidence='Found: '+elements.join(', ')+'.';
    if(clip.unknown?.length)info.unknown='Site-specific elements: '+clip.unknown.join(', ')+'.';
    return info;
  }
  // Code is left out, so examples inside code do not count.
  const s=text.replace(/```[\s\S]*?```/g,' ').replace(/`[^`\n]+`/g,' ');
  const strong=[],shared=[],ambiguous=[];
  const markdown=()=>info.matches.add('markdown');
  if(/\[(b|i|u|s|quote|url|list|code|img|size|color|spoiler)(=[^\]\n]*)?\][\s\S]*?\[\/\1\]/i.test(s)){strong.push('BBCode tags');info.matches.add('bbcode');}
  if(/>![^\n]+?!</.test(s)){strong.push('Reddit spoiler >!x!<');markdown();}
  if(/<@!?\d+>/.test(s)){strong.push('Discord mention <@id>');markdown();}
  if(/\|\|[^|\n]+\|\|/.test(s)){shared.push('spoiler ||x|| (Discord, Telegram)');markdown();}
  if(/```/.test(text)){shared.push('code fence');markdown();}
  if(/~~[^~\n]+~~/.test(s)){shared.push('~~strikethrough~~');markdown();}
  if(/\*\*[^*\n]+\*\*/.test(s)){shared.push('**bold**');markdown();}
  if(/\[[^\]\n]+\]\(https?:\/\/[^)\s]+\)/.test(s)){shared.push('[label](link)');markdown();}
  if(/^#{1,6} \S/m.test(s)){shared.push('# heading');markdown();}
  if(/(^|[^*\w])\*[^*\s][^*\n]*?\*(?![*\w])/m.test(s.replace(/\*\*[^*\n]+\*\*/g,' '))){ambiguous.push('*x* (italic in Markdown, bold in WhatsApp, Slack and YouTube)');markdown();}
  if(/(^|\W)_[^_\s][^_\n]*?_(?!\w)/m.test(s)){ambiguous.push('_x_ (italic in most formats)');markdown();}
  const all=[...strong,...shared,...ambiguous];
  if(!all.length){
    info.matches.add('plain');
    info.source=/^[ \t]*([-*\u2022]|\d+[.)]) \S/m.test(s)?'No distinguishing markup, plain lists':'No distinguishing markup';
    return info;
  }
  const names=[info.matches.has('markdown')&&'Markdown family',info.matches.has('bbcode')&&'BBCode'].filter(Boolean).join(' or ');
  info.source=`${names}, ${strong.length?'strong evidence':shared.length?'shared markers only':'ambiguous'}`;
  info.evidence='Found: '+all.join(', ')+'.';
  return info;
}
function renderFormatInfo(){
  $('format-info').hidden=!sourceText.trim();
  $('format-source').textContent=formatInfo.source;
  $('format-evidence').textContent=[formatInfo.evidence,formatInfo.unknown].filter(Boolean).join(' ');
  $('format-evidence').hidden=!formatInfo.evidence&&!formatInfo.unknown;
  $('format-received').textContent='Received: '+formatInfo.received+'.';
}
// Title, author and date of a copied forum post: page details shown above the captured text, not part of it.
function renderPostInfo(doc){
  const box=$('post-info');box.replaceChildren();
  if(doc?.title){const t=document.createElement('strong');t.className='post-title';t.textContent=doc.title;box.append(t);}
  if(doc?.post){const b=document.createElement('span');b.className='post-byline';b.textContent=doc.post;box.append(b);}
  box.hidden=!doc?.title&&!doc?.post;
}
// Results of a rich capture keep its markup and placeholders: shown formatted, copied and pasted in the capture's
// format with every link, mention and code restored from the capture.
const resultFormat=()=>formatInfo.matches.has('mforum')?'mforum':'formatted';
function resultPlain(text,structured){return structured?markupExport(text,'plain').text:text;}
// Corrected text in every export format. The corrections of a rich capture go through the window, which edits the
// copied HTML and reads it again, so links, quotes and mentions stay; Original formatting pastes that HTML as it is.
function revisedRequest(kind){return kind==='fact'?{text:factRevised(),original:factOriginal,edits:factEdits(),formattingIds:[]}:{text:revised(),original:reviewOriginal,edits:acceptedEdits(),formattingIds:selectedFormatting()};}
function exportRevised(kind,format){
  const r=revisedRequest(kind);if(!r.text)return;
  if(format==='plain'){post('copyAs',{format,text:r.text});return;}
  if(captureMode!=='chrome'&&richClipboard){post('exportRevised',{...r,format});return;}
  const out=markupExport(r.text,format);post('copyAs',{format,text:out.text,...(out.html?{html:out.html}:{})});
}
function revisedRows(){
  for(const [id,kind,original] of [['review-export','review','copy'],['fact-revised-export','fact','fact-copy']]){
    const row=exportRow(null,{onExport:format=>exportRevised(kind,format)});
    row.children[0].after?.($(original));
    $(id).replaceChildren();$(id).append(row);
  }
}
function applyStructured(action,text,original){const out=markupExport(text,resultFormat());post(action,{text:out.text,html:out.html,model:text,original});}
function showResultText(id,text,structured){const el=$(id);if(structured){renderMarkup(el,text);return;}el.classList?.remove?.('md');el.textContent=text;}
const missingNotice=what=>`Some links or other objects are missing from the ${what}. Try again before replacing your selection, or copy the ${what}.`;
// Quotes, link texts, mentions and code of a rich capture stay as they are: Review drops suggestions inside them.
function insideProtected(p,original){
  if(!captureDoc||original!==captureDoc.text)return false;
  return captureDoc.ranges.some(([s,e])=>p.start===p.end?s<p.start&&p.start<e:p.start<e&&s<p.end);
}
function protectParts(list,original){return list.map(p=>p.changed&&insideProtected(p,original)?{...p,after:p.before,changed:false}:p);}
// One copy button per export format; a dot marks the formats that match the captured text.
function exportRow(source,{small=false,label='Copy as',onExport=null}={}){
  const row=document.createElement('div');row.className='export-row'+(small?' small':'');
  const title=document.createElement('span');title.className='export-label';title.textContent=label;row.append(title);
  for(const [format,name,tip] of exportFormats){
    const match=formatInfo.matches.has(format);
    const button=document.createElement('button');button.type='button';button.className='export-button'+(match?' matches':'');button.dataset.format=format;button.dataset.sub=exportSubs[format]||'';
    button.title=tip+(match?' Matches the captured text.':'');button.disabled=busy;
    if(match){const dot=document.createElement('span');dot.className='match-dot';button.append(dot);}
    const text=document.createElement('span');text.textContent=name;button.append(text);
    button.onclick=()=>{
      if(onExport){if(!busy)onExport(format);return;}
      const content=typeof source==='function'?source():source;
      if(busy||!content||!content.length)return;
      const out=markupExport(content,format);
      post('copyAs',{format,text:out.text,...(out.html?{html:out.html}:{})});
    };
    row.append(button);
  }
  return row;
}
// Fact check results for copying, laid out like the cards: a rule between claims, the verdict, the claim as a quote,
// the explanation, the sources as links, then a correction or question.
// Text symbols mark the verdicts in copied results; they take the text color, so they work where color does not.
const verdictSymbol={supported:'✓',wrong:'✗',disputed:'◐',needs_context:'?',unverifiable:'○'};
let factKinds=[];
function factResultsBlocks(){
  const line=runs=>({type:'p',lines:[{runs}]});
  const link=x=>({text:'',token:{kind:'link',label:x.title||x.url,url:x.url}});
  // Added words follow the result language (Settings); "Same as the text" reads the checked text.
  const w=s=>resultWord(s,factOriginal),n=factClaims.length;
  const summary=n?`${n} ${w(n===1?'claim':'claims')}: ${factKinds.map(([v,t,k])=>`${verdictSymbol[v]} ${k} ${w(t)}`).join(', ')}.`:w('No checkable factual claims found in this text.');
  const blocks=[line([{text:summary,bold:true}])];
  for(const c of factClaims){
    blocks.push({type:'hr'},line([{text:`${verdictSymbol[c.verdict]} ${w(verdictLabel[c.verdict])}`,bold:true}]),{type:'quote',blocks:[line([{text:c.quote}])]});
    if(c.explanation)blocks.push(...markupBlocks(c.explanation));
    if(c.sources?.length===1)blocks.push(line([{text:`↗ ${w('Source:')} `},link(c.sources[0])]));
    else if(c.sources?.length)blocks.push(line([{text:`↗ ${w('Sources:')}`}]),{type:'ul',items:c.sources.map(x=>({text:{runs:[link(x)]}}))});
    if(c.correction)blocks.push(line([{text:`→ ${w('Correction:')}`,bold:true},{text:' '+c.correction}]));
    if(c.verdict==='needs_context'&&c.question)blocks.push(line([{text:`? ${w('Question:')}`,bold:true},{text:' '+c.question}]));
  }
  return blocks;
}
const maxDialogQuestions=20;
// One message of the Summary/Explain dialog; answers get their sources, a note and a Copy button.
function dialogMessage(role,text,extra={}){
  const box=document.createElement('div');box.className='msg '+(role==='user'?'msg-question':'msg-answer')+(extra.pending?' msg-pending':'');
  const body=document.createElement('div');body.className='msg-text';box.append(body);
  // Answers use the Markdown subset of the prompt; questions and the waiting line stay as typed.
  if(role==='user'||extra.pending)body.textContent=text;else renderMarkup(body,text);
  if(role==='user'||extra.pending)return box;
  const main=extra.main;
  if(extra.sources?.length){
    const sources=document.createElement('ul');sources.className='fact-sources';
    for(const s of extra.sources){
      const item=document.createElement('li');const link=document.createElement('button');link.type='button';link.className='link source-link';
      link.textContent=s.title;link.title=s.url;link.onclick=()=>post('openSource',{url:s.url});
      const domain=document.createElement('span');domain.className='hint';domain.textContent=s.domain;
      item.append(link,domain);sources.append(item);
    }
    box.append(sources);
  }
  const foot=document.createElement('div');foot.className='msg-foot';
  const note=document.createElement('span');note.className='hint';note.textContent=extra.note||'';
  const use=document.createElement('button');use.type='button';use.className='link use-capture';use.textContent='Use as captured text';use.disabled=busy;use.onclick=()=>useAsCapture(markupPlain(text));
  // The first answer copies from the column next to the dialog; follow-up answers keep their own row.
  if(main){$('answer-export').replaceChildren();$('answer-export').append(exportRow(text));$('use-answer').disabled=busy;$('use-answer').onclick=()=>useAsCapture(markupPlain(text));foot.append(note);}
  else foot.append(note,exportRow(text,{small:true}),use);
  box.append(foot);
  return box;
}
function renderDialog(){
  const a=answers[answerKey(activeTask)],list=$('dialog');list.replaceChildren();
  if(!a)return;
  list.append(dialogMessage('assistant',a.text,{main:true}));
  for(const t of a.turns)list.append(t.role==='user'?dialogMessage('user',t.text):dialogMessage('assistant',t.text,{sources:t.sources,note:!t.searches?'Answered without web search':t.sources?.length?'':'Searched the web, no source cited'}));
  if(a.pending)list.append(dialogMessage('user',a.pending),dialogMessage('assistant','Searching the web and writing an answer...',{pending:true}));
  const full=a.turns.length/2+(a.pending?1:0)>=maxDialogQuestions;
  $('dialog-form').hidden=full;$('dialog-keys').hidden=full;$('dialog-full').hidden=!full;
}
function askDialog(){
  const a=answers[answerKey(activeTask)],question=$('dialog-question').value.trim();
  if(busy||!a||!question||a.pending||a.turns.length/2>=maxDialogQuestions)return;
  if(!connected){notice('Connect ChatGPT in Settings first.');return;}
  a.pending=question;dialogKey=answerKey(activeTask);$('dialog-question').value='';sizeQuestion();renderDialog();notice('Searching the web for an answer...');
  post('discuss',{kind:activeTask,text:sourceText,turns:[{role:'assistant',text:a.text},...a.turns.map(t=>({role:t.role,text:t.text})),{role:'user',text:question}],captureId});
  $('dialog').lastElementChild?.scrollIntoView?.({block:'nearest',behavior:'smooth'});
}
function sizeQuestion(){const q=$('dialog-question');if(!q.style)return;q.style.height='auto';q.style.height=Math.min(q.scrollHeight+2,160)+'px';}
function conversationText(kind,a){
  const lines=[(kind==='summary'?'Summary':'Explanation')+':',markupPlain(a.text)];
  for(const t of a.turns){lines.push('',t.role==='user'?'You: '+t.text:'TextFixer:\n'+markupPlain(t.text));if(t.sources?.length)lines.push('Sources:',...t.sources.map(s=>`- ${s.title}: ${s.url}`));}
  return lines.join('\n');
}
function renderRewrite(){
  const presets=$('rewrite-presets');presets.replaceChildren();
  for(const [label,request] of rewritePresets){const b=document.createElement('button');b.type='button';b.className='preset';b.textContent=label;b.title=request;b.disabled=busy;b.onclick=()=>requestRewrite(request);presets.append(b);}
  const last=rewriteSteps.at(-1);
  // Without a result the request field shows only for Own request; a preset that is running shows nothing yet.
  // Own request without a result shows the original text in the result card, with the request field below it.
  const own=!last&&taskOption.rewrite==='more';
  $('rewrite-empty').hidden=true;$('rewrite-result').hidden=!last&&!own;$('rewrite-form').hidden=!last&&!own;
  $('rewrite-result').classList?.toggle?.('original-only',own);
  if(own)showResultText('rewrite-text',sourceText,false);
  if(!last)return;
  showResultText('rewrite-text',last.text,rewriteStructured);$('rewrite-export').replaceChildren();$('rewrite-export').append(exportRow(()=>rewriteSteps.at(-1)?.text||''));$('rewrite-model').textContent=rewriteModel;
  $('rewrite-steps').textContent='Changes requested: '+rewriteSteps.map(s=>rewritePresets.find(p=>p[1]===s.request)?.[0]||s.request).join(', then ');
  const changes=$('rewrite-changes');changes.replaceChildren();
  // A rich capture compares the plain forms of its marked-up text and the rewrite, so quotes and links line up.
  const diffFrom=rewriteStructured&&captureDoc?.model?resultPlain(captureDoc.model,true):sourceText;
  for(const part of textFixerDiff(diffFrom,resultPlain(last.text,rewriteStructured))){
    if(!part.changed){const span=document.createElement('span');span.textContent=part.after;changes.append(span);continue;}
    if(part.before){const del=document.createElement('del');del.textContent=part.before;changes.append(del);}
    if(part.after){const ins=document.createElement('ins');ins.textContent=part.after;changes.append(ins);}
  }
}

// ---- Main area: a band in the task's color on top, the result on the left, and a column on the right with
// everything that passes the result on (copy formats, paste, keep working). The captured text sits behind the
// band's source button, in a dialog with its origin, format and size.
function captureWords(){return (sourceText.match(/\S+/g)||[]).length;}
function renderBand(){
  if($('open-source')){$('open-source').disabled=!sourceText;$('open-source').classList?.toggle?.('selected',!activeTask&&!!sourceText);}
  // Before a task is chosen, the captured text fills the card under the band.
  $('choose-preview').hidden=!!activeTask||!sourceText||$('choose-view').hidden;$('choose-preview-text').replaceChildren();appendGapped($('choose-preview-text'),sourceText);if(!$('choose-preview').hidden)renderPreviewFacts();
  const card=taskCards.find(c=>c[0]===activeTask);
  document.documentElement?.style?.setProperty?.('--band',card?.[4]||'#264859');
  $('band-task').textContent=card?.[1]||'Choose a task';
  // Rewrite lists the requested changes in order; the other tasks show their chosen option.
  const steps=activeTask==='rewrite'&&!$('rewrite-result').hidden?rewriteSteps.map(s=>{const p=rewritePresets.find(x=>x[1]===s.request)?.[0];return p?tr(p):s.request.length>40?s.request.slice(0,39)+'…':s.request;}).join(' → '):'';
  const option=steps||(card?tr(optionLabel(activeTask,taskOption[activeTask]||'')):'');
  $('band-option').textContent=option;$('band-option').hidden=!option;
  // App and site names are data, not UI text; the rest is translated here.
  const from=captureMode==='chrome'?'Chrome':captureClip?.app||tr('Clipboard');
  const n=captureWords();
  $('source-summary').textContent=[from,captureClip?.host,tr(`${n.toLocaleString()} ${n===1?'word':'words'}`)].filter(Boolean).join(' \u00b7 ');
}
// Facts under the captured text before a task is chosen: size, whether TextFixer can replace it, origin and format.
function renderPreviewFacts(){
  const box=$('preview-facts');box.replaceChildren();
  const head=document.createElement('h3');head.className='aside-head';head.textContent='About this text';box.append(head);
  const fact=(label,value,{wide=false,data=false,tone='',tip=''}={})=>{
    if(!value)return;
    const tile=document.createElement('div');tile.className='fact-tile'+(wide?' wide':'')+(tone?' '+tone:'');if(tip)tile.title=tip;
    const l=document.createElement('span');l.className='fact-label';l.textContent=label;
    const v=document.createElement('span');v.className='fact-value';v.textContent=value;if(data)v.dataset.noTr='';
    tile.append(l,v);box.append(tile);
  };
  fact('Words',captureWords().toLocaleString(),{data:true});
  fact('Characters',sourceText.length.toLocaleString(),{data:true});
  fact('Paragraphs',String(sourceText.split(/\r?\n[ \t]*\r?\n/).filter(p=>p.trim()).length),{data:true});
  fact('Editing',readOnlyCapture?'Read-only':canReplace?'Replaceable':'Copy only',{tone:readOnlyCapture||!canReplace?'muted-tone':'ok-tone',tip:readOnlyCapture?'Copy the result and paste it yourself.':canReplace?'TextFixer can replace the selection.':'Replace is not available for this capture.'});
  const origin=[captureMode==='chrome'?'Chrome':captureClip?.app,windowTitle(),captureClip?.host].filter(Boolean).join(' \u00b7 ');
  fact('Origin',origin,{wide:true,data:true});
  fact('Source format',formatInfo?.source,{wide:true});
  fact('Captured with',captureHow(),{wide:true});
  // "Received: ..." is translated as one sentence; the tile shows the part after the colon.
  if(formatInfo?.received)fact('Received',tr('Received: '+formatInfo.received+'.').replace(/^[^:]+:\s*/,'').replace(/\.$/,''),{wide:true});
  const evidence=[formatInfo?.evidence,formatInfo?.unknown].filter(Boolean).map(x=>tr(x)).join(' ');
  fact('Evidence',evidence,{wide:true});
  const post=$('post-info');
  if(post&&!post.hidden)fact('Post',[...post.children].map(c=>c.textContent).filter(Boolean).join(' \u00b7 '),{wide:true,data:true});
}
// The window title without a trailing " - App" that repeats the app name.
function windowTitle(){
  const w=captureClip?.window||'',app=captureClip?.app||'';
  return app&&w.toLowerCase().endsWith(' - '+app.toLowerCase())?w.slice(0,-(app.length+3)):w;
}
const captureHow=()=>captureMode==='chrome'?tr('Chrome extension'):tr(`${shortcutLabels[captureTask]||shortcutLabels.open}, clipboard`);
// Moves a view's export row and action buttons into a column next to its result. Groups: [label, element ids,
// optional id of an element whose visibility the group follows].
const asideSubs={apply:'In the source app','apply-translation':'In the source app','apply-rewrite':'In the source app','fact-apply':'In the source app','use-review':'Run another task','use-translation':'Run another task','use-rewrite':'Run another task','use-fact':'Run another task','use-answer':'Run another task','copy-conversation':'Questions and answers','rewrite-reset':'Back to the original'};
function asideLayout(id,groups){
  const head=document.createElement('h3');head.className='aside-head';head.textContent='Pass on';
  const box=$(id);if(!box)return;
  const main=document.createElement('div');main.className='result-main';
  const aside=document.createElement('aside');aside.className='result-aside';
  main.append(...[...(box.childNodes||[])]);
  box.append(main,aside);aside.append(head);box.classList?.add?.('result-layout');
  for(const [label,ids,follow] of groups){
    const group=document.createElement('div');group.className='aside-group';
    const title=document.createElement('h3');title.className='aside-label';title.textContent=label;group.append(title);
    for(const part of ids){const el=$(part);if(!el)continue;if(asideSubs[part]&&el.dataset)el.dataset.sub=asideSubs[part];group.append(el);}
    if(follow)group.dataset.follow=follow;
    aside.append(group);
  }
  // Toolbars whose buttons all moved are left empty.
  for(const bar of main.querySelectorAll?.('.toolbar')||[])if(!bar.querySelector?.('button, input, .hint'))bar.remove?.();
}
function syncAsides(){
  renderBand();
  // Rewrite: once there is a result, the presets and the request field sit below it.
  const rw=$('rewrite-result'),rwMain=rw?.querySelector?.('.result-main');
  if(rwMain){const target=rw.hidden?$('rewrite-view'):rwMain,before=rw.hidden?$('rewrite-empty'):null;for(const id of ['rewrite-presets','rewrite-form']){const el=$(id);if(el&&el.parentElement!==target)target.insertBefore(el,before);}}
  // The band joins the result card below it; without a visible result it stands alone.
  const joined=[...(document.querySelectorAll?.('.result-layout')||[])].some(el=>!el.hidden&&el.offsetParent!==null);
  $('choose-view')?.classList?.toggle?.('joined',joined);
  // The model of the visible result shows in the band; notes about the result become the band title's tooltip.
  const view=[...(document.querySelectorAll?.('.result-layout')||[])].find(el=>!el.hidden&&el.offsetParent!==null);
  const model=!view?'':view.id==='factcheck-view'?factMeta?.model||'':view.id==='rewrite-result'?rewriteModel:view.querySelector?.('#model,#translation-model,#rewrite-model,#answer-model')?.textContent||'';
  const note=view?.querySelector?.('#answer-hint,#translation-hint,#fact-meta')?.textContent||'';
  if($('band-model')){$('band-model').textContent=model;$('band-model').hidden=!model;}
  if($('band-task')?.setAttribute){if(note)$('band-task').setAttribute('title',tr(note));else $('band-task').removeAttribute?.('title');}
  for(const group of document.querySelectorAll?.('.aside-group')||[]){
    const follow=group.dataset.follow&&$(group.dataset.follow);
    group.hidden=(follow&&follow.hidden)||![...group.children].slice(1).some(c=>!c.hidden);
  }
}
asideLayout('results',[['Copy as',['review-export']],['Paste',['apply','plain-text-option','readonly-hint']],['Keep working',['use-review']]]);
asideLayout('translation-view',[['Copy as',['translation-export']],['Paste',['apply-translation','translation-readonly']],['Keep working',['use-translation']]]);
asideLayout('rewrite-result',[['Copy as',['rewrite-export']],['Paste',['apply-rewrite','rewrite-readonly','rewrite-plain']],['Keep working',['use-rewrite','rewrite-reset']]]);
asideLayout('answer-view',[['Copy as',['answer-export']],['Keep working',['use-answer']],['Conversation',['copy-conversation']]]);
asideLayout('factcheck-view',[['Copy check as',['fact-export']],['Copy revised text as',['fact-revised-export'],'fact-revision'],['Paste',['fact-apply','fact-plain-option','fact-readonly'],'fact-revision'],['Keep working',['use-fact'],'fact-revision']]);
