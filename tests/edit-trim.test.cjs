const assert=require('node:assert/strict');
const {minimalTextEdit}=require('../src/TextFixer/Web/diff.js');
const cases=[['recieve','receive'],['tesst','test'],['same','same'],['','insert'],['delete',''],['🙂a','🙃a'],['a🙂b','a🙂c'],['a','a🙂'],['🙂',''],[' a ',' a! '],['Grüße','Größe'],['abc','xyz']];
for(const[before,after]of cases){
  const edit=minimalTextEdit(6,before,after),original='prefix'+before+'suffix';
  const actual=edit?original.slice(0,edit.start)+edit.after+original.slice(edit.end):original;
  assert.equal(actual,'prefix'+after+'suffix');
  if(edit){assert.equal(original.slice(edit.start,edit.end),edit.before);for(const i of[edit.start,edit.end])assert.ok(!(i>0&&i<original.length&&/[\uD800-\uDBFF]/.test(original[i-1])&&/[\uDC00-\uDFFF]/.test(original[i])));}
}
assert.deepEqual(minimalTextEdit(0,'recieve','receive'),{start:3,end:5,before:'ie',after:'ei'});
console.log(`PASS: ${cases.length} minimal edit reconstruction and Unicode boundary cases.`);
