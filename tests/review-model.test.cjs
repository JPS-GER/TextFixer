const assert=require('node:assert/strict');
const diff=require('../src/TextFixer/Web/diff.js');
global.textFixerDiff=diff;global.minimalTextEdit=diff.minimalTextEdit;
const {reviewParts,reviewFallback,reviewWording,reviewSuperseded,reviewAcceptWording,reviewEdits,reviewRevised,reviewSegments}=require('../src/TextFixer/Web/review-model.js');
let checks=0;
function same(actual,expected,label){assert.deepEqual(actual,expected,label);checks++;}
function verify(original,parts,wordings){
 const edits=reviewEdits(parts,wordings);let pos=0,previous=-1,out='';
 for(const e of edits){
  assert.ok(e.start>previous&&e.start>=pos,'strictly increasing, nonoverlapping edit offsets');
  same(e.before,original.slice(e.start,e.end),'exact original substring');
  assert.ok(!/[\r\n]/.test(e.before+e.after),'no line-break edits');
  assert.ok(!(e.start>0&&/[\uD800-\uDBFF]/.test(original[e.start-1])&&/[\uDC00-\uDFFF]/.test(original[e.start])),'start is not inside a surrogate pair');
  out+=original.slice(pos,e.start)+e.after;pos=e.end;previous=e.start;
 }
 out+=original.slice(pos);
 same(reviewRevised(original,parts,wordings),out,'revision reconstructed from normal edit contract');
 const segments=reviewSegments(original,parts,wordings);
 same(segments.map(s=>s.before).join(''),original,'full original context represented once');
 same(segments.map(s=>s.changed&&s.accepted?s.after:s.before).join(''),out,'visible choices match submitted text');
 return out;
}
same(reviewFallback('word','word,'),'punctuation','punctuation-only fallback');
same(reviewFallback('recieve','receive'),'spelling','single-word fallback');
same(reviewFallback('he have','he has a'),'grammar','phrase fallback');
same(reviewFallback('"','\u201c'),'punctuation','quote fallback');
let original='A tesst here.\r\n\r\nMore words.';
let parts=reviewParts(original,original.replace('tesst','test'),[{start:2,end:7,after:'test',category:'wording'}]);
same(parts.find(p=>p.changed).category,'wording','resolved model category');
same(verify(original,parts,[]),original.replace('tesst','test'),'ordinary correction');
const rewrite=reviewWording(original,{quotes:['A tesst here.'],suggestedRewrite:'A better sentence.'},0);
reviewAcceptWording(rewrite,[rewrite]);
same(reviewSuperseded(parts.find(p=>p.changed),[rewrite]),true,'overlapping diff superseded');
same(verify(original,parts,[rewrite]),'A better sentence.\r\n\r\nMore words.','wording accepted');
rewrite.accepted=false;same(verify(original,parts,[rewrite]),original.replace('tesst','test'),'undo restores accepted diff');
parts.find(p=>p.changed).accepted=false;reviewAcceptWording(rewrite,[rewrite]);verify(original,parts,[rewrite]);
rewrite.accepted=false;same(verify(original,parts,[rewrite]),original,'undo preserves prior rejection');
same(reviewWording('aaa',{quotes:['aa'],suggestedRewrite:'b'},0),null,'overlapping repeated quote is ambiguous');
same(reviewWording('one one',{quotes:['one'],suggestedRewrite:'two'},0),null,'repeated quote omitted');
same(reviewWording('one',{quotes:[''],suggestedRewrite:'two'},0),null,'empty quote omitted');
same(reviewWording('one',{quotes:['absent'],suggestedRewrite:'two'},0),null,'absent quote omitted');
const multiline=reviewWording('one\r\ntwo',{quotes:['one\r\ntwo'],suggestedRewrite:'three'},0);
same(multiline.layoutProtected,true,'multiline wording protected');reviewAcceptWording(multiline,[multiline]);same(multiline.accepted,false,'protected wording stays rejected');
original='One two three.';parts=reviewParts(original,original);
const a=reviewWording(original,{quotes:['One two'],suggestedRewrite:'Both'},0),b=reviewWording(original,{quotes:['two three'],suggestedRewrite:'last'},1);
reviewAcceptWording(a,[a,b]);reviewAcceptWording(b,[a,b]);same(a.accepted,false,'overlapping wordings mutually exclusive');same(verify(original,parts,[a,b]),'One last.','last chosen wording wins');
original='Hi word';parts=reviewParts(original,'Hi word,',[{start:3,end:7,after:'word,',category:'punctuation'}]);
same(parts.find(p=>p.changed).category,'punctuation','insertion matches inclusive category boundary');
const edge=reviewWording(original,{quotes:['word'],suggestedRewrite:'term'},0);reviewAcceptWording(edge,[edge]);same(verify(original,parts,[edge]),'Hi term','insertion on rewrite boundary superseded');
original='x y';parts=reviewParts(original,'x a y');same(verify(original,parts,[]),'x a y','pure insertion retains full context');
original='One\u00a0two';parts=reviewParts(original,'One two');same(verify(original,parts,[]),original,'NBSP-only suggestion ignored');
original='First.\r\n\r\nSecond.';parts=reviewParts(original,'First.\r\nSecond.');same(verify(original,parts,[]),original,'line-break correction protected');
original='\ud83d\ude00 falsch, second tesst.';parts=reviewParts(original,'\ud83d\ude00 richtig, second test.');
const unicode=reviewWording(original,{quotes:['\ud83d\ude00 falsch'],suggestedRewrite:'\ud83d\ude03 richtig'},0);reviewAcceptWording(unicode,[unicode]);same(verify(original,parts,[unicode]),'\ud83d\ude03 richtig, second test.','Unicode and independent corrections compose');
// Vary which overlapping baseline choices were accepted before wording replacement.
for(let i=0;i<80;i++){
 original='Bad '+i+' tesst. Good tail.';parts=reviewParts(original,'Great '+i+' test. Good end.');
 parts.filter(p=>p.changed).forEach((p,j)=>p.accepted=!!(i&(1<<j)));
 const before=verify(original,parts,[]),w=reviewWording(original,{quotes:['Bad '+i+' tesst.'],suggestedRewrite:'Reworded '+i+'.'},0);
 reviewAcceptWording(w,[w]);verify(original,parts,[w]);w.accepted=false;same(verify(original,parts,[w]),before,'undo round trip '+i);
}
console.log('PASS: '+checks+' review model checks, exact edit reconstruction and wording undo.');
