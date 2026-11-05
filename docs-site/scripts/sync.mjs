// Builds everything in docs-site that comes from somewhere else in the repo, so each fact has one source:
//   - brand assets and the font, from site/
//   - the rule reference, from the bundled style guide in styleguide/
//   - the release notes, from the GitHub releases (when reachable), docs/release-notes and src/FlareAI.Flare/whats-new.md
//   - the contributor area (/docs/dev/): docs/testing, PLAN.md, docs/flare-api-notes.md, docs/dev and CLAUDE.md
// Runs before `npm run dev` and `npm run build`. Everything it writes is git-ignored (see docs-site/.gitignore).
//
//   RELEASES_REQUIRED=1  fail when the GitHub releases can't be read (the deploy sets it, so the release notes page
//                        never silently gets shorter). GITHUB_TOKEN, when set, only raises the API rate limit.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SITE = path.resolve(HERE, '..');
const ROOT = path.resolve(SITE, '..');
const DOCS = path.join(SITE, 'src', 'content', 'docs');
const REPO = 'abmorrill4/house-style';
const GH = `https://github.com/${REPO}`;

const read = (p) => fs.readFileSync(p, 'utf8').replace(/\r\n/g, '\n');
const write = (p, text) => {
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p, text.endsWith('\n') ? text : text + '\n');
};
const rm = (p) => fs.rmSync(p, { recursive: true, force: true });
const q = (s) => JSON.stringify(s);

function frontmatter(fields, raw = []) {
  const lines = ['---'];
  for (const [k, v] of Object.entries(fields)) {
    if (v === undefined) continue;
    if (k === 'sidebar') lines.push('sidebar:', `  order: ${v}`);
    else lines.push(`${k}: ${typeof v === 'string' ? q(v) : v}`);
  }
  lines.push(...raw, '---', '');
  return lines.join('\n');
}

// ---------------------------------------------------------------------------------------------------------------------
// Brand assets
// ---------------------------------------------------------------------------------------------------------------------
function copyAssets() {
  const copy = (from, to) => {
    fs.mkdirSync(path.dirname(to), { recursive: true });
    fs.copyFileSync(path.join(ROOT, from), path.join(SITE, to));
  };
  copy('site/brand/mark.svg', 'src/assets/mark.svg');
  copy('site/brand/mark-white.svg', 'src/assets/mark-white.svg');
  copy('site/fonts/switzer-400.woff2', 'src/assets/fonts/switzer-400.woff2');
  copy('site/fonts/switzer-500.woff2', 'src/assets/fonts/switzer-500.woff2');
  copy('site/favicon.svg', 'public/favicon.svg');
  copy('site/favicon.ico', 'public/favicon.ico');
  copy('site/apple-touch-icon.png', 'public/apple-touch-icon.png');
  copy('site/og.png', 'public/og.png');
}

// ---------------------------------------------------------------------------------------------------------------------
// Rule reference
// ---------------------------------------------------------------------------------------------------------------------
const ENT = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };
const decode = (s) =>
  s.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (m, e) => {
    if (e[0] === '#') return String.fromCodePoint(e[1].toLowerCase() === 'x' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10));
    return ENT[e.toLowerCase()] ?? m;
  });
