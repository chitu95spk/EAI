#!/usr/bin/env python
"""CounterAI service: CLI that EAI calls as a subprocess. Every output line is one JSON object (UTF-8).

Commands:
  devices
  check   --images DIR --labels DIR
  count   --images FILE|DIR --weights W [--threshold 0.8] [--device AUTO] [--expected name=N ...] [--save-dir DIR]
  train   --images DIR --labels DIR --out W [--val NAME ...] [--holdout N] [--iters N] [--batch N]
          [--crop N] [--lr X] [--init W] [--device auto|cpu|cuda|xpu]
  prep    --image FILE --out-dir DIR [--weights W] [--threshold 0.8] [--device AUTO]   (labelling: enhanced crop + auto points)
  serve   --weights W [--threshold 0.8] [--device AUTO]   (stdin/stdout JSON lines; used by the C# CounterAiEngine)
  convert --weights W.pt [--out X.xml] [--fp32]

CounterAI is a point/center counter (FastNet). It is unrelated to the EAI Counter mode (Segment AI).
"""
import argparse
import json
import sys
import time
from pathlib import Path

import torch  # noqa: F401  (import before anything Qt-related on Windows)
import cv2
import numpy as np

import fast_counter as fc
import train_fastnet as tf


def emit(**kw):
    print(json.dumps(kw, ensure_ascii=False, default=lambda o: o.item() if hasattr(o, "item") else str(o)), flush=True)


def cmd_devices(a):
    emit(event="devices", devices=[{"key": k, "label": l} for k, l in tf.available_devices()],
         torch=torch.__version__, diameter=fc.DIAMETER)


def cmd_check(a):
    pairs = tf.find_pairs(a.images, a.labels, log=lambda s: None)
    ok, rows = tf.check(pairs, log=lambda s: None)
    emit(event="check", pairs=len(pairs), ok=bool(ok), rows=rows)


def cmd_count(a):
    expected = {}
    for item in a.expected:
        name, _, val = item.rpartition("=")
        expected[Path(name).stem] = int(val)
    files = fc.collect_images([a.images])
    if not files:
        emit(event="error", message="no images")
        return 2
    t0 = time.perf_counter()
    net = fc.load_net(a.weights, a.device)
    fc.count(np.zeros((300, 300), np.uint8), net)
    emit(event="loaded", seconds=round(time.perf_counter() - t0, 3), files=len(files))
    save = Path(a.save_dir) if a.save_dir else None
    if save:
        save.mkdir(parents=True, exist_ok=True)
    for f in files:
        gray = cv2.imread(str(f), cv2.IMREAD_GRAYSCALE)
        if gray is None:
            emit(event="item", name=f.name, error="unreadable")
            continue
        t = time.perf_counter()
        pts = fc.count(gray, net, a.threshold)
        ms = (time.perf_counter() - t) * 1000
        row = dict(event="item", name=f.name, count=int(len(pts)), ms=round(ms, 1),
                   points=[[int(x), int(y)] for x, y in pts])
        if f.stem in expected:
            row["expected"] = expected[f.stem]
            row["delta"] = int(len(pts)) - expected[f.stem]
        if save:
            out = save / f"{f.stem}_count.png"
            cv2.imwrite(str(out), fc.draw(gray, pts))
            row["image"] = str(out)
        emit(**row)
    emit(event="done")


def cmd_train(a):
    def progress(it, total, loss):
        emit(event="progress", it=it, total=total, loss=round(float(loss), 6))

    res = tf.run_training(a.images, a.labels, val=tuple(a.val), holdout=0 if a.val else a.holdout, iters=a.iters,
                          batch=a.batch, crop=a.crop, lr=a.lr, init=a.init, out=a.out,
                          log=lambda s: emit(event="log", text=s), progress=progress, device=a.device)

    def table(r):
        if not r:
            return None
        return [dict(threshold=float(t), mae=float(np.abs(e).mean()), bias=float(e.mean()),
                     max_err=int(np.abs(e).max()), f1=float(f1)) for t, (e, f1) in r.items()]

    emit(event="done", out=res["out"], stopped=res["stopped"], best=res["best"],
         train=table(res["train_res"]), val=table(res["val_res"]), n_train=res["n_train"], n_val=res["n_val"])


