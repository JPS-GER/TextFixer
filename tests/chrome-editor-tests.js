(async()=>{
  const results=[];
  const assert=(condition,message)=>{if(!condition)throw new Error(message);};
  const fixture=document.getElementById('fixtures');
  function select(root){root.focus();const range=document.createRange();range.selectNodeContents(root);const selection=getSelection();selection.removeAllRanges();selection.addRange(range);}
  function edit(text,before,after){const start=text.indexOf(before);return {start,end:start+before.length,before,after};}
  async function test(name,fn){try{await fn();results.push('PASS '+name);}catch(error){results.push('FAIL '+name+': '+error.message);}}
  await test('textarea preserves Unicode and blank paragraphs',async()=>{
    fixture.innerHTML='<textarea></textarea>';const field=fixture.firstChild;
    field.value='Grüße 🙂\n\nA tesst.';field.focus();field.select();
    const capture=await editorRequest({action:'capture'});assert(capture.success,capture.message);
    assert(capture.text===field.value,'capture changed original text');
    const response=await editorRequest({action:'apply',captureId:capture.captureId,original:capture.text,edits:[edit(capture.text,'tesst','test')]});
    assert(response.success,response.message);assert(field.value==='Grüße 🙂\n\nA test.','paragraphs or Unicode changed');
  });
  await test('rich text retains bold, italic and empty paragraph nodes',async()=>{
    fixture.innerHTML='<div contenteditable="true"><p><b>A tesst</b> paragraph.</p><p><br></p><p>Second <i>line</i>.</p></div>';
    const root=fixture.firstChild;select(root);
    const empty=root.children[1],italic=root.querySelector('i');
    const capture=await editorRequest({action:'capture'});assert(capture.success,capture.message);
    assert(capture.text==='A tesst paragraph.\n\nSecond line.','incorrect paragraph serialization: '+JSON.stringify(capture.text));
    const response=await editorRequest({action:'apply',captureId:capture.captureId,original:capture.text,edits:[edit(capture.text,'tesst','test')]});
    assert(response.success,response.message);assert(root.querySelector('b').textContent==='A test','bold correction failed');
    assert(root.children[1]===empty&&empty.innerHTML==='<br>','empty paragraph was replaced');
    assert(root.querySelector('i')===italic,'untouched italic markup was replaced');
  });
  await test('cross-format correction refuses before any edit',async()=>{
    fixture.innerHTML='<div contenteditable="true"><p>te<b>ss</b>t and eror</p></div>';const root=fixture.firstChild;select(root);const html=root.innerHTML;
    const capture=await editorRequest({action:'capture'});
    const response=await editorRequest({action:'apply',captureId:capture.captureId,original:capture.text,edits:[edit(capture.text,'tesst','test'),edit(capture.text,'eror','error')]});
    assert(!response.success,'cross-format correction should fail');assert(root.innerHTML===html,'preflight allowed partial mutation');
  });
  await test('source mutation invalidates a capture',async()=>{
    fixture.innerHTML='<div contenteditable="true">A tesst.</div>';const root=fixture.firstChild;select(root);
    const capture=await editorRequest({action:'capture'});root.firstChild.data='Changed text.';await Promise.resolve();
    const response=await editorRequest({action:'apply',captureId:capture.captureId,original:capture.text,edits:[edit(capture.text,'tesst','test')]});
    assert(!response.success,'mutated editor accepted');assert(root.textContent==='Changed text.','mutated editor overwritten');
  });
  await test('surrogate split and newline edits are refused',async()=>{
    fixture.innerHTML='<textarea></textarea>';const field=fixture.firstChild;field.value='🙂\n\ntext';field.focus();field.select();
    const capture=await editorRequest({action:'capture'});
    let response=await editorRequest({action:'apply',captureId:capture.captureId,original:capture.text,edits:[{start:1,end:2,before:capture.text.slice(1,2),after:'x'}]});
    assert(!response.success,'surrogate split accepted');
    response=await editorRequest({action:'apply',captureId:capture.captureId,original:capture.text,edits:[{start:2,end:3,before:'\n',after:''}]});
    assert(!response.success,'newline deletion accepted');assert(field.value==='🙂\n\ntext','refused edits changed source');
  });
  await test('NBSP at a bold edge and double spaces survive',async()=>{
    fixture.innerHTML='<div contenteditable="true"><p><b>A&nbsp;</b>&nbsp;tesst</p></div>';const root=fixture.firstChild;select(root);
    const capture=await editorRequest({action:'capture'});assert(capture.text==='A\u00a0\u00a0tesst','NBSP capture changed');
    const response=await editorRequest({action:'apply',captureId:capture.captureId,original:capture.text,edits:[edit(capture.text,'tesst','test')]});
    assert(response.success,response.message);assert(root.querySelector('b').textContent==='A\u00a0','bold edge space changed');
    assert(root.textContent.replace(/\u00a0/g,' ')==='A  test','double space lost');
  });
  await test('identical-text re-render does not invalidate selection',async()=>{
    fixture.innerHTML='<div contenteditable="true"><p>A tesst.</p></div>';const root=fixture.firstChild;select(root);
    const capture=await editorRequest({action:'capture'});root.innerHTML='<p><span>A tesst.</span></p>';await Promise.resolve();
    const response=await editorRequest({action:'apply',captureId:capture.captureId,original:capture.text,edits:[edit(capture.text,'tesst','test')]});
    assert(response.success,response.message);assert(root.textContent==='A test.','re-rendered editor not corrected');
  });
  await test('asynchronous editor applies after a cancelled native command',async()=>{
    fixture.innerHTML='<div contenteditable="true">A tesst.</div>';const root=fixture.firstChild;select(root);
    const capture=await editorRequest({action:'capture'}),originalCommand=document.execCommand;
    document.execCommand=()=>{setTimeout(()=>{root.textContent='A test.';},180);return false;};
    try{
      const response=await editorRequest({action:'apply',captureId:capture.captureId,original:capture.text,edits:[edit(capture.text,'tesst','test')]});
      assert(response.success,response.message);assert(root.textContent==='A test.','delayed write missing');
    }finally{document.execCommand=originalCommand;}
  });
  await test('unapplied command reports clean failure',async()=>{
    fixture.innerHTML='<textarea></textarea>';const field=fixture.firstChild;field.value='A tesst.';field.focus();field.select();
    const capture=await editorRequest({action:'capture'}),originalCommand=document.execCommand;
    document.execCommand=()=>false;
    try{
      const response=await editorRequest({action:'apply',captureId:capture.captureId,original:capture.text,edits:[edit(capture.text,'tesst','test')]});
      assert(!response.success,'cancelled command was accepted');assert(response.message.includes('No text changes were observed'),'unchanged text reported as partial apply');
    }finally{document.execCommand=originalCommand;}
  });
  await test('computed block boundaries omit source-code indentation',async()=>{
    fixture.innerHTML='<div contenteditable="true">\n  <section>One</section>\n  <span style="display:block">Two</span>\n  <p><br></p>\n  <article>Three</article>\n</div>';
    const root=fixture.firstChild;select(root);const capture=await editorRequest({action:'capture'});
    assert(capture.success,capture.message);assert(capture.text==='One\nTwo\n\nThree','incorrect rendered structure: '+JSON.stringify(capture.text));
  });
  await test('inline wrapper whitespace is retained',async()=>{
    fixture.innerHTML='<div contenteditable="true">a<span> </span>b</div>';const root=fixture.firstChild;select(root);
    const capture=await editorRequest({action:'capture'});assert(capture.success,capture.message);assert(capture.text==='a b','inline space disappeared');
  });
  await test('selection change is observed before insertText',async()=>{
    fixture.innerHTML='<div contenteditable="true">A tesst.</div>';const root=fixture.firstChild;select(root);
    const capture=await editorRequest({action:'capture'}),originalCommand=document.execCommand;let called=false;
    document.execCommand=()=>{called=true;return false;};
    const moveCaret=()=>{const range=document.createRange();range.selectNodeContents(root);range.collapse(false);const selection=getSelection();selection.removeAllRanges();selection.addRange(range);};
    // Queue an editor selection synchronization before the content script resumes.
    const pending=editorRequest({action:'apply',captureId:capture.captureId,original:capture.text,edits:[edit(capture.text,'tesst','test')]});
    queueMicrotask(moveCaret);
    try{const response=await pending;assert(!response.success,'moved selection accepted');assert(!called,'insertText ran after selection moved');assert(root.textContent==='A tesst.','selection test changed text');}
    finally{document.execCommand=originalCommand;}
  });
  await test('large batch stops at content-script deadline',async()=>{
    fixture.innerHTML='<textarea></textarea>';const field=fixture.firstChild;field.value=Array(101).fill('eror').join(' ');field.focus();field.select();
    const capture=await editorRequest({action:'capture'}),originalCommand=document.execCommand,originalNow=Date.now;
    const edits=Array.from({length:101},(_,i)=>({start:i*5,end:i*5+4,before:'eror',after:'error'}));let calls=0,skew=0;
    Date.now=()=>originalNow()+skew;
    document.execCommand=function(...args){const result=originalCommand.apply(this,args);if(++calls===1)skew=60001;return result;};
    try{const response=await editorRequest({action:'apply',captureId:capture.captureId,original:capture.text,edits});assert(!response.success,'timed-out batch reported complete');assert(response.appliedCount===1&&calls===1,'batch continued editing after deadline');assert(response.totalCount===101,'batch count missing');}
    finally{Date.now=originalNow;document.execCommand=originalCommand;}
  });
  const failures=results.filter(r=>r.startsWith('FAIL')).length;
  document.getElementById('results').textContent=results.join('\n')+`\nRESULT ${results.length-failures}/${results.length} passed`;
  document.title=failures?'FAIL':'PASS';fixture.remove();
})().catch(error=>document.getElementById('results').textContent='FATAL '+error.stack);
