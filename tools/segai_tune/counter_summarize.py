"""Print a compact summary of a counter_errors.py output and draw overlay PNGs for the worst images.
Usage: python -X utf8 counter_summarize.py C:\\Demo\\LucGiac.counter3\\analysis\\LABEL_final.json [n_overlays=6]
Overlay: green = GT (matched), red = GT missed (FN), yellow = TP prediction, magenta = FP prediction.
"""
import sys, json
from pathlib import Path
import numpy as np, cv2
p = Path(sys.argv[1]); nov = int(sys.argv[2]) if len(sys.argv) > 2 else 6
d = json.loads(p.read_text()); rows = d["rows"]; s = d["summary"]
print({k: v for k, v in s.items() if k != "by_bg"})
print("scores of FP (n=%d): " % sum(len(r["fps"]) for r in rows), np.round(np.percentile([e["score"] for r in rows for e in r["fps"]], [10, 50, 90]), 3) if s["fp"] else "-")
print("FP area / median GT area:", np.round(np.median([e["area"] for r in rows for e in r["fps"]]), 0), np.round(np.median([e["area"] for r in rows for e in r["fns"]]), 0) if s["fn"] else "-")
wrong = [r for r in rows if r["gt"] != r["count"]]
print("wrong-count images:", len(wrong), "| by gt count:", sorted(((r["gt"], r["count"]) for r in wrong)))
big = [r for r in rows if r["gt"] >= 20]; print("images with gt>=20:", len(big), "exact:", sum(r["gt"] == r["count"] for r in big))
small = [r for r in rows if r["gt"] < 20]; print("images with gt<20:", len(small), "exact:", sum(r["gt"] == r["count"] for r in small))
# FP type detail: duplicates vs localisation
print("FP by class:", s["fp_classes"], "FN by class:", s["fn_classes"], "FN touching/overlapping:", s["fn_overlapping_gt"], "border:", s["fn_border"])
out = p.parent / (p.stem + "_overlays"); out.mkdir(exist_ok=True)
worst = sorted(rows, key=lambda r: -(len(r["fps"]) + len(r["fns"])))[:nov]
for r in worst:
    im = cv2.imread(r["image"]); fpi = {e["i"] for e in r["fps"]}; fnj = {e["j"] for e in r["fns"]}
    for j, g in enumerate(r["gt_boxes"]):
        cv2.polylines(im, [np.asarray(g, np.int32)], True, (0, 0, 255) if j in fnj else (0, 200, 0), 2)
    for i, b in enumerate(r["boxes"]):
        cv2.polylines(im, [np.asarray(b, np.int32)], True, (255, 0, 255) if i in fpi else (0, 220, 255), 1)
    f = out / (Path(r["image"]).parent.name + "_" + Path(r["image"]).stem + f"_{r['bg']}_gt{r['gt']}_n{r['count']}.png")
    cv2.imwrite(str(f), im); print("overlay", f)
