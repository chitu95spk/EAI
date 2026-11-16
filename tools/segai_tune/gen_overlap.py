"""Synthetic images with OVERLAPPING (piled) screws for the Counter experiments (round 5, project c2g_ov).

Same patches / backgrounds / lighting pipeline as tools/autoloop/synthgen.compose, but the placement loop is replaced:
  * a new screw may cover a large part of earlier screws (cap `ov_max` of its own area, 0.15..0.70 per image),
  * every earlier screw must keep >= MIN_VISIBLE of its pixels visible (a human would not label a screw that is ~hidden),
  * clustered placement: most screws are dropped near an existing screw, with random / near-parallel / crossing angles,
  * labels = the full oriented box of the whole screw (as the real labelled photo does for occluded screws), later screws on top.
Usage: python -X utf8 gen_overlap.py SPLIT N SEEDBASE OUTDIR
  SPLIT = train (train source screws, TRAIN_BG families)  |  tune (tune source screw, TRAIN_BG)  |  final (final source screws, all families)
Writes OUTDIR\\img\\ov_NNNN.png and OUTDIR\\manifest.json (records shaped like the frozen manifest). Asserts: no seed / sha256 overlap with the
frozen manifest and the other ov sets, and source-screw isolation (train sources vs tune/final).
"""
from pathlib import Path
import sys, json, math, random
import numpy as np, cv2
sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds
sg = ds.sg

MIN_VISIBLE = 0.40


def pose_matrix_ang(patch, target_len, rng, cx, cy, ang):
    """synthgen.pose_matrix with the in-plane angle given (instead of uniform 0..360)."""
    ph, pw = patch["img"].shape[:2]
    s = target_len / max(patch["long"], 1.0) * rng.uniform(0.9, 1.1)
    tilt = rng.uniform(0.82, 1.0); tilt_ang = rng.uniform(0, math.pi); shear = rng.uniform(-0.12, 0.12)
    flip_x = -1.0 if rng.random() < 0.5 else 1.0; flip_y = -1.0 if rng.random() < 0.5 else 1.0
    def rot(a):
        c, s_ = math.cos(a), math.sin(a); return np.array([[c, -s_], [s_, c]])
    A = rot(math.radians(ang)) @ rot(tilt_ang) @ np.diag([1.0, tilt]) @ rot(-tilt_ang) @ np.array([[1, shear], [0, 1]]) @ np.diag([flip_x, flip_y]) * s
    centre = np.array([pw / 2.0, ph / 2.0]); t = np.array([cx, cy]) - A @ centre
    return np.hstack([A, t[:, None]])


def obb_axis_angle(obb):
    """long-axis angle (deg) of an oriented box given as 4 corners"""
    p = np.asarray(obb); e1, e2 = p[1] - p[0], p[3] - p[0]
    e = e1 if np.linalg.norm(e1) >= np.linalg.norm(e2) else e2
    return math.degrees(math.atan2(e[1], e[0]))


