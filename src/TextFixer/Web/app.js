const $=id=>document.getElementById(id);
const post=(action,extra={})=>window.chrome.webview.postMessage({action,...extra});
let connected=false,busy=false,canReplace=false,sourceText='',reviewOriginal='',parts=[],captureMode='clipboard',richClipboard=false;
function notice(message){$('notice').textContent=message;$('notice').hidden=!message;}
function update(){
  $('count').textContent=`${$('source').value.length.toLocaleString()} / 30,000`;
  $('check').disabled=busy||!connected||!$('source').value.trim();
  $('signin').disabled=busy;$('signout').disabled=busy;$('source').disabled=busy;
  $('cancel').hidden=!busy;
  const formattedPaste=captureMode!=='chrome'&&richClipboard&&!$('allow-plain-text').checked;
  $('copy').disabled=busy;$('apply').disabled=busy||!canReplace||(captureMode!=='chrome'&&!formattedPaste&&!$('allow-plain-text').checked);
  $('allow-plain-text').disabled=busy;$('plain-text-option').hidden=captureMode==='chrome'||!canReplace;
  $('accept-all').disabled=busy;$('reject-all').disabled=busy;
  document.querySelectorAll('#inline-text button, #changes input').forEach(control=>control.disabled=busy||control.dataset.layoutProtected==='true');
  $('replace-hint').textContent=canReplace?(captureMode==='chrome'?'Applies individual corrections directly in Chrome.':formattedPaste?'Pastes corrections with the original paragraph markup and formatting.':'Replaces the captured selection as plain text.'):'Capture a selection to enable replacement.';
  $('apply').textContent=captureMode==='chrome'?'Apply in Chrome':formattedPaste?'Paste with formatting':'Paste plain text';
  $('copy').textContent=formattedPaste?'Copy with formatting':'Copy revised text';
  $('scope').textContent=captureMode==='chrome'?'Chrome direct editing. Only this text is sent for review.':'Clipboard capture. Only this text is sent for review.';
  $('route').className='route '+(captureMode==='chrome'?'direct':'clipboard');
  $('route-title').textContent=captureMode==='chrome'?'DIRECT CHROME EDITING':'CLIPBOARD MODE';
  $('route-description').textContent=captureMode==='chrome'?'Corrections go directly to the captured editor. No clipboard paste is used.':formattedPaste?'Alt+Q captured rich text. Original paragraph markup and formatting will be included in the paste.':'Alt+Q uses copy and paste. Use Chrome\'s right-click review action for direct editing.';
  $('formatting').textContent=richClipboard?'Original paragraph markup and formatting are available for copy and paste. The destination controls how they are displayed.':'This selection includes formatting. Plain-text replacement may remove formatting.';
}
function revised(){return parts.map(p=>p.changed&&!p.accepted?p.before:p.after).join('');}
function acceptedEdits(){
  let offset=0;const edits=[];
  for(const p of parts){if(p.changed&&p.accepted&&!p.layoutProtected){const edit=minimalTextEdit(offset,p.before,p.after);if(edit)edits.push(edit);}offset+=p.before.length;}
  return edits;
}
function preview(){$('preview').textContent=revised();showInline();}
function showInline(){
  const container=$('inline-text');container.replaceChildren();
  let index=0;
  parts.forEach(p=>{
    if(!p.changed){const span=document.createElement('span');span.textContent=p.before;container.append(span);return;}
    const correctionIndex=index++;
    const button=document.createElement('button');button.type='button';button.className='inline-correction'+(p.accepted?'':' rejected');button.disabled=busy;
    button.disabled=busy||!!p.layoutProtected;
    button.dataset.layoutProtected=String(!!p.layoutProtected);
    button.title=p.layoutProtected?'Not applied: this suggestion would change line breaks.':p.accepted?'Accepted correction. Click to reject.':'Rejected correction. Click to accept.';
    button.setAttribute('aria-pressed',String(p.accepted));
    button.setAttribute('aria-label',`Correction ${correctionIndex+1}: ${p.before||'insert'} to ${p.after||'delete'}. ${button.title}`);
    if(p.before){const old=document.createElement('del');old.textContent=p.before;button.append(old);}
    if(p.after){const next=document.createElement('ins');next.textContent=p.after;button.append(next);}
    button.onclick=()=>{
      if(busy||p.layoutProtected)return;
      p.accepted=!p.accepted;showChanges();
      $('inline-text').querySelectorAll('button')[correctionIndex]?.focus();
    };
    container.append(button);
  });
}
function showChanges(){
  const container=$('changes');container.replaceChildren();
  const changes=parts.filter(p=>p.changed);$('correction-count').textContent=changes.length;
  if(!changes.length){const p=document.createElement('p');p.className='empty';p.textContent='No spelling or grammar changes suggested.';container.append(p);}
  changes.forEach((p,index)=>{
    const label=document.createElement('label');label.className='change';
    const checkbox=document.createElement('input');checkbox.type='checkbox';checkbox.checked=p.accepted;checkbox.setAttribute('aria-label',`Accept correction ${index+1}`);
    checkbox.disabled=busy||!!p.layoutProtected;checkbox.dataset.layoutProtected=String(!!p.layoutProtected);checkbox.onchange=()=>{if(busy||p.layoutProtected)return;p.accepted=checkbox.checked;preview();};
    const content=document.createElement('span');content.className='change-content';
    const old=document.createElement('del');old.textContent=p.before||'(insertion)';
    const next=document.createElement('ins');next.textContent=p.after||'(deletion)';
    content.append(old,next);label.append(checkbox,content);container.append(label);
  });preview();
}
function showFindings(findings){
  const container=$('findings');container.replaceChildren();$('finding-count').textContent=findings.length;
  if(!findings.length){const p=document.createElement('p');p.className='empty';p.textContent='No specific reasoning or terminology concerns found.';container.append(p);}
  findings.forEach(f=>{
    const card=document.createElement('article');card.className='finding';
    const top=document.createElement('div');top.className='finding-top';
    const kind=document.createElement('span');kind.className='kind';kind.textContent=f.kind.replaceAll('_',' ');
    const dismiss=document.createElement('button');dismiss.className='link';dismiss.textContent='Dismiss';dismiss.onclick=()=>{card.remove();$('finding-count').textContent=container.children.length;};
    top.append(kind,dismiss);card.append(top);
    (f.quotes||[]).forEach(q=>{const quote=document.createElement('blockquote');quote.textContent=q;card.append(quote);});
    const explanation=document.createElement('p');explanation.textContent=f.explanation;card.append(explanation);
    if(f.suggestedRewrite){const rewrite=document.createElement('p');rewrite.textContent=`Possible wording: ${f.suggestedRewrite}`;card.append(rewrite);const note=document.createElement('small');note.textContent='Not applied automatically.';card.append(note);}
    container.append(card);
  });
}
$('source').addEventListener('input',()=>{sourceText=$('source').value;canReplace=false;richClipboard=false;$('formatting').hidden=true;$('results').hidden=true;update();});
$('signin').onclick=()=>post('signin');$('signout').onclick=()=>post('signout');$('usage').onclick=()=>post('usage');$('cancel').onclick=()=>post('cancel');
$('open-settings').onclick=()=>{$('settings').showModal();post('chromeStatus');};$('close-settings').onclick=()=>$('settings').close();
$('check').onclick=()=>{notice('Checking your text...');post('check',{text:sourceText});};
$('copy').onclick=()=>post('copy',{text:revised(),original:reviewOriginal,edits:acceptedEdits(),allowPlainText:$('allow-plain-text').checked});$('apply').onclick=()=>post('apply',{text:revised(),original:reviewOriginal,edits:acceptedEdits(),allowPlainText:$('allow-plain-text').checked});
$('allow-plain-text').onchange=update;
$('register-chrome').onclick=()=>post('registerChrome');$('extension-folder').onclick=()=>post('extensionFolder');$('refresh-chrome').onclick=()=>post('chromeStatus');
$('accept-all').onclick=()=>{parts.forEach(p=>p.accepted=!p.layoutProtected);showChanges();};$('reject-all').onclick=()=>{parts.forEach(p=>p.accepted=false);showChanges();};
window.chrome.webview.addEventListener('message',event=>{
  const m=event.data;
  if(m.type==='capture'){if($('settings').open)$('settings').close();captureMode=m.mode||'clipboard';richClipboard=!!m.canPreserveFormatting;$('allow-plain-text').checked=false;sourceText=m.text;$('source').value=m.text;canReplace=m.canReplace;$('formatting').hidden=!m.hasFormatting||captureMode==='chrome';$('results').hidden=true;notice(m.text?'Selection captured. Review starts when you click Check text.':'');}
  if(m.type==='chromeStatus'){
    $('chrome-status').textContent=m.connected?'Connected to Chrome. Use the extension context menu to capture text.':m.registered?'Desktop connection registered. Load or reload the extension, then use its right-click review action.':'Chrome connection is not registered for this executable. Complete setup below.';
    $('setup-details').open=!m.connected;$('extension-path').textContent=m.folder||'chrome-extension';$('extension-folder').disabled=!m.folder;
  }
  if(m.type==='status'){connected=m.status.connected;$('connection').textContent=m.status.message;$('daily-status').textContent=connected?'Using ChatGPT plan':'Connect ChatGPT in Settings';$('signin').hidden=connected;$('signout').hidden=!connected;}
  if(m.type==='busy'){busy=m.busy;$('check').textContent=busy&&m.action==='check'?'Checking...':'Check text';}
  if(m.type==='notice')notice(m.message);
  if(m.type==='connected')notice('Using ChatGPT plan. You can manage this connection and its usage in ChatGPT settings.');
  if(m.type==='result'){
    reviewOriginal=m.original;parts=textFixerDiff(m.original,m.result.correctedText);
    // A browser's nonbreaking-space representation alone is not a writing correction.
    parts.forEach(p=>{if(p.changed&&p.before.replace(/\u00a0/g,' ')===p.after.replace(/\u00a0/g,' ')){p.changed=false;p.after=p.before;}});
    parts.forEach(p=>{p.layoutProtected=p.changed&&/[\r\n]/.test(p.before+p.after);if(p.layoutProtected)p.accepted=false;});
    captureMode=m.mode||'clipboard';richClipboard=!!m.canPreserveFormatting;canReplace=m.canReplace;showChanges();showFindings(m.result.findings);$('model').textContent=m.model||'';$('results').hidden=false;
    notice(parts.some(p=>p.layoutProtected)?'Review ready. Suggestions that would change line breaks were excluded to preserve your paragraph layout.':'Review ready. Accept or reject corrections, then copy or replace your text.');
  }
  if(m.type==='applied'){canReplace=false;notice(m.formatted?'Formatted paste sent to the original editor, including the original paragraph markup. Check the result there.':'Plain-text paste sent to the original editor. Check the result there; use Ctrl+Z there to undo.');}
  if(m.type==='chromeApplied'){canReplace=false;notice(m.message);}
  if(m.type==='targetLost'){canReplace=false;}
  if(m.type==='error'){notice(`${m.message}${m.code?'\nCode: '+m.code:''}${m.param?' ('+m.param+')':''}`);$('usage').classList.toggle('primary',!!m.usageLimit);if(m.requiresSignIn){connected=false;$('signin').hidden=false;}}
  update();
});
update();post('ready');
