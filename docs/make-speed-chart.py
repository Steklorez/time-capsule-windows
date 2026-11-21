"""Draws the speed comparison charts for the README (SVG; Russian and English, light and dark).

Run:  python docs/make-speed-chart.py
The numbers are averages of three runs of tools/speed-compare.ps1 on one Windows 11 laptop against a
Time Capsule 4th generation (2026-11-21), once over Wi-Fi and once over a network cable.
Replace them when better measurements exist.
"""
import os

# (what, connection, AFP drive, built-in SMB1)
LARGE = [("write", "wifi", 25.4, 15.2), ("write", "cable", 28.6, 23.0),
         ("read", "wifi", 28.5, 13.0), ("read", "cable", 41.1, 22.8)]          # MB/s, one 400 MB file
SMALL = [("create", "wifi", 30, 35), ("create", "cable", 30, 47),
         ("read", "wifi", 94, 38), ("read", "cable", 117, 39),
         ("delete", "wifi", 87, 13), ("delete", "cable", 95, 17)]              # files/s, 200 files of 10 KB

TEXT = {
    "en": {
        "title": "Apple Time Capsule on Windows 11: AFP drive vs built-in SMB",
        "sub": "One laptop, Time Capsule 4th gen. Averages of three runs; longer bars are faster.",
        "afp": "Time Capsule Drive (AFP)", "smb": "Windows built-in SMB1",
        "large": "One large file (400 MB), MB/s", "small": "Small files (200 × 10 KB), files per second",
        "write": "Write", "read": "Read", "create": "Create", "delete": "Delete", "wifi": "Wi-Fi", "cable": "cable",
        "faster": "{:.1f}× faster", "slower": "{:.0f}% slower",
        "note": "SMB1 is switched off in Windows 11 by default and has to be enabled by hand; the AFP drive needs no SMB1.",
        "dec": ".",
    },
    "ru": {
        "title": "Apple Time Capsule в Windows 11: диск по AFP и встроенный SMB",
        "sub": "Один ноутбук, Time Capsule 4-го поколения. Среднее по трём прогонам; чем длиннее полоса, тем быстрее.",
        "afp": "Time Capsule Drive (AFP)", "smb": "Встроенный в Windows SMB1",
        "large": "Один большой файл (400 МБ), МБ/с", "small": "Мелкие файлы (200 × 10 КБ), файлов в секунду",
        "write": "Запись", "read": "Чтение", "create": "Создание", "delete": "Удаление", "wifi": "Wi-Fi", "cable": "кабель",
        "faster": "в {:.1f} раза быстрее", "slower": "на {:.0f}% медленнее",
        "note": "SMB1 в Windows 11 по умолчанию выключен, его нужно включать вручную; диску по AFP он не нужен.",
        "dec": ",",
    },
}

THEME = {
    "light": dict(surface="#fcfcfb", ink="#0b0b0b", ink2="#52514e", muted="#898781", axis="#c3c2b7", afp="#2a78d6", smb="#eb6834"),
    "dark": dict(surface="#1a1a19", ink="#f0efec", ink2="#c3c2b7", muted="#898781", axis="#383835", afp="#3987e5", smb="#d95926"),
}

W, X0, SPAN = 820, 170, 390          # canvas width, bar baseline, pixels for the panel's largest value
BAR, GAP, ROW = 16, 2, 50            # bar thickness, gap between the two bars of a row, row pitch
FONT = "-apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif"


def bar(x, y, w, h, fill):
    """Bar growing right from the baseline: square at the baseline, 4 px rounded at the data end."""
    r = min(4, w / 2)
    return (f'<path d="M{x},{y} h{w - r:.1f} a{r},{r} 0 0 1 {r},{r} v{h - 2 * r} a{r},{r} 0 0 1 -{r},{r} h-{w - r:.1f} z" '
            f'fill="{fill}"/>')


def num(v, t, digits):
    return f"{v:.{digits}f}".replace(".", t["dec"])


def panel(out, y, heading, rows, t, c, digits):
    out.append(f'<text x="24" y="{y}" font-size="14" font-weight="600" fill="{c["ink"]}">{heading}</text>')
    top = y + 18
    scale = SPAN / max(max(a, s) for _, _, a, s in rows)
    out.append(f'<line x1="{X0}" y1="{top - 4}" x2="{X0}" y2="{top + ROW * len(rows) - 10}" stroke="{c["axis"]}" stroke-width="1"/>')
    for i, (key, link, afp, smb) in enumerate(rows):
        ry = top + i * ROW
        out.append(f'<text x="{X0 - 12}" y="{ry + BAR + 5}" font-size="13" text-anchor="end" fill="{c["ink2"]}">{t[key]}, {t[link]}</text>')
        for j, (value, colour) in enumerate(((afp, c["afp"]), (smb, c["smb"]))):
            by = ry + j * (BAR + GAP)
            w = value * scale
            out.append(bar(X0, by, w, BAR, colour))
            weight = ' font-weight="600"' if j == 0 else ""
            out.append(f'<text x="{X0 + w + 8:.1f}" y="{by + 12}" font-size="12"{weight} fill="{c["ink"]}">{num(value, t, digits)}</text>')
        ratio = afp / smb
        verdict = t["faster"].format(ratio) if ratio >= 1 else t["slower"].format((1 - ratio) * 100)
        verdict = verdict.replace(".", t["dec"])
        out.append(f'<text x="{W - 24}" y="{ry + BAR + 5}" font-size="13" text-anchor="end" fill="{c["ink2"]}">{verdict}</text>')
    return top + ROW * len(rows)


def chart(lang, mode):
    t, c = TEXT[lang], THEME[mode]
    out = []
    out.append(f'<text x="24" y="34" font-size="18" font-weight="600" fill="{c["ink"]}">{t["title"]}</text>')
    out.append(f'<text x="24" y="56" font-size="13" fill="{c["ink2"]}">{t["sub"]}</text>')
    # legend: a swatch beside each name, text in ink (never in the series colour)
    lx = 24
    for key in ("afp", "smb"):
        out.append(f'<rect x="{lx}" y="76" width="12" height="12" rx="2" fill="{c[key]}"/>')
        out.append(f'<text x="{lx + 18}" y="87" font-size="13" fill="{c["ink"]}">{t[key]}</text>')
        lx += 18 + int(len(t[key]) * 7.4) + 28
    y = panel(out, 126, t["large"], LARGE, t, c, 1)
    y = panel(out, y + 22, t["small"], SMALL, t, c, 0)
    out.append(f'<text x="24" y="{y + 14}" font-size="12" fill="{c["muted"]}">{t["note"]}</text>')
    h = y + 34
    head = (f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{h}" viewBox="0 0 {W} {h}" font-family="{FONT}" role="img" '
            f'aria-label="{t["title"]}">\n<rect width="{W}" height="{h}" rx="8" fill="{c["surface"]}"/>\n')
    return head + "\n".join(out) + "\n</svg>\n"


here = os.path.dirname(os.path.abspath(__file__))
for lang in TEXT:
    for mode in THEME:
        path = os.path.join(here, f"speed-{lang}-{mode}.svg")
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(chart(lang, mode))
        print("wrote", os.path.relpath(path, os.path.dirname(here)))
