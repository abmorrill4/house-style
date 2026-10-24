"""Builds the House Style brand assets: site/brand/*.svg, site/favicon.svg, favicon.ico, apple-touch-icon.png, og.png.
Run: python scripts/build-brand.py   (dev only; needs fonttools, brotli, pillow and Microsoft Edge for rasterising)
The brand rules are in site/brand/README.md."""
import subprocess, pathlib, tempfile
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from PIL import Image

REPO = pathlib.Path(__file__).resolve().parents[1]
SITE = REPO / "site"
BRAND = SITE / "brand"; BRAND.mkdir(exist_ok=True)
OUT = pathlib.Path(tempfile.mkdtemp())  # scratch for screenshots and the ICO frames
EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"

TEAL, TEAL_DARK, INK, PAPER, NIGHT = "#0d7480", "#5cc3ce", "#1f3038", "#f7f9fa", "#10191e"

# The mark on a 64 grid: a house-shaped page, two lines of text cut out of it.
MARK_D = ("M32 5 59 28v31H5V28z"
          "M19 35a3 3 0 0 0 0 6h26a3 3 0 0 0 0-6z"
          "M19 45a3 3 0 0 0 0 6h14a3 3 0 0 0 0-6z")
MARK_TOP, MARK_BOTTOM = 5, 59

def mark_svg(fill):
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><path fill="{fill}" fill-rule="evenodd" d="{MARK_D}"/></svg>\n'

def favicon_svg(rx=14):
    # Tile with the mark in white; the cut-out lines show the tile through.
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">'
            f'<rect width="64" height="64" rx="{rx}" fill="{TEAL}"/>'
            f'<path fill="#fff" fill-rule="evenodd" transform="translate(9.6 9.6) scale(.7)" d="{MARK_D}"/></svg>\n')

def wordmark_paths(text, size):
    """Outlines `text` in Switzer 500 at font size `size`, baseline at y=0. Returns (path d, width, cap height, descent below the baseline)."""
    font = TTFont(REPO / "site" / "fonts" / "switzer-500.woff2")
    gs, cmap, upm = font.getGlyphSet(), font.getBestCmap(), font["head"].unitsPerEm
    s = size / upm
    tracking = -0.02 * upm  # matches the site's display tracking
    pen, x = SVGPathPen(gs), 0.0
    # ponytail: no GPOS kerning; Switzer's default spacing reads fine for these two words.
    for ch in text:
        name = cmap[ord(ch)]
        gs[name].draw(TransformPen(pen, (s, 0, 0, -s, x * s, 0)))
        x += gs[name].width + tracking
    cap = font["OS/2"].sCapHeight * s
    return pen.getCommands(), (x - tracking) * s, cap, -font["hhea"].descent * s

def lockup_svg(mark_fill, text_fill):
    d, w, cap, desc = wordmark_paths("House Style", 100)
    mh = cap * 1.4                       # mark drawn height is 1.4x the cap height
    k = mh / (MARK_BOTTOM - MARK_TOP)
    gap = cap * 0.45
    mark_w = 54 * k                      # mark spans x 5..59
    total = mark_w + gap + w
    top = -cap - (mh - cap) / 2          # centre the mark on the caps
    height = max(mh, desc - top)         # room for the descender of the "y"
    return ('<svg xmlns="http://www.w3.org/2000/svg" '
            f'viewBox="0 {top:.1f} {total:.1f} {height:.1f}">'
            f'<path fill="{mark_fill}" fill-rule="evenodd" transform="translate({-5*k:.2f} {top - MARK_TOP*k:.2f}) scale({k:.4f})" d="{MARK_D}"/>'
            f'<path fill="{text_fill}" transform="translate({mark_w + gap:.2f} 0)" d="{d}"/></svg>\n')

def shoot(html, png, w, h, transparent=True):
    page = OUT / "_shot.html"; page.write_text(html, encoding="utf-8")
    args = [EDGE, "--headless=new", "--disable-gpu", "--hide-scrollbars", f"--window-size={w},{h}",
            f"--screenshot={png}", page.as_uri()]
    if transparent: args.insert(3, "--default-background-color=00000000")
    subprocess.run(args, check=True, capture_output=True)
    page.unlink()

def raster_icon(size, png, rx=14):
    svg = favicon_svg(rx).replace("<svg ", f'<svg width="{size}" height="{size}" ')
    shoot(f"<html><body style='margin:0'>{svg}</body></html>", png, size, size)

def og_image(png):
    font = (REPO / "site" / "fonts" / "switzer-500.woff2").as_uri()
    lock = lockup_svg("#fff", "#fff").replace("<svg ", '<svg height="64" style="align-self:flex-start" ')
    html = f"""<html><head><style>
@font-face{{font-family:S;src:url({font})}}
body{{margin:0;width:1200px;height:630px;background:{TEAL};color:#fff;font-family:S;box-sizing:border-box;
padding:72px 80px;display:flex;flex-direction:column;justify-content:space-between}}
h1{{font-size:84px;line-height:1;letter-spacing:-.03em;font-weight:500;margin:0;max-width:980px}}
p{{font-size:30px;margin:0;opacity:.85}}</style></head><body>
{lock}<h1>Lint your Flare topics against your style guide</h1><p>gethousestyle.com</p></body></html>"""
    shoot(html, png, 1200, 630, transparent=False)

if __name__ == "__main__":
    for name, svg in {"mark.svg": mark_svg(TEAL), "mark-ink.svg": mark_svg(INK), "mark-white.svg": mark_svg("#fff"),
                      "lockup.svg": lockup_svg(TEAL, INK), "lockup-white.svg": lockup_svg("#fff", "#fff")}.items():
        (BRAND / name).write_text(svg, newline="\n")
    (SITE / "favicon.svg").write_text(favicon_svg(), newline="\n")
    for n in (16, 32, 48):
        raster_icon(n, OUT / f"icon-{n}.png")
    Image.open(OUT / "icon-48.png").save(SITE / "favicon.ico", sizes=[(16, 16), (32, 32), (48, 48)],
                                         append_images=[Image.open(OUT / f"icon-{n}.png") for n in (16, 32)])
    raster_icon(180, SITE / "apple-touch-icon.png", rx=0)  # iOS rounds the corners itself
    og_image(SITE / "og.png")
