/* Local word diff. No positions from the model are trusted. */
function textFixerDiff(before, after) {
  if (before === after) return [{before, after, changed:false, accepted:true}];
  const tokens = text => text.match(/\s+|[\p{L}\p{N}_]+|[^\s\p{L}\p{N}_]/gu) || [];
  let a = tokens(before), b = tokens(after), start=0, end=0;
  while(start<a.length && start<b.length && a[start]===b[start]) start++;
  while(end<a.length-start && end<b.length-start && a[a.length-1-end]===b[b.length-1-end]) end++;
  const prefix=a.slice(0,start).join(''), suffix=end ? a.slice(-end).join('') : '';
  a=a.slice(start,a.length-end);b=b.slice(start,b.length-end);
  const parts=[];
  const add=(oldText,newText,changed)=>{
    const last=parts.at(-1);
    if(last && last.changed===changed){last.before+=oldText;last.after+=newText;}
    else parts.push({before:oldText,after:newText,changed,accepted:true});
  };
  add(prefix,prefix,false);
  if(a.length*b.length>2000000) add(a.join(''),b.join(''),true);
  else {
    const width=b.length+1, table=new Uint32Array((a.length+1)*width);
    for(let i=a.length-1;i>=0;i--) for(let j=b.length-1;j>=0;j--)
      table[i*width+j]=a[i]===b[j]?table[(i+1)*width+j+1]+1:Math.max(table[(i+1)*width+j],table[i*width+j+1]);
    let i=0,j=0;
    while(i<a.length||j<b.length){
      if(i<a.length&&j<b.length&&a[i]===b[j]){add(a[i],b[j],false);i++;j++;}
      else if(j<b.length&&(i===a.length||table[i*width+j+1]>=table[(i+1)*width+j])){add('',b[j++],true);}
      else add(a[i++],'',true);
    }
  }
  add(suffix,suffix,false);
  return parts.filter(p=>p.before||p.after);
}
function minimalTextEdit(offset,before,after){
  const a=Array.from(before),b=Array.from(after);let prefix=0,suffix=0;
  while(prefix<a.length&&prefix<b.length&&a[prefix]===b[prefix])prefix++;
  while(suffix<a.length-prefix&&suffix<b.length-prefix&&a[a.length-1-suffix]===b[b.length-1-suffix])suffix++;
  const oldText=a.slice(prefix,a.length-suffix).join(''),newText=b.slice(prefix,b.length-suffix).join('');
  if(!oldText&&!newText)return null;
  const start=offset+a.slice(0,prefix).join('').length;
  return {start,end:start+oldText.length,before:oldText,after:newText};
}
if(typeof module!=='undefined'){module.exports=textFixerDiff;module.exports.minimalTextEdit=minimalTextEdit;}