const text = (html) => decode(html.replace(/<[^>]*>/g, '')).replace(/\s+/g, ' ').trim();
const esc = (s) => s.replace(/([\\`*_\[\]<>|])/g, '\\$1');
const cell = (s) => esc(s).replace(/\n/g, ' ');
const slug = (s) => (s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'x');
const upper1 = (s) => s.charAt(0).toUpperCase() + s.slice(1);

const LEVELS = { error: 'Error', warning: 'Warning', suggestion: 'Suggestion', off: 'Off' };

function conditions(attrs) {
  const m = /MadCap:conditions="([^"]*)"/.exec(attrs || '');
  if (!m) return '';
  return m[1].split(/[\s,;]+/).filter(Boolean).map((c) => c.replace(/^(Content|Product)\./, '')).join(', ');
}

/** The default changes House Style ships to Google's rules: "google:topic/key" -> { setting, reason }. */
function readDefaults(guideDir) {
  const file = path.join(guideDir, 'Company', 'Google-overrides.htm');
  const map = new Map();
  if (!fs.existsSync(file)) return map;
  for (const row of read(file).match(/<tr>[\s\S]*?<\/tr>/g) || []) {
    const cells = [...row.matchAll(/<td>([\s\S]*?)<\/td>/g)].map((m) => text(m[1]));
    if (cells.length >= 3) map.set(cells[0], { setting: cells[1].toLowerCase(), reason: cells[2] });
  }
  return map;
}

function parseTopic(file, layer, defaults) {
  const html = read(file);
  const topic = slug(path.basename(file, '.htm'));
  const title = text((/<h1[^>]*>([\s\S]*?)<\/h1>/.exec(html) || [, topic])[1]);
  const intro = /<p>([\s\S]*?)<\/p>/.exec(html);
  const source = intro ? /href="(https:\/\/developers\.google\.com\/[^"]*)"/.exec(intro[1]) : null;
  const sections = [];
  let current = { heading: title, rules: [] };
  const tokens = html.matchAll(/<h2[^>]*>([\s\S]*?)<\/h2>|<li id="rule-([^"]+)"([^>]*)>((?:(?!<ul>|<\/li>)[\s\S])*)(?:<ul>([\s\S]*?)<\/ul>)?<\/li>|<table[^>]*>([\s\S]*?)<\/table>/g);
  const push = (rule) => {
    rule.id = `${layer}:${topic}/${rule.key}`;
    const d = layer === 'google' ? defaults.get(rule.id) : null;
    if (d) rule.defaultChange = d;
    current.rules.push(rule);
  };
  for (const m of tokens) {
    if (m[1] !== undefined) {
      if (current.rules.length) sections.push(current);
      current = { heading: text(m[1]), rules: [] };
    } else if (m[2] !== undefined) {
      const nested = [...(m[5] || '').matchAll(/<li[^>]*>([\s\S]*?)<\/li>/g)].map((x) => text(x[1]));
      const bad = nested.find((n) => /^bad:/i.test(n));
      const good = nested.find((n) => /^good:/i.test(n));
      push({ key: m[2], kind: 'guidance', level: '', text: text(m[4]), bad: bad && bad.replace(/^bad:\s*/i, ''), good: good && good.replace(/^good:\s*/i, ''), applies: conditions(m[3]) });
    } else if (m[6] !== undefined) {
      const rows = (m[6].match(/<tr[\s\S]*?<\/tr>/g) || []).map((r) => ({
        attrs: (/<tr([^>]*)>/.exec(r) || [, ''])[1],
        cells: [...r.matchAll(/<t[dh][^>]*>([\s\S]*?)<\/t[dh]>/g)].map((c) => text(c[1])),
      }));
      if (rows.length < 2) continue;
      const head = rows[0].cells.map((h) => h.toLowerCase());
      const col = (...names) => head.findIndex((h) => names.includes(h));
      const avoidC = col('avoid'), patC = col('pattern'), useC = col('use'), sevC = col('severity'), whyC = col('why'), exC = col('example');
      const termC = col('term'), ruleC = col('rule'), badC = col('bad'), goodC = col('good');
      for (const r of rows.slice(1)) {
        const idm = /id="rule-([^"]+)"/.exec(r.attrs);
        if (!idm) continue;
        const c = r.cells;
        const applies = conditions(r.attrs);
        if ((avoidC >= 0 || patC >= 0) && sevC >= 0) {
          const sev = (c[sevC] || 'warning').toLowerCase();
          push({ key: idm[1], kind: 'swap', level: LEVELS[sev] || upper1(sev), avoid: c[avoidC >= 0 ? avoidC : patC], pattern: avoidC < 0, use: useC >= 0 ? c[useC] : '', why: whyC >= 0 ? c[whyC] : '', example: exC >= 0 ? c[exC] : '', applies });
        } else if (termC >= 0 && ruleC >= 0) {
          push({ key: idm[1], kind: 'term', level: '', term: c[termC], text: c[ruleC], bad: badC >= 0 ? c[badC] : '', good: goodC >= 0 ? c[goodC] : '', applies });
        }
      }
    }
  }
  if (current.rules.length) sections.push(current);
  return { topic, title, sections, source: source && source[1], layer, count: sections.reduce((n, s) => n + s.rules.length, 0) };
}

function levelText(rule) {
  let lvl = rule.level || 'AI review';
  if (rule.defaultChange) {
    const s = rule.defaultChange.setting;
    lvl = s === 'off' ? 'Off by default' : `${LEVELS[s] || upper1(s)} by default`;
  }
  return lvl;
}

function ruleRow(rule) {
  const lines = [];
  if (rule.kind === 'swap') {
    const use = rule.use ? (rule.use === '(delete)' ? 'delete it' : `use \`${rule.use.replace(/`/g, "'")}\``) : 'no replacement';
    lines.push(`${rule.pattern ? 'Pattern' : 'Avoid'} \`${rule.avoid.replace(/`/g, "'")}\`, ${use}.`);
    if (rule.why) lines.push(esc(rule.why));
  } else if (rule.kind === 'term') {
    lines.push(`**${cell(rule.term)}.** ${cell(rule.text)}`);
  } else {
    lines.push(cell(rule.text));
  }
  if (rule.defaultChange) lines.push(`*House Style default:* ${cell(rule.defaultChange.reason)}`);
  const ex = [];
  if (rule.kind === 'swap' && rule.example) ex.push(`Bad: ${cell(rule.example)}`);
  if (rule.bad) ex.push(`Bad: ${cell(rule.bad)}`);
  if (rule.good) ex.push(`Good: ${cell(rule.good)}`);
  const body = lines.map((l) => l.replace(/\n/g, ' ')).join('<br>');
  return `| \`${rule.id}\` | ${levelText(rule)} | ${body} | ${ex.join('<br>')} | ${rule.applies ? cell(rule.applies) : ''} |`;
}

