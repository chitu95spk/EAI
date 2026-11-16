"""EasyAI auto loop: labelme (SAM-confirmed boxes) -> synthetic data -> YOLO-OBB train -> harness eval -> repeat until target.

Run:  python -I autoloop.py --dir "<project images folder>" [--out <dir>] [--max-rounds 0]
      [--model yolo11s-obb.pt] [--start-rung 0] [--cycles 1] [--target-step 0.0]
  --cycles N      repeat the whole improvement loop N times; cycle k starts k rungs higher and raises the target by k*--target-step.
  --max-rounds R  rounds per cycle (0 = until the cycle's target is reached).
Stop early: create a file named STOP in the run folder.

Honest-test design (one labelled image only):
  * Screws are split by x: the right ~25% become the HELD-OUT test set, the rest is the train set.
  * Train images are synthesised only from pixels left of the split (test region painted out), so no test pixel is seen.
  * Test tiles are cut at native scale from the held-out region. Metrics are measured there, never on synthetic data.
  * Other images in the folder (no labels) get a qualitative overlay + count only.
shortcut: tile-level metric, objects crossing a test-tile border are dropped; upgrade to full-image matching when a labelled 2nd image exists.
"""
import argparse, json, math, os, random, shutil, sys, time
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))  # python -I does not put the script folder on sys.path
import synthgen  # noqa: E402

N_TEST_IMAGES = 100  # labelled synthetic test images, objects and seeds disjoint from the training set
TARGET = {"map50": 0.95, "recall": 0.98}  # default target
SCHEDULE = [  # (model, synth images, epochs) ; the last row repeats with growing size
    ("yolo11n-obb.pt", 300, 60),
    ("yolo11s-obb.pt", 600, 120),
    ("yolo11s-obb.pt", 1000, 180),
    ("yolo11m-obb.pt", 1500, 225),
]
TILE = 1024


def log(msg):
    print(time.strftime("%H:%M:%S"), msg, flush=True)


def load_labelme(json_path):
    d = json.loads(Path(json_path).read_text(encoding="utf-8-sig"))
    shapes = []
    for s in d["shapes"]:
        pts = np.array(s["points"], dtype=np.float64)
        if s["shape_type"] == "oriented_rectangle" and len(pts) == 4:
            pass
        elif s["shape_type"] == "rectangle" and len(pts) == 2:
            (x0, y0), (x1, y1) = pts
            pts = np.array([[x0, y0], [x1, y0], [x1, y1], [x0, y1]], dtype=np.float64)
        elif s["shape_type"] == "polygon" and len(pts) >= 3:
            pts = cv2.boxPoints(cv2.minAreaRect(pts.astype(np.float32))).astype(np.float64)
        else:
            continue
        shapes.append((s["label"], pts))
    if not shapes:
        raise SystemExit("No usable shapes in " + str(json_path))
    return shapes


def clip_obb(pts, w, h):
    """Corners clipped to the image; None when <60% of the object remains visible (then it would be a false negative)."""
    full = abs(cv2.contourArea(pts.astype(np.float32)))
    if full < 16:
        return None
    img = np.array([[0, 0], [w, 0], [w, h], [0, h]], dtype=np.float32)
    area, inter = cv2.intersectConvexConvex(img, pts.astype(np.float32))
    if area <= 0 or area / full < 0.6:
        return None
    if area / full > 0.999:
        return pts
    return cv2.boxPoints(cv2.minAreaRect(inter.reshape(-1, 2))).astype(np.float64)


def write_pair(img_dir, lbl_dir, name, img, objs, cls):
    h, w = img.shape[:2]
    cv2.imwrite(str(img_dir / (name + ".jpg")), img, [cv2.IMWRITE_JPEG_QUALITY, 95])
    lines = []
    for label, pts in objs:
        p = clip_obb(pts, w, h)
        if p is None:
            continue
        lines.append(str(cls[label]) + " " + " ".join(f"{min(max(v, 0), 1):.6f}" for v in (p / [w, h]).reshape(-1)))
    (lbl_dir / (name + ".txt")).write_text("\n".join(lines), encoding="utf-8")
    return len(lines)


