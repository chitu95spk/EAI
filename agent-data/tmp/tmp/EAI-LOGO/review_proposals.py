import json
import math
import sys
from pathlib import Path
from PIL import Image, ImageDraw

batch = Path(sys.argv[1]).resolve()
output = Path(sys.argv[2]).resolve()
repo = Path(r'E:\Code\EasyVision_Unisen - NoService').resolve()
if not output.is_relative_to(repo) or output.exists():
    raise SystemExit('Review output must be new and inside the workspace')
output.mkdir(parents=True)
project = batch / 'project'
records = []
tiles = []
for annotation in sorted(project.glob('*.json')):
    document = json.loads(annotation.read_text(encoding='utf-8-sig'))
    image = Image.open(project / (annotation.stem + '.png')).convert('RGB')
    overview = image.copy()
    overview.thumbnail((800, 534))
    scale = overview.width / image.width
    draw = ImageDraw.Draw(overview)
    for index, shape in enumerate(document.get('shapes', [])):
        points = shape.get('points', [])
        valid = shape.get('label') == 'NG' and shape.get('shape_type') == 'oriented_rectangle' and len(points) == 4
        valid = valid and all(len(p) == 2 and all(math.isfinite(v) for v in p) for p in points)
        record = {'image': annotation.stem + '.png', 'index': index, 'valid_ng_obb': valid,
                  'review_status': 'pending_visual_review', 'provenance': 'agent'}
        if valid:
            left, top = min(p[0] for p in points), min(p[1] for p in points)
            right, bottom = max(p[0] for p in points), max(p[1] for p in points)
            pad = max(60, min(250, (right - left + bottom - top) * .12))
            bounds = (max(0, int(left - pad)), max(0, int(top - pad)), min(image.width, int(right + pad)), min(image.height, int(bottom + pad)))
            crop = image.crop(bounds)
            crop_draw = ImageDraw.Draw(crop)
            q = [(x - bounds[0], y - bounds[1]) for x, y in points]
            crop_draw.line(q + [q[0]], fill='red', width=3)
            crop.thumbnail((760, 510))
            tile = Image.new('RGB', (800, 560), 'white')
            tile.paste(crop, (10, 35))
            ImageDraw.Draw(tile).text((10, 10), f'{annotation.stem} | box {index + 1}', fill='black')
            tiles.append(tile)
            q = [(x * scale, y * scale) for x, y in points]
            draw.line(q + [q[0]], fill='red', width=2)
            area = abs(sum(points[i][0] * points[(i+1)%4][1] - points[(i+1)%4][0] * points[i][1] for i in range(4))) / 2
            record['image_area_fraction'] = area / (image.width * image.height)
            record['possible_whole_object_expansion'] = record['image_area_fraction'] > .25
        records.append(record)
    overview.save(output / (annotation.stem + '-overview.jpg'), quality=92)
for start in range(0, len(tiles), 4):
    sheet = Image.new('RGB', (1600, 1120), 'white')
    for index, tile in enumerate(tiles[start:start+4]):
        sheet.paste(tile, ((index % 2) * 800, (index // 2) * 560))
    sheet.save(output / f'crops-{start//4+1:02d}.jpg', quality=94)
(output / 'review.json').write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'images': len(list(project.glob('*.json'))), 'proposals': len(records),
                  'invalid': sum(not r['valid_ng_obb'] for r in records),
                  'large_box_flags': sum(r.get('possible_whole_object_expansion', False) for r in records),
                  'crop_sheets': math.ceil(len(tiles) / 4), 'output': str(output)}, ensure_ascii=False))
