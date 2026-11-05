// Writes a small redirect page for each page of the old user guide (gethousestyle.com/guide/...), pointing at the
// matching page of the new documentation (gethousestyle.com/docs/...). The plugin's Help button and old links keep working.
//
//   node scripts/guide-redirects.mjs <output folder>      (pages.yml passes site/guide)
//
// Each target is checked against the pages in src/content/docs, so a renamed page fails the build instead of leaving a
// dead redirect.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SITE = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const DOCS = path.join(SITE, 'src', 'content', 'docs');

// old topic file name (as built by scripts/build-guide.py, overview.htm is index.html) -> new page
export const MAP = {
  'index.html': 'get-started/what-house-style-does',
  'overview.html': 'get-started/what-house-style-does',
  'install.html': 'get-started/install',
  'settings.html': 'get-started/set-up',
  'check-style.html': 'check-your-writing/lint-a-topic',
  'editor.html': 'check-your-writing/work-in-the-editor',
  'rewrite.html': 'ai-assists/rewrite-text',
  'check-project.html': 'check-your-writing/lint-a-project',
  'assists.html': 'ai-assists/draft-metadata-and-alt-text',
  'style-rules.html': 'check-your-writing/browse-and-change-rules',
  'requests.html': 'team-style-guide/ask-for-a-rule-change',
  'style-guide.html': 'team-style-guide/maintain-the-style-guide',
  'team.html': 'team-style-guide/set-up-a-team-style-guide',
  'publish-guide.html': 'team-style-guide/publish-to-confluence',
  'llm-export.html': 'ai-assists/export-for-ai-assistants',
  'models.html': 'settings-and-privacy/models-and-cost',
  'troubleshooting.html': 'troubleshooting',
};

const exists = (slug) => [`${slug}.md`, `${slug}.mdx`, `${slug}/index.md`, `${slug}/index.mdx`].some((f) => fs.existsSync(path.join(DOCS, f)));

const page = (url) => `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>This page has moved</title>
<meta name="robots" content="noindex">
<link rel="canonical" href="${url}">
<meta http-equiv="refresh" content="0; url=${url}">
<script>location.replace(${JSON.stringify(url)} + location.hash)</script>
</head>
<body>
<p>The House Style user guide has moved. Go to <a href="${url}">${url}</a>.</p>
</body>
</html>
`;

const out = process.argv[2];
if (!out) {
  console.error('Usage: node scripts/guide-redirects.mjs <output folder>');
  process.exit(2);
}
fs.mkdirSync(out, { recursive: true });
for (const [file, slug] of Object.entries(MAP)) {
  if (!exists(slug)) throw new Error(`${file} redirects to ${slug}, but that page doesn't exist in docs-site/src/content/docs.`);
  fs.writeFileSync(path.join(out, file), page(`https://gethousestyle.com/docs/${slug}/`));
}
console.log(`Wrote ${Object.keys(MAP).length} redirect pages to ${out}`);
