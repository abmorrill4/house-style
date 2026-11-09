// Writes dist/llms.txt (an index) and dist/llms-full.txt (the user guide in one file) after the build, from the same
// Markdown the pages are built from. The contributor area (/docs/dev/) and the 404 page stay out. The rule reference is
// listed in llms.txt only, because it is long. See https://llmstxt.org/.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SITE = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const DOCS = path.join(SITE, 'src', 'content', 'docs');
const ORIGIN = 'https://gethousestyle.com/docs';
const VIDEOS = JSON.parse(fs.readFileSync(path.join(SITE, 'src', 'data', 'videos.json'), 'utf8'));

/** MDX to plain Markdown: drops import lines and turns each <Video slug="..." /> into a link to the video. */
function plain(body) {
  return body
    .replace(/^import .*;\n/gm, '')
    .replace(/<Video slug="([^"]+)" \/>/g, (tag, slug) => {
      const v = VIDEOS.find((x) => x.slug === slug);
      return v ? `Video: [${v.title}](https://www.youtube.com/watch?v=${v.youtubeId}) (${v.duration})` : '';
    })
    .replace(/\n{3,}/g, '\n\n');
}

const walk = (dir) =>
  fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(path.join(dir, e.name)) : [path.join(dir, e.name)]));

function page(file) {
  const rel = path.relative(DOCS, file).replace(/\\/g, '/').replace(/\.mdx?$/, '');
  const raw = fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n');
  const m = /^---\n([\s\S]*?)\n---\n([\s\S]*)$/.exec(raw);
  const fm = m ? m[1] : '';
  const body = plain(m ? m[2] : raw);
  const get = (k) => {
    const x = new RegExp(`^${k}:\\s*(.*)$`, 'm').exec(fm);
    if (!x) return '';
    try { return JSON.parse(x[1]); } catch { return x[1].replace(/^['"]|['"]$/g, ''); }
  };
  const slug = rel === 'index' ? '' : rel.replace(/\/index$/, '') + '/';
  const order = /order:\s*(-?\d+)/.exec(fm);
  return { rel, slug, url: `${ORIGIN}/${slug}`, title: get('title'), description: get('description'), body, order: order ? +order[1] : 999 };
}

const GROUPS = [
  ['get-started', 'Get started'],
  ['check-your-writing', 'Check your writing'],
  ['ai-assists', 'AI assists'],
  ['team-style-guide', 'Team style guide'],
  ['settings-and-privacy', 'Settings and privacy'],
  ['troubleshooting', 'Troubleshoot'],
];

const pages = walk(DOCS)
  .filter((f) => /\.mdx?$/.test(f))
  .map(page)
  .filter((p) => !p.rel.startsWith('dev') && p.rel !== '404');

const out = ['# House Style', '', '> House Style lints MadCap Flare topics against the Google developer documentation style guide and a team style guide, and lists the issues it finds. Every change is a Flare tracked change.', ''];
for (const [dir, label] of GROUPS) {
  const list = pages.filter((p) => p.rel === dir || p.rel.startsWith(dir + '/')).sort((a, b) => a.order - b.order || a.title.localeCompare(b.title));
  out.push(`## ${label}`, '', ...list.map((p) => `- [${p.title}](${p.url})${p.description ? ': ' + p.description : ''}`), '');
}
out.push(
  '## Optional',
  '',
  `- [Rule reference](${ORIGIN}/reference/rules/): Every rule in the style guide that comes with House Style.`,
  `- [Release notes](${ORIGIN}/release-notes/): What changed in each version.`,
  `- [Complete user guide in one file](${ORIGIN}/llms-full.txt)`,
  '',
);
fs.writeFileSync(path.join(SITE, 'dist', 'llms.txt'), out.join('\n'));

const full = [];
for (const [dir] of GROUPS) {
  for (const p of pages.filter((x) => x.rel === dir || x.rel.startsWith(dir + '/')).sort((a, b) => a.order - b.order || a.title.localeCompare(b.title))) {
    full.push(`# ${p.title}\n\nSource: ${p.url}\n\n${p.body.trim()}\n`);
  }
}
const notes = pages.find((p) => p.rel === 'release-notes');
if (notes) full.push(`# ${notes.title}\n\nSource: ${notes.url}\n\n${notes.body.trim()}\n`);
fs.writeFileSync(path.join(SITE, 'dist', 'llms-full.txt'), '# House Style user guide\n\n' + full.join('\n'));
console.log(`llms: ${pages.length} pages indexed, ${full.length} in llms-full.txt`);
