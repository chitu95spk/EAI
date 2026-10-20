import collections
import hashlib
import json
from pathlib import Path

root = Path(r'E:\Training\TrainLOGONEW')
out = Path(__file__).resolve().parent
rows = []
counts = collections.Counter()
types = collections.Counter()
for folder in ('json', 'images'):
    for path in sorted((root / folder).glob('*.json')):
        raw = path.read_bytes()
        item = json.loads(raw)
        shapes = item.get('shapes', [])
        slim = []
        for shape in shapes:
            label = shape.get('label', '')
            kind = shape.get('shape_type', '')
            counts[label] += 1
            types[kind] += 1
            slim.append({k: shape.get(k) for k in ('label', 'shape_type', 'points', 'direction', 'flags', 'group_id')})
        rows.append({'file': str(path), 'bytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest(),
                     'imagePath': item.get('imagePath'), 'width': item.get('imageWidth'),
                     'height': item.get('imageHeight'), 'shapes': slim, 'flags': item.get('flags')})
        del raw, item
summary = {'images': len(list((root / 'images').glob('*.png'))), 'json_files': len(rows),
           'class_counts': dict(counts), 'shape_types': dict(types), 'annotations': rows}
(out / 'dataset-audit.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({k: v for k, v in summary.items() if k != 'annotations'}, ensure_ascii=False))
for row in rows:
    print(json.dumps({k: v for k, v in row.items() if k not in ('shapes', 'sha256')}, ensure_ascii=False))
    for shape in row['shapes'][:3]:
        print(json.dumps(shape, ensure_ascii=False))