function buildRules() {
  const guideDir = path.join(ROOT, 'styleguide', 'Content', 'Resources', 'StyleGuide');
  const defaults = readDefaults(guideDir);
  const out = path.join(DOCS, 'reference', 'rules');
  rm(path.join(DOCS, 'reference'));
  const groups = [
    { layer: 'company', dir: 'Company', folder: 'starter', label: 'Starter rules', order: 1 },
    { layer: 'google', dir: 'Google', folder: 'google', label: 'Google developer documentation style guide', order: 2 },
  ];
  const summary = [];
  let total = 0;
  for (const g of groups) {
    const dir = path.join(guideDir, g.dir);
    const topics = fs.readdirSync(dir).filter((f) => f.endsWith('.htm')).sort((a, b) => a.localeCompare(b));
    const parsed = [];
    for (const f of topics) {
      if (f === 'Google-overrides.htm' || f === 'About.htm') continue;
      const t = parseTopic(path.join(dir, f), g.layer, defaults);
      if (!t.count) continue;
      parsed.push(t);
      const lines = [
        frontmatter({
          title: t.title,
          description: `${t.count} ${t.count === 1 ? 'rule' : 'rules'} from the ${g.layer === 'google' ? 'Google developer documentation style guide' : 'starter rules'}, with level and examples.`,
          editUrl: false,
        }),
      ];
      if (t.source) lines.push(`These rules are compiled from the Google developer documentation style guide page [${t.title}](${t.source}), which is licensed CC BY 4.0.\n`);
      lines.push('Turn a rule off or change its level in **House Style > Rules**. See [Browse and change rules](/docs/check-your-writing/browse-and-change-rules/).\n');
      for (const s of t.sections) {
        if (t.sections.length > 1 || s.heading !== t.title) lines.push(`## ${esc(s.heading)}\n`);
        lines.push('| Rule ID | Level | Rule | Example | Applies to |', '|---|---|---|---|---|', ...s.rules.map(ruleRow), '');
      }
      write(path.join(out, g.folder, `${t.topic}.md`), lines.join('\n'));
    }
    const n = parsed.reduce((a, t) => a + t.count, 0);
    total += n;
    summary.push({ ...g, parsed, count: n });
  }
  const idx = [
    frontmatter({
      title: 'Rule reference',
      description: `All ${total} rules in the style guide that comes with House Style, with their level and examples.`,
      editUrl: false,
    }),
    'This is every rule in the style guide that comes with House Style: the starter rules, and the Google developer documentation style guide. It is built from the guide itself, so it is always the same as the rules in **House Style > Rules**.',
    '',
    'Your team and your project can add rules and change these. Where rules disagree, a later source wins: Google, then the starter rules, then your team\'s guide, then the open project. See [What House Style does](/docs/get-started/what-house-style-does/).',
    '',
    '## How to read a rule',
    '',
    '- **Rule ID.** The permanent name of the rule, such as `google:tense/1`. Style guide owners and release notes use it, and the AI review cites it.',
    '- **Level.** *Error*, *Warning* or *Suggestion* for a rule with an exact fix. *AI review* marks guidance that the AI review applies. A rule marked *by default* has a level or an on/off state that House Style changes from Google\'s, for help topics, and the reason is shown. You can change it back.',
    '- **Applies to.** A rule with nothing here applies to every topic. Otherwise it applies only to that kind of content (such as Code, Steps or UI) or to that product.',
    '',
  ];
  for (const g of summary) {
    idx.push(`## ${g.label}`, '', `${g.count} rules.`, '');
    for (const t of g.parsed) idx.push(`- [${t.title}](/docs/reference/rules/${g.folder}/${t.topic}/) (${t.count})`);
    idx.push('');
  }
  write(path.join(out, 'index.md'), idx.join('\n'));
  console.log(`rules: ${total} rules on ${summary.reduce((n, g) => n + g.parsed.length, 0)} pages`);
}

