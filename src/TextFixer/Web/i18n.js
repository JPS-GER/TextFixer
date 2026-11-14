'use strict';
// App language. The window is written in English; for German, each element's text is looked up as a whole sentence:
// its text with every child element as {0}, {1} (a shortcut, a bold word), so German word order works. Text that code
// sets later is translated the same way when it appears. Elements marked data-no-tr (captured text, results, answers)
// are never translated. Words added to results (copied fact check labels) follow the result language instead.
let appLanguage='en';
const i18nKeep=new WeakMap();   // element -> its English children while it shows German
const i18nAttrs=['title','placeholder','aria-label'];

function i18nWords(lang){return lang==='de'?i18nDe:null;}
// Translates one English UI string; unknown strings stay as they are.
function tr(text,lang=appLanguage){
  const words=i18nWords(lang);if(!words||!text)return text;
  const lead=text.match(/^\s*/)[0],tail=text.match(/\s*$/)[0],core=text.trim().replace(/\s+/g,' ');
  if(!core)return text;
  if(Object.hasOwn(words,core))return lead+words[core]+tail;
  for(const [pattern,to] of i18nDePatterns){const m=core.match(pattern);if(m)return lead+(typeof to==='function'?to(...m.slice(1)):core.replace(pattern,to))+tail;}
  return text;
}
// Captured text, results, answers, quotes and clipboard entries are content, never UI text.
const i18nContent='[data-no-tr],script,style,textarea,pre,code,blockquote,#choose-text,#post-info,#inline-text,#original-text,#source,#preview,#translation-text,#translation-original,#rewrite-text,#rewrite-changes,#rewrite-steps,#fact-preview,.msg-text,.fact-explanation,.change-content,.history-text,.md';
function i18nSkipped(el){return !el||el.closest?.(i18nContent)!=null;}
function i18nKey(el){
  let n=0,hasText=false;const parts=[];
  for(const c of el.childNodes){
    if(c.nodeType===3){parts.push(c.nodeValue);if(c.nodeValue.trim())hasText=true;}
    else if(c.nodeType===1)parts.push(`{${n++}}`);
  }
  return hasText?parts.join('').trim().replace(/\s+/g,' '):null;
}
function i18nElement(el){
  if(el.nodeType!==1)return;
  // A text field's content is the user's text, but its placeholder and title are UI text.
  const field=el.tagName==='TEXTAREA'&&!i18nSkipped(el.parentElement);
  if(!field&&i18nSkipped(el))return;
  for(const a of i18nAttrs){
    const v=el.getAttribute(a);if(!v)continue;
    const store=`data-en-${a}`,en=el.getAttribute(store);
    if(appLanguage==='en'){if(en!==null&&v===tr(en,'de')){el.setAttribute(a,en);}continue;}
    const source=en!==null&&v===tr(en,'de')?en:v,t=tr(source);
    if(t!==source){el.setAttribute(store,source);if(v!==t)el.setAttribute(a,t);}
  }
  if(field)return;
  const key=i18nKey(el);if(!key)return;
  const kept=i18nKeep.get(el);
  if(appLanguage==='en'){
    if(kept&&kept.shown===key){i18nKeep.delete(el);el.replaceChildren(...kept.nodes);}
    return;
  }
  if(kept&&kept.shown===key)return;
  const words=i18nWords(appLanguage);
  let target=words&&Object.hasOwn(words,key)?words[key]:null;
  if(target===null&&!key.includes('{')){const t=tr(key);if(t!==key)target=t;}
  if(target===null)return;
  const children=[...el.childNodes].filter(c=>c.nodeType===1);
  const nodes=[...el.childNodes];
  const out=[];
  for(const piece of target.split(/(\{\d+\})/)){
    const m=piece.match(/^\{(\d+)\}$/);
    if(m){if(children[+m[1]])out.push(children[+m[1]]);}
    else if(piece)out.push(document.createTextNode(piece));
  }
  // Keep the text's own leading and trailing spaces, which separate it from the elements around it.
  const first=nodes[0],last=nodes.at(-1);
  if(first?.nodeType===3&&/^\s/.test(first.nodeValue)&&out[0]?.nodeType===3)out[0].nodeValue=' '+out[0].nodeValue.trimStart();
  if(last?.nodeType===3&&/\s$/.test(last.nodeValue)&&out.at(-1)?.nodeType===3)out.at(-1).nodeValue=out.at(-1).nodeValue.trimEnd()+' ';
  i18nKeep.set(el,{nodes,shown:i18nKey({childNodes:out})});
  el.replaceChildren(...out);
}
function i18nTree(root){
  if(!root||root.nodeType!==1)return;
  i18nElement(root);
  for(const el of root.querySelectorAll('*'))i18nElement(el);
}
let i18nObserver=null;
function setAppLanguage(lang){
  lang=lang==='de'?'de':'en';
  if(lang===appLanguage&&i18nObserver)return;
  appLanguage=lang;
  if(typeof document==='undefined'||!document.documentElement?.querySelectorAll)return;
  document.documentElement.lang=lang;
  i18nObserver?.disconnect();
  i18nTree(document.body);
  if(typeof MutationObserver==='undefined')return;
  i18nObserver=new MutationObserver(records=>{
    const seen=new Set();
    for(const r of records){
      const el=r.type==='characterData'?r.target.parentElement:r.target;
      if(!el||seen.has(el))continue;seen.add(el);
      if(r.type==='childList'){i18nElement(el);for(const n of r.addedNodes)if(n.nodeType===1)i18nTree(n);}
      else i18nElement(el);
    }
  });
  i18nObserver.observe(document.body,{subtree:true,childList:true,characterData:true,attributes:true,attributeFilter:i18nAttrs});
}

// Words added to results. "text" follows the language of the text: German when it reads as German.
let resultLanguage='text';
function textLooksGerman(text){
  const words=(text||'').toLowerCase().match(/[a-zäöüß]+/g)||[];
  let de=0,en=0;
  for(const w of words){if(i18nGermanWords.has(w))de++;else if(i18nEnglishWords.has(w))en++;}
  return de>en;
}
function resultLang(text){return resultLanguage==='de'||resultLanguage==='en'?resultLanguage:textLooksGerman(text)?'de':'en';}
function resultWord(english,text){return resultLang(text)==='de'?tr(english,'de'):english;}
const i18nGermanWords=new Set('der die das und ist nicht ein eine einen dem den des zu mit für auf auch sich von im es ich wir sie er aber oder wie noch nur bei wird werden hat haben sind war dass als nach aus so wenn kein keine'.split(' '));
const i18nEnglishWords=new Set('the and is are not a an of to in that it with for on as was were be by this from but or have has had will would can which there their they we you'.split(' '));