GAP = 40  # px kept empty between train and test pixels


def split_at(shapes, thr):
    train = [s for s in shapes if s[1][:, 0].max() <= thr - GAP]
    test = [s for s in shapes if s[1][:, 0].min() >= thr]  # objects in the gap are dropped from both sets
    return train, test


def split(shapes):
    """Best vertical cut: needs >=4 test and >=8 train objects, maximises the smaller side then the kept total."""
    best = None
    lo = min(p[:, 0].min() for _, p in shapes)
    hi = max(p[:, 0].max() for _, p in shapes)
    for thr in np.linspace(lo, hi, 200):
        train, test = split_at(shapes, thr)
        if len(test) < 4 or len(train) < 8:
            continue
        score = (min(len(train), len(test) * 2), len(train) + len(test))
        if best is None or score > best[0]:
            best = (score, thr, train, test)
    if best is None:
        raise SystemExit("Cannot hold out >=4 test objects and keep >=8 for training on one cut.")
    return best[1], best[2], best[3]


def transpose_all(img, shapes):
    return cv2.transpose(img), [(l, p[:, ::-1].copy()) for l, p in shapes]


def synth_one(img, train, thr, rng, size=TILE):
    h, w = img.shape[:2]
    s = rng.uniform(0.45, 1.3)
    a = rng.uniform(-180, 180)
    flip = rng.random() < 0.5
    cx, cy = rng.uniform(0, thr), rng.uniform(0, h)  # crop centre in the train region
    M = cv2.getRotationMatrix2D((cx, cy), a, s)
    M[0, 2] += size / 2 - cx + rng.uniform(-size * 0.15, size * 0.15)
    M[1, 2] += size / 2 - cy + rng.uniform(-size * 0.15, size * 0.15)
    if flip:
        F = np.array([[-1, 0, size], [0, 1, 0]], dtype=np.float64)
        M = (np.vstack([F, [0, 0, 1]]) @ np.vstack([M, [0, 0, 1]]))[:2]
    out = cv2.warpAffine(img, M, (size, size), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=bg_value(img))
    gain = rng.uniform(0.8, 1.2)
    out = np.clip(out.astype(np.float32) * gain + rng.uniform(-12, 12), 0, 255)
    out += np.random.default_rng(rng.randrange(1 << 30)).normal(0, rng.uniform(0, 4), out.shape)
    out = np.clip(out, 0, 255).astype(np.uint8)
    objs = [(l, cv2.transform(p.reshape(1, -1, 2), M).reshape(-1, 2)) for l, p in train]
    return out, objs


_BG = {}


def bg_value(img):
    k = id(img)
    if k not in _BG:
        _BG[k] = tuple(float(v) for v in np.median(img.reshape(-1, 3), axis=0))
    return _BG[k]


