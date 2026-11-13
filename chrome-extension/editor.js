(()=>{
  const VERSION='0.2.2';
  const previous=globalThis.__textFixerEditor;
  if(previous?.version===VERSION&&previous.listener&&chrome.runtime.onMessage.hasListener?.(previous.listener))return;
  if(previous?.listener){try{chrome.runtime.onMessage.removeListener(previous.listener);}catch{}}
  globalThis.__textFixerEditor={version:VERSION};
  let capture=null;
  const blockDisplays=new Set(['block','list-item','table-cell','table-row','flex','grid','flow-root']);
  const comparable=text=>text.replace(/\u00a0/g,' ');
  const sameText=(left,right)=>comparable(left)===comparable(right);
  function fail(message){throw new Error(message);}
  function clear(){capture=null;}
  function linearize(root){
    const units=[];
    const styles=new WeakMap();
    const style=node=>{if(!styles.has(node))styles.set(node,getComputedStyle(node));return styles.get(node);};
    const isBlock=node=>node?.nodeType===Node.ELEMENT_NODE&&blockDisplays.has(style(node).display);
    const add=(character,node,offset,virtual=false)=>{if(units.length>=150000)fail('This editor is too large for direct editing.');units.push({character,node,offset,virtual});};
    const last=()=>units.at(-1)?.character;
    function walk(node){
      if(node.nodeType===Node.TEXT_NODE){
        // Source-code indentation between block elements is not a rendered blank paragraph.
        if(/^[ \t\r\n]+$/.test(node.data)&&['normal','nowrap'].includes(style(node.parentElement).whiteSpace)
          &&(isBlock(node.previousSibling)||isBlock(node.nextSibling)||(isBlock(node.parentElement)&&!node.previousSibling&&!node.nextSibling)))return;
        for(let i=0;i<node.data.length;i++)add(node.data[i],node,i);return;
      }
      if(node.nodeType!==Node.ELEMENT_NODE||['SCRIPT','STYLE'].includes(node.tagName)||node.contentEditable==='false')return;
      if(node.hidden||style(node).display==='none')return;
      const parent=node.parentNode,index=parent?Array.prototype.indexOf.call(parent.childNodes,node):0;
      if(node.tagName==='BR'){
        // A trailing BR in a paragraph is generally the editor's empty-line caret placeholder.
        if(isBlock(parent)&&node===parent.lastChild)return;
        add('\n',parent,index,true);return;
      }
      const block=node!==root&&isBlock(node);
      if(block&&units.length&&last()!=='\n')add('\n',parent,index,true);
      const start=units.length;
      for(const child of node.childNodes)walk(child);
      if(block&&(units.length===start||last()!=='\n'))add('\n',parent,index+1,true);
    }
    walk(root);
    // Final block terminator is structural, not a selected trailing paragraph.
    if(units.at(-1)?.virtual)units.pop();
    return {units,text:units.map(u=>u.character).join('')};
  }
  function captureSelection(){
    clear();
    const element=document.activeElement;
    if(element instanceof HTMLTextAreaElement||element instanceof HTMLInputElement){
      if(element instanceof HTMLInputElement&&!['text','search','url','tel'].includes(element.type))fail('This input type is not supported. Password and email-address fields are excluded.');
      if(element.readOnly||element.disabled)fail('The selected field is not editable.');
      const start=element.selectionStart,end=element.selectionEnd;
      if(start===null||end===null||start===end)fail('Select text inside the field first.');
      capture={kind:'field',root:element,start,end,text:element.value.slice(start,end),fullText:element.value};
    }else{
      const selection=getSelection();
      if(!selection?.rangeCount||selection.isCollapsed)fail('Select text inside an editable field first.');
      const range=selection.getRangeAt(0),anchor=range.startContainer.nodeType===Node.ELEMENT_NODE?range.startContainer:range.startContainer.parentElement;
      const root=anchor?.closest('[contenteditable="true"],[contenteditable=""],[contenteditable="plaintext-only"]');
      if(!root||!root.isContentEditable||!root.contains(range.endContainer))fail('This editor is not supported for direct editing. Select text inside a standard text field or contenteditable editor.');
      if(root.querySelector('[contenteditable="false"]')&&range.cloneContents().querySelector('[contenteditable="false"]'))fail('The selection contains a protected editor element. Select a smaller text-only passage.');
      const map=linearize(root),startRange=range.cloneRange(),endRange=range.cloneRange(),point=document.createRange();
      startRange.collapse(true);endRange.collapse(false);
      const included=[];
      map.units.forEach((unit,index)=>{
        if(!unit.virtual){if(range.comparePoint(unit.node,unit.offset)===0&&range.comparePoint(unit.node,unit.offset+1)===0)included.push(index);}
        else{
          point.setStart(unit.node,unit.offset);point.collapse(true);
          if(point.compareBoundaryPoints(Range.START_TO_START,startRange)>0&&point.compareBoundaryPoints(Range.START_TO_START,endRange)<0)included.push(index);
        }
      });
      if(!included.length)fail('The selection contains no supported text.');
      const start=included[0],end=included.at(-1)+1;
      capture={kind:'rich',root,start,end,text:map.text.slice(start,end),fullText:map.text};
    }
    if(!capture.text.trim()||capture.text.length>30000){clear();fail('Select between 1 and 30,000 characters.');}
    capture.id=crypto.randomUUID();
    return {success:true,captureId:capture.id,text:capture.text,hasFormatting:capture.kind==='rich'};
  }
  function validateEdits(edits,text){
    if(!Array.isArray(edits)||!edits.length)fail('No supported corrections were selected.');
    if(edits.length>200)fail('Apply at most 200 corrections at a time.');
    let end=-1,previousStart=-1;
    for(const edit of edits){
      if(!Number.isInteger(edit.start)||!Number.isInteger(edit.end)||edit.start<0||edit.end<edit.start||edit.end>text.length||edit.start<=end||edit.start<=previousStart||typeof edit.before!=='string'||typeof edit.after!=='string')fail('Invalid or overlapping correction ranges.');
      if(text.slice(edit.start,edit.end)!==edit.before||/[\r\n]/.test(edit.before+edit.after))fail('A correction no longer matches or would change paragraph structure.');
      // Never split a UTF-16 surrogate pair at either boundary.
      for(const offset of[edit.start,edit.end])if(offset>0&&offset<text.length&&/[\uD800-\uDBFF]/.test(text[offset-1])&&/[\uDC00-\uDFFF]/.test(text[offset]))fail('A correction splits a Unicode character.');
      end=edit.end-1;
      previousStart=edit.start;
    }
  }
  async function waitForEditor(read,expected,previous){
    const deadline=Date.now()+1000;let stableSince=0,actual=read();
    while(Date.now()<deadline){
      actual=read();
      if(sameText(actual,expected)){
        stableSince ||= Date.now();
        if(Date.now()-stableSince>=75)return {matched:true,actual};
      }else stableSince=0;
      await new Promise(resolve=>setTimeout(resolve,25));
    }
    actual=read();return {matched:false,actual,unchanged:sameText(actual,previous)};
  }
  function nodeRange(map,start,end){
    const first=map.units[start],last=map.units[end-1];
    if(start===end){
      const next=map.units[start],previous=map.units[start-1];
      if(next&&!next.virtual)return {node:next.node,start:next.offset,end:next.offset};
      if(previous&&!previous.virtual)return {node:previous.node,start:previous.offset+1,end:previous.offset+1};
      fail('This insertion touches a paragraph boundary.');
    }
    if(!first||!last||first.virtual||last.virtual||first.node!==last.node||map.units.slice(start,end).some(u=>u.virtual||u.node!==first.node))fail('A correction crosses formatting or paragraph boundaries. Select a smaller passage or omit that correction.');
    return {node:first.node,start:first.offset,end:last.offset+1};
  }
  async function apply(message){
    const batchDeadline=Date.now()+60000;
    const saved=capture;
    if(!saved||saved.id!==message.captureId||!saved.root.isConnected)fail('The editor changed. Capture and review the selection again.');
    if(message.original!==saved.text)fail('The review does not match the captured selection.');
    if(saved.kind==='field'&&(saved.root.readOnly||saved.root.disabled))fail('The original field is no longer editable.');
    validateEdits(message.edits,saved.text);
    const read=()=>saved.kind==='field'?saved.root.value:linearize(saved.root).text;
    const initialMap=saved.kind==='rich'?linearize(saved.root):null;
    let full=initialMap?.text??read();
    if(!sameText(full,saved.fullText))fail('The editor text changed. Capture it again.');
    // Preflight the entire batch before touching any editor content.
    if(initialMap)for(const [index,edit] of message.edits.entries()){
      try{nodeRange(initialMap,saved.start+edit.start,saved.start+edit.end);}
      catch{fail(`Selected correction ${index+1} (${JSON.stringify(edit.before.slice(0,60))}) crosses formatting or paragraph boundaries. Omit that correction and capture the text again.`);}
    }
    capture=null;
    let applied=0,unknownChange=false;
    try{
      for(const edit of [...message.edits].reverse()){
        if(Date.now()>=batchDeadline)fail(`Stopped at the time limit after applying ${applied} of ${message.edits.length} corrections. Capture the remaining text again.`);
        const start=saved.start+edit.start,end=saved.start+edit.end;
        saved.root.focus({preventScroll:true});
        const currentMap=saved.kind==='rich'?linearize(saved.root):null;
        if(!sameText(currentMap?.text??read(),full)){
          unknownChange=true;fail('The editor changed while applying corrections. No further corrections were attempted.');
        }
        let richTarget=null;
        if(saved.kind==='field')saved.root.setSelectionRange(start,end);
        else{
          const target=nodeRange(currentMap,start,end),range=document.createRange();
          richTarget=target;
          range.setStart(target.node,target.start);range.setEnd(target.node,target.end);
          const selection=getSelection();selection.removeAllRanges();selection.addRange(range);
        }
        // Let framework selectionchange handlers synchronize their own caret model first.
        await new Promise(resolve=>setTimeout(resolve,0));
        let selectionMatches=document.activeElement===saved.root;
        if(saved.kind==='field')selectionMatches&&=saved.root.selectionStart===start&&saved.root.selectionEnd===end;
        else{
          const selection=getSelection(),range=selection?.rangeCount===1?selection.getRangeAt(0):null;
          selectionMatches&&=!!range&&range.startContainer===richTarget.node&&range.endContainer===richTarget.node
            &&range.startOffset===richTarget.start&&range.endOffset===richTarget.end;
        }
        if(!selectionMatches)fail('The editor moved the selection. No correction was sent to the changed selection.');
        if(!sameText(read(),full)){unknownChange=true;fail('The text changed while positioning the selection. No further corrections were attempted.');}
        if(Date.now()>=batchDeadline)fail(`Stopped at the time limit after applying ${applied} of ${message.edits.length} corrections.`);
        const expected=full.slice(0,start)+edit.after+full.slice(end);
        document.execCommand('insertText',false,edit.after);
        // Some frameworks cancel the native command and apply it asynchronously instead.
        const outcome=await waitForEditor(read,expected,full);
        if(!outcome.matched){
          unknownChange=!outcome.unchanged;
          fail(outcome.unchanged?'The editor did not apply this correction.':'The editor produced unexpected text. Check the result and use Undo if needed.');
        }
        full=outcome.actual;applied++;
      }
      return {success:true,message:`Applied ${applied} correction${applied===1?'':'s'} directly in the editor. Check the result. Undo may require one Ctrl+Z per correction, depending on the editor.`};
    }catch(error){
      if(!unknownChange&&!applied){try{unknownChange=!sameText(read(),full);}catch{unknownChange=true;}}
      return {success:false,appliedCount:applied,totalCount:message.edits.length,message:`${error.message}${applied||unknownChange?' Some text may already have changed; review the editor before trying again.':' No text changes were observed.'}`};
    }
  }
  const listener=(message,sender,respond)=>{
    if(sender.id!==chrome.runtime.id)return;
    if(message.action==='capture'){try{respond(captureSelection());}catch(error){respond({success:false,message:error.message});}return;}
    if(message.action==='apply'){apply(message).then(respond,error=>respond({success:false,message:error.message}));return true;}
  };
  globalThis.__textFixerEditor.listener=listener;
  chrome.runtime.onMessage.addListener(listener);
})();
