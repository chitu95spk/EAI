"""Leakage check: every training-project image (c2/c2a/c2d/c2f ... *.images) vs the frozen tune/final test images.
Checks file-name overlap (informational: both use 0000.png), file sha256, decoded-pixel sha256, source-screw ids, seeds.
Usage: python -X utf8 counter_leakcheck.py PROJECT_IMAGES_DIR [more dirs...]
"""
import sys, json, hashlib
from pathlib import Path
import cv2
SEG = Path(r"C:\Demo\LucGiac.segai-tune")
man = json.loads((SEG / "dataset/manifest.json").read_text())
def fsha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def psha(p):
    im = cv2.imread(str(p), cv2.IMREAD_UNCHANGED)
    return hashlib.sha256(im.tobytes()).hexdigest() + f"{im.shape}"
test = {s: [r for r in man["records"] if r["split"] == s] for s in ("tune", "final")}
test_f = {fsha(r["image"]): (s, r["image"]) for s in test for r in test[s]}
test_p = {psha(r["image"]): (s, r["image"]) for s in test for r in test[s]}
test_names = {Path(r["image"]).name for r in test["final"]}
test_src = {s: {x for r in test[s] for x in r["sources"]} for s in test}
test_seed = {r["seed"] for s in test for r in test[s]}
print("final images", len(test["final"]), "tune", len(test["tune"]), "final distinct source screws", sorted(test_src["final"]), "tune", sorted(test_src["tune"]))
bad = 0
for d in sys.argv[1:]:
    imgs = sorted(Path(d).rglob("*.png"))
    nm = sum(p.name in test_names for p in imgs)
    hf = [p for p in imgs if fsha(p) in test_f]
    hp = [p for p in imgs if psha(p) in test_p]
    print(f"{d}: {len(imgs)} png | same file name as a final image: {nm} (naming scheme collides by design) | file-sha256 equal to tune/final: {len(hf)} | decoded-pixel equal: {len(hp)}")
    bad += len(hf) + len(hp)
# source / seed overlap from manifests of every train-side record we know
tr = [r for r in man["records"] if r["split"] in ("train", "val")]
for mp in (r"C:\Demo\LucGiac.counter3\extra\manifest.json", r"C:\Demo\LucGiac.counter3\extra2\manifest.json"):
    if Path(mp).exists():
        tr += json.loads(Path(mp).read_text())["records"]
tsrc = {x for r in tr for x in r["sources"]}
print("train-side records", len(tr), "train source screws", len(tsrc))
print("source overlap train vs final:", sorted(tsrc & test_src["final"]), "| vs tune:", sorted(tsrc & test_src["tune"]))
print("seed overlap:", len({r["seed"] for r in tr} & test_seed), "| sha overlap (manifest):", len({r["sha256"] for r in tr} & set(test_f)))
print("backgrounds in final (unseen families):", sorted({r["bg"] for r in test["final"]}))
print("backgrounds in train-side:", sorted({r["bg"] for r in tr}))
print("LEAK" if (bad or tsrc & test_src["final"] or tsrc & test_src["tune"]) else "NO LEAK (file hash, pixel hash, source ids, seeds)")
