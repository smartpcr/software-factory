"""
Extract the prototype <style> block from docs/examples/forge-board.html
into per-section CSS files under src/styles/. Verbatim — only the leading
two-space indentation that lived inside <style> is removed (CSS doesn't
need it). Section banners stay in place so the boundary remains visible.

Run from the Forge.UI directory:
    python scripts/extract-css.py
"""

from __future__ import annotations
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
REPO_ROOT = ROOT.parent.parent.parent  # src/forge/Forge.UI -> repo root
PROTO = REPO_ROOT / "docs" / "examples" / "forge-board.html"
OUT = ROOT / "src" / "styles"

# Section banner -> output filename. Match exactly what the prototype writes
# in its `/* ============ <NAME> ============ */` comments.
SECTIONS = [
    ("RESET + BASE",                    "reset.css"),
    ("THEMES",                          "theme.css"),
    ("APP SHELL",                       "shell.css"),
    ("SIDEBAR (expandable)",            "sidebar.css"),
    ("MAIN",                            "main.css"),
    ("FILTER BAR (shared)",             "filter-bar.css"),
    ("BOARD (Plan & Design view)",      "board.css"),
    ("TIMELINE VIEW",                   "timeline.css"),
    ("WORKSTREAMS VIEW",                "workstream.css"),
    ("PR VIEW",                         "pr.css"),
    ("OVERVIEW VIEW",                   "overview.css"),
    ("DETAIL PANEL",                    "detail-panel.css"),
    ("TOAST + MODAL",                   "toast-modal.css"),
]

# locate the <style>...</style> block
html = PROTO.read_text(encoding="utf-8")
m = re.search(r"<style>\s*\n(.*?)\n\s*</style>", html, re.DOTALL)
if not m:
    sys.exit("Could not locate <style> block in forge-board.html")

style = m.group(1)
lines = style.split("\n")

# Strip the constant 2-space indent so the output reads as plain CSS.
def dedent(line: str) -> str:
    return line[2:] if line.startswith("  ") else line

lines = [dedent(l) for l in lines]

# Find each section banner (line containing the name).
indices: list[int] = []
for name, _file in SECTIONS:
    for i, line in enumerate(lines):
        if line.strip() == name:
            indices.append(i)
            break
    else:
        sys.exit(f"Section banner not found: {name}")

# Section text starts 1 line BEFORE the name (the opening /* =====) and runs
# until the next section's opening /* =====.
banner_starts = [i - 1 for i in indices]
banner_starts.append(len(lines))  # sentinel

OUT.mkdir(parents=True, exist_ok=True)
for (name, fname), start, end in zip(SECTIONS, banner_starts[:-1], banner_starts[1:]):
    chunk = "\n".join(lines[start:end]).rstrip() + "\n"
    (OUT / fname).write_text(chunk, encoding="utf-8", newline="\n")
    print(f"  wrote {fname:<20} {end - start:>4} lines  ({name})")

# Index file pulls them all together in original order.
index_lines = [
    "/* Forge UI — verbatim port of docs/examples/forge-board.html.",
    " * Do not modify; section banners and rule order match the prototype byte-for-byte.",
    " * See docs/forge_implementation.md (Phase 0.4).",
    " */",
    "",
    *(f"@import './{fname}';" for _name, fname in SECTIONS),
    "",
]
(OUT / "index.css").write_text("\n".join(index_lines), encoding="utf-8", newline="\n")
print(f"  wrote index.css")
