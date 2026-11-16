"""Benchmark Easy AI: synth -> train -> IoU that matches boxes (OBB), with timings.

Mean IoU = average IoU of predictions matched (greedy by confidence, IoU>=0.5) to ground truth on the held-out val set.
Also prints precision / recall / F1 at IoU 0.5 and the share of GT with IoU>=0.9.
Usage: python bench_iou.py --dir D:\\Demo\\LucGiac --out C:\\Demo\\bench1 [--model yolo11n-obb.pt --n-train 300 --n-test 100 --epochs 40 --imgsz 640]
       python bench_iou.py --eval-only --weights best.pt --data C:\\Demo\\bench1\\ds
"""
import argparse
import json
import sys
import time
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))


def poly_iou(a, b):
    a = a.astype(np.float32).reshape(-1, 2)
    b = b.astype(np.float32).reshape(-1, 2)
    inter, _ = cv2.intersectConvexConvex(a, b)
    if inter <= 0:
        return 0.0
    ua = cv2.contourArea(a) + cv2.contourArea(b) - inter
    return float(inter / ua) if ua > 0 else 0.0


def read_gt(lab: Path, w, h):
    out = []
    if lab.exists():
        for ln in lab.read_text(encoding="utf-8").splitlines():
            v = ln.split()
            if len(v) >= 9:
                p = np.array(v[1:9], dtype=np.float32).reshape(4, 2)
                p[:, 0] *= w
                p[:, 1] *= h
                out.append(p)
    return out


def evaluate(weights, ds: Path, imgsz, conf=0.25, split="val"):
    from ultralytics import YOLO

    model = YOLO(str(weights))
    imgs = sorted((ds / "images" / split).glob("*.*"))
    ious, tp, fp, fn, n_gt = [], 0, 0, 0, 0
    t0 = time.perf_counter()
    for p in imgs:
        im = cv2.imread(str(p))
        h, w = im.shape[:2]
        gts = read_gt(ds / "labels" / split / (p.stem + ".txt"), w, h)
        r = model.predict(str(p), conf=conf, imgsz=imgsz, verbose=False)[0]
        preds = []
        if r.obb is not None and len(r.obb):
            P = r.obb.xyxyxyxy.cpu().numpy()
            C = r.obb.conf.cpu().numpy()
            preds = [P[i] for i in np.argsort(-C)]
        used = set()
        for pr in preds:
            best, bj = 0.0, -1
            for j, g in enumerate(gts):
                if j in used:
                    continue
                v = poly_iou(pr, g)
                if v > best:
                    best, bj = v, j
            if best >= 0.5:
                used.add(bj)
                tp += 1
                ious.append(best)
            else:
                fp += 1
        fn += len(gts) - len(used)
        n_gt += len(gts)
    prec = tp / max(tp + fp, 1)
    rec = tp / max(n_gt, 1)
    return {
        "images": len(imgs), "gt": n_gt, "tp": tp, "fp": fp, "fn": fn,
        "mean_iou_matched": round(float(np.mean(ious)), 4) if ious else 0.0,
        "median_iou": round(float(np.median(ious)), 4) if ious else 0.0,
        "gt_iou_ge_0.9": round(sum(1 for v in ious if v >= 0.9) / max(n_gt, 1), 4),
        "precision": round(prec, 4), "recall": round(rec, 4),
        "f1": round(2 * prec * rec / max(prec + rec, 1e-9), 4),
        "eval_s": round(time.perf_counter() - t0, 1),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir")
    ap.add_argument("--out", default=r"C:\Demo\bench1")
    ap.add_argument("--model", default=str(HERE / "yolo11n-obb.pt"))
    ap.add_argument("--n-train", type=int, default=300)
    ap.add_argument("--n-test", type=int, default=100)
    ap.add_argument("--epochs", type=int, default=40)
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--eval-only", action="store_true")
    ap.add_argument("--weights")
    ap.add_argument("--data")
    a = ap.parse_args()
    if a.eval_only:
        print(json.dumps(evaluate(a.weights, Path(a.data), a.imgsz), indent=1))
        return 0
    out = Path(a.out)
    ds = out / "ds"
    T = {}
    t = time.perf_counter()
    import synthgen

    class Ev:
        def emit(self, *x, **k):
            pass

    if not (ds / "data.yaml").exists():
        synthgen.generate(Path(a.dir), ds, a.n_train, a.n_test, a.seed, Ev())
    T["synth_s"] = round(time.perf_counter() - t, 1)
    from ultralytics import YOLO

    t = time.perf_counter()
    m = YOLO(a.model)
    m.train(data=str(ds / "data.yaml"), epochs=a.epochs, imgsz=a.imgsz, batch=a.batch, device=0, workers=4, cache="ram",
            patience=0, plots=False, fliplr=0.5, flipud=0.5, degrees=180, scale=0.4, project=str(out / "runs"), name="t", exist_ok=True,
            val=False, amp=True)
    T["train_s"] = round(time.perf_counter() - t, 1)
    w = out / "runs" / "t" / "weights" / "last.pt"
    res = evaluate(w, ds, a.imgsz)
    T["total_s"] = round(T["synth_s"] + T["train_s"] + res["eval_s"], 1)
    rep = {"args": vars(a), "time": T, "result": res}
    (out / "bench.json").write_text(json.dumps(rep, indent=1), encoding="utf-8")
    print("BENCH", json.dumps(rep))
    return 0


if __name__ == "__main__":
    sys.exit(main())
