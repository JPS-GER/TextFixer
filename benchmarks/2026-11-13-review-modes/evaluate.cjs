// Scores results.json against the planted mistakes in texts.json.
const fs = require('fs'), path = require('path'), vm = require('vm');
const web = path.join(__dirname, '../../src/TextFixer/Web/');
const ctx = {}; vm.createContext(ctx);
for (const f of ['diff.js', 'review-model.js']) vm.runInContext(fs.readFileSync(web + f, 'utf8'), ctx);
const { textFixerDiff, reviewParts, minimalTextEdit } = ctx;
const reviewEdits = ctx.reviewEdits, reviewRevised = ctx.reviewRevised;

const texts = JSON.parse(fs.readFileSync(path.join(__dirname, 'texts.json'), 'utf8'));
const results = JSON.parse(fs.readFileSync(path.join(__dirname, 'results.json'), 'utf8'));
const order = ['spelling', 'grammar', 'punctuation', 'wording'];

// Same merge as focusParts in app.js.
function merge(original, tabs) {
  const clash = (a, b) => a.start === a.end && b.start === b.end ? a.start === b.start : a.start === a.end ? b.start < a.start && a.start < b.end : b.start === b.end ? a.start < b.start && b.start < a.end : a.start < b.end && b.start < a.end;
  const kept = [];
  // A change another tab already made (same text in the same range) is a duplicate, not a second edit.
  const duplicate=p=>{let out='',pos=p.start;for(const k of kept.filter(k=>k.start>=p.start&&k.end<=p.end).sort((a,b)=>a.start-b.start||a.end-b.end)){out+=original.slice(pos,k.start)+k.after;pos=Math.max(pos,k.end);}return out+original.slice(pos,p.end)===p.after;};
  for (const focus of order) {
    const t = tabs.find(x => x.focus === focus); if (!t || t.error) continue;
    for (const p of reviewParts(original, t.corrected, [])) {
      if (!p.changed || duplicate(p)) continue;
      if (!kept.some(k => clash(k, p))) { kept.push({ ...p, category: focus }); continue; }
      const e = minimalTextEdit(p.start, p.before, p.after);
      if (e && !kept.some(k => clash(k, e))) kept.push({ ...e, changed: true, accepted: true, category: focus });
    }
  }
  kept.sort((a, b) => a.start - b.start || a.end - b.end);
  let out = '', pos = 0;
  for (const k of kept) { out += original.slice(pos, k.start) + k.after; pos = Math.max(pos, k.end); }
  return { text: out + original.slice(pos), changes: kept };
}
const norm = s => s.replace(/\r\n/g, '\n');
function score(original, final, fixes) {
  const fixed = fixes.filter(([wrong, right]) => norm(final).includes(right) && (wrong === right || !norm(final).includes(wrong) || right.includes(wrong)));
  const units = reviewParts(original, final, []).filter(p => p.changed);
  const unexplained = units.filter(u => !fixes.some(([wrong]) => wrong.includes(u.before.trim()) && u.before.trim()));
  return { fixed: fixed.length, total: fixes.length, missed: fixes.filter(f => !fixed.includes(f)).map(f => f[0] + ' -> ' + f[1]), changes: units.length, other: unexplained.map(u => `[${u.before}] -> [${u.after}]`) };
}

const rows = [];
let sum = { singleMs: 0, multiMs: 0, firstMs: 0, singleFixed: 0, multiFixed: 0, total: 0, singleFind: 0, multiFind: 0, singleOther: 0, multiOther: 0 };
for (const r of results) {
  const t = texts.find(x => x.id === r.id), original = t.text;
  if (r.single.error) { console.log(r.id, 'single error', r.single.error); continue; }
  const multiMs = r.tabs.reduce((a, x) => a + x.ms, 0);
  const merged = merge(original, r.tabs);
  const worth = r.tabs.find(x => x.focus === 'worth');
  const s = score(original, r.single.corrected, t.fixes), m = score(original, merged.text, t.fixes);
  const row = {
    id: r.id, model: r.model, singleMs: r.single.ms, multiMs, firstTabMs: r.tabs[0].ms,
    single: s, multi: m,
    singleFindings: r.single.findings.length, multiFindings: worth && !worth.error ? worth.findings.length : 'error',
    tabErrors: r.tabs.filter(x => x.error).map(x => x.focus + ': ' + x.error),
    tabMs: Object.fromEntries(r.tabs.map(x => [x.focus, x.ms])),
    singleFindingText: r.single.findings.map(f => f.Kind + ': ' + f.Explanation),
    multiFindingText: worth && !worth.error ? worth.findings.map(f => f.Kind + ': ' + f.Explanation) : [],
  };
  rows.push(row);
  sum.singleMs += r.single.ms; sum.multiMs += multiMs; sum.firstMs += r.tabs[0].ms;
  sum.singleFixed += s.fixed; sum.multiFixed += m.fixed; sum.total += t.fixes.length;
  sum.singleFind += row.singleFindings; sum.multiFind += typeof row.multiFindings === 'number' ? row.multiFindings : 0;
  sum.singleOther += s.other.length; sum.multiOther += m.other.length;
}
if (process.argv[2]) { const r = results.find(x => x.id === process.argv[2]); const t = texts.find(x => x.id === r.id); console.log(merge(t.text, r.tabs).text); } else console.log(JSON.stringify({ rows, sum }, null, 2));
