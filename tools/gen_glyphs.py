# -*- coding: utf-8 -*-
"""从 Bootstrap Icons SVG 生成 IconGlyphs.cs（内嵌路径数据，零运行时依赖）

用法：python tools/gen_glyphs.py
源 SVG 在 tools/glyphs-svg/（下载自 https://icons.getbootstrap.com/，MIT）。
"""
import re, os

SVG_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "glyphs-svg")
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "src", "PlainSnip", "IconGlyphs.cs")

MAPPING = {
    "Select":  "arrows-move",
    "Rect":    "square",
    "Ellipse": "circle",
    "Pen":     "pencil-fill",
    "Mosaic":  "grid-fill",
    "Undo":    "arrow-counterclockwise",
    "Redo":    "arrow-clockwise",
    "Pin":     "pin-fill",
    "Save":    "floppy-fill",
    "Copy":    "files",
    "Cancel":  "x-lg",
}

entries = []
for key, fname in MAPPING.items():
    with open(os.path.join(SVG_DIR, fname + ".svg"), encoding="utf-8") as f:
        svg = f.read()
    ds = re.findall(r'\bd="([^"]+)"', svg)
    assert ds, fname
    # 多条 path 拼接（各子路径以 M 开头，独立子路径可直接相连）
    joined = " ".join(ds)
    entries.append((key, joined))

lines = []
lines.append("// 由 tools/gen_glyphs.py 从 Bootstrap Icons v1 (MIT, https://icons.getbootstrap.com/) 生成，")
lines.append("// 路径数据原始 viewBox = 0 0 16 16。仅内嵌 d 数据，运行时零依赖。")
lines.append("using System.Collections.Generic;")
lines.append("")
lines.append("namespace PlainSnip;")
lines.append("")
lines.append("public static class IconGlyphs")
lines.append("{")
lines.append('    private static readonly Dictionary<string, string> Paths = new()')
lines.append("    {")
for key, d in entries:
    lines.append(f'        ["{key}"] = "{d}",')
lines.append("    };")
lines.append("")
lines.append("    public static string? Get(string key) => Paths.TryGetValue(key, out var d) ? d : null;")
lines.append("}")
lines.append("")

with open(OUT, "w", encoding="utf-8-sig", newline="\n") as f:
    f.write("\n".join(lines))
print("written", OUT, len(entries), "glyphs")
