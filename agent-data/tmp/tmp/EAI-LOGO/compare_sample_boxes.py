import json
import math
import sys
from pathlib import Path

def signed_area(poly):
    return sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1] for i in range(len(poly))) / 2 if poly else 0

def cross(a, b):
    return a[0] * b[1] - a[1] * b[0]

def sub(a, b):
    return a[0] - b[0], a[1] - b[1]

def intersection(subject, clip):
    output = list(subject)
    orientation = 1 if signed_area(clip) >= 0 else -1
    for i, a in enumerate(clip):
        b = clip[(i + 1) % len(clip)]
        line = sub(b, a)
        incoming, output = output, []
        if not incoming:
            break
        s = incoming[-1]
        for e in incoming:
            sin = orientation * cross(line, sub(s, a)) >= -1e-8
            ein = orientation * cross(line, sub(e, a)) >= -1e-8
            if ein != sin:
                segment = sub(e, s)
                denominator = cross(segment, line)
                if abs(denominator) > 1e-12:
                    t = cross(sub(a, s), line) / denominator
                    output.append((s[0] + t * segment[0], s[1] + t * segment[1]))
            if ein:
                output.append(e)
            s = e
    return abs(signed_area(output))

batch = Path(sys.argv[1])
result = Path(sys.argv[2])
canonical = Path(__file__).resolve().parent / 'project'
rows = []
for name in ('NhanL1 (10)', 'NhanL1 (12)'):
    gt = json.loads((canonical / (name + '.json')).read_text(encoding='utf-8'))['shapes']
    predicted_file = batch / 'project' / (name + '.json')
    if not predicted_file.exists():
        continue
    proposed = json.loads(predicted_file.read_text(encoding='utf-8-sig'))['shapes']
    used = set()
    for index, shape in enumerate(gt):
        human = shape['points']
        ha = abs(signed_area(human))
        scores = []
        for pi, proposal in enumerate(proposed):
            if pi in used:
                continue
            points = proposal['points']
            pa = abs(signed_area(points))
            overlap = intersection(points, human)
            scores.append((overlap / max(ha + pa - overlap, 1e-9), overlap / max(ha, 1e-9), pi, pa))
        iou, coverage, pi, pa = max(scores, default=(0, 0, -1, 0))
        if pi >= 0:
            used.add(pi)
        points = proposed[pi]['points'] if pi >= 0 else []
        long_first = bool(points) and math.dist(points[0], points[1]) >= math.dist(points[1], points[2])
        rows.append({'image': name + '.png', 'human_box_index': index, 'proposal_index': pi,
                     'rotated_iou': iou, 'human_area_coverage': coverage, 'proposal_human_area_ratio': pa / max(ha, 1e-9),
                     'generated_long_edge_first': long_first, 'extent_gate_pass': iou >= .5 and coverage >= .7,
                     'meaning': 'agreement with supplied localization example, not exhaustive detection quality'})
report = {'batch': str(batch), 'matching': 'greedy per human box; these diagnostic examples have distinct separated defects',
          'all_extent_gates_pass': len(rows) == 3 and all(row['extent_gate_pass'] for row in rows), 'boxes': rows}
if result.exists():
    raise SystemExit('Refusing to overwrite comparison evidence')
result.write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report, separators=(',', ':')))
