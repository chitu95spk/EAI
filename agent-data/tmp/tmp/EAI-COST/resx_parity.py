import sys
import xml.etree.ElementTree as ET

def names(path):
    root = ET.parse(path).getroot()
    out = []
    for el in root.iter("data"):
        n = el.get("name")
        if n is not None:
            out.append(n)
    return out

en_path, vi_path = sys.argv[1], sys.argv[2]
en = names(en_path)
vi = names(vi_path)
en_set, vi_set = set(en), set(vi)
print("en file: %s" % en_path)
print("vi file: %s" % vi_path)
print("en data names: %d (distinct %d)" % (len(en), len(en_set)))
print("vi data names: %d (distinct %d)" % (len(vi), len(vi_set)))
dup_en = sorted({n for n in en if en.count(n) > 1})
dup_vi = sorted({n for n in vi if vi.count(n) > 1})
print("duplicate names en: %d %s" % (len(dup_en), dup_en[:20]))
print("duplicate names vi: %d %s" % (len(dup_vi), dup_vi[:20]))
missing_in_vi = sorted(en_set - vi_set)
missing_in_en = sorted(vi_set - en_set)
print("missing in vi (present in en): %d" % len(missing_in_vi))
for n in missing_in_vi:
    print("  - " + n)
print("missing in en (present in vi): %d" % len(missing_in_en))
for n in missing_in_en:
    print("  - " + n)
print("PARITY: %s" % ("PASS" if not missing_in_vi and not missing_in_en else "FAIL"))
sys.exit(0 if not missing_in_vi and not missing_in_en else 1)
