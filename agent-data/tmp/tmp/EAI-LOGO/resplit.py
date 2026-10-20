"""Stratified, leak-free Train/Val split of E:\\Training\\TrainLOGONEW.

Groups: near-identical images (64-bit dHash distance <= THRESH inside one family) always stay on the same side.
Strata: the family (name without the (n) suffix). Every family with at least 5 images gets at least 2 validation images.
Greedy within a family: groups are taken for Val, mixing small and large defects, until the family reaches its image target.
Usage: python resplit.py [--apply]
"""
import json, glob, os, re, sys, random, collections, shutil, datetime, math, statistics
from PIL import Image

IMAGES = r'E:\Training\TrainLOGONEW\images'
VAL_FRACTION = 0.22
THRESH = 2
SEED = 20261016
apply = '--apply' in sys.argv


def dhash(path):
    with Image.open(path) as im:
        px = list(im.convert('L').resize((9, 8)).getdata())
    bits = 0
    for y in range(8):
        for x in range(8):
            bits = (bits << 1) | int(px[y * 9 + x] > px[y * 9 + x + 1])
    return bits


rows = []
for js in sorted(glob.glob(os.path.join(IMAGES, '*.json'))):
    name = os.path.splitext(os.path.basename(js))[0]
    png = os.path.join(IMAGES, name + '.png')
    if not os.path.exists(png):
        continue
    d = json.load(open(js, encoding='utf8'))
    W, H = d.get('imageWidth') or 5472, d.get('imageHeight') or 3648
    area = 0.0
    boxfeat = []
    for s in d['shapes']:
        q = s['points']
        e1 = ((q[1][0]-q[0][0])**2 + (q[1][1]-q[0][1])**2) ** .5
        e2 = ((q[2][0]-q[1][0])**2 + (q[2][1]-q[1][1])**2) ** .5
        boxfeat.append((math.log(max(1.0, e1*e2)), math.log(max(e1, e2)/max(1.0, min(e1, e2)))))
    for s in d['shapes']:
        p = s['points']
        area += abs(sum(p[i][0] * p[(i + 1) % len(p)][1] - p[(i + 1) % len(p)][0] * p[i][1] for i in range(len(p)))) / 2 / (W * H)
    rows.append({'name': name, 'js': js, 'family': re.sub(r'\s*\(\d+\)\s*$', '', name), 'boxes': len(d['shapes']),
                 'area': area, 'boxfeat': boxfeat, 'hash': dhash(png), 'old': 'val' if d.get('flags', {}).get('val') else 'train'})

# Distinct NG defects (unusual size or shape for their family, or many boxes) are kept in Train so the model sees the rare cases.
def robust_z(v, pool):
    med = statistics.median(pool)
    mad = statistics.median(abs(x - med) for x in pool) or 1e-6
    return abs(v - med) / (1.4826 * mad)

fam_pool = collections.defaultdict(lambda: ([], []))
for r in rows:
    for a, b in r['boxfeat']:
        fam_pool[r['family']][0].append(a)
        fam_pool[r['family']][1].append(b)
FORCE_TRAIN_FAMILIES = {'MopL2'}  # one physical dent seen several times: a single distinct defect type, no independent val sample
for r in rows:
    pa, pb = fam_pool[r['family']]
    reasons = []
    for a, b in r['boxfeat']:
        if len(pa) >= 8 and robust_z(a, pa) > 2.5:
            reasons.append('size')
        if len(pb) >= 8 and robust_z(b, pb) > 2.5:
            reasons.append('shape')
    if r['boxes'] >= 3:
        reasons.append('many-boxes')
    if r['family'] in FORCE_TRAIN_FAMILIES:
        reasons.append('distinct-family')
    r['distinct'] = sorted(set(reasons))

# union-find on near-duplicates inside a family
parent = list(range(len(rows)))


def find(i):
    while parent[i] != i:
        parent[i] = parent[parent[i]]
        i = parent[i]
    return i


for i in range(len(rows)):
    for j in range(i + 1, len(rows)):
        if rows[i]['family'] == rows[j]['family'] and bin(rows[i]['hash'] ^ rows[j]['hash']).count('1') <= THRESH:
            parent[find(i)] = find(j)
groups = collections.defaultdict(list)
for i, r in enumerate(rows):
    groups[find(i)].append(r)

rng = random.Random(SEED)
by_family = collections.defaultdict(list)
for g in groups.values():
    by_family[g[0]['family']].append(g)

val_names = set()
for fam, gs in sorted(by_family.items()):
    n = sum(len(g) for g in gs)
    target = 0 if n < 3 else max(2 if n >= 5 else 1, round(VAL_FRACTION * n))
    # order groups by mean defect size so the picks alternate small / large; ties broken by the seeded shuffle
    rng.shuffle(gs)
    gs.sort(key=lambda g: sum(r['area'] for r in g) / len(g))
    order = []
    lo, hi = 0, len(gs) - 1
    while lo <= hi:
        order.append(gs[lo]); lo += 1
        if lo <= hi:
            order.append(gs[hi]); hi -= 1
    # start the alternation from the middle so val is not only the extremes
    order = order[len(order) // 3:] + order[:len(order) // 3]
    got = 0
    for g in order:
        if got >= target:
            break
        if any(r['distinct'] for r in g):
            continue
        if got + len(g) > target + 1:
            continue
        for r in g:
            val_names.add(r['name'])
        got += len(g)

n_val = len(val_names)
print('distinct (kept in Train):', sum(1 for r in rows if r['distinct']), {k: sum(1 for r in rows if k in r['distinct']) for k in ('size','shape','many-boxes','distinct-family')})
print('groups', len(groups), 'images', len(rows), 'near-dup groups >1:', sum(1 for g in groups.values() if len(g) > 1))
print('family               imgs val(old->new)  boxes val(old->new)')
for fam in sorted(by_family):
    fr = [r for r in rows if r['family'] == fam]
    ov = sum(1 for r in fr if r['old'] == 'val'); nv = sum(1 for r in fr if r['name'] in val_names)
    ob = sum(r['boxes'] for r in fr if r['old'] == 'val'); nb = sum(r['boxes'] for r in fr if r['name'] in val_names)
    print('%-20s %4d  %2d -> %2d        %3d -> %3d' % (fam, len(fr), ov, nv, ob, nb))
print('VAL images %d (old %d), VAL boxes %d (old %d), TRAIN images %d' % (
    n_val, sum(1 for r in rows if r['old'] == 'val'), sum(r['boxes'] for r in rows if r['name'] in val_names),
    sum(r['boxes'] for r in rows if r['old'] == 'val'), len(rows) - n_val))

out = os.path.join(os.path.dirname(__file__), 'val-names-new.txt')
open(out, 'w', encoding='utf8').write(';'.join(sorted(val_names)))
if apply:
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
    backup = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(__file__))), 'artifacts', 'EAI-LOGO', 'resplit-backup-' + stamp)
    os.makedirs(backup)
    for r in rows:
        shutil.copy2(r['js'], backup)
        d = json.load(open(r['js'], encoding='utf8'))
        flags = d.get('flags') or {}
        is_val = r['name'] in val_names
        flags['val'] = is_val
        flags['train'] = not is_val
        d['flags'] = flags
        open(r['js'], 'w', encoding='utf8', newline='').write(json.dumps(d, ensure_ascii=False, indent=2))
    print('applied; JSON backup in', backup)
else:
    print('dry run (use --apply); val list written to', out)
