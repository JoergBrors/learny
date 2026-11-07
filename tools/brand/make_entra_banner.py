"""Erzeugt das Bannerlogo fuer die Entra-External-ID-Anmeldeseiten aus der vorhandenen Learny-Marke.

Geometrie und Farben stammen aus dem Bestand (Components/Layout/MainLayout.razor: Karten-Symbol, wwwroot/app.css: --brand-600, --ink).
Es entsteht keine neue Markenidentitaet. Graph-Grenze fuer bannerLogo: PNG/JPEG, hoechstens 36 x 245 Pixel.
Nur Entwicklungswerkzeug (Python + Pillow); das Ergebnis liegt im Repository:  python tools/brand/make_entra_banner.py
"""
import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), "..", "..", "infra", "entra-external-id", "branding", "banner-logo.png")
SS = 8                      # Supersampling
H = 36                      # Hoehe (Graph-Maximum)
BRAND = (31, 140, 130, 255)  # --brand-600 #1F8C82
INK = (41, 50, 60, 255)      # --ink #29323C
WHITE = (255, 255, 255, 255)
TEXT = "Learny"


def font(px):
    for f in (r"C:\Windows\Fonts\segoeuib.ttf", r"C:\Windows\Fonts\arialbd.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"):
        if os.path.exists(f):
            return ImageFont.truetype(f, px)
    return ImageFont.load_default()


def banner():
    fnt = font(int(H * SS * 0.62))
    probe = ImageDraw.Draw(Image.new("RGBA", (10, 10)))
    tw = int(probe.textlength(TEXT, font=fnt))
    gap = int(H * SS * 0.28)
    w = H * SS + gap + tw + int(H * SS * 0.05)
    img = Image.new("RGBA", (w, H * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    k = H * SS / 64.0
    # Karten-Symbol wie in MainLayout.razor (viewBox 64): Rechteck x6 y10 w52 h44 rx10, drei Linien
    d.rounded_rectangle([6 * k, 10 * k, 58 * k, 54 * k], radius=10 * k, fill=BRAND)
    for (x1, y1, x2) in ((18, 24, 46), (18, 33, 46), (18, 42, 34)):
        d.line([(x1 * k, y1 * k), (x2 * k, y1 * k)], fill=WHITE, width=int(4 * k))
        for x in (x1, x2):
            d.ellipse([x * k - 2 * k, y1 * k - 2 * k, x * k + 2 * k, y1 * k + 2 * k], fill=WHITE)
    d.text((H * SS + gap, H * SS * 0.5), TEXT, font=fnt, fill=INK, anchor="lm")
    out = img.resize((max(1, w // SS), H), Image.LANCZOS)
    assert out.size[0] <= 245 and out.size[1] <= 36, out.size
    return out


if __name__ == "__main__":
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    b = banner()
    b.save(OUT, optimize=True)
    print("geschrieben:", os.path.normpath(OUT), b.size, os.path.getsize(OUT), "Bytes")
