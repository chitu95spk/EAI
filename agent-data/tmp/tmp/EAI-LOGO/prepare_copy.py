import hashlib
import json
import shutil
from pathlib import Path
from PIL import Image, ImageDraw

repo = Path(r'E:\Code\EasyVision_Unisen - NoService').resolve()
source = Path(r'E:\Training\TrainLOGONEW')
target = repo / 'tmp' / 'EAI-LOGO' / 'project'
if target.exists():
    raise SystemExit('Refusing to overwrite an existing dataset copy')
target.mkdir(parents=True)
annotations = {}
manifest = []
for image in sorted((source / 'images').glob('*.png')):
    dest = target / image.name
    shutil.copy2(image, dest)
    with image.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    with Image.open(image) as decoded:
        size = list(decoded.size)
    stem = image.stem
    candidate = source / 'images' / (stem + '.json')
    if not candidate.exists():
        candidate = source / 'json' / (stem + '.json')
    row = {'rel': image.name, 'sha256': digest, 'bytes': image.stat().st_size,
           'size': size, 'capture_group_proxy': stem.split(' (')[0],
           'provenance': 'unreviewed', 'annotation_count': 0}
    if candidate.exists():
        raw = candidate.read_bytes()
        item = json.loads(raw)
        item['imageData'] = None
        item['imagePath'] = image.name
        (target / candidate.name).write_text(json.dumps(item, ensure_ascii=False, indent=2), encoding='utf-8')
        annotations[stem] = item
        row.update({'annotation_source': str(candidate), 'annotation_sha256': hashlib.sha256(raw).hexdigest(),
                    'annotation_count': len(item.get('shapes', [])), 'provenance': 'human_localization_example_not_exhaustive'})
        group = row['capture_group_proxy']
        row['split'] = 'test_examples' if group == 'NHAN_PHONG_CHONG' else ('calibration_example' if group == 'NhanL2' else 'train_examples')
    else:
        row['split'] = 'unlabelled_pool'
    manifest.append(row)
    print('copied ' + image.name, flush=True)
(target / 'classes.txt').write_text('NG\nOK\n', encoding='utf-8')
(target / '.easyai').mkdir()
shutil.copy2(source / 'LOGO_SegMent.easyai.data' / 'project.json', target / '.easyai' / 'project.json')
frozen = {'source': str(source), 'copy': str(target), 'originals_read_only': True,
          'class_policy': 'all requested defects are NG; no confirmed OK images supplied',
          'annotation_authority': 'images/*.json, matching current sidecar cache where conflicting with json/*.json',
          'readiness': 'diagnostic_research_only_label_completeness_unknown', 'images': manifest}
out = repo / 'tmp' / 'EAI-LOGO'
(out / 'manifest.json').write_text(json.dumps(frozen, ensure_ascii=False, indent=2), encoding='utf-8')
refs = out / 'references'
refs.mkdir()
for stem in ('NhanL1 (4)', 'NhanL1 (8)', 'NhanL1 (11)'):
    item = annotations[stem]
    image = Image.open(target / (stem + '.png')).convert('RGB')
    image.thumbnail((1200, 800))
    sx, sy = image.width / item['imageWidth'], image.height / item['imageHeight']
    draw = ImageDraw.Draw(image)
    for shape in item['shapes']:
        points = [(x * sx, y * sy) for x, y in shape['points']]
        draw.line(points + [points[0]], fill='lime', width=3)
    image.save(refs / (stem + '-NG-examples.jpg'), quality=92)
(refs / 'label-policy.txt').write_text(
    'Human localization examples use the class NG and tight oriented_rectangle boxes around defect regions. '
    'The green boxes show example scope; do not draw a box around the whole bottle or whole printed label. '
    'Examples may be incomplete: all other regions are unknown, not verified normal. '
    'Defects include curled or lifted label edges, bubbles/bulging, wrinkles, tears, scratches and bottle dents. '
    'Printed text, logos, barcodes, normal seams, shadows and specular reflections are not defects by themselves. '
    'Inspect actual pixels; filenames are not proof. Find all clearly visible defect regions. '
    'Use high-resolution crops when needed; preserve multiple separate local defects. '
    'Use only NG, oriented_rectangle, four image-coordinate corners. '
    'Do not force a defect on an unclear image; explain uncertainty instead.\n', encoding='utf-8')
print(json.dumps({'images': len(manifest), 'human_examples': len(annotations),
                  'boxes': sum(len(a['shapes']) for a in annotations.values()), 'copy': str(target)}, ensure_ascii=False))