def compose_overlap(patches, bg_kind, rng, py_rng):
    w, h = sg.CANVAS_W, sg.CANVAS_H
    obj_mean = np.mean([p["mean"] for p in patches], axis=0)
    canvas = sg.make_bg(bg_kind, rng, obj_mean)
    r = rng.random()
    if r < 0.15: mode, n_target = "sparse", int(rng.integers(3, 9))       # few screws, strongly piled
    elif r < 0.60: mode, n_target = "medium", int(rng.integers(8, 19))
    else: mode, n_target = "dense", int(rng.integers(16, 33))
    lo, hi = sg.TARGET_LEN[mode]
    if mode == "sparse": lo, hi = 120, 240
    base_len = rng.uniform(lo, hi)
    ov_max = float(rng.choice([0.10, 0.20, 0.35, 0.55]))
    p_cluster = float(rng.choice([0.4, 0.7, 0.85]))
    p_parallel = float(rng.choice([0.2, 0.5]))
    own = np.full((h, w), -1, np.int16)          # id of the topmost screw per pixel
    placed = []; areas = []; vis = []             # vis[i] = still-visible pixel count of screw i
    attempts = 0
    while len(placed) < n_target and attempts < n_target * 80:
        attempts += 1
        patch = patches[int(rng.integers(0, len(patches)))]
        tl = base_len * rng.uniform(0.85, 1.15)
        ang = rng.uniform(0, 360)
        if placed and rng.random() < p_cluster:
            ref = placed[int(rng.integers(0, len(placed)))]
            rc = np.asarray(ref["obb"]).mean(axis=0)
            d = rng.uniform(0.20, 0.90) * tl; a = rng.uniform(0, 2 * math.pi)
            cx, cy = rc[0] + d * math.cos(a), rc[1] + d * math.sin(a)
            if rng.random() < p_parallel:
                ang = obb_axis_angle(ref["obb"]) + rng.normal(0, 14) + (180 if rng.random() < 0.5 else 0)
        else:
            margin = 0.25 * tl if rng.random() < 0.15 else -0.35 * tl
            cx = rng.uniform(-margin, w + margin); cy = rng.uniform(-margin, h + margin)
        M = pose_matrix_ang(patch, tl, rng, cx, cy, ang)
        rr = sg.render_object(patch, M, w, h)
        if rr is None: continue
        x0, y0, bgr, alpha, poly = rr
        obb = sg.clip_obb(cv2.boxPoints(cv2.minAreaRect(poly.astype(np.float32))).astype(np.float64), w, h)
        if obb is None: continue
        sel = alpha > 128; area = int(sel.sum())
        if area < 30: continue
        roi = own[y0:y0 + alpha.shape[0], x0:x0 + alpha.shape[1]]
        under = roi[sel]; hit = under >= 0
        if hit.sum() / area > ov_max: continue
        ok = True
        if hit.any():
            ids, cnt = np.unique(under[hit], return_counts=True)
            for i, c in zip(ids, cnt):
                if (vis[i] - c) < MIN_VISIBLE * areas[i]: ok = False; break
        if not ok: continue
        if hit.any():
            for i, c in zip(ids, cnt): vis[i] -= c
        k = len(placed)
        roi[sel] = k
        placed.append({"x0": x0, "y0": y0, "bgr": bgr, "alpha": alpha, "label": patch["label"], "obb": obb, "src": patch["id"], "gain": rng.uniform(0.82, 1.2)})
        areas.append(area); vis.append(area)

    # ---- everything below is identical to synthgen.compose (shadows, feather, lighting, blur, noise)
    shadow_strength = rng.uniform(0.0, 0.55)
    if shadow_strength > 0.05 and placed:
        sh = np.zeros((h, w), np.float32); dx, dy = rng.uniform(-14, 14), rng.uniform(-14, 14)
        for p in placed:
            a = p["alpha"].astype(np.float32) / 255.0
            xa, ya = int(round(p["x0"] + dx)), int(round(p["y0"] + dy))
            xs0, ys0 = max(0, xa), max(0, ya); xs1, ys1 = min(w, xa + a.shape[1]), min(h, ya + a.shape[0])
            if xs1 > xs0 and ys1 > ys0:
                sh[ys0:ys1, xs0:xs1] = np.maximum(sh[ys0:ys1, xs0:xs1], a[ys0 - ya:ys1 - ya, xs0 - xa:xs1 - xa])
        sh = cv2.GaussianBlur(sh, (0, 0), rng.uniform(4, 11))
        canvas = canvas * (1 - shadow_strength * sh)[..., None]; shadow_dir = (dx, dy)
    else:
        shadow_dir = (rng.uniform(-6, 6), rng.uniform(-6, 6))
    if placed:
        ct = np.zeros((h, w), np.float32); ao = np.zeros((h, w), np.float32)
        cdx, cdy = shadow_dir[0] * 0.3, shadow_dir[1] * 0.3
        for p in placed:
            a = p["alpha"].astype(np.float32) / 255.0
            for target, (ox, oy) in ((ct, (cdx, cdy)), (ao, (0.0, 0.0))):
                xa, ya = int(round(p["x0"] + ox)), int(round(p["y0"] + oy))
                xs0, ys0 = max(0, xa), max(0, ya); xs1, ys1 = min(w, xa + a.shape[1]), min(h, ya + a.shape[0])
                if xs1 > xs0 and ys1 > ys0:
                    target[ys0:ys1, xs0:xs1] = np.maximum(target[ys0:ys1, xs0:xs1], a[ys0 - ya:ys1 - ya, xs0 - xa:xs1 - xa])
        ct = cv2.GaussianBlur(ct, (0, 0), rng.uniform(1.4, 3.2)); ao = cv2.GaussianBlur(ao, (0, 0), rng.uniform(3.0, 6.0))
        canvas = canvas * (1 - rng.uniform(0.22, 0.5) * ct - rng.uniform(0.10, 0.25) * ao)[..., None]
    for p in placed:
        a = p["alpha"].astype(np.float32); ys, xs = p["y0"], p["x0"]
        a = cv2.erode(a, np.ones((3, 3), np.uint8)) if rng.random() < 0.7 else a
        a = (cv2.GaussianBlur(a, (0, 0), rng.uniform(0.6, 1.4)) / 255.0)[..., None]
        reg = canvas[ys:ys + a.shape[0], xs:xs + a.shape[1]]
        fg = np.clip(p["bgr"].astype(np.float32) * p["gain"], 0, 255)
        fg = sg.harmonize(fg, p["alpha"], reg.reshape(-1, 3).mean(axis=0), rng)
        soft = rng.uniform(0.0, 0.9)
        if soft > 0.25: fg = cv2.GaussianBlur(fg, (0, 0), soft)
        reg[:] = reg * (1 - a) + fg * a
    light = 0.78 + 0.45 * sg.lowfreq(rng, h, w, int(rng.integers(160, 420)))
    canvas = canvas * light[..., None]
    gamma = rng.uniform(0.75, 1.3); canvas = 255.0 * np.power(np.clip(canvas, 0, 255) / 255.0, gamma)
    contrast = rng.uniform(0.7, 1.15); canvas = (canvas - canvas.mean()) * contrast + canvas.mean()
    blur = 0.0
    if rng.random() < 0.35:
        blur = rng.uniform(0.6, 1.8); canvas = cv2.GaussianBlur(canvas, (0, 0), blur)
    noise = rng.uniform(0, 6); canvas = canvas + rng.normal(0, noise, canvas.shape).astype(np.float32)
    out = np.clip(canvas, 0, 255).astype(np.uint8)
    objs = [(p["label"], p["obb"]) for p in placed]
    covered = [1.0 - v / a for v, a in zip(vis, areas)]
    info = {"mode": mode, "count": len(objs), "bg": bg_kind, "sources": sorted({p["src"] for p in placed}), "blur": round(blur, 2), "noise": round(noise, 2),
            "ov_max": ov_max, "n_overlapped": int(sum(c > 0.02 for c in covered)), "max_covered": round(max(covered), 3) if covered else 0.0,
            "mean_covered": round(float(np.mean(covered)), 3) if covered else 0.0}
    return out, objs, info


