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
    const head=line.match(/^\s{0,3}(#{1,6})\s+(.*?)\s*#*$/);
    if(head){blocks.push({type:'h',text:head[2],level:head[1].length});current=null;continue;}
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

// ---- Export: one parsed structure, written out per target format (docs/text-formats.md).
// Blocks come from markupBlocks or are built directly; a line or item text is either markup text or {runs}.
const markupRuns=x=>typeof x==='string'?markupInline(x):x.runs;
const markupEscapeHtml=s=>s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
// Markdown: escape inline markers; a line that would start a block construct gets a backslash.
const markupEscapeMarkdown=s=>s.replace(/[\\`*_[\]<>]/g,'\\$&');
const markupMarkdownLineStart=s=>s.replace(/^(\s*)([#>+-]|\d+[.)])(?=\s|$)/,'$1\\$2');
const markupWriters={
  html:{esc:markupEscapeHtml,b:t=>`<strong>${t}</strong>`,i:t=>`<em>${t}</em>`},
  markdown:{esc:markupEscapeMarkdown,b:t=>`**${t}**`,i:t=>`*${t}*`},
  bbcode:{esc:s=>s,b:t=>`[b]${t}[/b]`,i:t=>`[i]${t}[/i]`},
  plain:{esc:s=>s,b:t=>t,i:t=>t},
};
function markupInlineAs(x,w){return markupRuns(x).map(r=>{let t=w.esc(r.text);if(r.italic)t=w.i(t);if(r.bold)t=w.b(t);return t;}).join('');}

function markupHtml(blocks){
  const w=markupWriters.html;
  const list=l=>`<${l.type}${l.type==='ol'&&l.start>1?` start="${l.start}"`:''}>`+l.items.map(i=>`<li>${markupInlineAs(i.text,w)}${i.sub?list(i.sub):''}</li>`).join('')+`</${l.type}>`;
  return blocks.map(b=>b.type==='h'?`<h${b.level||3}>${markupInlineAs(b.text,w)}</h${b.level||3}>`
    :b.type==='p'?`<p>${b.lines.map(l=>markupInlineAs(l,w)).join('<br>')}</p>`:list(b)).join('');
}
function markupMarkdown(blocks){
  const w=markupWriters.markdown,out=[];
  const list=(l,indent)=>{const lines=[];l.items.forEach((i,n)=>{const marker=l.type==='ol'?`${(l.start||1)+n}. `:'- ';lines.push(indent+marker+markupInlineAs(i.text,w));if(i.sub)lines.push(...list(i.sub,indent+' '.repeat(marker.length)));});return lines;};
  for(const b of blocks){
    if(b.type==='h')out.push('#'.repeat(b.level||3)+' '+markupInlineAs(b.text,w));
    else if(b.type==='p')out.push(b.lines.map(l=>markupMarkdownLineStart(markupInlineAs(l,w))).join('  \n'));
    else out.push(list(b,'').join('\n'));
  }
  return out.join('\n\n');
}
function markupBBCode(blocks){
  const w=markupWriters.bbcode,out=[];
  // [list=1] always starts at 1, so a list that starts later keeps literal numbers.
  const list=l=>l.type==='ol'&&l.start>1
    ?l.items.map((i,n)=>`${l.start+n}. ${markupInlineAs(i.text,w)}`+(i.sub?'\n'+list(i.sub):'')).join('\n')
    :`[list${l.type==='ol'?'=1':''}]\n`+l.items.map(i=>`[*]${markupInlineAs(i.text,w)}`+(i.sub?'\n'+list(i.sub):'')).join('\n')+'\n[/list]';
  for(const b of blocks){
    if(b.type==='h')out.push(`[b]${markupInlineAs(b.text,w)}[/b]`);
    else if(b.type==='p')out.push(b.lines.map(l=>markupInlineAs(l,w)).join('\n'));
    else out.push(list(b));
  }
  return out.join('\n\n');
}
function markupPlainBlocks(blocks){
  const w=markupWriters.plain,out=[];
  const list=(l,indent)=>l.items.flatMap((i,n)=>[indent+(l.type==='ol'?`${(l.start||1)+n}. `:'• ')+markupInlineAs(i.text,w),...(i.sub?list(i.sub,indent+'   '):[])]);
  for(const b of blocks){
    if(b.type==='h')out.push(markupInlineAs(b.text,w));
    else if(b.type==='p')out.push(b.lines.map(l=>markupInlineAs(l,w)).join('\n'));
    else out.push(list(b,'').join('\n'));
  }
  return out.join('\n\n');
}

// Export formats in button order: [key, label, tooltip].
const exportFormats=[
  ['formatted','Formatted text','Bold, lists and headings for Word, Outlook, Teams and Google Docs. Apps without formatting get plain text.'],
  ['plain','Plain text','No markup. Lists use • bullets.'],
  ['markdown','Markdown','For GitHub, Discord, Obsidian and the Markdown editor of Reddit.'],
  ['bbcode','BBCode','For classic forums: [b], [i], [list].'],
  ['mforum','M! Forum','For the post editor on maniac-forum.de. Pastes as formatted text the forum editor accepts.'],
];
// Text and, for rich formats, an HTML fragment for the clipboard. Source: markup text or blocks.
function markupExport(source,format){
  const blocks=typeof source==='string'?markupBlocks(source):source;
  const plain=markupPlainBlocks(blocks);
  if(format==='formatted'||format==='mforum')return {text:plain,html:markupHtml(blocks)};
  if(format==='markdown')return {text:markupMarkdown(blocks)};
  if(format==='bbcode')return {text:markupBBCode(blocks)};
  return {text:plain};
}
