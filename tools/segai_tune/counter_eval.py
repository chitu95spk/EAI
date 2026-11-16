"""Evaluate a SegAI model (Counter project output) with the same contract as run_experiment.evaluate.
Does not touch REPORT.md. Usage:
  python -X utf8 counter_eval.py MODEL LABEL SPLIT[,SPLIT] [json-opts] [repeats]
SPLIT in tune|final|real. Output: C:\\Demo\\LucGiac.counter\\metrics\\LABEL_SPLIT.json
"""
import sys, json, time, os
from pathlib import Path
import numpy as np, cv2
from seg_native import SegNative
SEG = Path(r"C:\Demo\LucGiac.segai-tune")
OUT = Path(os.environ.get("CE_OUT") or r"C:\Demo\LucGiac.counter\metrics")  # CE_OUT: separate folder for backend comparisons

def iou(a, b):
    a = cv2.convexHull(np.asarray(a, np.float32)); b = cv2.convexHull(np.asarray(b, np.float32))
    z = float(cv2.intersectConvexConvex(a, b)[0])
    return z / max(cv2.contourArea(a) + cv2.contourArea(b) - z, 1e-9)

def evaluate(engine, records, path, repeats):
    images = [cv2.imread(r["image"]) for r in records]
    assert all(x is not None for x in images)
    for k in range(5): engine.run(images[k % len(images)])
    raw = []
    for j, (r, im) in enumerate(zip(records, images)):
        cycles = []; natives = []
        for _ in range(repeats):
            b = time.perf_counter(); p = engine.run(im); cycles.append((time.perf_counter() - b) * 1000)
            natives.append(p.get("native_ms"))
        used = set(); pairs = []
        order = sorted(range(p["count"]), key=lambda x: p["scores"][x], reverse=True)
        for ix in order:
            ch = [(iou(p["boxes"][ix], g), gi) for gi, g in enumerate(r["boxes"]) if gi not in used]
            if ch:
                v, gi = max(ch)
                if v >= .5: used.add(gi); pairs.append([ix, gi, v])
        raw.append(dict(image=r["image"], bg=r["bg"], gt=r["count"], count=p["count"], scores=p["scores"],
                        tp=len(pairs), fp=p["count"] - len(pairs), fn=r["count"] - len(pairs), pairs=pairs,
                        cycles_ms=cycles, native_ms=natives, phases=p.get("phases", {})))
    tp = sum(r["tp"] for r in raw); fp = sum(r["fp"] for r in raw); fn = sum(r["fn"] for r in raw)
    ps = [s for r in raw for s in r["scores"]]
    ts = [r["scores"][pr[0]] for r in raw for pr in r["pairs"]]
    times = [x for r in raw for x in r["cycles_ms"]]; nt = [x for r in raw for x in r["native_ms"] if x is not None]
    pr_ = tp / max(1, tp + fp); rc = tp / max(1, tp + fn)
    s = dict(images=len(raw), objects=sum(r["gt"] for r in raw), tp=tp, fp=fp, fn=fn, precision=pr_, recall=rc,
             f1=2 * pr_ * rc / max(pr_ + rc, 1e-9), count_exact=sum(r["count"] == r["gt"] for r in raw),
             all_matched=sum(r["fp"] == 0 and r["fn"] == 0 for r in raw),
             mean_score=float(np.mean(ps)) if ps else None, mean_tp_score=float(np.mean(ts)) if ts else None,
             p50_ms=float(np.median(times)), p95_ms=float(np.percentile(times, 95)),
             native_p50_ms=float(np.median(nt)) if nt else None, native_p95_ms=float(np.percentile(nt, 95)) if nt else None)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(dict(summary=s, raw=raw, repeats=repeats), indent=1), encoding="utf-8")
    return s

if __name__ == "__main__":
    model, label, splits = sys.argv[1], sys.argv[2], sys.argv[3].split(",")
    opts = json.loads(sys.argv[4]) if len(sys.argv) > 4 else {}
    rep = int(sys.argv[5]) if len(sys.argv) > 5 else 3
    man = json.loads((SEG / "dataset/manifest.json").read_text())
    import os, ctypes as C
    mask = os.environ.get("CE_AFFINITY")  # hex logical-CPU mask, e.g. 555 = six P-core threads (one per core)
    if mask:
        k32 = C.WinDLL("kernel32", use_last_error=True)
        k32.GetCurrentProcess.restype = C.c_void_p
        k32.SetProcessAffinityMask.argtypes = [C.c_void_p, C.c_size_t]
        assert k32.SetProcessAffinityMask(k32.GetCurrentProcess(), C.c_size_t(int(mask, 16))), C.get_last_error()
    eng = SegNative(model, **opts)
    try:
        for sp in splits:
            recs = [r for r in man["records"] if r["split"] == sp]
            s = evaluate(eng, recs, OUT / f"{label}_{sp}.json", 15 if sp == "real" else rep)
            print(label, sp, json.dumps(s), flush=True)
    finally:
        eng.close()
