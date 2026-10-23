#!/usr/bin/env python3
"""Checks Localization/Strings.json against the code: every key used by the
macOS app (L("key")) or the Windows app (Localizer.T("key"), S.key in XAML,
CaptureState.Failed("key")) exists, and every key has every language."""
import json
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent.parent
table = json.loads((root / "Localization/Strings.json").read_text(encoding="utf-8"))
codes = [language["code"] for language in table["languages"]]
strings = table["strings"]

patterns = {
    "*.swift": [r'\bL\("([^"]+)"'],
    "*.cs": [r'Localizer\.T\("([^"]+)"', r'CaptureState\.Failed\("([^"]+)"'],
    "*.xaml": [r'DynamicResource S\.([\w.]+)'],
}
used = {}
for folder in ("AirGlass", "Windows/AirGlass"):
    for glob, regexes in patterns.items():
        for path in (root / folder).rglob(glob):
            text = path.read_text(encoding="utf-8")
            for regex in regexes:
                for key in re.findall(regex, text):
                    used.setdefault(key, path.relative_to(root))

problems = [f"{path}: unknown key {key!r}" for key, path in used.items() if key not in strings]
for key, texts in strings.items():
    missing = [code for code in codes if not texts.get(code)]
    if missing:
        problems.append(f"{key!r} has no text for {', '.join(missing)}")
    if any(("{0}" in texts.get(code, "")) != ("{0}" in texts.get("en", "")) for code in codes):
        problems.append(f"{key!r}: {{0}} must appear in every language or none")

for problem in problems:
    print(problem, file=sys.stderr)
print(f"{len(strings)} keys, {len(codes)} languages, {len(used)} used in code"
      + ("" if not problems else f", {len(problems)} problem(s)"))
sys.exit(1 if problems else 0)
