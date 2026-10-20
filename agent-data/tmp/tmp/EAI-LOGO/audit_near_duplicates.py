import json
from pathlib import Path
from PIL import Image

task = Path(__file__).resolve().parent
manifest = json.loads((task / 'manifest.json').read_text(encoding='utf-8'))
rows = []
for row in manifest['images']:
    with Image.open(Path(manifest['copy']) / row['rel']) as image:
        pixels = list(image.convert('L').resize((9, 8)).getdata())
    bits = 0
    for y in range(8):
        for x in range(8):
            bits = (bits << 1) | int(pixels[y * 9 + x] > pixels[y * 9 + x + 1])
    rows.append((row, bits))
pairs = []
for i, (a, ah) in enumerate(rows):
    for b, bh in rows[i + 1:]:
        distance = (ah ^ bh).bit_count()
        if distance <= 4:
            pairs.append({'a': a['rel'], 'b': b['rel'], 'dhash_distance': distance,
                          'cross_group': a['capture_group_proxy'] != b['capture_group_proxy'],
                          'splits': [a['split'], b['split']]})
report = {'method': '64-bit dHash, candidate-only; similar bottle layout is not proof of duplicate capture',
          'threshold': 4, 'pairs': pairs}
(task / 'near-duplicate-candidates.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
cross = [p for p in pairs if p['cross_group']]
print(json.dumps({'pairs': len(pairs), 'cross_group_candidates': len(cross), 'cross_group_first': cross[:10]}))