// ---------------------------------------------------------------------------------------------------------------------
// Release notes
// ---------------------------------------------------------------------------------------------------------------------
const verKey = (v) => v.split('.').map((n) => parseInt(n, 10) || 0);
const verCmp = (a, b) => {
  const x = verKey(a), y = verKey(b);
  for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return y[i] - x[i];
  return 0;
};

/** Headings in a release body drop below the version heading, and the install boilerplate every release repeats is cut. */
function releaseBody(md) {
  let body = md.replace(/\r\n/g, '\n').trim();
  body = body.replace(/\n#{1,3} Install or update[\s\S]*$/, '');
  body = body.replace(/^# .*\n+/, '');
  body = body.replace(/^(#{1,2}) /gm, (m, h) => '#'.repeat(h.length + 2) + ' ');
  return body.trim();
}

async function fetchReleases() {
  const headers = { Accept: 'application/vnd.github+json', 'User-Agent': 'house-style-docs' };
  if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
  const all = [];
  for (let page = 1; page <= 5; page++) {
    const res = await fetch(`https://api.github.com/repos/${REPO}/releases?per_page=100&page=${page}`, { headers });
    if (!res.ok) throw new Error(`GitHub releases answered ${res.status}`);
    const batch = await res.json();
    all.push(...batch);
    if (batch.length < 100) break;
  }
  return all.filter((r) => !r.draft && !r.prerelease);
}

async function buildReleaseNotes() {
  const versions = new Map(); // version -> { date, body, from }
  const dir = path.join(ROOT, 'docs', 'release-notes');
  for (const f of fs.readdirSync(dir).filter((n) => /^\d+\.\d+\.\d+\.md$/.test(n))) {
    versions.set(f.slice(0, -3), { body: releaseBody(read(path.join(dir, f))), from: 'file' });
  }
  const wn = read(path.join(ROOT, 'src', 'FlareAI.Flare', 'whats-new.md'));
  for (const m of wn.matchAll(/^## (\d+\.\d+\.\d+)[^\n]*\n([\s\S]*?)(?=^## |\Z)/gm)) {
    if (!versions.has(m[1])) versions.set(m[1], { body: releaseBody(m[2]), from: 'whats-new' });
  }
  let note = '';
  try {
    for (const r of await fetchReleases()) {
      const v = r.tag_name.replace(/^v/, '');
      if (!/^\d+\.\d+\.\d+$/.test(v) || !r.body || !r.body.trim()) continue;
      versions.set(v, { body: releaseBody(r.body), date: r.published_at, from: 'github' });
    }
  } catch (err) {
    if (process.env.RELEASES_REQUIRED === '1') throw new Error(`Can't read the GitHub releases: ${err.message}`);
    note = `\n:::note\nThis copy was built without a connection to GitHub, so it lists only the versions kept in the repository.\n:::\n`;
    console.warn(`release notes: ${err.message}; using the files in the repository only`);
  }
  const fmt = (iso) => new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' });
  const out = [
    frontmatter({ title: 'Release notes', description: 'What changed in each version of House Style, newest first.', editUrl: false }),
    'Each version lists what changed for writers. House Style updates itself when you close Flare. See [Install and update](/docs/get-started/install/).',
    note,
  ];
  for (const v of [...versions.keys()].sort(verCmp)) {
    const e = versions.get(v);
    out.push(`## ${v}`, '');
    if (e.date) out.push(`*${fmt(e.date)}*`, '');
    out.push(e.body, '');
  }
  write(path.join(DOCS, 'release-notes.md'), out.join('\n'));
  console.log(`release notes: ${versions.size} versions`);
}

// ---------------------------------------------------------------------------------------------------------------------
// Contributor area: /docs/dev/
// ---------------------------------------------------------------------------------------------------------------------
const QUALITY = [
  ['docs/testing/TEST-PLAN.md', 'quality/test-plan', 1, 'Test plan'],
  ['docs/testing/BENCH.md', 'quality/bench', 2, 'Bench'],
  ['docs/testing/CORPUS.md', 'quality/corpus', 3, 'Test corpus'],
  ['docs/testing/STRESS-REPORT.md', 'quality/stress-report', 4, 'Stress report'],
  ['docs/testing/GOOGLE-GUIDE-REPORT.md', 'quality/google-guide-report', 5, 'Google guide report'],
  ['docs/testing/ai-cost.md', 'quality/ai-cost', 6, 'AI cost'],
  ['docs/testing/channels.md', 'quality/channels', 7, 'Release channels'],
  ['docs/testing/BUG-TEMPLATE.md', 'quality/bug-template', 8, 'Bug template'],
];
const DEVELOPERS = [
  ['docs/flare-api-notes.md', 'developers/flare-api-notes', 2, 'Flare API notes'],
  ['docs/dev/repo-map.md', 'developers/repo-map', 4, 'Repo map'],
  ['docs/dev/coding-standards.md', 'developers/coding-standards', 5, 'Coding standards'],
];

function section(md, headingRe) {
  const lines = md.split('\n');
  const start = lines.findIndex((l) => headingRe.test(l));
  if (start < 0) throw new Error(`No section matching ${headingRe} found`);
  const level = /^#+/.exec(lines[start])[0].length;
  let end = lines.length;
  for (let i = start + 1; i < lines.length; i++) {
    const m = /^(#+) /.exec(lines[i]);
    if (m && m[1].length <= level) { end = i; break; }
  }
  return lines.slice(start + 1, end).join('\n').trim();
}

function devPage(srcRel, slugPath, order, fallbackTitle, transform) {
  const md = read(path.join(ROOT, srcRel));
  const h1 = /^# (.+)$/m.exec(md);
  let body = md.replace(/^# .+\n+/, '');
  if (transform) body = transform(body);
  return { srcRel, slugPath, order, title: fallbackTitle || (h1 && h1[1]), body };
}

/** A relative link in a copied file points at another copied page, or else at the file on GitHub. */
function rewriteLinks(page, pages) {
  const bySrc = new Map(pages.map((p) => [p.srcRel, p]));
  const fix = (target) => {
    if (/^([a-z]+:|#|\/|mailto:)/i.test(target)) return target;
    const [file, hash] = target.split('#');
    if (!file) return target;
    const rel = path.posix.normalize(path.posix.join(path.posix.dirname(page.srcRel), decodeURI(file)));
    const hit = bySrc.get(rel);
    if (hit) return `/docs/dev/${hit.slugPath}/${hash ? '#' + hash : ''}`;
    const abs = path.join(ROOT, rel);
    if (!rel.startsWith('..') && fs.existsSync(abs)) return `${GH}/${fs.statSync(abs).isDirectory() ? 'tree' : 'blob'}/main/${rel}${hash ? '#' + hash : ''}`;
    return null;
  };
  // Inline links, outside code fences. A link to a file that doesn't exist here loses its link and keeps its text.
  let fenced = false;
  return page.body
    .split('\n')
    .map((line) => {
      if (/^\s*(```|~~~)/.test(line)) fenced = !fenced;
      if (fenced) return line;
      return line.replace(/(?<!!)\[([^\]]*)\]\(([^)\s]+)\)/g, (m, label, target) => {
        const fixed = fix(target);
        return fixed === null ? label : `[${label}](${fixed})`;
      });
    })
    .join('\n');
}

function buildDev() {
  const quality = path.join(DOCS, 'dev', 'quality');
  const developers = path.join(DOCS, 'dev', 'developers');
  rm(quality);
  rm(developers);

  const claude = read(path.join(ROOT, 'CLAUDE.md'));
  const plan = read(path.join(ROOT, 'PLAN.md'));
  const pages = [];
  for (const [src, slugPath, order, title] of QUALITY) pages.push(devPage(src, slugPath, order, title));
  pages.push(
    devPage('CLAUDE.md', 'developers/architecture', 1, 'Architecture', () =>
      [
        'The plugin\'s layout and the constraints every change must keep. This page is built from the "Map" and "Hard constraints" sections of `CLAUDE.md`, with the original design notes from `PLAN.md` at the end.',
        '',
        '## Map',
        '',
        section(claude, /^## Map/),
        '',
        '## Hard constraints',
        '',
        section(claude, /^## Hard constraints/),
        '',
        '## Original design notes',
        '',
        'From `PLAN.md`, written at the start of the project. Names and target frameworks have changed since. The sections above are current.',
        '',
        section(plan, /^## 1\. Architecture/).replace(/^#{3,}/gm, '####'),
      ].join('\n')),
  );
  pages.push(
    devPage('CLAUDE.md', 'developers/security-invariants', 3, 'Security invariants', () =>
      ['These rules come from the 2026-10-22 security audit. This page is built from `CLAUDE.md`.', '', section(claude, /^## Security invariants/)].join('\n')),
  );
  for (const [src, slugPath, order, title] of DEVELOPERS) pages.push(devPage(src, slugPath, order, title));

  for (const p of pages) {
    const sourceUrl = p.srcRel === 'CLAUDE.md' ? `${GH}/blob/main/CLAUDE.md` : `${GH}/blob/main/${p.srcRel}`;
    const body = rewriteLinks(p, pages);
    const first = body.split('\n').find((l) => l.trim() && !l.startsWith('#') && !l.startsWith('>') && !l.startsWith('|') && !l.startsWith('```'));
    const desc = (first || p.title).replace(/\[([^\]]*)\]\([^)]*\)/g, '$1').replace(/[`*_]/g, '').slice(0, 200);
    const editUrl = p.srcRel === 'CLAUDE.md' ? `${GH}/edit/main/CLAUDE.md` : `${GH}/edit/main/${p.srcRel}`;
    write(
      path.join(DOCS, 'dev', `${p.slugPath}.md`),
      frontmatter({
        title: p.title,
        description: desc,
        editUrl,
        pagefind: false,
        sidebar: p.order,
      }, ['head:', '  - tag: meta', '    attrs:', '      name: robots', '      content: noindex']) +
        `*Built from [\`${p.srcRel}\`](${sourceUrl}) when the site is built. Edit that file, not this page.*\n\n` +
        body,
    );
  }
  console.log(`dev: ${pages.length} pages`);
}

copyAssets();
buildRules();
await buildReleaseNotes();
buildDev();