def build_dataset(root, img, train, test, thr, n_synth, seed, cls):
    for sub in ("images/train", "images/val", "labels/train", "labels/val"):
        (root / sub).mkdir(parents=True, exist_ok=True)
    h, w = img.shape[:2]
    masked = img.copy()
    masked[:, int(max(0, thr - 40)):] = np.array(bg_value(img), dtype=np.uint8)  # test region never reaches training
    rng = random.Random(seed)
    kept = 0
    for i in range(n_synth):
        o, objs = synth_one(masked, train, max(thr - 40, 64), rng)
        kept += write_pair(root / "images/train", root / "labels/train", f"syn_{i:05d}", o, objs, cls)
    # test tiles: native scale, taken from the held-out strip only (x >= thr).
    # Every test object must land fully inside some tile; tiles are placed around the objects still uncovered, so no object is silently dropped
    # (the old fixed top/bottom strips lost objects in the middle, leaving 1 of 4 test objects and a random-looking metric).
    x0 = int(thr)
    tiles, evaluated, uncovered = 0, 0, []
    pending = list(test)
    while pending:
        lab, p = pending[0]
        cx, cy = (p[:, 0].min() + p[:, 0].max()) / 2, (p[:, 1].min() + p[:, 1].max()) / 2
        tx = int(min(max(cx - TILE / 2, x0), max(x0, w - TILE)))
        ty = int(min(max(cy - TILE / 2, 0), max(0, h - TILE)))
        tx1, ty1 = min(w, tx + TILE), min(h, ty + TILE)
        inside = [(l, q) for l, q in pending if q[:, 0].min() >= tx and q[:, 0].max() <= tx1 and q[:, 1].min() >= ty and q[:, 1].max() <= ty1]
        if not inside:  # object larger than a tile: cannot be evaluated at native scale
            uncovered.append(pending.pop(0))
            continue
        inside_ids = {id(q) for _, q in inside}
        pending = [o for o in pending if id(o[1]) not in inside_ids]
        crop = img[ty:ty1, tx:tx1].copy()
        evaluated += write_pair(root / "images/val", root / "labels/val", f"test_{tx}_{ty}", crop, [(l, q - [tx, ty]) for l, q in inside], cls)
        tiles += 1
    if evaluated < len(test):
        log(f"WARNING: only {evaluated}/{len(test)} test objects are in the validation labels ({len(uncovered)} larger than a {TILE}px tile) - metrics cover fewer objects than reported")
    return kept, tiles, evaluated


def evaluate(model, data_yaml, imgsz):
    r = model.val(data=str(data_yaml), imgsz=imgsz, split="val", verbose=False, plots=False, conf=0.25, iou=0.5)
    return {"map50": float(r.box.map50), "map50_95": float(r.box.map), "precision": float(r.box.mp), "recall": float(r.box.mr)}


def reached(m, target):
    return m["map50"] >= target["map50"] and m["recall"] >= target["recall"]


