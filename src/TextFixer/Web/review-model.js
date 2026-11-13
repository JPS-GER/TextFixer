/* Review state uses UTF-16 offsets into the unchanged capture. No HTML is interpreted. */
const reviewCategories=['spelling','grammar','punctuation','wording'];
function reviewOverlap(a,b){
  if(a.start===a.end)return b.start<=a.start&&a.start<=b.end;
  if(b.start===b.end)return a.start<=b.start&&b.start<=a.end;
  return a.start<b.end&&b.start<a.end;
}
function reviewFallback(before,after){
  if(before.replace(/[\p{P}\s]/gu,'')===after.replace(/[\p{P}\s]/gu,''))return 'punctuation';
  const words=s=>s.match(/[\p{L}\p{N}_]+/gu)||[];
  const a=words(before),b=words(after);
  return a.length<=1&&b.length<=1&&(a.length||b.length)?'spelling':'grammar';
}
// exactSpaces: a non-breaking space against a normal space counts as a change (spaces set to plain).
function reviewParts(original,corrected,corrections=[],exactSpaces=false){
  let offset=0;
  return textFixerDiff(original,corrected).map(p=>{
    p.start=offset;p.end=offset+p.before.length;offset=p.end;
    if(p.changed&&!exactSpaces&&p.before.replace(/\u00a0/g,' ')===p.after.replace(/\u00a0/g,' ')){p.changed=false;p.after=p.before;}
    p.layoutProtected=p.changed&&/[\r\n]/.test(p.before+p.after);
    if(p.layoutProtected)p.accepted=false;
    let best=null,score=-1;
    for(const c of corrections||[]){
      if(!reviewCategories.includes(c.category)||!Number.isInteger(c.start)||!Number.isInteger(c.end)||c.start<0||c.end<c.start||c.end>original.length||!reviewOverlap(p,c))continue;
      const overlap=Math.max(0,Math.min(p.end,c.end)-Math.max(p.start,c.start));
      if(overlap>score){best=c;score=overlap;}
    }
    p.category=best?.category||reviewFallback(p.before,p.after);
    return p;
  });
}
// Punctuation style per group (Settings > Punctuation style, same keys as Punctuation.cs):
// keep: characters an answer introduces become plain, the user's own stay; plain: every one becomes plain; typographic: unchanged.
const reviewStyleMaps={
  quotes:{'\u201E':'"','\u201C':'"','\u201D':'"','\u00AB':'"','\u00BB':'"','\u201A':"'",'\u2018':"'",'\u2019':"'",'\u2039':"'",'\u203A':"'"},
  dashes:{'\u2013':'-','\u2014':'-','\u2012':'-','\u2010':'-','\u2011':'-','\u2212':'-'},
  ellipsis:{'\u2026':'...'},
  spaces:{'\u00A0':' ','\u202F':' ','\u2009':' ','\u2007':' ','\u2002':' ','\u2003':' ','\u2004':' ','\u2005':' ','\u2006':' ','\u200A':' ','\u205F':' ','\u3000':' '},
  multiply:{'\u00D7':'x'},
  arrows:{'\u2192':'->','\u2190':'<-','\u2194':'<->','\u21D2':'=>','\u21D0':'<=','\u21D4':'<=>'},
  primes:{'\u2032':"'",'\u2033':'"'},
};
function reviewStyleMap(style,modes){const map={};for(const [key,chars] of Object.entries(reviewStyleMaps))if(modes.includes(style?.[key]))Object.assign(map,chars);return map;}
// Text written by an answer (a correction, rewrite or translation). Characters found in reference count as the user's own.
function reviewPlainStyle(text,reference,style){
  if(typeof text!=='string'||!style)return text;
  const plain=reviewStyleMap(style,['plain']),keep=reviewStyleMap(style,['keep']);
  let out='';
  for(const c of text)out+=plain[c]??(keep[c]!==undefined&&!reference.includes(c)?keep[c]:c);
  return out;
}
// Converts every "plain" group character, so unchanged user text gets style corrections too.
function reviewStyleText(text,style){
  const plain=reviewStyleMap(style,['plain']);
  let out='';for(const c of text)out+=plain[c]??c;return out;
}
function reviewStyleParts(parts,style){
  const all=reviewStyleMap(style,['keep','plain']);
  const convert=s=>{let out='';for(const c of s)out+=all[c]??c;return out;};
  for(const p of parts){
    if(!p.changed)continue;
    p.after=reviewPlainStyle(p.after,p.before,style);
    const same=style?.spaces==='plain'?p.after===p.before:p.after.replace(/\u00a0/g,' ')===p.before.replace(/\u00a0/g,' ');
    if(same){p.changed=false;p.after=p.before;p.layoutProtected=false;p.accepted=true;continue;}
    if(convert(p.before)===p.after){p.styleOnly=true;p.category='punctuation';}
  }
  return parts;
}
function reviewWording(original,finding,index){
  const quote=finding.quotes?.[0],after=finding.suggestedRewrite;
  if(typeof quote!=='string'||!quote||typeof after!=='string'||!after||after===quote)return null;
  const start=original.indexOf(quote);
  if(start<0||original.indexOf(quote,start+1)>=0)return null;
  return {start,end:start+quote.length,before:quote,after,category:'worth',changed:true,accepted:false,created:false,findingIndex:index,layoutProtected:/[\r\n]/.test(quote+after)};
}
function reviewSuperseded(part,wordings){return wordings.some(w=>w.accepted&&reviewOverlap(part,w));}
function reviewAcceptWording(wording,wordings){
  if(wording.layoutProtected)return;
  for(const other of wordings)if(other!==wording&&reviewOverlap(wording,other))other.accepted=false;
  wording.created=true;wording.accepted=true;
}
function reviewEdits(parts,wordings){
  const selected=[...parts.filter(p=>p.changed&&p.accepted&&!p.layoutProtected&&!reviewSuperseded(p,wordings)),...wordings.filter(w=>w.accepted)];
  const edits=selected.map(p=>minimalTextEdit(p.start,p.before,p.after)).filter(Boolean).sort((a,b)=>a.start-b.start||a.end-b.end);
  const result=[];
  for(const e of edits){
    const previous=result.at(-1);
    // Adjacent insertions/replacements can trim to the same start. Combine them
    // without changing reconstruction so the C# validator gets strictly increasing starts.
    if(previous&&previous.end===e.start&&(previous.start===previous.end||e.start===e.end)){
      previous.end=e.end;previous.before+=e.before;previous.after+=e.after;
    }else result.push({...e});
  }
  return result;
}
function reviewRevised(original,parts,wordings){
  let position=0,result='';
  for(const e of reviewEdits(parts,wordings)){result+=original.slice(position,e.start)+e.after;position=e.end;}
  return result+original.slice(position);
}
function reviewSegments(original,parts,wordings){
  const overlays=wordings.filter(w=>w.accepted).sort((a,b)=>a.start-b.start);
  const segments=[];let position=0;
  function baseRange(start,end){
    for(const p of parts){
      if(p.changed&&!reviewSuperseded(p,wordings)){
        if(p.start>=start&&p.end<=end)segments.push(p);
      }else{
        const a=Math.max(start,p.start),b=Math.min(end,p.end);
        if(a<b)segments.push({before:original.slice(a,b),after:original.slice(a,b),changed:false,superseded:p.changed});
      }
    }
  }
  for(const w of overlays){baseRange(position,w.start);segments.push(w);position=w.end;}
  baseRange(position,original.length);
  return segments;
}
if(typeof module!=='undefined')module.exports={reviewOverlap,reviewFallback,reviewParts,reviewWording,reviewSuperseded,reviewAcceptWording,reviewEdits,reviewRevised,reviewSegments};
