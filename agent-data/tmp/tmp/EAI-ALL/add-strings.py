# Usage: python tmp/EAI-ALL/add-strings.py <tsv-file>   (lines: Key<TAB>English<TAB>Vietnamese)
# Adds <data> entries to Strings.resx and Strings.vi.resx (UTF-8, keeps line endings, skips existing keys).
import io, sys, re
from xml.sax.saxutils import escape

base = 'EasyLabel/src/EasyAI.Wpf/Resources/'
rows = []
for line in io.open(sys.argv[1], encoding='utf-8'):
    line = line.rstrip('\r\n')
    if not line.strip() or line.startswith('#'):
        continue
    key, en, vi = line.split('\t')
    rows.append((key, en, vi))

def add(path, idx):
    s = io.open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    added = 0
    block = ''
    for r in rows:
        if re.search(r'<data name="%s"' % re.escape(r[0]), s):
            continue
        block += '  <data name="%s" xml:space="preserve">%s    <value>%s</value>%s  </data>%s' % (r[0], nl, escape(r[idx]), nl, nl)
        added += 1
    if added:
        pos = s.rindex('</root>')
        s = s[:pos] + block + s[pos:]
        io.open(path, 'w', encoding='utf-8', newline='').write(s)
    print(path, 'added', added)

add(base + 'Strings.resx', 1)
add(base + 'Strings.vi.resx', 2)
