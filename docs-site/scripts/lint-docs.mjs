// Lints the user guide pages with House Style itself: the style engine of the plugin, run through tools/FlareAI.Mcp,
// against the bundled style guide. It reports findings and never fails the build (the exit code is 0 unless the checker
// can't run). Windows only, because the checker is a .NET Framework program.
//
//   dotnet build tools/FlareAI.Mcp -c Release
//   node scripts/lint-docs.mjs [--exe <FlareAI.Mcp.exe>] [--json <file>] [files...]
//
// Pages that are generated or copied from elsewhere (rule reference, release notes, the contributor area) are not linted.
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const SITE = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const ROOT = path.resolve(SITE, '..');
const DOCS = path.join(SITE, 'src', 'content', 'docs');

const args = process.argv.slice(2);
const opt = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 ? args.splice(i, 2)[1] : fallback;
};
const exe = opt('--exe', path.join(ROOT, 'tools', 'FlareAI.Mcp', 'bin', 'Release', 'net48', 'FlareAI.Mcp.exe'));
const jsonOut = opt('--json', '');
const USER_DIRS = ['get-started', 'check-your-writing', 'ai-assists', 'team-style-guide', 'settings-and-privacy', 'troubleshooting'];

const walk = (dir) =>
  fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(path.join(dir, e.name)) : [path.join(dir, e.name)]));
const files = args.length
  ? args.map((f) => path.resolve(f))
  : [path.join(DOCS, 'index.mdx'), ...USER_DIRS.flatMap((d) => walk(path.join(DOCS, d)))].filter((f) => /\.mdx?$/.test(f));

/** Plain-text blocks of a page: one per paragraph, list item, table cell row or heading, with the line each starts on. */
function blocks(file) {
  const lines = fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n').split('\n');
  let i = 0;
  if (lines[0] === '---') {
    i = lines.indexOf('---', 1) + 1;
  }
  const out = [];
  let fenced = false;
  let para = null;
  const flush = () => {
    if (para) out.push(para);
    para = null;
  };
  const stripTags = (s) => {
    let prev;
    do {
      prev = s;
      s = s.replace(/<[^>]*>/g, '');
    } while (s !== prev);
    return s.replace(/[<>]/g, '');
  };
  const clean = (s) =>
    stripTags(
      s
        .replace(/`[^`]*`/g, '')
        .replace(/!\[[^\]]*\]\([^)]*\)/g, '')
        .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1'),
    )
      .replace(/[*_]{1,3}([^*_]+)[*_]{1,3}/g, '$1')
      .replace(/\s+/g, ' ')
      .trim();
  for (; i < lines.length; i++) {
    const raw = lines[i];
    if (/^\s*```/.test(raw)) { flush(); fenced = !fenced; continue; }
    if (fenced || /^import |^:::|^\s*$|^\|[\s:|-]+\|$/.test(raw)) { flush(); continue; }
    const h = /^(#{1,6})\s+(.*)$/.exec(raw);
    if (h) { flush(); out.push({ line: i + 1, text: clean(h[2]), heading: true }); continue; }
    if (raw.startsWith('|')) {
      flush();
      for (const c of raw.split('|').slice(1, -1)) out.push({ line: i + 1, text: clean(c) });
      continue;
    }
    const li = /^\s*(?:[-*]|\d+\.)\s+(.*)$/.exec(raw);
    if (li) { flush(); para = { line: i + 1, text: li[1] }; continue; }
    if (para) para.text += ' ' + raw.trim();
    else para = { line: i + 1, text: raw.trim() };
  }
  flush();
  return out
    .map((b) => ({ ...b, text: clean(b.text) }))
    .filter((b) => b.text.split(' ').length >= 3);
}

if (!fs.existsSync(exe)) {
  console.error(`The checker isn't built: ${exe}\nRun: dotnet build tools/FlareAI.Mcp -c Release`);
  process.exit(2);
}

const child = spawn(exe, ['--guide', path.join(ROOT, 'styleguide')], { stdio: ['pipe', 'pipe', 'inherit'] });
const pending = new Map();
let buffer = '';
child.stdout.setEncoding('utf8');
child.stdout.on('data', (d) => {
  buffer += d;
  let n;
  while ((n = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, n);
    buffer = buffer.slice(n + 1);
    if (!line.trim()) continue;
    const msg = JSON.parse(line);
    const p = pending.get(msg.id);
    if (p) { pending.delete(msg.id); msg.error ? p.reject(new Error(msg.error.message)) : p.resolve(msg.result); }
  }
});
let nextId = 1;
const rpc = (method, params) =>
  new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });

await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'lint-docs', version: '1' } });

const findings = [];
for (const file of files) {
  const bs = blocks(file);
  if (!bs.length) continue;
  const res = await rpc('tools/call', {
    name: 'lint',
    arguments: { texts: bs.map((b) => b.text), headings: bs.flatMap((b, i) => (b.heading ? [i] : [])) },
  });
  for (const f of res.structuredContent.findings) {
    const b = bs[f.index];
    findings.push({ file: path.relative(ROOT, file).replace(/\\/g, '/'), line: b.line, severity: f.severity, match: f.match, replacement: f.replacement, rule: f.rule_id, message: f.message });
  }
}
child.stdin.end();

for (const f of findings) {
  const level = f.severity === 'error' ? 'error' : 'warning';
  console.log(`${f.file}:${f.line}: ${f.severity}: "${f.match}"${f.replacement ? ` -> "${f.replacement}"` : ''} (${f.rule}) ${f.message}`);
  if (process.env.GITHUB_ACTIONS) console.log(`::${level} file=${f.file},line=${f.line},title=House Style ${f.rule}::${f.message.replace(/[\r\n]+/g, ' ')}`);
}
console.log(`\n${findings.length} findings in ${files.length} pages.`);
if (jsonOut) fs.writeFileSync(jsonOut, JSON.stringify(findings, null, 1));
