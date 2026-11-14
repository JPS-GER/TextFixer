'use strict';
// The Markdown subset the Summary, Explain, dialog and fact-check prompts allow: paragraphs, "- " bullet lists,
// "1. " numbered lists, one level of nesting, **bold** and *italic*. Headings are shown as a bold line.
// Model output is untrusted: elements are built with textContent only, never innerHTML.

// Splits text into blocks: {type:'p',lines}, {type:'h',text}, {type:'ul'|'ol',items:[{text,sub}]}.
function markupBlocks(text){
  const blocks=[];let current=null;
  const listItem=line=>{
    let m=line.match(/^(\s*)[-*•]\s+(.*)$/);if(m)return {indent:m[1].length,type:'ul',text:m[2]};
    m=line.match(/^(\s*)(\d{1,3})[.)]\s+(.*)$/);if(m)return {indent:m[1].length,type:'ol',text:m[3],start:+m[2]};
    return null;
  };
  for(const raw of String(text??'').replace(/\r\n?/g,'\n').split('\n')){
    const line=raw.replace(/\s+$/,'');
    if(!line.trim()){current=null;continue;}
    const head=line.match(/^\s{0,3}#{1,6}\s+(.*?)\s*#*$/);
    if(head){blocks.push({type:'h',text:head[1]});current=null;continue;}
    const item=listItem(line);
    if(item){
      const last=current?.type==='ul'||current?.type==='ol'?current:null;
      if(item.indent>=2&&last?.items.length){const parent=last.items.at(-1);(parent.sub??={type:item.type,items:[]}).items.push({text:item.text});continue;}
      if(!last||last.type!==item.type){current={type:item.type,items:[],start:item.start};blocks.push(current);}
      current.items.push({text:item.text});continue;
    }
    // A plain line right after a list item continues that item; otherwise it belongs to a paragraph.
    if(current?.items){const target=current.items.at(-1);const sub=target.sub?.items.at(-1);(sub??target).text+=' '+line.trim();continue;}
    if(current?.type!=='p'){current={type:'p',lines:[]};blocks.push(current);}
    current.lines.push(line.trim());
  }
  return blocks;
}

// Inline runs: [{text,bold,italic}]. Unpaired markers stay as text.
function markupInline(text){
  const runs=[],re=/\*\*(?=\S)(.+?)(?<=\S)\*\*|__(?=\S)(.+?)(?<=\S)__|(?<![\w*])\*(?=[^\s*])(.+?)(?<=[^\s*])\*(?![\w*])/g;
  let last=0,m;
  while((m=re.exec(text))){
    if(m.index>last)runs.push({text:text.slice(last,m.index)});
    if(m[3]!==undefined)runs.push({text:m[3],italic:true});
    else for(const inner of markupInline(m[1]??m[2]))runs.push({...inner,bold:true});
    last=re.lastIndex;
  }
  if(last<text.length)runs.push({text:text.slice(last)});
  return runs;
}

function markupAppendInline(parent,text){
  for(const r of markupInline(text)){
    const el=document.createElement(r.bold?'strong':r.italic?'em':'span');
    if(r.bold&&r.italic){const em=document.createElement('em');em.textContent=r.text;el.append(em);}else el.textContent=r.text;
    parent.append(el);
  }
}

function markupList(list){
  const el=document.createElement(list.type);
  if(list.type==='ol'&&list.start>1)el.setAttribute('start',String(list.start));
  for(const item of list.items){
    const li=document.createElement('li');markupAppendInline(li,item.text);
    if(item.sub)li.append(markupList(item.sub));
    el.append(li);
  }
  return el;
}

// Fills target with the formatted text.
function renderMarkup(target,text){
  target.replaceChildren();
  target.classList?.add?.('md');
  for(const b of markupBlocks(text)){
    if(b.type==='h'){const p=document.createElement('p');p.className='md-head';const s=document.createElement('strong');s.textContent=markupPlainInline(b.text);p.append(s);target.append(p);}
    else if(b.type==='p'){
      const p=document.createElement('p');
      b.lines.forEach((line,i)=>{if(i)p.append(document.createElement('br'));markupAppendInline(p,line);});
      target.append(p);
    }
    else target.append(markupList(b));
  }
}

function markupPlainInline(text){return markupInline(text).map(r=>r.text).join('');}

// Plain text for copying and for "Use as captured text": no markers, real bullet characters.
function markupPlain(text){
  const out=[];
  const list=(l,indent)=>l.items.forEach((item,i)=>{
    out.push(indent+(l.type==='ol'?`${(l.start||1)+i}. `:'• ')+markupPlainInline(item.text));
    if(item.sub)list(item.sub,indent+'   ');
  });
  for(const b of markupBlocks(text)){
    if(out.length)out.push('');
    if(b.type==='h')out.push(markupPlainInline(b.text));
    else if(b.type==='p')out.push(...b.lines.map(markupPlainInline));
    else list(b,'');
  }
  return out.join('\n');
}