def cmd_prep(a):
    """Prepare one image for labelling: enhanced crop of the wire bundle (PNG) + optional auto points (original coords)."""
    gray = cv2.imread(str(a.image), cv2.IMREAD_GRAYSCALE)
    if gray is None:
        emit(event="error", message="unreadable image")
        return 2
    y0, y1, x0, x1 = fc.find_bundle_bbox(gray, margin=int(fc.DIAMETER * 1.1))
    out = Path(a.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    png = out / f"{Path(a.image).stem}_label.png"
    cv2.imwrite(str(png), fc.enhance(gray)[y0:y1, x0:x1])
    pts = []
    if a.weights and Path(a.weights).is_file():
        net = fc.load_net(a.weights, a.device)
        pts = [[int(x), int(y)] for x, y in fc.count(gray, net, a.threshold)]
    emit(event="prep", image=str(png), x0=int(x0), y0=int(y0), w=int(x1 - x0), h=int(y1 - y0),
         diameter=fc.DIAMETER, auto=pts)


def cmd_serve(a):
    """Persistent detector: load the net once, then answer JSON requests from stdin (one per line).

    Request : {"id": 1, "image": "path", "threshold": 0.8}   or {"cmd": "quit"}
    Response: {"event": "result", "id": 1, "count": N, "ms": T, "points": [[x, y], ...]}  /  {"event": "result", "id": 1, "error": "..."}
    """
    net = fc.load_net(a.weights, a.device)
    fc.count(np.zeros((300, 300), np.uint8), net)  # warm up
    emit(event="ready", diameter=fc.DIAMETER)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except ValueError:
            emit(event="result", error="bad json")
            continue
        if req.get("cmd") == "quit":
            break
        rid = req.get("id")
        try:
            gray = cv2.imread(str(req["image"]), cv2.IMREAD_GRAYSCALE)
            if gray is None:
                emit(event="result", id=rid, error="unreadable")
                continue
            t = time.perf_counter()
            pts = fc.count(gray, net, float(req.get("threshold", a.threshold)))
            emit(event="result", id=rid, count=int(len(pts)), ms=round((time.perf_counter() - t) * 1000, 1),
                 points=[[int(x), int(y)] for x, y in pts])
        except Exception as e:  # noqa: BLE001
            emit(event="result", id=rid, error=str(e))
    return 0


def cmd_convert(a):
    import fastnet_openvino as fo
    xml = fo.convert(a.weights, a.out, fp16=not a.fp32, log=lambda s: emit(event="log", text=s))
    emit(event="done", xml=str(xml))


def main():
    ap = argparse.ArgumentParser(description="CounterAI service")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("devices").set_defaults(fn=cmd_devices)
    p = sub.add_parser("check"); p.add_argument("--images", required=True); p.add_argument("--labels", required=True); p.set_defaults(fn=cmd_check)
    p = sub.add_parser("count")
    p.add_argument("--images", required=True); p.add_argument("--weights", required=True)
    p.add_argument("--threshold", type=float, default=0.8); p.add_argument("--device", default="AUTO")
    p.add_argument("--expected", action="append", default=[]); p.add_argument("--save-dir")
    p.set_defaults(fn=cmd_count)
    p = sub.add_parser("train")
    p.add_argument("--images", required=True); p.add_argument("--labels", required=True); p.add_argument("--out", required=True)
    p.add_argument("--val", action="append", default=[]); p.add_argument("--holdout", type=int, default=2)
    p.add_argument("--iters", type=int, default=1200); p.add_argument("--batch", type=int, default=6)
    p.add_argument("--crop", type=int, default=192); p.add_argument("--lr", type=float, default=3e-3)
    p.add_argument("--init"); p.add_argument("--device", default="auto", choices=["auto", "cpu", "cuda", "xpu"])
    p.set_defaults(fn=cmd_train)
    p = sub.add_parser("prep")
    p.add_argument("--image", required=True); p.add_argument("--out-dir", required=True)
    p.add_argument("--weights"); p.add_argument("--threshold", type=float, default=0.8); p.add_argument("--device", default="AUTO")
    p.set_defaults(fn=cmd_prep)
    p = sub.add_parser("serve")
    p.add_argument("--weights", required=True); p.add_argument("--threshold", type=float, default=0.8); p.add_argument("--device", default="AUTO")
    p.set_defaults(fn=cmd_serve)
    p = sub.add_parser("convert")
    p.add_argument("--weights", required=True); p.add_argument("--out"); p.add_argument("--fp32", action="store_true")
    p.set_defaults(fn=cmd_convert)
    a = ap.parse_args()
    try:
        return a.fn(a) or 0
    except SystemExit as e:
        emit(event="error", message=str(e.code))
        return 1
    except Exception as e:  # noqa: BLE001
        import traceback
        emit(event="error", message=str(e), trace=traceback.format_exc())
        return 1


if __name__ == "__main__":
    sys.exit(main())
