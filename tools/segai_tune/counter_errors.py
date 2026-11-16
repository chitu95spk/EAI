"""Error analysis of a Counter (instance head) SegAI model on a split of the frozen manifest.
Usage: python -X utf8 counter_errors.py MODEL LABEL SPLIT [json-opts]
Env: SEGAI_NATIVE_DIR / SEGAI_BACKEND as in seg_native.py. Output: C:\\Demo\\LucGiac.counter3\\analysis\\LABEL_SPLIT.json
FP classes: duplicate (IoU>=.5 with an already matched GT), loc (best IoU .1-.5 with some GT), bg (IoU<.1 with every GT).
FN classes: loc (a prediction overlaps it .1-.5), merged (missed but a TP prediction covers it >=.3 IoU), miss (nothing nearby).
"""
import sys, json, os
from pathlib import Path
import numpy as np, cv2
from seg_native import SegNative
from counter_eval import iou
SEG = Path(r"C:\Demo\LucGiac.segai-tune")
OUT = Path(r"C:\Demo\LucGiac.counter3\analysis")

def area(b): return float(cv2.contourArea(np.asarray(b, np.float32)))

def analyse(eng, records):
    rows = []
    for r in records:
        im = cv2.imread(r["image"]); h, w = im.shape[:2]
        p = eng.run(im)
        gts = r["boxes"]; pr = p["boxes"]; sc = p["scores"]
        M = np.zeros((len(pr), len(gts)))
        for i, b in enumerate(pr):
            for j, g in enumerate(gts):
                M[i, j] = iou(b, g)
        used = {}; tp = []
        for i in sorted(range(len(pr)), key=lambda x: -sc[x]):
            best = None
            for j in range(len(gts)):
                if j in used or M[i, j] < .5: continue
                if best is None or M[i, j] > M[i, best]: best = j
            if best is not None: used[best] = i; tp.append(i)
        fps = []
        for i in range(len(pr)):
            if i in tp: continue
            j = int(np.argmax(M[i])) if len(gts) else -1
            m = float(M[i, j]) if j >= 0 else 0
            k = "duplicate" if m >= .5 else ("loc" if m >= .1 else "bg")
            c = np.mean(pr[i], axis=0)
            fps.append(dict(i=i, cls=k, best_iou=m, gt=j, score=sc[i], area=area(pr[i]), cx=float(c[0]), cy=float(c[1])))
        fns = []
        for j in range(len(gts)):
            if j in used: continue
            col = M[:, j]; m = float(col.max()) if len(pr) else 0
            ci = int(col.argmax()) if len(pr) else -1
            k = "loc" if m >= .1 and ci not in tp else ("merged" if m >= .1 else "miss")
            # overlap with other GT (touching / stacked)
            ov = max([iou(gts[j], gts[q]) for q in range(len(gts)) if q != j] or [0])
            c = np.mean(gts[j], axis=0)
            border = bool(c[0] < 40 or c[1] < 40 or c[0] > w - 40 or c[1] > h - 40)
            fns.append(dict(j=j, cls=k, best_iou=m, gt_overlap=ov, border=border, area=area(gts[j]), cx=float(c[0]), cy=float(c[1])))
        rows.append(dict(image=r["image"], bg=r.get("bg"), gt=len(gts), count=len(pr), tp=len(tp), fps=fps, fns=fns,
                         boxes=pr, scores=sc, gt_boxes=gts))
    return rows

def summary(rows):
    s = dict(images=len(rows), exact=sum(x["gt"] == x["count"] for x in rows),
             tp=sum(x["tp"] for x in rows), fp=sum(len(x["fps"]) for x in rows), fn=sum(len(x["fns"]) for x in rows))
    for key, name in (("fps", "fp"), ("fns", "fn")):
        c = {}
        for x in rows:
            for e in x[key]: c[e["cls"]] = c.get(e["cls"], 0) + 1
        s[name + "_classes"] = c
    s["fn_border"] = sum(e["border"] for x in rows for e in x["fns"])
    s["fn_overlapping_gt"] = sum(e["gt_overlap"] > 0.02 for x in rows for e in x["fns"])
    bgs = {}
    for x in rows:
        d = bgs.setdefault(str(x["bg"]), dict(n=0, exact=0, fp=0, fn=0))
        d["n"] += 1; d["exact"] += x["gt"] == x["count"]; d["fp"] += len(x["fps"]); d["fn"] += len(x["fns"])
    s["by_bg"] = bgs
    fs = [e["score"] for x in rows for e in x["fps"]]
    ts = [x["scores"][i] for x in rows for i in range(len(x["scores"])) if i not in {e["i"] for e in x["fps"]}]
    s["fp_score_mean"] = float(np.mean(fs)) if fs else None
    s["tp_score_mean"] = float(np.mean(ts)) if ts else None
    return s

if __name__ == "__main__":
    model, label, split = sys.argv[1], sys.argv[2], sys.argv[3]
    import re
    raw = sys.argv[4] if len(sys.argv) > 4 else "{}"
    opts = json.loads(re.sub(r'([{,]\s*)([A-Za-z_]\w*)\s*:', r'\1"\2":', raw))  # tolerate quote-stripped PowerShell args
    opts.setdefault("instance_mode", True)
    man = json.loads((SEG / "dataset/manifest.json").read_text())
    recs = [r for r in man["records"] if r["split"] == split]
    eng = SegNative(model, **opts)
    try:
        rows = analyse(eng, recs)
    finally:
        eng.close()
    OUT.mkdir(parents=True, exist_ok=True)
    s = summary(rows)
    (OUT / f"{label}_{split}.json").write_text(json.dumps(dict(summary=s, rows=rows)), encoding="utf-8")
    print(json.dumps(s, indent=1))
