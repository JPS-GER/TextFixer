const $=id=>document.getElementById(id);
const post=(action,extra={})=>window.chrome.webview.postMessage({action,...extra});
let connected=false,busy=false,canReplace=false,readOnlyCapture=false,sourceText='',reviewOriginal='',parts=[],captureMode='clipboard',richClipboard=false;
let pendingAutoCheck=false;
// "One request per tab" review: tabState holds ready | running | queued | failed | stopped per requested tab.
let reviewMode='single',partialReview=false,tabState={},tabResults={},tabErrors={},autoTab=true;
const focusOrder=['spelling','grammar','punctuation','wording','worth'];
function locked(){return busy&&!partialReview;}
let activeTab='spelling',reviewFindings=[],wordings=[];
const reviewTabs=[['spelling','Spelling'],['grammar','Grammar'],['punctuation','Punctuation'],['wording','Wording'],['worth','Worth checking'],['formatting','Formatting']];
function activeCorrection(p){return p.category===activeTab&&!p.layoutProtected&&(p.category==='worth'||!reviewSuperseded(p,wordings));}
function tabCount(key){
  if(key==='worth')return reviewFindings.filter(f=>!f.dismissed).length;
  if(key==='formatting')return captureMode==='chrome'?0:formattingSuggestions.length;
  return parts.filter(p=>p.changed&&p.category===key).length;
}
function renderTabs(){
  const container=$('review-tabs');container.replaceChildren();
  reviewTabs.forEach(([key,title])=>{
    const button=document.createElement('button');button.type='button';button.id='tab-'+key;button.className='review-tab'+(activeTab===key?' selected':'');
    button.dataset.category=key;button.setAttribute('role','tab');button.setAttribute('aria-selected',String(activeTab===key));
    const state=tabState[key];
    button.disabled=tabCount(key)===0||(state&&state!=='ready');if(state)button.className+=' state-'+state;button.tabIndex=activeTab===key||(!tabCount(activeTab)&&reviewTabs.find(([k])=>tabCount(k)>0)?.[0]===key)?0:-1;
    const name=document.createElement('span');name.textContent=title;
    const count=document.createElement('span');count.className='number';count.textContent=tabCount(key);
    if(state&&state!=='ready'){count.className='tab-state';count.textContent=state==='failed'?'!':state==='stopped'?'-':'';button.title=tabStateTitle[state];}
    button.append(name,count);
    button.onclick=()=>{if(button.disabled)return;autoTab=false;activeTab=key;showChanges();$('tab-'+key)?.focus();};
    button.onkeydown=e=>{
      if(!['ArrowRight','ArrowLeft','Home','End'].includes(e.key))return;e.preventDefault();
      const available=reviewTabs.filter(([k])=>tabCount(k)>0).map(([k])=>k),i=available.indexOf(key);
      const next=e.key==='Home'?available[0]:e.key==='End'?available.at(-1):available[(i+(e.key==='ArrowRight'?1:-1)+available.length)%available.length];
      if(next){activeTab=next;showChanges();$('tab-'+next)?.focus();}
    };
    container.append(button);
  });
  $('worth-results').hidden=activeTab!=='worth';
  $('formatting-results').hidden=activeTab!=='formatting'||captureMode==='chrome'||!formattingSuggestions.length;
}
const tabStateTitle={running:'Checking now',queued:'Queued',failed:'Could not be checked',stopped:'Stopped'};
function focusParts(original){
  // Each tab answer fixes only its own category. Earlier (left) tabs win where two answers touch the same text.
  const previous=new Map(parts.filter(p=>p.changed).map(p=>[`${p.start}|${p.end}|${p.after}`,p.accepted]));
  const kept=[];
  // Changes may touch at a boundary; two insertions at one position, or any shared character, conflict.
  const clash=(a,b)=>a.start===a.end&&b.start===b.end?a.start===b.start:a.start===a.end?b.start<a.start&&a.start<b.end:b.start===b.end?a.start<b.start&&b.start<a.end:a.start<b.end&&b.start<a.end;
  for(const focus of focusOrder){
    const r=tabResults[focus];if(!r||focus==='worth')continue;
    for(const p of reviewParts(original,r.correctedText,r.corrections)){
      if(!p.changed)continue;
      if(!kept.some(k=>clash(k,p))){kept.push({...p,category:focus});continue;}
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
  const running=focusOrder.find(k=>tabState[k]==='running');
  if(!running)return;
  notice(`Checking ${reviewTabs.find(([k])=>k===running)[1]} (${focusOrder.indexOf(running)+1} of ${focusOrder.length}). Finished tabs are ready to review.`);
}
function applyReviewMeta(m){captureMode=m.mode||'clipboard';richClipboard=!!m.canPreserveFormatting;canReplace=m.canReplace;readOnlyCapture=!!m.readOnly;showFormatting(m.formattingSuggestions||[]);$('model').textContent=m.model||'';}
function readyNotice(){
  const failed=focusOrder.filter(k=>tabState[k]==='failed');
  if(failed.length){notice('Review ready. Not checked: '+failed.map(k=>reviewTabs.find(([t])=>t===k)[1]+' ('+tabErrors[k]+')').join('; '));return;}
  notice(parts.some(p=>p.layoutProtected)?'Review ready. Suggestions that would change line breaks were excluded to preserve your paragraph layout.':(readOnlyCapture?'Review ready. Accept or reject corrections, then copy your text.':'Review ready. Accept or reject corrections, then copy or replace your text.'));
}
function startCapturedReview(){
  if(!pendingAutoCheck||!connected||busy||!sourceText.trim())return;
  pendingAutoCheck=false;
  notice('Checking your text...');post('check',{text:sourceText});
}
function notice(message){$('notice').textContent=message||'Ready';$('notice').hidden=false;}
function update(){
  $('count').textContent=`${$('source').value.length.toLocaleString()} / 30,000`;
  $('signin').disabled=busy;$('signout').disabled=busy;$('source').disabled=busy;
  $('cancel').hidden=!busy;
  document.querySelectorAll('#formatting-findings input, #formatting-findings button').forEach(x=>x.disabled=locked()||$('allow-plain-text').checked);
  const formattedPaste=captureMode!=='chrome'&&richClipboard&&!$('allow-plain-text').checked;
  $('copy').disabled=busy;$('apply').disabled=busy||!canReplace||(captureMode!=='chrome'&&!formattedPaste&&!$('allow-plain-text').checked);
  $('allow-plain-text').disabled=busy;$('plain-text-option').hidden=captureMode==='chrome'||!canReplace;
  $('apply').hidden=readOnlyCapture;$('copy').classList.toggle('primary',readOnlyCapture);$('readonly-hint').hidden=!readOnlyCapture;
  const bulkCount=activeTab==='worth'?wordings.filter(w=>w.created&&!w.layoutProtected).length:tabCount(activeTab);
  $('accept-all').disabled=locked()||!bulkCount||(activeTab==='formatting'&&$('allow-plain-text').checked);$('reject-all').disabled=locked()||!bulkCount||(activeTab==='formatting'&&$('allow-plain-text').checked);
  document.querySelectorAll('#inline-text button, #changes input').forEach(control=>control.disabled=locked()||control.dataset.layoutProtected==='true'||control.dataset.inactive==='true');
  document.querySelectorAll('#findings button').forEach(control=>control.disabled=locked()||control.dataset.layoutProtected==='true');
  $('apply').textContent=captureMode==='chrome'?'Apply in Chrome':formattedPaste?'Paste with formatting':'Paste plain text';
  $('copy').textContent=formattedPaste?'Copy with formatting':'Copy plain text';
  $('route').className='capture-icon';
  const captureLabel=captureMode==='chrome'?'Captured from Chrome':'Captured with Alt+Q';
  $('route').title=captureLabel;$('route').setAttribute('aria-label',captureLabel);
  $('clipboard-icon').hidden=captureMode==='chrome';$('chrome-icon').hidden=captureMode!=='chrome';
  $('route').hidden=!canReplace&&!readOnlyCapture;
  $('route-title').textContent=captureMode==='chrome'?'Captured from Chrome':'Captured with Alt+Q';
  $('formatting').hidden=richClipboard||captureMode==='chrome'||!canReplace;
  $('formatting').textContent=richClipboard?'':'This selection includes formatting. Plain-text replacement may remove formatting.';
}
let formattingSuggestions=[];
function selectedFormatting(){return formattingSuggestions.filter(f=>f.accepted).map(f=>f.id);}
function showFormatting(items){
  formattingSuggestions=items.map(f=>({...f,accepted:false}));
  $('formatting-results').hidden=activeTab!=='formatting'||!items.length||captureMode==='chrome';
  renderFormatting();
}
function renderFormatting(){
  const container=$('formatting-findings');container.replaceChildren();
  formattingSuggestions.forEach((f,index)=>{
    const card=document.createElement('article');card.className='formatting-suggestion';
    const label=document.createElement('label');label.className='formatting-label';
    const input=document.createElement('input');input.type='checkbox';input.checked=f.accepted;
    input.disabled=locked()||$('allow-plain-text').checked;
    input.setAttribute('aria-label',f.description);
    input.onchange=()=>{if(input.disabled)return;f.accepted=input.checked;renderFormatting();};
    const description=document.createElement('span');description.textContent=f.description;
    label.append(input,description);card.append(label);
    const context=document.createElement('div');context.className='inline-text formatting-context';
    const valid=['underline','strikethrough','link'].includes(f.property)&&Number.isInteger(f.runStart)&&Number.isInteger(f.runEnd)&&f.runStart>=0&&f.runEnd<=reviewOriginal.length&&f.runStart<=f.start&&f.end<=f.runEnd;
    if(valid){
      // Render only plain text with known CSS classes, never captured HTML or links.
      let left=Math.max(0,f.runStart-35),right=Math.min(reviewOriginal.length,f.runEnd+35);
      if(left>0&&/[\uDC00-\uDFFF]/.test(reviewOriginal[left]))left--;
      if(right<reviewOriginal.length&&/[\uDC00-\uDFFF]/.test(reviewOriginal[right]))right++;
      const beforeContext=document.createElement('span');beforeContext.textContent=(left?'...':'')+reviewOriginal.slice(left,f.runStart);
      const button=document.createElement('button');button.type='button';button.className='formatting-correction'+(f.accepted?' accepted':'');
      button.disabled=input.disabled;button.setAttribute('aria-pressed',String(f.accepted));
      button.setAttribute('aria-label',f.description+'. '+(f.accepted?'Accepted. Click to reject.':'Not accepted. Click to accept.'));
      button.title=f.accepted?'Accepted formatting fix. Click to reject.':'Click to accept this formatting fix.';
      for(const corrected of [false,true]){
        const variant=document.createElement('span');variant.className=corrected?'formatting-after':'formatting-before';
        const name=document.createElement('small');name.textContent=corrected?'After':'Before';variant.append(name);
        const run=document.createElement('span');run.className='format-decoration format-'+f.property;
        const space=document.createElement('span');space.className='format-space';space.textContent='\u00b7';space.title='Space (kept in the text)';
        const leading=f.start===f.runStart;
        const content=document.createElement('span');content.textContent=reviewOriginal.slice(leading?f.end:f.runStart,leading?f.runEnd:f.start);
        if(corrected){run.append(content);if(leading)variant.append(space,run);else variant.append(run,space);}
        else {if(leading)run.append(space,content);else run.append(content,space);variant.append(run);}
        button.append(variant);
      }
      button.onclick=()=>{if(button.disabled)return;f.accepted=!f.accepted;renderFormatting();$('formatting-findings').querySelectorAll('button')[index]?.focus();};
      const afterContext=document.createElement('span');afterContext.textContent=reviewOriginal.slice(f.runEnd,right)+(right<reviewOriginal.length?'...':'');
      context.append(beforeContext,button,afterContext);card.append(context);
    }
    container.append(card);
  });
}
function revised(){return reviewRevised(reviewOriginal,parts,wordings);}
function acceptedEdits(){return reviewEdits(parts,wordings);}
function preview(){$('preview').textContent=revised();showInline();}
function toggleCorrection(p){
  if(locked()||!activeCorrection(p))return;
  p.accepted=!p.accepted;showChanges();
}
function showInline(){
  const container=$('inline-text');container.replaceChildren();
  reviewSegments(reviewOriginal,parts,wordings).forEach(p=>{
    if(!p.changed){const span=document.createElement('span');span.textContent=p.before;if(p.superseded)span.className='inactive-correction';container.append(span);return;}
    if(p.category!==activeTab){
      const span=document.createElement('span');span.className='inactive-correction';span.textContent=p.accepted?p.after:p.before;
      span.title=(p.accepted?'Accepted':'Rejected')+' '+(p.category==='worth'?'wording from Worth checking':p.category)+' correction';container.append(span);return;
    }
    const button=document.createElement('button');button.type='button';button.className='inline-correction'+(p.accepted?'':' rejected');
    button.disabled=locked()||!!p.layoutProtected;button.dataset.layoutProtected=String(!!p.layoutProtected);button.dataset.category=p.category;
    button.title=p.layoutProtected?'Not applied: this suggestion would change line breaks.':p.accepted?'Accepted correction. Click to reject.':'Rejected correction. Click to accept.';
    button.setAttribute('aria-pressed',String(p.accepted));
    button.setAttribute('aria-label',`${p.before||'insert'} to ${p.after||'delete'}. ${button.title}`);
    if(p.before)button.append(removedText(p.before,!p.after&&p.accepted));
    if(p.after){const next=document.createElement('ins');next.textContent=p.after;button.append(next);}
    button.onclick=()=>toggleCorrection(p);container.append(button);
  });
}
// A struck-through comma reads like a dash; short punctuation or space removals render as a labelled box instead.
function removedText(text,label){
  const old=document.createElement('del');old.textContent=text;
  if(text.length<=4&&!/[\p{L}\p{N}\r\n]/u.test(text)){
    const space=!text.trim();old.className='punct';
    old.dataset.label=label?(space?'remove space':'remove'):(space?'space':'');
  }
  return old;
}
function showChanges(){
  const container=$('changes');container.replaceChildren();
  const changes=[...parts.filter(p=>p.changed&&p.category===activeTab),...wordings.filter(w=>w.created&&activeTab==='worth')];
  $('correction-count').textContent=changes.length;
  if(!changes.length){const p=document.createElement('p');p.className='empty';p.textContent='No text corrections in this category.';container.append(p);}
  changes.forEach((p,index)=>{
    const inactive=!activeCorrection(p),label=document.createElement('label');label.className='change'+(inactive?' inactive-change':'');
    const checkbox=document.createElement('input');checkbox.type='checkbox';checkbox.checked=p.accepted;checkbox.setAttribute('aria-label',`Accept correction ${index+1}`);
    checkbox.disabled=locked()||inactive;checkbox.dataset.layoutProtected=String(!!p.layoutProtected);checkbox.dataset.inactive=String(inactive);
    checkbox.onchange=()=>{
      if(locked()||!activeCorrection(p))return;
      if(p.category==='worth'&&checkbox.checked)reviewAcceptWording(p,wordings);else p.accepted=checkbox.checked;
      showChanges();
    };
    const content=document.createElement('span');content.className='change-content';
    const old=p.before?removedText(p.before,false):document.createElement('del');if(!p.before)old.textContent='(insertion)';
    const next=document.createElement('ins');next.textContent=p.after||'(deletion)';
    content.append(old,next);label.append(checkbox,content);container.append(label);
  });
  preview();renderTabs();showFindings();update();
}
function showFindings(){
  const container=$('findings');container.replaceChildren();$('finding-count').textContent=tabCount('worth');
  if(!tabCount('worth')){const p=document.createElement('p');p.className='empty';p.textContent='No specific reasoning or terminology concerns found.';container.append(p);}
  reviewFindings.forEach((f,index)=>{
    if(f.dismissed)return;
    const card=document.createElement('article');card.className='finding';
    const top=document.createElement('div');top.className='finding-top';
    const kind=document.createElement('span');kind.className='kind';kind.textContent=f.kind.replaceAll('_',' ');
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
        use.onclick=()=>{if(locked()||w.layoutProtected)return;if(w.accepted)w.accepted=false;else reviewAcceptWording(w,wordings);activeTab='worth';showChanges();};
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
  if(activeTab==='formatting'){
    if($('allow-plain-text').checked)return;formattingSuggestions.forEach(f=>f.accepted=accepted);renderFormatting();
  }else if(activeTab==='worth'){
    wordings.filter(w=>w.created&&!reviewFindings[w.findingIndex].dismissed).forEach(w=>{if(accepted)reviewAcceptWording(w,wordings);else w.accepted=false;});
  }else parts.filter(p=>p.changed&&activeCorrection(p)).forEach(p=>p.accepted=accepted);
  showChanges();
}
$('source').addEventListener('input',()=>{pendingAutoCheck=false;sourceText=$('source').value;canReplace=false;readOnlyCapture=false;richClipboard=false;$('formatting').hidden=true;$('results').hidden=true;update();});
$('signin').onclick=()=>post('signin');$('signout').onclick=()=>post('signout');$('usage').onclick=()=>post('usage');$('cancel').onclick=()=>post('cancel');
$('open-help').onclick=()=>$('help').showModal();$('close-help').onclick=()=>$('help').close();
$('open-settings').onclick=()=>{$('settings').showModal();post('chromeStatus');};$('close-settings').onclick=()=>$('settings').close();
$('copy').onclick=()=>post('copy',{text:revised(),original:reviewOriginal,edits:acceptedEdits(),formattingIds:selectedFormatting(),allowPlainText:$('allow-plain-text').checked});$('apply').onclick=()=>post('apply',{text:revised(),original:reviewOriginal,edits:acceptedEdits(),formattingIds:selectedFormatting(),allowPlainText:$('allow-plain-text').checked});
$('allow-plain-text').onchange=update;
$('open-history').onclick=()=>showHistory(!historyVisible());$('close-history').onclick=()=>showHistory(false);
$('history-clear').onclick=$('settings-history-clear').onclick=()=>post('historyClear');$('history-enabled').onchange=e=>post('historyEnabled',{enabled:e.target.checked});
document.querySelectorAll('input[name=review-mode]').forEach(r=>r.onchange=()=>{if(r.checked)post('reviewMode',{mode:r.value});});
$('autostart').onchange=e=>post('autostart',{enabled:e.target.checked});$('register-chrome').onclick=()=>post('registerChrome');$('extension-folder').onclick=()=>post('extensionFolder');$('refresh-chrome').onclick=()=>post('chromeStatus');
$('accept-all').onclick=()=>bulkCorrections(true);$('reject-all').onclick=()=>bulkCorrections(false);
window.chrome.webview.addEventListener('message',event=>{
  const m=event.data;
  if(m.type==='capture'){activeTab='spelling';partialReview=false;tabState={};tabResults={};parts=[];wordings=[];reviewFindings=[];$('source-section').hidden=!m.text;if($('help').open)$('help').close();showFormatting([]);if($('settings').open)$('settings').close();captureMode=m.mode||'clipboard';richClipboard=!!m.canPreserveFormatting;readOnlyCapture=!!m.readOnly;$('allow-plain-text').checked=false;sourceText=m.text;$('source').value=m.text;$('original-text').textContent=m.text;$('source-section').open=false;canReplace=m.canReplace;$('formatting').hidden=!m.hasFormatting||captureMode==='chrome';$('results').hidden=true;pendingAutoCheck=!!m.text;notice(m.text&&!connected?'Connect ChatGPT in Settings to review this selection.':'');startCapturedReview();}
  if(m.type==='history'){historyEnabled=!!m.enabled;historyEntries=m.entries||[];$('history-enabled').checked=historyEnabled;renderHistory();}
  if(m.type==='historyCopied'){copiedId=m.id;clearTimeout(copiedTimer);copiedTimer=setTimeout(()=>{copiedId=null;renderHistory();},1600);renderHistory();}
  if(m.type==='capture'&&m.text)showHistory(false);
  if(m.type==='autostart')$('autostart').checked=!!m.enabled;
  if(m.type==='chromeStatus'){
    $('chrome-status').textContent=m.connected?'Connected to Chrome. Use the extension context menu to capture text.':m.registered?'Desktop connection registered. Load or reload the extension, then use its right-click review action.':'Chrome connection is not registered for this executable. Complete setup below.';
    $('setup-details').open=!m.connected;$('extension-path').textContent=m.folder||'chrome-extension';$('extension-folder').disabled=!m.folder;
  }
  if(m.type==='status'){connected=m.status.connected;$('connection').textContent=m.status.message;$('daily-status').textContent=connected?'Using ChatGPT plan':'Connect ChatGPT in Settings';$('signin').hidden=connected;$('signout').hidden=!connected;startCapturedReview();}
  if(m.type==='busy'){busy=m.busy;if(!busy&&partialReview){partialReview=false;for(const k of focusOrder)if(tabState[k]==='running'||tabState[k]==='queued')tabState[k]='stopped';if(!$('results').hidden)renderTabs();}}
  if(m.type==='notice')notice(m.message);
  if(m.type==='connected')notice('Using ChatGPT plan. You can manage this connection and its usage in ChatGPT settings.');
  if(m.type==='reviewMode'){reviewMode=m.mode;for(const r of document.querySelectorAll?.('input[name=review-mode]')||[])r.checked=r.value===reviewMode;}
  if(m.type==='tabProgress'){
    if(!partialReview){partialReview=true;tabState={};tabResults={};tabErrors={};autoTab=true;reviewOriginal=m.original;parts=focusParts(m.original);reviewFindings=[];wordings=[];activeTab='spelling';$('results').hidden=false;}
    for(const k of m.done||[])if(!tabState[k]||tabState[k]==='queued'||tabState[k]==='running')tabState[k]=tabState[k]==='failed'?'failed':'ready';
    tabState[m.running]='running';for(const k of m.pending||[])tabState[k]='queued';
    showChanges();tabProgressNotice();
  }
  if(m.type==='tabResult'){
    if(m.original!==reviewOriginal)return;
    applyReviewMeta(m);
    if(m.error){tabState[m.focus]='failed';tabErrors[m.focus]=m.error;}
    else{
      tabState[m.focus]='ready';
      if(m.focus==='worth'){reviewFindings=(m.result.findings||[]).map(f=>({...f,dismissed:false}));wordings=reviewFindings.map((f,i)=>reviewWording(reviewOriginal,f,i)).filter(Boolean);}
      else{tabResults[m.focus]=m.result;parts=focusParts(reviewOriginal);}
    }
    if(autoTab)activeTab=reviewTabs.find(([k])=>tabState[k]==='ready'&&tabCount(k)>0)?.[0]||'spelling';
    showChanges();
    if(m.pending?.length)tabProgressNotice();else readyNotice();
  }
  if(m.type==='result'){
    partialReview=false;tabState={};
    activeTab='spelling';reviewOriginal=m.original;parts=reviewParts(m.original,m.result.correctedText,m.result.corrections);
    reviewFindings=(m.result.findings||[]).map(f=>({...f,dismissed:false}));
    wordings=reviewFindings.map((f,i)=>reviewWording(reviewOriginal,f,i)).filter(Boolean);
    captureMode=m.mode||'clipboard';richClipboard=!!m.canPreserveFormatting;canReplace=m.canReplace;readOnlyCapture=!!m.readOnly;showFormatting(m.formattingSuggestions||[]);activeTab=reviewTabs.find(([key])=>tabCount(key)>0)?.[0]||'spelling';showChanges();$('model').textContent=m.model||'';$('results').hidden=false;
    notice(parts.some(p=>p.layoutProtected)?'Review ready. Suggestions that would change line breaks were excluded to preserve your paragraph layout.':(readOnlyCapture?'Review ready. Accept or reject corrections, then copy your text.':'Review ready. Accept or reject corrections, then copy or replace your text.'));
  }
  if(m.type==='applied'){canReplace=false;notice('Paste sent. Alt+Q cannot verify the result in the editor. Please check it there.'+(m.clipboardRestored===false?' The revised text remains on the clipboard.':''));}
  if(m.type==='chromeApplied'){canReplace=false;notice(m.message);}
  if(m.type==='targetLost'){canReplace=false;}
  if(m.type==='error'){notice(`${m.message}${m.code?'\nCode: '+m.code:''}${m.param?' ('+m.param+')':''}`);$('usage').classList.toggle('primary',!!m.usageLimit);if(m.requiresSignIn){connected=false;$('signin').hidden=false;}}
  update();
});
update();post('ready');

// Clipboard history view
let historyEntries=[],historyEnabled=true,copiedId=null,copiedTimer=0,historyClock=0;
let historyShown=false;
function historyVisible(){return historyShown;}
function showHistory(on){historyShown=on;$('main').classList.toggle('show-history',on);$('open-history').classList.toggle('active',on);if(on){post('historyList');renderHistory();historyClock=historyClock||setInterval(()=>{if(historyVisible())renderHistory();},30000);}}
function ago(ms){const s=Math.max(0,Math.round((Date.now()-ms)/1000));if(s<60)return'Just now';const m=Math.round(s/60);if(m<60)return m+' min ago';const h=Math.round(m/60);if(h<24)return h+(h===1?' hour ago':' hours ago');const d=Math.round(h/24);return d+(d===1?' day ago':' days ago');}
function el(tag,cls,text){const e=document.createElement(tag);if(cls)e.className=cls;if(text!==undefined)e.textContent=text;return e;}
function renderHistory(){
  const list=$('history-list');list.replaceChildren();
  $('history-count').textContent=historyEnabled?historyEntries.length:'';
  $('history-clear').hidden=!historyEnabled||!historyEntries.length;
  const empty=!historyEnabled?'Clipboard history is off. Turn it on in Settings.':historyEntries.length?'':'Copied text appears here.';
  $('history-empty').textContent=empty;$('history-empty').hidden=!empty;
  if(!historyEnabled)return;
  for(const entry of historyEntries){
    const card=el('article','history-entry'+(entry.id===copiedId?' copied':''));
    const body=el('div','history-text',entry.preview);body.title='Copy to clipboard';body.onclick=()=>post('historyRestore',{id:entry.id});
    const top=el('div','history-top');top.append(el('span','hint',ago(entry.copiedAt)));
    if(entry.hasFormatting)top.append(el('span','kind','Formatted'));
    const meta=el('div','history-meta');
    const actions=el('div','actions');
    const copy=el('button','link',entry.id===copiedId?'Copied':'Copy');copy.onclick=()=>post('historyRestore',{id:entry.id});
    const remove=el('button','link','Remove');remove.onclick=()=>post('historyDelete',{id:entry.id});
    actions.append(copy,remove);meta.append(actions);card.append(top,body,meta);list.append(card);
  }
}
