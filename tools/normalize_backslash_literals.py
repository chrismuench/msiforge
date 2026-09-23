"""Normalise C# backslash character literals to exactly one escaped backslash.

Generating C# from Python makes it very easy to emit '\\\\' (two characters,
which C# rejects) or '\' (an unterminated escape). This puts every
Replace('<backslash>', '/') call back to the single correct form.
"""
import glob
import os

B = chr(92)
BAD = [
    "Replace('" + B * 4 + "', '/')",
    "Replace('" + B + "', '/')",
]
GOOD = "Replace('" + B * 2 + "', '/')"

changed = 0
for path in glob.glob(os.path.join('src', '**', '*.cs'), recursive=True):
    parts = path.replace(B, '/').split('/')
    if 'obj' in parts or 'bin' in parts:
        continue
    text = open(path, encoding='utf-8').read()
    updated = text
    for bad in BAD:
        updated = updated.replace(bad, GOOD)
    if updated != text:
        open(path, 'w', encoding='utf-8').write(updated)
        print('normalised', path)
        changed += 1

print(f'{changed} file(s) changed')