def overlay_others(model, folder, ref_name, out_dir, imgsz):
    for p in sorted(Path(folder).glob("*")):
        if p.suffix.lower() not in (".bmp", ".png", ".jpg", ".jpeg") or p.name == ref_name:
            continue
        r = model.predict(str(p), imgsz=imgsz, conf=0.25, verbose=False)[0]
        n = 0 if r.obb is None else len(r.obb)
        cv2.imwrite(str(out_dir / ("overlay_" + p.stem + ".jpg")), r.plot(line_width=3, labels=False), [cv2.IMWRITE_JPEG_QUALITY, 85])
        log(f"qualitative {p.name}: {n} detections -> overlay_{p.stem}.jpg")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True, help="folder with the labelled image + its labelme .json (read only)")
    ap.add_argument("--out", default=str(Path(__file__).resolve().parents[2] / "artifacts" / "EAI-AUTOLOOP"))
    ap.add_argument("--run", default=None, help="use this run folder (the app pre-creates it and writes autoloop.log there)")
    ap.add_argument("--max-rounds", type=int, default=0, help="rounds per cycle; 0 = no limit")
    ap.add_argument("--model", default=None, help="YOLO-OBB weights used for every round, e.g. yolo11s-obb.pt (default: follow SCHEDULE)")
    ap.add_argument("--start-rung", type=int, default=0, help="first SCHEDULE row of cycle 1; higher = harder baseline (more synth images and epochs)")
    ap.add_argument("--cycles", type=int, default=1, help="repeat the whole improvement loop; each new cycle starts one rung higher")
    ap.add_argument("--target-map50", type=float, default=TARGET["map50"])
    ap.add_argument("--target-recall", type=float, default=TARGET["recall"])
    ap.add_argument("--target-step", type=float, default=0.0, help="added to the target map50 and recall each new cycle (raises the bar)")
    ap.add_argument("--smoke", action="store_true", help="tiny run to prove the pipeline")
    a = ap.parse_args()

    folder = Path(a.dir)
    jsons = [j for j in folder.glob("*.json")]
    ref = None
    for j in jsons:
        for ext in (".bmp", ".png", ".jpg", ".jpeg"):
            if j.with_suffix(ext).exists():
                ref = (j, j.with_suffix(ext))
                break
        if ref:
            break
    if not ref:
        raise SystemExit("No image+labelme .json pair found in " + str(folder))
    shapes = load_labelme(ref[0])
    img = cv2.imread(str(ref[1]), cv2.IMREAD_COLOR)
    labels = sorted({l for l, _ in shapes})
    cls = {l: i for i, l in enumerate(labels)}
    cands = []
    for axis in (0, 1):  # axis 1 = cut horizontally (image transposed)
        i2, s2 = (img, shapes) if axis == 0 else transpose_all(img, shapes)
        try:
            cands.append((axis, i2, s2) + split(s2))
        except SystemExit:
            pass
    if not cands:
        raise SystemExit("Cannot hold out >=4 test objects and keep >=8 for training on either axis.")
    axis, img, shapes, thr, train, test = max(cands, key=lambda c: (min(len(c[4]), len(c[5]) * 2), len(c[4]) + len(c[5])))
    log(f"reference {ref[1].name}: {len(shapes)} objects -> train {len(train)}, test {len(test)} (dropped {len(shapes) - len(train) - len(test)} in the gap), cut={'x' if axis == 0 else 'y'}={thr:.0f}")

    run = Path(a.run) if a.run else Path(a.out) / ("run-" + time.strftime("%Y%m%d-%H%M%S"))
    run.mkdir(parents=True, exist_ok=bool(a.run))
    (run / "RUNNING").write_text(str(os.getpid()))  # the app reads this pid to tell running from crashed
    import atexit
    atexit.register(lambda: (run / "RUNNING").unlink(missing_ok=True))
    from ultralytics import YOLO

    if a.cycles < 1 or a.start_rung < 0 or a.max_rounds < 0 or a.target_step < 0:
        raise SystemExit("--cycles must be >= 1; --start-rung, --max-rounds and --target-step must be >= 0.")
    last = len(SCHEDULE) - 1
    history, best = [], None
    rnd = 0  # global round counter: folder names and seeds never repeat across cycles
    stop = False
    for cycle in range(a.cycles):
        target = {"map50": min(1.0, a.target_map50 + a.target_step * cycle),
                  "recall": min(1.0, a.target_recall + a.target_step * cycle)}
        log(f"cycle {cycle + 1}/{a.cycles}: start rung {a.start_rung + cycle}, target map50>={target['map50']:.3f} recall>={target['recall']:.3f}")
        cyc_hist, cycle_round = [], 0
        while True:
            if (run / "STOP").exists():
                log("STOP file found, ending.")
                stop = True
                break
            if a.max_rounds and cycle_round >= a.max_rounds:
                log(f"cycle {cycle + 1}: max rounds per cycle reached")
                break
            this_rnd = rnd
            rnd += 1
            cycle_round += 1
            rung = a.start_rung + cycle + (cycle_round - 1)
            name, n_synth, epochs = SCHEDULE[min(rung, last)]
            if rung > last:
                extra = rung - last
                n_synth += 500 * extra
                epochs += 75 * extra
            if a.model:
                name = a.model
            if a.smoke:
                name, n_synth, epochs = "yolo11n-obb.pt", 40, 3
            root = run / f"r{this_rnd}" / "data"
            n_test_imgs = 20 if a.smoke else N_TEST_IMAGES
            log(f"round {this_rnd}: generating {n_synth} train + {n_test_imgs} test images (cut-out objects on varied backgrounds)")
            summary, meta = synthgen.generate(folder, root, n_synth, n_test_imgs, 1000 + this_rnd, synthgen.Events(run / "events.jsonl"),
                                              img_path=ref[1], json_path=ref[0])
            kept, evaluated = summary["train"]["objects_total"], summary["test"]["objects_total"]
            seen_names = {m["name"] for m in meta["test"] if m["bg"] in synthgen.TRAIN_BG}
            for part, sel in (("seen", lambda n: n in seen_names), ("unseen", lambda n: n not in seen_names)):
                lst = [str(root / "images" / "val" / (m["name"] + ".jpg")) for m in meta["test"] if sel(m["name"])]
                (root / f"val_{part}.txt").write_text("\n".join(lst), encoding="utf-8")
                (root / f"data_{part}.yaml").write_text(
                    f"path: {root.as_posix()}\ntrain: images/train\nval: val_{part}.txt\nnames:\n" + "".join(f"  {i}: {l}\n" for l, i in cls.items()),
                    encoding="utf-8")
            log("leakage check " + json.dumps(summary["leakage"]))
            tiles = n_test_imgs
            log(f"cycle {cycle + 1} round {this_rnd}: {name}, {n_synth} synth train images ({kept} labels), {n_test_imgs} labelled test images ({evaluated} labels, test objects never used in training), {epochs} epochs")
            model = YOLO(name)
            model.train(data=str(root / "data.yaml"), epochs=epochs, imgsz=TILE, batch=8, device=0, workers=2, project=str(run / f"r{this_rnd}"),
                        name="train", exist_ok=True, patience=0, plots=False, verbose=False, fliplr=0.5, flipud=0.5, degrees=180, scale=0.4)
            wpath = run / f"r{this_rnd}" / "train" / "weights" / "best.pt"
            best_model = YOLO(str(wpath))
            m = evaluate(best_model, root / "data.yaml", TILE)
            for part in ("seen", "unseen"):
                if (root / f"val_{part}.txt").read_text(encoding="utf-8").strip():
                    sub = evaluate(best_model, root / f"data_{part}.yaml", TILE)
                    m[part + "_map50"], m[part + "_recall"], m[part + "_precision"] = sub["map50"], sub["recall"], sub["precision"]
            m.update(round=this_rnd, cycle=cycle + 1, model=name, synth=n_synth, epochs=epochs, target=target, weights=str(wpath),
                     test_evaluated=evaluated, test_total=evaluated, test_images=n_test_imgs)
            history.append(m)
            cyc_hist.append(m)
            log("metrics " + json.dumps({k: (round(v, 4) if isinstance(v, float) else v) for k, v in m.items() if k not in ("weights", "target")}))
            if best is None or (m["map50"], m["recall"]) > (best["map50"], best["recall"]):
                best = m
                shutil.copy(wpath, run / "best.pt")
            report_target = {"map50": a.target_map50, "recall": a.target_recall, "step": a.target_step}
            tmp = run / "report.json.tmp"  # atomic replace: the app polls report.json while the trainer is writing it
            tmp.write_text(json.dumps({"target": report_target, "cycles": a.cycles, "history": history, "best": best,
                                       "test_objects": evaluated, "test_images": n_test_imgs}, indent=2), encoding="utf-8")
            os.replace(tmp, run / "report.json")
            if reached(m, target):
                log(f"cycle {cycle + 1}: TARGET REACHED at round {this_rnd}")
                break
            if len(cyc_hist) >= 4 and max(h["map50"] for h in cyc_hist[-3:]) <= max(h["map50"] for h in cyc_hist[:-3]) + 0.005:
                log("WARNING: no improvement in 3 rounds (still looping, create STOP to end)")
        if stop:
            break

    if best:
        overlay_others(YOLO(str(run / "best.pt")), folder, ref[1].name, run, 1280)
    log(f"done. best={json.dumps({k: round(v, 4) for k, v in best.items() if isinstance(v, float)}) if best else None}; run={run}")


if __name__ == "__main__":
    main()
