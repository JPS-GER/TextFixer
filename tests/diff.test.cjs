const assert = require('node:assert/strict');
const diff = require('../src/TextFixer/Web/diff.js');
const cases = [
  ['This is a tesst.', 'This is a test.'],
  ['One two three', 'One TWO three!'],
  ['Hallo Welt!\r\nDas ist falsch.', 'Hallo Welt!\r\nDas ist richtig.'],
  ['', 'inserted'], ['removed', ''], ['same', 'same'],
  ['a a a b a', 'a b a a a'], ['hello world', 'hello, world'],
  ['🙂 spelling eror', '🙂 spelling error'],
  ['x '.repeat(1600), 'y '.repeat(1600)],
];
let seed = 46;
const random = () => (seed = (seed * 1664525 + 1013904223) >>> 0) / 4294967296;
for (let i=0;i<300;i++) {
  const text = () => Array.from({length:Math.floor(random()*40)},()=>['one','two',' ', '\r\n', '.', 'ä', '🙂'][Math.floor(random()*7)]).join('');
  cases.push([text(),text()]);
}
for(const [before,after] of cases){
  const parts=diff(before,after);
  assert.equal(parts.map(p=>p.before).join(''),before,'original reconstruction');
  assert.equal(parts.map(p=>p.after).join(''),after,'corrected reconstruction');
  assert.equal(parts.map(p=>p.changed&&!p.accepted?p.before:p.after).join(''),after,'all accepted');
  parts.forEach(p=>p.accepted=false);
  assert.equal(parts.map(p=>p.changed&&!p.accepted?p.before:p.after).join(''),before,'all rejected');
}
console.log(`PASS: ${cases.length} diff cases, original/corrected and accept/reject reconstruction.`);
