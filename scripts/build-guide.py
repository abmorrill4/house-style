"""Builds the user guide (docs/user-guide, a Flare project) into site/guide/ as plain pages in the site's own layout.

The guide's topics are plain XHTML: the only Flare markup is MadCap:xref and MadCap:variable, so no Flare build is
needed and the Pages workflow can run this on every push. Order comes from Project/TOCs/Main.fltoc.

    python3 scripts/build-guide.py            # writes site/guide/*.html (git-ignored; published by pages.yml)
"""
import html
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GUIDE = ROOT / "docs" / "user-guide"
OUT = ROOT / "site" / "guide"

PAGE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title} · House Style User Guide</title>
<meta name="color-scheme" content="light dark">
<link rel="icon" href="../favicon.ico" sizes="48x48">
<link rel="icon" href="../favicon.svg" type="image/svg+xml">
<link rel="apple-touch-icon" href="../apple-touch-icon.png">
<meta property="og:image" content="https://gethousestyle.com/og.png">
<meta name="twitter:card" content="summary_large_image">
<link rel="stylesheet" href="../style.css">
<script>document.documentElement.classList.add('js')</script>
</head>
<body>
<a class="skip" href="#main">Skip to content</a>
<header class="site-head">
  <div class="wrap">
    <a class="wordmark" href="../" aria-label="House Style home"><svg viewBox="0 0 64 64" aria-hidden="true" focusable="false"><path fill-rule="evenodd" d="M32 5 59 28v31H5V28zM19 35a3 3 0 0 0 0 6h26a3 3 0 0 0 0-6zM19 45a3 3 0 0 0 0 6h14a3 3 0 0 0 0-6z"/></svg>House Style</a>
    <nav class="site-nav" aria-label="Main">
      <a class="hide-sm" href="./" aria-current="page">User guide</a>
      <a href="../install.html">Install</a>
      <a class="hide-sm" href="../privacy.html">Privacy</a>
      <a class="hide-sm" href="https://github.com/abmorrill4/house-style">GitHub</a>
      <a class="btn btn-primary" href="https://github.com/abmorrill4/house-style/releases/latest">Download</a>
    </nav>
  </div>
</header>
<main id="main">
  <section class="doc-hero"><div class="wrap"><h1>{title}</h1></div></section>
  <div class="wrap">
    <section class="doc-section guide">
      <nav class="guide-toc" aria-label="User guide">
{toc}
      </nav>
      <div class="body">
{body}
      </div>
    </section>
  </div>
</main>
<footer class="site-foot">
  <div class="wrap">
    <div class="row">
      <span class="what">House Style for MadCap Flare</span>
      <nav aria-label="Footer">
        <a href="https://github.com/abmorrill4/house-style">GitHub</a>
        <a href="https://github.com/abmorrill4/house-style/releases">Release notes</a>
        <a href="../privacy.html">Privacy</a>
      </nav>
      <span class="right">MIT licensed</span>
    </div>
  </div>
</footer>
</body>
</html>
"""


def variables():
    values = {}
    for f in (GUIDE / "Project" / "VariableSets").glob("*.flvar"):
        for v in ET.parse(f).getroot().iter("Variable"):
            values[f.stem + "." + v.get("Name")] = v.get("EvaluatedDefinition") or (v.text or "")
    return values


def page_name(topic):
    """overview.htm is the guide's front page."""
    return "index.html" if topic == "overview.htm" else topic[:-4] + ".html"


def convert(text, values):
    title = html.unescape(re.search(r"<title>(.*?)</title>", text, re.S).group(1).strip())
    body = re.search(r"<body[^>]*>(.*)</body>", text, re.S).group(1)
    body = re.sub(r"\s*<h1[^>]*>.*?</h1>", "", body, count=1, flags=re.S)
    body = re.sub(r'<MadCap:variable name="([^"]+)"\s*/>', lambda m: html.escape(values.get(m.group(1), m.group(1))), body)
    body = re.sub(r"<MadCap:xref ", "<a ", body).replace("</MadCap:xref>", "</a>")
    body = re.sub(r'href="([^":#]+)\.htm(#[^"]*)?"', lambda m: 'href="' + page_name(m.group(1) + ".htm") + (m.group(2) or "") + '"', body)
    if "MadCap:" in body:
        raise SystemExit("Unsupported Flare markup left in a topic: " + re.search(r"<MadCap:\w+", body).group(0))
    return title, body.strip()


def main():
    values = variables()
    topics = [e.get("Link").rsplit("/", 1)[-1] for e in ET.parse(GUIDE / "Project" / "TOCs" / "Main.fltoc").getroot().iter("TocEntry")]
    pages = [(t,) + convert((GUIDE / "Content" / t).read_text(encoding="utf-8"), values) for t in topics]
    OUT.mkdir(parents=True, exist_ok=True)
    for topic, title, body in pages:
        toc = "\n".join('        <a href="{0}"{1}>{2}</a>'.format(page_name(t), ' aria-current="page"' if t == topic else "", html.escape(n))
                        for t, n, _ in pages)
        (OUT / page_name(topic)).write_text(PAGE.format(title=html.escape(title), toc=toc, body=body), encoding="utf-8")
    print("Wrote %d pages to %s" % (len(pages), OUT))


if __name__ == "__main__":
    sys.exit(main())
