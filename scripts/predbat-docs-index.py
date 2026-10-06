#!/usr/bin/env python3
"""Builds src/Joule.Api/Knowledge/predbat-docs-index.json from Predbat's documentation at a pinned ref.

The index holds facts only (file, heading anchor and the setting keys each section mentions), never Predbat's prose, so the
settings catalogue's citations can be checked by a unit test without redistributing the docs.

Usage: scripts/predbat-docs-index.py [ref]   (default: the docsRef in predbat-settings.json)
"""
import hashlib, json, os, re, sys, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
KNOWLEDGE = os.path.join(ROOT, "src", "Joule.Api", "Knowledge")
FILES = ["customisation.md", "car-charging.md", "energy-rates.md", "compare.md", "predheat.md", "install.md", "apps-yaml.md",
         "manual-api.md", "output-data.md", "faq.md", "what-does-predbat-do.md", "predbat-plan-card.md", "web-interface.md"]


def slug(text):
    # Python-Markdown's toc slugify, as used by Predbat's mkdocs site.
    text = re.sub(r"[^\w\s-]", "", text).strip().lower()
    return re.sub(r"[-\s]+", "-", text)


def main():
    ref = sys.argv[1] if len(sys.argv) > 1 else json.load(open(os.path.join(KNOWLEDGE, "predbat-settings.json")))["docsRef"]
    sections, sources = {}, {}
    entity = re.compile(r"(?:input_number|switch|select|sensor|binary_sensor)\.(?:predbat_)?([a-z0-9_]+)")
    bold = re.compile(r"\*\*([a-z][a-z0-9_]+)\*\*|`([a-z][a-z0-9_]+)`")
    for name in FILES:
        url = f"https://raw.githubusercontent.com/springfall2008/batpred/{ref}/docs/{name}"
        text = urllib.request.urlopen(url, timeout=30).read().decode("utf-8")
        sources[name] = hashlib.sha256(text.encode("utf-8")).hexdigest()
        current, seen = None, {}
        for line in text.split("\n"):
            heading = re.match(r"^(#{1,6})\s+(.*)$", line)
            if heading:
                anchor = slug(heading.group(2))
                count = seen.get(anchor, 0)
                seen[anchor] = count + 1
                current = f"{name}#{anchor}" if count == 0 else f"{name}#{anchor}_{count}"
                sections[current] = set()
                continue
            if current is None:
                continue
            for match in entity.finditer(line):
                sections[current].add(match.group(1))
            for match in bold.finditer(line):
                sections[current].add(match.group(1) or match.group(2))
    index = {"ref": ref, "about": "Facts extracted from Predbat's docs at this ref by scripts/predbat-docs-index.py: section anchors and the setting keys each mentions. No documentation prose.",
             "sources": sources, "sections": {k: sorted(v) for k, v in sections.items()}}
    path = os.path.join(KNOWLEDGE, "predbat-docs-index.json")
    with open(path, "w", encoding="utf-8") as out:
        json.dump(index, out, indent=1, sort_keys=False)
        out.write("\n")
    print(f"Wrote {path}: {len(sections)} sections from {len(sources)} files at {ref}.")


if __name__ == "__main__":
    main()
