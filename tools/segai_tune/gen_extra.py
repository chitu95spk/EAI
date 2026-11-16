"""Extra TRAIN-ONLY synthetic images for the Counter experiments (counter3). Same compose/patches/backgrounds as dataset.py
(train source identities only, TRAIN_BG families only), new seeds. Writes C:\\Demo\\LucGiac.counter3\\extra\\manifest.json with
records shaped like the frozen manifest (split="train") and asserts no seed/hash/source overlap with tune/final/val/train.
Usage: python -X utf8 gen_extra.py N [seedbase=20261150000]
"""
from pathlib import Path
import sys, json, random, inspect
import numpy as np, cv2
sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds
sg = ds.sg
OUT = Path(r"C:\Demo\LucGiac.counter3\extra")

def main():
    global OUT
    n = int(sys.argv[1]); base = int(sys.argv[2]) if len(sys.argv) > 2 else 20261150000
    if len(sys.argv) > 3: OUT = Path(sys.argv[3])   # e.g. C:\Demo\LucGiac.counter3\extra2 (image names x0000.png never collide with train 0000.png)
    man = json.loads((ds.OUT / "dataset" / "manifest.json").read_text())
    image = cv2.imread(str(ds.SOURCE)); objects = sg.load_polygons(ds.SOURCE.with_suffix(".json"))
    bg = sg.estimate_background(image, objects); masks = sg.all_mattes(image, objects, bg)
    bad = sg.touching_ids(objects, masks)
    clean = sorted([o for o in objects if o["id"] not in bad], key=lambda o: o["poly"][:, 0].mean())
    train_src = clean[:-3]
    assert [o["id"] for o in train_src] == man["source_groups"]["train"], "train source group differs from frozen manifest"
    patches = [sg.cut_patch(image, o, bg=bg, mask_full=masks.get(o["id"])) for o in train_src]
    code = inspect.getsource(sg.compose)
    tail = '''    semantic = np.zeros((h,w),np.uint8)
    for p in placed:
        ys,xs=p["y0"],p["x0"]
        view=semantic[ys:ys+p["alpha"].shape[0], xs:xs+p["alpha"].shape[1]]
        view[p["alpha"]>128]=1
    return out, objs, info, semantic'''
    code = code.replace("    return out, objs, info", tail)
    ns = dict(sg.__dict__); exec(compile(code, "compose+semantic", "exec"), ns); compose = ns["compose"]
    OUT.mkdir(parents=True, exist_ok=True); (OUT / "img").mkdir(exist_ok=True)
    recs = []
    for i in range(n):
        seed = base + i
        kind = sg.TRAIN_BG[i % len(sg.TRAIN_BG)]   # balanced over seen families (no wood/dots/carbon: those stay unseen)
        img, objs, info, mask = compose(patches, kind, np.random.default_rng(seed), random.Random(seed))
        ip = OUT / "img" / (f"x{i:04d}.png" if OUT.name == "extra" else f"{OUT.name}_{i:04d}.png")
        cv2.imwrite(str(ip), img)
        recs.append(dict(split="train", index=i, seed=seed, image=str(ip), sha256=ds.sha(ip),
                         boxes=[np.asarray(pts).tolist() for label, pts in objs], **info))
        print(f"{i+1}/{n} objects={len(objs)} bg={kind}", flush=True)
    others = [r for r in man["records"] if r["split"] in ("tune", "final", "val", "train")]
    assert not ({r["seed"] for r in recs} & {r["seed"] for r in others}), "seed overlap"
    assert not ({r["sha256"] for r in recs} & {r["sha256"] for r in others}), "hash overlap"
    tf = [r for r in others if r["split"] in ("tune", "final")]
    assert not ({x for r in recs for x in r["sources"]} & {x for r in tf for x in r["sources"]}), "source overlap with tune/final"
    assert all(r["bg"] in sg.TRAIN_BG for r in recs)
    (OUT / "manifest.json").write_text(json.dumps(dict(base_seed=base, n=n, leakage="passed (seed, sha256, sources vs tune/final/val/train)", records=recs), indent=1), encoding="utf-8")
    print("EXTRA OK", len(recs))

if __name__ == "__main__":
    main()
