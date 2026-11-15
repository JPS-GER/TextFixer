'use strict';
// The window's markup: paragraphs, "- " bullet lists, "1. " numbered lists, one level of nesting, "# " headings,
// "> " quotes (nested "> > "), "---" rules, **bold**, *italic*, __underline__, ~~strikethrough~~, ||spoiler|| and
// ⟦n⟧ placeholders for protected objects of a rich capture (links, videos, images, mentions, code).
// Model output is untrusted: elements are built with textContent only, never innerHTML.

// Protected objects of the current capture by id: {id,kind,label,url,html}. Set per capture by app.js.
let markupItems=new Map();
function markupSetItems(items){markupItems=items instanceof Map?items:new Map((items||[]).map(i=>[String(i.id),i]));}

// Splits text into blocks: {type:'p',lines}, {type:'h',text,level}, {type:'ul'|'ol',items:[{text,sub}],start},
// {type:'quote',blocks}, {type:'hr'}. Line breaks and paragraph spacing are written by each target (markupWriters).
function markupBlocks(text){
  const lines=String(text??'').replace(/\r\n?/g,'\n').split('\n');
  const blocks=[];let current=null;
  const listItem=line=>{
    let m=line.match(/^(\s*)[-*•]\s+(.*)$/);if(m)return {indent:m[1].length,type:'ul',text:m[2]};
    m=line.match(/^(\s*)(\d{1,3})[.)]\s+(.*)$/);if(m)return {indent:m[1].length,type:'ol',text:m[3],start:+m[2]};
    return null;
  };
  for(let n=0;n<lines.length;n++){
    const line=lines[n].replace(/\s+$/,'');
    if(!line.trim()){current=null;continue;}
    // A quote: consecutive lines starting with ">", read again without one level.
    if(/^\s{0,3}>/.test(line)){
      const inner=[];
      while(n<lines.length&&/^\s{0,3}>/.test(lines[n]))inner.push(lines[n++].replace(/^\s{0,3}> ?/,''));
      n--;blocks.push({type:'quote',blocks:markupBlocks(inner.join('\n'))});current=null;continue;
    }
    if(/^\s{0,3}(-{3,}|\*{3,}|_{3,})$/.test(line)){blocks.push({type:'hr'});current=null;continue;}
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

// Inline runs: [{text,bold,italic,underline,strike,spoiler}] or [{token:item}]. Unpaired markers and unknown
// placeholders stay as text.
function markupInline(text){
  const runs=[],re=/⟦(\d+)⟧|\*\*(?=\S)(.+?)(?<=\S)\*\*|__(?=\S)(.+?)(?<=\S)__|~~(?=\S)(.+?)(?<=\S)~~|\|\|(?=\S)(.+?)(?<=\S)\|\||(?<![\w*])\*(?=[^\s*])(.+?)(?<=[^\s*])\*(?![\w*])/g;
  const flags=[null,null,'bold','underline','strike','spoiler','italic'];
  let last=0,m;
  while((m=re.exec(text))){
    if(m[1]!==undefined&&!markupItems.has(m[1]))continue;
    if(m.index>last)runs.push({text:text.slice(last,m.index)});
    if(m[1]!==undefined)runs.push({text:'',token:markupItems.get(m[1])});
    else{const g=[2,3,4,5,6].find(i=>m[i]!==undefined);for(const inner of markupInline(m[g]))runs.push(inner.token?inner:{...inner,[flags[g]]:true});}
    last=re.lastIndex;
  }
  if(last<text.length)runs.push({text:text.slice(last)});
  return runs;
}

// How a protected object reads in the window: link text, video title, mention, code.
function markupTokenElement(item){
  if(item.kind==='code'||item.kind==='codeblock'){const c=document.createElement('code');c.textContent=item.label;return c;}
  const el=document.createElement('span');el.className='md-token md-'+item.kind;
  el.textContent=item.kind==='video'?'▶ '+item.label:item.kind==='image'?'Image: '+(item.url||item.label):item.kind==='poll'?'Poll':item.label;
  if(item.url)el.title=item.url;
  return el;
}

function markupAppendInline(parent,text){
  for(const r of markupRuns(text)){
    if(r.token){parent.append(markupTokenElement(r.token));continue;}
    // Outer to inner; the innermost element holds the text.
    const marks=[['spoiler','span','md-spoiler'],['strike','s'],['underline','u'],['bold','strong'],['italic','em']].filter(([flag])=>r[flag]);
    let outer=null,inner=parent;
    for(const [,tag,cls] of marks.length?marks:[[null,'span']]){const el=document.createElement(tag);if(cls)el.className=cls;if(outer)inner.append(el);else outer=el;inner=el;}
    inner.textContent=r.text;
    parent.append(outer);
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

function markupRenderBlocks(target,blocks){
  for(const b of blocks){
    if(b.type==='h'){const p=document.createElement('p');p.className='md-head';const s=document.createElement('strong');s.textContent=markupPlainInline(b.text);p.append(s);target.append(p);}
    else if(b.type==='p'){
      const p=document.createElement('p');
      b.lines.forEach((line,i)=>{if(i)p.append(document.createElement('br'));markupAppendInline(p,line);});
      target.append(p);
    }
    else if(b.type==='quote'){const q=document.createElement('blockquote');markupRenderBlocks(q,b.blocks);target.append(q);}
    else if(b.type==='hr')target.append(document.createElement('hr'));
    else target.append(markupList(b));
  }
}

// Fills target with the formatted text.
function renderMarkup(target,text){
  target.replaceChildren();
  target.classList?.add?.('md');
  markupRenderBlocks(target,markupBlocks(text));
}

function markupPlainInline(text){return markupInlineAs(text,markupWriters.plain);}

// Plain text for copying and for "Use as captured text": no markers, real bullet characters.
function markupPlain(text){return markupPlainBlocks(markupBlocks(text));}

// ---- Export: one parsed structure, written out per target format (docs/text-formats.md).
// Blocks come from markupBlocks or are built directly; a line or item text is either markup text or {runs}.
const markupRuns=x=>typeof x==='string'?markupInline(x):x.runs;
const markupEscapeHtml=s=>s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
// Markdown: escape inline markers; a line that would start a block construct gets a backslash.
const markupEscapeMarkdown=s=>s.replace(/[\\`*_[\]<>~|]/g,'\\$&');
const markupMarkdownLineStart=s=>s.replace(/^(\s*)([#>+-]|\d+[.)])(?=\s|$)/,'$1\\$2');
// Only http and https addresses become links; anything else stays text.
const markupSafeUrl=u=>/^https?:\/\/[^\s"<>]+$/i.test(u||'')?u:'';
// True when a visible link text is just the address, possibly shortened ("www.example.com/pa...").
function markupSameAddress(label,url){
  const bare=s=>String(s||'').trim().replace(/^https?:\/\//i,'').replace(/\/$/,'');
  const visible=bare(label).replace(/(\.\.\.|…)$/,'');
  return !!visible&&bare(url).toLowerCase().startsWith(visible.toLowerCase());
}
const markupLinkText=item=>markupSameAddress(item.label,item.url)?item.url:item.label;
const markupPlainToken=item=>{
  const url=markupSafeUrl(item.url);
  if(item.kind==='link')return url&&!markupSameAddress(item.label,url)?`${item.label} (${url})`:url||item.label;
  if(item.kind==='video')return url?`${item.label} (${url})`:item.label;
  if(item.kind==='image')return url||item.label;
  if(item.kind==='poll')return '[Poll]';
  return item.label;
};
const markupHtmlToken=(item,forum)=>{
  const url=markupSafeUrl(item.url),e=markupEscapeHtml;
  if(item.kind==='link'||item.kind==='video'||item.kind==='image'){
    if(!url)return e(item.label);
    // The forum shows every link as its address and turns video and image links into players and pictures.
    const text=forum||item.kind==='image'?url:item.kind==='video'?item.label:markupLinkText(item);
    return `<a href="${e(url)}">${e(text)}</a>`;
  }
  if(item.kind==='mention')return forum&&!/\son\w+\s*=/i.test(item.html||'')&&/^<mention-component\b[^<]*><\/mention-component>$|^<mention-component\b[^<]*>[^<]*<\/mention-component>$/i.test(item.html||'')?item.html:e(item.label);
  if(item.kind==='code')return forum?e(item.label):`<code>${e(item.label)}</code>`;
  if(item.kind==='codeblock')return `<pre><code>${e(item.label)}</code></pre>`;
  return '';
};
const markupMarkdownToken=(item,reddit)=>{
  const url=markupSafeUrl(item.url);
  if(item.kind==='link')return !url?markupEscapeMarkdown(item.label):markupSameAddress(item.label,url)?url:`[${markupEscapeMarkdown(item.label)}](${url})`;
  if(item.kind==='video')return url?`[${markupEscapeMarkdown(item.label)}](${url})`:markupEscapeMarkdown(item.label);
  if(item.kind==='image')return url?(reddit?url:`![](${url})`):'';
  if(item.kind==='code')return '`'+item.label.replace(/`/g,'')+'`';
  if(item.kind==='codeblock')return '```\n'+item.label+'\n```';
  if(item.kind==='poll')return '';
  return markupEscapeMarkdown(item.label);
};
const markupBBCodeToken=item=>{
  const url=markupSafeUrl(item.url);
  if(item.kind==='link')return !url?item.label:markupSameAddress(item.label,url)?`[url]${url}[/url]`:`[url=${url}]${item.label}[/url]`;
  if(item.kind==='video')return url?`[url=${url}]${item.label}[/url]`:item.label;
  if(item.kind==='image')return url?`[img]${url}[/img]`:'';
  if(item.kind==='code'||item.kind==='codeblock')return `[code]${item.label}[/code]`;
  if(item.kind==='poll')return '';
  return item.label;
};
// YouTube comments link bare addresses only.
const markupYouTubeToken=item=>{
  const url=markupSafeUrl(item.url);
  if(item.kind==='link')return url&&!markupSameAddress(item.label,url)?`${item.label} ${url}`:url||item.label;
  if(item.kind==='video')return url?`${item.label} ${url}`:item.label;
  if(item.kind==='image')return url||'';
  if(item.kind==='poll')return '';
  return item.label;
};
// Writers, one per export target: esc (text), b/i/u/s/sp (marks), tok (protected object), nl (line break inside a
// paragraph). HTML targets add para (one paragraph from its lines) and gap (between two paragraphs); text targets
// separate paragraphs with an empty line. A missing mark keeps the text. The M! Forum editor writes one <p> per line
// without margin and an empty paragraph as the empty line.
const markupHtmlPara=lines=>`<p>${lines.join('<br>')}</p>`;
const markupWriters={
  html:{nl:'<br>',para:markupHtmlPara,gap:'',esc:markupEscapeHtml,b:t=>`<strong>${t}</strong>`,i:t=>`<em>${t}</em>`,u:t=>`<u>${t}</u>`,s:t=>`<s>${t}</s>`,sp:t=>'Spoiler: '+t,tok:i=>markupHtmlToken(i,false)},
  mforum:{nl:'',para:lines=>lines.map(l=>`<p>${l}</p>`).join(''),gap:'<p><br></p>',esc:markupEscapeHtml,b:t=>`<strong>${t}</strong>`,i:t=>`<em>${t}</em>`,u:t=>`<u>${t}</u>`,s:t=>`<s>${t}</s>`,sp:t=>`<spoiler>${t}</spoiler>`,tok:i=>markupHtmlToken(i,true)},
  markdown:{nl:'  \n',esc:markupEscapeMarkdown,b:t=>`**${t}**`,i:t=>`*${t}*`,s:t=>`~~${t}~~`,sp:t=>'Spoiler: '+t,tok:i=>markupMarkdownToken(i,false)},
  reddit:{nl:'  \n',esc:markupEscapeMarkdown,b:t=>`**${t}**`,i:t=>`*${t}*`,s:t=>`~~${t}~~`,sp:t=>`>!${t}!<`,tok:i=>markupMarkdownToken(i,true)},
  bbcode:{nl:'\n',esc:s=>s,b:t=>`[b]${t}[/b]`,i:t=>`[i]${t}[/i]`,u:t=>`[u]${t}[/u]`,s:t=>`[s]${t}[/s]`,sp:t=>`[spoiler]${t}[/spoiler]`,tok:markupBBCodeToken},
  youtube:{nl:'\n',esc:s=>s,b:t=>`*${t}*`,i:t=>`_${t}_`,s:t=>`-${t}-`,sp:t=>'Spoiler: '+t,tok:markupYouTubeToken},
  plain:{nl:'\n',esc:s=>s,sp:t=>'Spoiler: '+t,tok:markupPlainToken},
};
function markupInlineAs(x,w){
  return markupRuns(x).map(r=>{
    if(r.token)return w.tok(r.token);
    let t=w.esc(r.text);
    if(r.strike&&w.s)t=w.s(t);if(r.underline&&w.u)t=w.u(t);if(r.italic&&w.i)t=w.i(t);if(r.bold&&w.b)t=w.b(t);if(r.spoiler)t=w.sp(t);
    return t;
  }).join('');
}

function markupHtml(blocks,w=markupWriters.html){
  const list=l=>`<${l.type}${l.type==='ol'&&l.start>1?` start="${l.start}"`:''}>`+l.items.map(i=>`<li>${markupInlineAs(i.text,w)}${i.sub?list(i.sub):''}</li>`).join('')+`</${l.type}>`;
  // A code block alone on its line is a block of its own, not a paragraph.
  const alone=b=>b.lines.length===1&&typeof b.lines[0]==='string'&&/^⟦\d+⟧$/.test(b.lines[0])&&markupItems.get(b.lines[0].slice(1,-1))?.kind==='codeblock';
  const para=b=>b.type==='p'&&!alone(b);
  return blocks.map((b,n)=>(n&&para(b)&&para(blocks[n-1])?w.gap:'')+(b.type==='h'?`<h${b.level||3}>${markupInlineAs(b.text,w)}</h${b.level||3}>`
    :b.type==='p'?(alone(b)?markupInlineAs(b.lines[0],w):w.para(b.lines.map(l=>markupInlineAs(l,w))))
    :b.type==='quote'?`<blockquote>${markupHtml(b.blocks,w)}</blockquote>`
    :b.type==='hr'?'<hr>':list(b))).join('');
}
// Text formats: each block becomes lines; quotes are prefixed with "> " per level (BBCode wraps them instead).
function markupTextBlocks(blocks,w,{bullet,olStart=true,heading,quote,lineStart=s=>s}){
  const out=[];
  const list=(l,indent)=>l.items.flatMap((i,n)=>{const marker=l.type==='ol'?`${(olStart?l.start||1:1)+n}. `:bullet;return [indent+marker+markupInlineAs(i.text,w),...(i.sub?list(i.sub,indent+' '.repeat(Math.max(3,marker.length))):[])];});
  for(const b of blocks){
    if(b.type==='h')out.push(heading(b));
    else if(b.type==='p')out.push(b.lines.map(l=>lineStart(markupInlineAs(l,w))).join(w.nl));
    else if(b.type==='quote')out.push(quote(markupTextBlocks(b.blocks,w,{bullet,olStart,heading,quote,lineStart})));
    else if(b.type==='hr')out.push('---');
    else out.push(list(b,'').join('\n'));
  }
  return out.join('\n\n');
}
const markupQuotePrefix=text=>text.split('\n').map(l=>l?'> '+l:'>').join('\n');
function markupMarkdown(blocks,w=markupWriters.markdown){
  const out=[];
  const list=(l,indent)=>{const lines=[];l.items.forEach((i,n)=>{const marker=l.type==='ol'?`${(l.start||1)+n}. `:'- ';lines.push(indent+marker+markupInlineAs(i.text,w));if(i.sub)lines.push(...list(i.sub,indent+' '.repeat(marker.length)));});return lines;};
  for(const b of blocks){
    if(b.type==='h')out.push('#'.repeat(b.level||3)+' '+markupInlineAs(b.text,w));
    else if(b.type==='p')out.push(b.lines.map(l=>markupMarkdownLineStart(markupInlineAs(l,w))).join(w.nl));
    else if(b.type==='quote')out.push(markupQuotePrefix(markupMarkdown(b.blocks,w)));
    else if(b.type==='hr')out.push('---');
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
    else if(b.type==='p')out.push(b.lines.map(l=>markupInlineAs(l,w)).join(w.nl));
    else if(b.type==='quote')out.push(`[quote]\n${markupBBCode(b.blocks)}\n[/quote]`);
    else if(b.type==='hr')out.push('[hr]');
    else out.push(list(b));
  }
  return out.join('\n\n');
}
function markupPlainBlocks(blocks){
  return markupTextBlocks(blocks,markupWriters.plain,{bullet:'• ',heading:b=>markupInlineAs(b.text,markupWriters.plain),quote:markupQuotePrefix});
}
function markupYouTube(blocks){
  const w=markupWriters.youtube;
  return markupTextBlocks(blocks,w,{bullet:'• ',heading:b=>`*${markupInlineAs(b.text,{...w,b:t=>t})}*`,quote:markupQuotePrefix});
}

// Export formats in button order: [key, label, tooltip].
const exportFormats=[
  ['formatted','Formatted text','Bold, lists, quotes and links for Word, Outlook, Teams and Google Docs. Apps without formatting get plain text.'],
  ['plain','Plain text','No markup. Lists use • bullets, quotes start with ">".'],
  ['markdown','Markdown','For GitHub, Discord and Obsidian.'],
  ['reddit','Reddit','Markdown for the Markdown editor of Reddit, with >!spoiler!< tags.'],
  ['youtube','YouTube','For YouTube comments: *bold*, _italic_, -strikethrough- and plain addresses.'],
  ['bbcode','BBCode','For classic forums: [b], [i], [quote], [url], [list].'],
  ['mforum','M! Forum','For the post editor on maniac-forum.de. Pastes as formatted text the forum editor accepts, with spoilers and mentions.'],
];
// Short line under each format in the Pass on area.
const exportSubs={formatted:'Word, Outlook, Teams',plain:'No formatting',markdown:'GitHub, Discord',reddit:'Spoilers as >!text!<',youtube:'Comments',bbcode:'Classic forums',mforum:'maniac-forum.de'};
// Text and, for rich formats, an HTML fragment for the clipboard. Source: markup text or blocks.
function markupExport(source,format){
  const blocks=typeof source==='string'?markupBlocks(source):source;
  const plain=markupPlainBlocks(blocks);
  if(format==='formatted')return {text:plain,html:markupHtml(blocks)};
  if(format==='mforum')return {text:plain,html:markupHtml(blocks,markupWriters.mforum)};
  if(format==='markdown')return {text:markupMarkdown(blocks)};
  if(format==='reddit')return {text:markupMarkdown(blocks,markupWriters.reddit)};
  if(format==='youtube')return {text:markupYouTube(blocks)};
  if(format==='bbcode')return {text:markupBBCode(blocks)};
  return {text:plain};
}