def main():
    split, n, base, out = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), Path(sys.argv[4])
    man = json.loads((ds.OUT / "dataset" / "manifest.json").read_text())
    image = cv2.imread(str(ds.SOURCE)); objects = sg.load_polygons(ds.SOURCE.with_suffix(".json"))
    bgm = sg.estimate_background(image, objects); masks = sg.all_mattes(image, objects, bgm)
    bad = sg.touching_ids(objects, masks)
    clean = sorted([o for o in objects if o["id"] not in bad], key=lambda o: o["poly"][:, 0].mean())
    groups = {"train": clean[:-3], "tune": clean[-3:-2], "final": clean[-2:]}
    assert [o["id"] for o in groups["train"]] == man["source_groups"]["train"]
    assert [o["id"] for o in groups["tune"]] == man["source_groups"]["tune"]
    assert [o["id"] for o in groups["final"]] == man["source_groups"]["final"]
    patches = [sg.cut_patch(image, o, bg=bgm, mask_full=masks.get(o["id"])) for o in groups[split]]
    fam = sg.TEST_BG if split == "final" else sg.TRAIN_BG
    (out / "img").mkdir(parents=True, exist_ok=True)
    recs = []
    for i in range(n):
        seed = base + i
        kind = (sg.UNSEEN_BG[(i // 2) % len(sg.UNSEEN_BG)] if split == "final" and i % 2 == 0 else sg.TRAIN_BG[i % len(sg.TRAIN_BG)])
        img, objs, info = compose_overlap(patches, kind, np.random.default_rng(seed), random.Random(seed))
        ip = out / "img" / f"ov_{i:04d}.png"; cv2.imwrite(str(ip), img)
        recs.append(dict(split=split, index=i, seed=seed, image=str(ip), sha256=ds.sha(ip), boxes=[np.asarray(pts).tolist() for label, pts in objs], **info))
        print(f"{i+1}/{n} objects={len(objs)} overlapped={info['n_overlapped']} bg={kind} mode={info['mode']}", flush=True)
    others = man["records"]
    assert not ({r["seed"] for r in recs} & {r["seed"] for r in others if "seed" in r}), "seed overlap"
    assert not ({r["sha256"] for r in recs} & {r["sha256"] for r in others}), "hash overlap"
    mine = {x for r in recs for x in r["sources"]}
    allowed = {o["id"] for o in groups[split]}
    assert mine <= allowed, "source outside the split's group"
    if split == "train":
        tf = {x for r in others if r["split"] in ("tune", "final") for x in r["sources"]}
        assert not (mine & tf), "source overlap with tune/final"
    (out / "manifest.json").write_text(json.dumps(dict(base_seed=base, n=n, split=split, leakage="passed (seed, sha256, sources vs frozen manifest)", records=recs), indent=1), encoding="utf-8")
    print("OVERLAP SET OK", split, len(recs), "mean n_overlapped", np.mean([r["n_overlapped"] for r in recs]), flush=True)


if __name__ == "__main__":
    main()

