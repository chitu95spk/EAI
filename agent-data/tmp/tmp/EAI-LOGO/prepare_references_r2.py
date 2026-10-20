import json
import math
from pathlib import Path
from PIL import Image, ImageDraw

task = Path(__file__).resolve().parent
project = task / 'project'
output = task / 'references-r3'
if output.exists():
    raise SystemExit('Refusing to overwrite frozen references')
output.mkdir()
examples = []
for stem in ('NhanL1 (4)', 'NhanL1 (6)', 'NhanL1 (8)', 'NhanL1 (11)'):
    document = json.loads((project / (stem + '.json')).read_text(encoding='utf-8'))
    with Image.open(project / (stem + '.png')) as source:
        image = source.convert('RGB')
    for index, shape in enumerate(document['shapes']):
        points = shape['points']
        left, top = min(p[0] for p in points), min(p[1] for p in points)
        right, bottom = max(p[0] for p in points), max(p[1] for p in points)
        bounds = [max(0, int(left - 240)), max(0, int(top - 240)), min(image.width, int(right + 240)), min(image.height, int(bottom + 240))]
        crop = image.crop(bounds)
        draw = ImageDraw.Draw(crop)
        local = [(x - bounds[0], y - bounds[1]) for x, y in points]
        draw.line(local + [local[0]], fill='lime', width=5)
        crop.thumbnail((1400, 1400))
        name = stem + f'-box{index+1}-full-damage-scope.jpg'
        crop.save(output / name, quality=95)
        edges = [math.dist(points[i], points[(i+1) % 4]) for i in range(4)]
        area = abs(sum(points[i][0] * points[(i+1)%4][1] - points[(i+1)%4][0] * points[i][1] for i in range(4))) / 2
        examples.append({'reference': name, 'original_image_size': [image.width, image.height],
                         'crop_bounds_original_pixels': bounds, 'reference_display_size': list(crop.size),
                         'human_box_original_pixels': [[round(x, 1), round(y, 1)] for x, y in points],
                         'human_edge_lengths_pixels': [round(v, 1) for v in edges],
                         'human_box_area_pixels': round(area)})
(output / 'reference-geometry.json').write_text(json.dumps({'meaning': 'Reference boxes define full defect extent; these are reference coordinates only, never target coordinates.', 'examples': examples}, separators=(',', ':')), encoding='utf-8')
(output / 'extent-policy.txt').write_text(
    'Match the HUMAN BOX SCOPE illustrated by the green boxes. A crease, bright streak, scratch centreline or fold core can be only part of the damaged region. '
    'Include the full continuous band of distorted/lifted/warped material, including faint continuation, with one oriented box per separate region. '
    'Do not confine the box to the strongest highlight or darkest core. Do not include unrelated clean material. '
    'These examples are training images only. The two blinded diagnostic images and their box coordinates are absent. '
    'Coordinates and lengths in reference-geometry.json describe the references only and must never be copied to the target. '
    'For target crops, follow the explicit back-to-ORIGINAL formula returned by get_crop. Its reported scale is returned_picture_size/original_crop_size. '
    'Map EVERY corner with original_x=origin_x+crop_x/scale_x and original_y=origin_y+crop_y/scale_y. '
    'Never combine an original-image centre with an unscaled crop-space width/height. '
    'Return four cyclic original-image corners; the first edge of an elongated GENERATED box follows its long side. Preserve supplied human shapes unchanged.\n', encoding='utf-8')
print(json.dumps({'training_reference_shapes': len(examples), 'files': len(list(output.iterdir()))}))
