"""Erzeugt die PNG-Ausgabeformate des Prüfsiegels „Learny Verified“ aus derselben Geometrie wie die SVG-Dateien (wwwroot/brand/).
Nur Entwicklungswerkzeug (Python + Pillow); die erzeugten PNGs liegen im Repository. Aufruf:  python tools/brand/make_assets.py
"""
import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), "..", "..", "src", "LearnCards.Web", "wwwroot", "brand")
SS = 4  # Supersampling

def mark(size, ring, fill, check, bg=None):
    s = size * SS
    img = Image.new("RGBA", (s, s), bg or (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    k = s / 64.0
    d.ellipse([4 * k, 4 * k, 60 * k, 60 * k], outline=ring, width=int(5 * k))
    d.ellipse([11 * k, 11 * k, 53 * k, 53 * k], fill=fill)
    pts = [(21 * k, 33.5 * k), (29 * k, 41.5 * k), (44 * k, 24.5 * k)]
    d.line(pts, fill=check, width=int(5.5 * k), joint="curve")
    for p in (pts[0], pts[-1]):
        d.ellipse([p[0] - 2.75 * k, p[1] - 2.75 * k, p[0] + 2.75 * k, p[1] + 2.75 * k], fill=check)
    return img.resize((size, size), Image.LANCZOS)

def font(bold, px):
    for f in (r"C:\Windows\Fonts\segoeuib.ttf" if bold else r"C:\Windows\Fonts\segoeui.ttf", r"C:\Windows\Fonts\arialbd.ttf" if bold else r"C:\Windows\Fonts\arial.ttf",
              "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf" if bold else "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"):
        if os.path.exists(f):
            return ImageFont.truetype(f, px)
    return ImageFont.load_default()

LIGHT = dict(ring=(29, 131, 121, 255), fill=(29, 131, 121, 255), check=(255, 255, 255, 255))
DARK = dict(ring=(95, 208, 195, 255), fill=(42, 167, 155, 255), check=(11, 31, 29, 255))

def lockup(height, theme, title, sub):
    w = int(height * 300 / 64)
    s = SS
    img = Image.new("RGBA", (w * s, height * s), (0, 0, 0, 0))
    m = mark(height * s, **theme)
    img.alpha_composite(m, (0, 0))
    d = ImageDraw.Draw(img)
    k = height * s / 64.0
    d.text((72 * k, 31 * k), "Learny Verified", font=font(True, int(23 * k)), fill=title, anchor="ls")
    d.text((72 * k, 49 * k), "Digitally Signed Credential", font=font(False, int(12.5 * k)), fill=sub, anchor="ls")
    return img.resize((w, height), Image.LANCZOS)

os.makedirs(OUT, exist_ok=True)
for size in (32, 64, 180, 192, 512):
    mark(size, **LIGHT).save(os.path.join(OUT, f"learny-verified-mark-{size}.png"))
mark(180, **LIGHT, bg=(255, 255, 255, 255)).save(os.path.join(OUT, "apple-touch-icon.png"))
mark(32, **LIGHT).save(os.path.join(OUT, "favicon-32.png"))
mark(64, **DARK).save(os.path.join(OUT, "learny-verified-mark-dark-64.png"))
lockup(128, LIGHT, (29, 131, 121, 255), (89, 101, 100, 255)).save(os.path.join(OUT, "learny-verified.png"))
lockup(128, DARK, (95, 208, 195, 255), (184, 199, 197, 255)).save(os.path.join(OUT, "learny-verified-dark.png"))

# Freigabegrafik (LinkedIn/Social, 1200x627): neutral — Siegel und Name, keine personenbezogenen Daten
W, H = 1200, 627
share = Image.new("RGBA", (W, H), (244, 247, 246, 255))
sd = ImageDraw.Draw(share)
sd.rectangle([0, 0, W, 18], fill=(29, 131, 121, 255))
share.alpha_composite(mark(220, **LIGHT), (90, 190))
sd.text((350, 300), "Learny Verified", font=font(True, 86), fill=(29, 131, 121, 255), anchor="ls")
sd.text((352, 360), "Digitally Signed Credential", font=font(False, 40), fill=(89, 101, 100, 255), anchor="ls")
sd.text((352, 440), "Open Badges 3.0 · CLR 2.0 · signiertes PDF", font=font(False, 32), fill=(89, 101, 100, 255), anchor="ls")
sd.text((90, 570), "Keine Zertifizierung durch 1EdTech oder LinkedIn.", font=font(False, 24), fill=(120, 132, 131, 255), anchor="ls")
share.convert("RGB").save(os.path.join(OUT, "learny-verified-share.png"))
print("ok")
