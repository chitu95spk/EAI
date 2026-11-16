"""Synthetic OBB dataset generator for the self-learning (auto loop) flow.

Cuts the labelled objects out of ONE labelled image (labelme polygons = exact masks), then composes new images:
many / few objects per image, full-rotation poses, flips, shear, tilt, soft shadows, uneven lighting and hard procedural backgrounds
(uneven, bright, dark, low contrast, other colours, stripes, grid, granite).

Hold-out: objects are split by x position into TRAIN / GAP / TEST. Training images only contain TRAIN objects and TRAIN background families
with their own seeds; the test images only contain TEST objects, with extra background families the training set never saw, and other seeds.
A leakage report (disjoint objects, disjoint image hashes, disjoint seeds) is written next to the dataset.

Events (one JSON per line, run/events.jsonl) feed the live UI: gen_start, gen_image, gen_done.
"""
import argparse, hashlib, json, math, random, sys, time
from pathlib import Path

import cv2
import numpy as np

CANVAS_W, CANVAS_H = 1280, 1024
GAP = 40  # px kept empty between train and test objects in the source image

TRAIN_BG = ["flat", "gradient", "spot", "blotchy", "dark", "bright", "lowcon", "hue", "stripes", "grid", "granite"]
UNSEEN_BG = ["wood", "dots", "carbon"]  # families that exist ONLY in the test set (never in training)
TEST_BG = TRAIN_BG + UNSEEN_BG


# ----------------------------------------------------------------------------- events
class Events:
    def __init__(self, path):
        self.path = Path(path) if path else None
        if self.path:
            self.path.parent.mkdir(parents=True, exist_ok=True)

    def emit(self, type_, **kw):
        if not self.path:
            return
        rec = {"t": round(time.time(), 3), "type": type_}
        rec.update(kw)
        with open(self.path, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")


# ----------------------------------------------------------------------------- source objects
def load_polygons(json_path):
    d = json.loads(Path(json_path).read_text(encoding="utf-8-sig"))
    objs = []
    for i, s in enumerate(d["shapes"]):
        pts = np.array(s["points"], dtype=np.float64)
        st = s["shape_type"]
        if st == "polygon" and len(pts) >= 3:
            poly = pts
        elif st == "rectangle" and len(pts) == 2:
            (x0, y0), (x1, y1) = pts
            poly = np.array([[x0, y0], [x1, y0], [x1, y1], [x0, y1]], dtype=np.float64)
        elif st == "oriented_rectangle" and len(pts) == 4:
            poly = pts
        else:
            continue
        objs.append({"id": i, "label": s["label"], "poly": poly})
    if not objs:
        raise SystemExit("No usable shapes in " + str(json_path))
    return objs


def split_objects(objs, min_train=6, min_test=4, gap=GAP):
    """Best vertical cut: >=4 test and >=6 train objects; maximises the smaller side then the kept total."""
    lo = min(o["poly"][:, 0].min() for o in objs)
    hi = max(o["poly"][:, 0].max() for o in objs)
    best = None
    for thr in np.linspace(lo, hi, 300):
        train = [o for o in objs if o["poly"][:, 0].max() <= thr - gap]
        test = [o for o in objs if o["poly"][:, 0].min() >= thr]
        if len(test) < min_test or len(train) < min_train:
            continue
        score = (min(len(train), len(test) * 2), len(train) + len(test))
        if best is None or score > best[0]:
            best = (score, thr, train, test)
    if best is None:
        raise SystemExit(f"Cannot hold out >={min_test} test objects and keep >={min_train} for training on one cut.")
    return best[1], best[2], best[3]


def estimate_background(img, objs, margin=30, scale=8):
    """Smooth clean-plate of the source image: label shapes (dilated) are inpainted from the surrounding pixels at low resolution."""
    h, w = img.shape[:2]
    hole = np.zeros((h, w), np.uint8)
    for o in objs:
        cv2.fillPoly(hole, [np.round(o["poly"]).astype(np.int32)], 255)
    hole = cv2.dilate(hole, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * margin + 1, 2 * margin + 1)))
    sw, sh = max(1, w // scale), max(1, h // scale)
    small = cv2.resize(img, (sw, sh), interpolation=cv2.INTER_AREA)
    hole_s = cv2.resize(hole, (sw, sh), interpolation=cv2.INTER_NEAREST)
    filled = cv2.inpaint(small, hole_s, 5, cv2.INPAINT_TELEA)
    filled = cv2.GaussianBlur(filled, (0, 0), 2.0)
    return cv2.resize(filled, (w, h), interpolation=cv2.INTER_CUBIC)


def subject_matte(crop, bg_crop, poly_local, shadow_floor=22):
    """Real subject matte inside the label shape: difference to the clean plate, Otsu inside the shape, keep the component under the label centre,
    fill holes (specular metal), drop the soft contact shadow. Returns uint8 alpha (0..255) or None when the matte is implausible."""
    diff = np.abs(crop.astype(np.int16) - bg_crop.astype(np.int16)).max(axis=2).astype(np.uint8)
    diff = cv2.GaussianBlur(diff, (0, 0), 0.8)
    shape = np.zeros(diff.shape, np.uint8)
    cv2.fillPoly(shape, [np.round(poly_local).astype(np.int32)], 255)
    vals = diff[shape > 0]
    if vals.size < 50:
        return None
    t_otsu, _ = cv2.threshold(vals.reshape(-1, 1), 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    thr = max(float(t_otsu) * 0.8, float(shadow_floor))  # Otsu splits metal from the (weak) soft shadow; stay a bit under it to keep thin edges
    fg = ((diff >= thr) & (shape > 0)).astype(np.uint8) * 255
    fg = cv2.morphologyEx(fg, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)))
    fg = cv2.morphologyEx(fg, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3)))
    n, lab, stats, cent = cv2.connectedComponentsWithStats(fg, connectivity=8)
    if n <= 1:
        return None
    cx, cy = poly_local.mean(axis=0)
    best, best_score = 0, -1.0
    for k in range(1, n):  # biggest component, strongly preferring the one nearest the label centre
        d = math.hypot(cent[k][0] - cx, cent[k][1] - cy)
        score = stats[k, cv2.CC_STAT_AREA] / (1.0 + 0.05 * d)
        if score > best_score:
            best, best_score = k, score
    m = (lab == best).astype(np.uint8) * 255
    cnts, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)  # fill every hole
    m = np.zeros_like(m)
    cv2.drawContours(m, cnts, -1, 255, thickness=cv2.FILLED)
    m = cv2.erode(m, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3)))  # strip the bright fringe of the clean plate
    area_ratio = float((m > 0).sum()) / max(1.0, float((shape > 0).sum()))
    if area_ratio < 0.12 or area_ratio > 0.92:
        return None
    return cv2.GaussianBlur(m, (0, 0), 0.9)


def _core_marker(poly, shrink_w=0.45, shrink_l=0.8):
    """Thin rotated strip along the long axis of the label: sits inside the screw even when the label rectangle is loose."""
    (cx, cy), (rw, rh), ang = cv2.minAreaRect(poly.astype(np.float32))
    box = cv2.boxPoints(((cx, cy), (rw * (shrink_w if rw < rh else shrink_l), rh * (shrink_l if rw < rh else shrink_w)), ang))
    return np.round(box).astype(np.int32)


def all_mattes(img, objs, bg, shadow_floor=22):
    """Whole-image matting that separates touching screws: global difference to the clean plate gives the foreground, and a marker-based
    watershed (one marker per labelled screw + one for the plate) assigns every foreground pixel to exactly one screw.
    Returns {object id: uint8 mask in full-image coordinates}."""
    h, w = img.shape[:2]
    diff = np.abs(img.astype(np.int16) - bg.astype(np.int16)).max(axis=2).astype(np.uint8)
    diff = cv2.GaussianBlur(diff, (0, 0), 0.8)
    union = np.zeros((h, w), np.uint8)
    for o in objs:
        cv2.fillPoly(union, [np.round(o["poly"]).astype(np.int32)], 255)
    t_otsu, _ = cv2.threshold(diff[union > 0].reshape(-1, 1), 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    thr = max(float(t_otsu) * 0.8, float(shadow_floor))
    fg = ((diff >= thr) & (cv2.dilate(union, np.ones((7, 7), np.uint8)) > 0)).astype(np.uint8) * 255
    fg = cv2.morphologyEx(fg, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (11, 11)))  # bridge bright specular parts of the metal
    cnts0, _ = cv2.findContours(fg, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    cv2.drawContours(fg, cnts0, -1, 255, thickness=cv2.FILLED)
    markers = np.zeros((h, w), np.int32)
    markers[cv2.dilate(union, np.ones((9, 9), np.uint8)) == 0] = 1
    for i, o in enumerate(objs):
        core = np.zeros((h, w), np.uint8)
        cv2.fillPoly(core, [_core_marker(o["poly"])], 255)
        markers[(core > 0) & (fg > 0)] = i + 2
    # watershed wants 8-bit 3-channel; emphasise edges between neighbouring screws
    grad_src = cv2.cvtColor(cv2.GaussianBlur(img, (0, 0), 0.8), cv2.COLOR_BGR2GRAY) if img.ndim == 3 else img
    ws = cv2.watershed(cv2.cvtColor(grad_src, cv2.COLOR_GRAY2BGR), markers.copy())
    out = {}
    k3 = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))
    for i, o in enumerate(objs):
        own = cv2.fillPoly(np.zeros((h, w), np.uint8), [np.round(o["poly"]).astype(np.int32)], 255)
        own = cv2.dilate(own, np.ones((5, 5), np.uint8))
        m = (((ws == i + 2) & (fg > 0)) & (own > 0)).astype(np.uint8) * 255
        m = cv2.morphologyEx(m, cv2.MORPH_OPEN, k3)
        n, lab, stats, _ = cv2.connectedComponentsWithStats(m, connectivity=8)
        if n <= 1:
            out[o["id"]] = None
            continue
        m = (lab == 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))).astype(np.uint8) * 255
        cnts, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        m = np.zeros_like(m)
        cv2.drawContours(m, cnts, -1, 255, thickness=cv2.FILLED)
        m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, k3)
        m = cv2.erode(m, k3)
        shape_area = float((own > 0).sum())
        ratio = float((m > 0).sum()) / max(1.0, shape_area)
        out[o["id"]] = m if 0.12 <= ratio <= 0.92 else None
    return out


def touching_ids(objs, masks, gap=9):
    k = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (gap, gap))
    ids = set()
    for a in objs:
        if masks.get(a['id']) is None:
            ids.add(a['id']); continue
        da = cv2.dilate(masks[a['id']], k) > 0
        if any(b is not a and masks.get(b['id']) is not None and (da & (masks[b['id']] > 0)).any() for b in objs):
            ids.add(a['id'])
    return ids


def defringe(crop, alpha, core_thr=225, sigma=2.5):
    """Colour decontamination: pixels where the matte is partial still carry the old photo background. Replace them with the colour of the
    nearby solid subject pixels so no light/dark halo of the source background travels with the cut-out onto a new background."""
    core = (alpha >= core_thr).astype(np.float32)
    if core.sum() < 20:
        return crop
    f = crop.astype(np.float32)
    num = cv2.GaussianBlur(f * core[..., None], (0, 0), sigma)
    den = cv2.GaussianBlur(core, (0, 0), sigma)[..., None]
    inner = num / np.maximum(den, 1e-3)
    w = np.clip(1.0 - alpha.astype(np.float32) / core_thr, 0, 1)[..., None]  # 0 on solid pixels, 1 where alpha ~ 0
    out = f * (1 - w) + inner * w
    return np.clip(out, 0, 255).astype(np.uint8)


def harmonize(fg, alpha, bg_mean, rng):
    """Pull the object's colour toward the surrounding light (ambient colour cast), like a real object lit by the same scene."""
    lab = cv2.cvtColor(np.clip(fg, 0, 255).astype(np.float32) / 255.0, cv2.COLOR_BGR2LAB)
    bg_lab = cv2.cvtColor(np.clip(bg_mean, 0, 255).astype(np.float32).reshape(1, 1, 3) / 255.0, cv2.COLOR_BGR2LAB)[0, 0]
    sel = alpha > 128
    fg_mean = lab[sel].mean(axis=0) if sel.any() else lab.reshape(-1, 3).mean(axis=0)
    k_ab = rng.uniform(0.10, 0.38)
    k_l = rng.uniform(0.0, 0.18)
    lab[..., 1:] += k_ab * (bg_lab[1:] - fg_mean[1:])
    lab[..., 0] += k_l * (bg_lab[0] - fg_mean[0])
    out = cv2.cvtColor(lab, cv2.COLOR_LAB2BGR) * 255.0
    return np.clip(out, 0, 255)


def cut_patch(img, o, pad=8, bg=None, mask_full=None):
    """BGR patch + soft alpha + polygon in patch coordinates. Alpha is the real subject matte (background removed) when `bg` is given;
    it falls back to the label shape only if matting fails for that object. Pixels outside are never used (alpha 0)."""
    h, w = img.shape[:2]
    x0 = max(0, int(o["poly"][:, 0].min()) - pad)
    y0 = max(0, int(o["poly"][:, 1].min()) - pad)
    x1 = min(w, int(math.ceil(o["poly"][:, 0].max())) + pad)
    y1 = min(h, int(math.ceil(o["poly"][:, 1].max())) + pad)
    crop = img[y0:y1, x0:x1].copy()
    alpha = None
    if mask_full is not None:
        alpha = cv2.GaussianBlur(mask_full[y0:y1, x0:x1], (0, 0), 0.9)
    elif bg is not None:
        alpha = subject_matte(crop, bg[y0:y1, x0:x1], o["poly"] - [x0, y0])
    matted = alpha is not None
    if alpha is None:
        alpha = np.zeros(crop.shape[:2], np.uint8)
        cv2.fillPoly(alpha, [np.round(o["poly"] - [x0, y0]).astype(np.int32)], 255)
        alpha = cv2.GaussianBlur(alpha, (0, 0), 1.0)
    poly_local = o["poly"] - [x0, y0]
    if matted:  # tight label: the oriented box of the real silhouette, not the loose hand-drawn rectangle
        ys, xs = np.nonzero(alpha > 127)
        if xs.size > 50:
            poly_local = cv2.boxPoints(cv2.minAreaRect(np.stack([xs, ys], 1).astype(np.float32)))
    obb = cv2.boxPoints(cv2.minAreaRect(poly_local.astype(np.float32)))
    long_side = float(max(np.linalg.norm(obb[0] - obb[1]), np.linalg.norm(obb[1] - obb[2])))
    crop = defringe(crop, alpha)
    mean_col = crop[alpha > 200].reshape(-1, 3).mean(axis=0) if (alpha > 200).any() else np.array([128, 128, 128.0])
    return {"id": o["id"], "label": o["label"], "img": crop, "alpha": alpha, "poly": poly_local, "long": long_side, "mean": mean_col, "matted": matted}


# ----------------------------------------------------------------------------- backgrounds
def lowfreq(rng, h, w, cell):
    g = rng.random((max(2, h // cell + 2), max(2, w // cell + 2))).astype(np.float32)
    return cv2.resize(g, (w, h), interpolation=cv2.INTER_CUBIC)


def hsv_bgr(h, s, v):
    px = np.uint8([[[int(h) % 180, int(np.clip(s, 0, 255)), int(np.clip(v, 0, 255))]]])
    return cv2.cvtColor(px, cv2.COLOR_HSV2BGR)[0, 0].astype(np.float32)


def make_bg(kind, rng, obj_mean, w=CANVAS_W, h=CANVAS_H):
    """Float32 BGR background 0..255 for one procedural family."""
    ones = np.ones((h, w, 1), np.float32)
    if kind == "flat":
        base = hsv_bgr(rng.integers(0, 180), rng.uniform(0, 60), rng.uniform(70, 200))
        bg = ones * base
    elif kind == "gradient":
        a = hsv_bgr(rng.integers(0, 180), rng.uniform(0, 90), rng.uniform(40, 210))
        b = hsv_bgr(rng.integers(0, 180), rng.uniform(0, 90), rng.uniform(40, 210))
        ang = rng.uniform(0, 2 * math.pi)
        yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
        t = ((xx - w / 2) * math.cos(ang) + (yy - h / 2) * math.sin(ang)) / (0.5 * (abs(w * math.cos(ang)) + abs(h * math.sin(ang))) + 1e-6)
        t = np.clip(t * 0.5 + 0.5, 0, 1)[..., None]
        bg = a * (1 - t) + b * t
    elif kind == "spot":
        base = hsv_bgr(rng.integers(0, 180), rng.uniform(0, 50), rng.uniform(110, 230))
        cx, cy = rng.uniform(0.2, 0.8) * w, rng.uniform(0.2, 0.8) * h
        yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
        d = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) / (0.9 * max(w, h))
        bg = ones * base * np.clip(1.15 - 1.3 * d, 0.18, 1.2)[..., None]
    elif kind == "blotchy":
        base = hsv_bgr(rng.integers(0, 180), rng.uniform(0, 80), rng.uniform(60, 190))
        n = (lowfreq(rng, h, w, 220) - 0.5) * 90 + (lowfreq(rng, h, w, 40) - 0.5) * 40 + (lowfreq(rng, h, w, 9) - 0.5) * 14
        bg = ones * base + n[..., None] * np.array([1.0, 1.0, 1.0], np.float32)
    elif kind == "dark":
        bg = ones * hsv_bgr(rng.integers(0, 180), rng.uniform(0, 60), rng.uniform(8, 55))
        bg = bg + ((lowfreq(rng, h, w, 160) - 0.5) * 18)[..., None]
    elif kind == "bright":
        bg = ones * hsv_bgr(rng.integers(0, 180), rng.uniform(0, 25), rng.uniform(205, 250))
        bg = bg + ((lowfreq(rng, h, w, 200) - 0.5) * 16)[..., None]
    elif kind == "lowcon":  # background colour almost equal to the object colour
        delta = rng.uniform(-14, 14, 3).astype(np.float32)
        bg = ones * np.clip(obj_mean.astype(np.float32) + delta, 0, 255)
        bg = bg + ((lowfreq(rng, h, w, 120) - 0.5) * 16)[..., None] + rng.normal(0, 3, (h, w, 1)).astype(np.float32)
    elif kind == "hue":
        base = hsv_bgr(rng.integers(0, 180), rng.uniform(120, 255), rng.uniform(70, 230))
        bg = ones * base + ((lowfreq(rng, h, w, 180) - 0.5) * 30)[..., None]
    elif kind == "stripes":  # brushed metal
        base = hsv_bgr(rng.integers(0, 180), rng.uniform(0, 40), rng.uniform(90, 200))
        noise = rng.normal(0, 1, (h, w)).astype(np.float32)
        ang = rng.uniform(0, 180)
        k = np.zeros((1, 61), np.float32)
        k[0, :] = 1.0 / 61
        M = cv2.getRotationMatrix2D((30, 0), ang, 1.0)
        noise = cv2.GaussianBlur(noise, (0, 0), sigmaX=40, sigmaY=0.8) if ang < 90 else cv2.GaussianBlur(noise, (0, 0), sigmaX=0.8, sigmaY=40)
        noise = noise / (noise.std() + 1e-6)
        bg = ones * base + (noise * rng.uniform(10, 26))[..., None]
    elif kind == "grid":  # cutting mat
        base = hsv_bgr(rng.integers(0, 180), rng.uniform(20, 130), rng.uniform(60, 180))
        step = int(rng.integers(36, 96))
        ang = rng.uniform(0, 90)
        line = np.zeros((h * 2, w * 2), np.float32)
        for x in range(0, w * 2, step):
            cv2.line(line, (x, 0), (x, h * 2), 1.0, int(rng.integers(1, 4)))
        for y in range(0, h * 2, step):
            cv2.line(line, (0, y), (w * 2, y), 1.0, int(rng.integers(1, 4)))
        R = cv2.getRotationMatrix2D((w, h), ang, 1.0)
        line = cv2.warpAffine(line, R, (w * 2, h * 2))[h // 2:h // 2 + h, w // 2:w // 2 + w]
        bg = ones * base + line[..., None] * rng.uniform(-60, 60) + ((lowfreq(rng, h, w, 200) - 0.5) * 20)[..., None]
    elif kind == "granite":
        a = hsv_bgr(rng.integers(0, 180), rng.uniform(0, 40), rng.uniform(60, 120))
        b = hsv_bgr(rng.integers(0, 180), rng.uniform(0, 40), rng.uniform(130, 220))
        m = cv2.GaussianBlur(rng.random((h, w)).astype(np.float32), (0, 0), 1.6)
        m = (m - m.min()) / (m.max() - m.min() + 1e-6)
        m = np.clip((m - 0.5) * rng.uniform(3, 7) + 0.5, 0, 1)[..., None]
        bg = a * (1 - m) + b * m
    elif kind == "wood":  # grain with rings, test only
        base = hsv_bgr(rng.integers(5, 25), rng.uniform(60, 150), rng.uniform(80, 190))
        yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
        ang = rng.uniform(0, math.pi)
        u = xx * math.cos(ang) + yy * math.sin(ang)
        warp = (lowfreq(rng, h, w, 260) - 0.5) * rng.uniform(80, 220)
        rings = np.sin((u + warp) / rng.uniform(6, 16)) * 0.5 + 0.5
        fine = cv2.GaussianBlur(rng.normal(0, 1, (h, w)).astype(np.float32), (0, 0), sigmaX=0.8, sigmaY=14) if ang < 1.57 else cv2.GaussianBlur(rng.normal(0, 1, (h, w)).astype(np.float32), (0, 0), sigmaX=14, sigmaY=0.8)
        bg = ones * base + ((rings - 0.5) * rng.uniform(25, 55) + fine * 12)[..., None]
    elif kind == "dots":  # perforated sheet, test only
        base = hsv_bgr(rng.integers(0, 180), rng.uniform(0, 70), rng.uniform(70, 190))
        step = int(rng.integers(34, 80))
        rad = int(step * rng.uniform(0.18, 0.34))
        tile = np.zeros((step, step), np.float32)
        cv2.circle(tile, (step // 2, step // 2), rad, 1.0, -1)
        pat = np.tile(tile, (h // step + 2, w // step + 2))[:h, :w]
        pat = cv2.GaussianBlur(pat, (0, 0), 1.2)
        bg = ones * base + (pat * rng.uniform(-70, 70))[..., None] + ((lowfreq(rng, h, w, 200) - 0.5) * 18)[..., None]
    elif kind == "carbon":  # woven pattern, test only
        base = hsv_bgr(rng.integers(0, 180), rng.uniform(0, 40), rng.uniform(25, 90))
        step = int(rng.integers(10, 22))
        yy, xx = np.mgrid[0:h, 0:w]
        cell = ((xx // step) + (yy // step)) % 2
        wave = np.where(cell == 0, np.sin(xx / step * math.pi), np.sin(yy / step * math.pi)).astype(np.float32)
        bg = ones * base + (wave * rng.uniform(10, 24))[..., None] + ((lowfreq(rng, h, w, 180) - 0.5) * 16)[..., None]
    else:
        raise ValueError(kind)
    return np.clip(bg, 0, 255).astype(np.float32)


# ----------------------------------------------------------------------------- composition
def clip_obb(pts, w, h):
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


def pick_count(rng):
    r = rng.random()
    if r < 0.30:
        return "sparse", int(rng.integers(1, 5))
    if r < 0.65:
        return "medium", int(rng.integers(5, 16))
    return "dense", int(rng.integers(16, 41))


TARGET_LEN = {"sparse": (150, 330), "medium": (90, 200), "dense": (52, 125)}


def pose_matrix(patch, target_len, rng, cx, cy):
    """Patch -> canvas affine: centre, scale to target length, full rotation, optional flip, shear and tilt."""
    ph, pw = patch["img"].shape[:2]
    s = target_len / max(patch["long"], 1.0) * rng.uniform(0.9, 1.1)
    ang = rng.uniform(0, 360)
    tilt = rng.uniform(0.82, 1.0)  # foreshortening along a random axis = fake out-of-plane tilt
    tilt_ang = rng.uniform(0, math.pi)
    shear = rng.uniform(-0.12, 0.12)
    flip_x = -1.0 if rng.random() < 0.5 else 1.0
    flip_y = -1.0 if rng.random() < 0.5 else 1.0

    def rot(a):
        c, s_ = math.cos(a), math.sin(a)
        return np.array([[c, -s_], [s_, c]])

    A = rot(math.radians(ang)) @ rot(tilt_ang) @ np.diag([1.0, tilt]) @ rot(-tilt_ang) @ np.array([[1, shear], [0, 1]]) @ np.diag([flip_x, flip_y]) * s
    centre = np.array([pw / 2.0, ph / 2.0])
    t = np.array([cx, cy]) - A @ centre
    return np.hstack([A, t[:, None]])


def render_object(patch, M, w, h):
    """Warp patch+alpha to a canvas ROI. Returns (x0, y0, bgr, alpha, poly) or None when the ROI is empty."""
    ph, pw = patch["img"].shape[:2]
    corners = cv2.transform(np.array([[[0, 0], [pw, 0], [pw, ph], [0, ph]]], np.float64), M)[0]
    x0, y0 = int(math.floor(corners[:, 0].min())), int(math.floor(corners[:, 1].min()))
    x1, y1 = int(math.ceil(corners[:, 0].max())), int(math.ceil(corners[:, 1].max()))
    cx0, cy0, cx1, cy1 = max(0, x0), max(0, y0), min(w, x1), min(h, y1)
    if cx1 - cx0 < 3 or cy1 - cy0 < 3:
        return None
    M2 = M.copy()
    M2[0, 2] -= cx0
    M2[1, 2] -= cy0
    size = (cx1 - cx0, cy1 - cy0)
    bgr = cv2.warpAffine(patch["img"], M2, size, flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)
    alpha = cv2.warpAffine(patch["alpha"], M2, size, flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=0)
    poly = cv2.transform(patch["poly"].reshape(1, -1, 2), M)[0]
    return cx0, cy0, bgr, alpha, poly


def compose(patches, bg_kind, rng, py_rng, with_stats=True):
    """One image: returns (uint8 BGR, [(label, obb_corners)], info dict)."""
    w, h = CANVAS_W, CANVAS_H
    obj_mean = np.mean([p["mean"] for p in patches], axis=0)
    canvas = make_bg(bg_kind, rng, obj_mean)
    mode, n_target = pick_count(rng)
    lo, hi = TARGET_LEN[mode]
    base_len = rng.uniform(lo, hi)
    allow_overlap = {"sparse": 0.0, "medium": 0.04, "dense": 0.12}[mode]
    occ = np.zeros((h, w), np.uint8)
    placed = []
    attempts = 0
    while len(placed) < n_target and attempts < n_target * 40:
        attempts += 1
        patch = patches[int(rng.integers(0, len(patches)))]
        tl = base_len * rng.uniform(0.85, 1.15)
        # centre may sit slightly outside the canvas so objects are cut by the border sometimes
        margin = 0.25 * tl if rng.random() < 0.18 else -0.35 * tl
        cx = rng.uniform(-margin, w + margin) if margin > 0 else rng.uniform(-margin, w + margin)
        cy = rng.uniform(-margin, h + margin) if margin > 0 else rng.uniform(-margin, h + margin)
        M = pose_matrix(patch, tl, rng, cx, cy)
        r = render_object(patch, M, w, h)
        if r is None:
            continue
        x0, y0, bgr, alpha, poly = r
        obb = clip_obb(cv2.boxPoints(cv2.minAreaRect(poly.astype(np.float32))).astype(np.float64), w, h)
        if obb is None:
            continue
        sel = alpha > 128
        area = int(sel.sum())
        if area < 30:
            continue
        roi = occ[y0:y0 + alpha.shape[0], x0:x0 + alpha.shape[1]]
        if (roi[sel] > 0).sum() / area > allow_overlap:
            continue
        roi[sel] = 255
        placed.append({"x0": x0, "y0": y0, "bgr": bgr, "alpha": alpha, "label": patch["label"], "obb": obb, "src": patch["id"], "gain": rng.uniform(0.82, 1.2)})

    # soft shadows under the objects (below every object)
    shadow_strength = rng.uniform(0.0, 0.55)
    if shadow_strength > 0.05 and placed:
        sh = np.zeros((h, w), np.float32)
        dx, dy = rng.uniform(-14, 14), rng.uniform(-14, 14)
        for p in placed:
            a = p["alpha"].astype(np.float32) / 255.0
            xa, ya = int(round(p["x0"] + dx)), int(round(p["y0"] + dy))
            xs0, ys0 = max(0, xa), max(0, ya)
            xs1, ys1 = min(w, xa + a.shape[1]), min(h, ya + a.shape[0])
            if xs1 > xs0 and ys1 > ys0:
                sh[ys0:ys1, xs0:xs1] = np.maximum(sh[ys0:ys1, xs0:xs1], a[ys0 - ya:ys1 - ya, xs0 - xa:xs1 - xa])
        sh = cv2.GaussianBlur(sh, (0, 0), rng.uniform(4, 11))
        canvas = canvas * (1 - shadow_strength * sh)[..., None]
        shadow_dir = (dx, dy)
    else:
        shadow_dir = (rng.uniform(-6, 6), rng.uniform(-6, 6))

    # tight contact shadow + ambient occlusion hugging every object: this is what makes a pasted object sit ON the surface
    if placed:
        ct = np.zeros((h, w), np.float32)
        ao = np.zeros((h, w), np.float32)
        cdx, cdy = shadow_dir[0] * 0.3, shadow_dir[1] * 0.3
        for p in placed:
            a = p["alpha"].astype(np.float32) / 255.0
            for target, (ox, oy) in ((ct, (cdx, cdy)), (ao, (0.0, 0.0))):
                xa, ya = int(round(p["x0"] + ox)), int(round(p["y0"] + oy))
                xs0, ys0 = max(0, xa), max(0, ya)
                xs1, ys1 = min(w, xa + a.shape[1]), min(h, ya + a.shape[0])
                if xs1 > xs0 and ys1 > ys0:
                    target[ys0:ys1, xs0:xs1] = np.maximum(target[ys0:ys1, xs0:xs1], a[ys0 - ya:ys1 - ya, xs0 - xa:xs1 - xa])
        ct = cv2.GaussianBlur(ct, (0, 0), rng.uniform(1.4, 3.2))
        ao = cv2.GaussianBlur(ao, (0, 0), rng.uniform(3.0, 6.0))
        canvas = canvas * (1 - rng.uniform(0.22, 0.5) * ct - rng.uniform(0.10, 0.25) * ao)[..., None]

    for p in placed:
        a = p["alpha"].astype(np.float32)
        ys, xs = p["y0"], p["x0"]
        # feather: a hair of erosion hides leftover source-photo edge pixels, a soft edge matches the camera's own blur
        a = cv2.erode(a, np.ones((3, 3), np.uint8)) if rng.random() < 0.7 else a
        a = (cv2.GaussianBlur(a, (0, 0), rng.uniform(0.6, 1.4)) / 255.0)[..., None]
        reg = canvas[ys:ys + a.shape[0], xs:xs + a.shape[1]]
        fg = np.clip(p["bgr"].astype(np.float32) * p["gain"], 0, 255)
        fg = harmonize(fg, p["alpha"], reg.reshape(-1, 3).mean(axis=0), rng)
        soft = rng.uniform(0.0, 0.9)  # the object is as sharp/blurry as the background it sits in
        if soft > 0.25:
            fg = cv2.GaussianBlur(fg, (0, 0), soft)
        reg[:] = reg * (1 - a) + fg * a

    # global lighting, blur, noise
    light = 0.78 + 0.45 * lowfreq(rng, h, w, int(rng.integers(160, 420)))
    canvas = canvas * light[..., None]
    gamma = rng.uniform(0.75, 1.3)
    canvas = 255.0 * np.power(np.clip(canvas, 0, 255) / 255.0, gamma)
    contrast = rng.uniform(0.7, 1.15)
    canvas = (canvas - canvas.mean()) * contrast + canvas.mean()
    blur = 0.0
    if rng.random() < 0.35:
        blur = rng.uniform(0.6, 1.8)
        canvas = cv2.GaussianBlur(canvas, (0, 0), blur)
    noise = rng.uniform(0, 6)
    canvas = canvas + rng.normal(0, noise, canvas.shape).astype(np.float32)
    out = np.clip(canvas, 0, 255).astype(np.uint8)
    objs = [(p["label"], p["obb"]) for p in placed]
    info = {"mode": mode, "count": len(objs), "bg": bg_kind, "sources": sorted({p["src"] for p in placed}), "blur": round(blur, 2), "noise": round(noise, 2)}
    return out, objs, info


# ----------------------------------------------------------------------------- dataset writer
def write_pair(img_dir, lbl_dir, name, img, objs, cls):
    h, w = img.shape[:2]
    path = img_dir / (name + ".jpg")
    cv2.imwrite(str(path), img, [cv2.IMWRITE_JPEG_QUALITY, int(random.Random(name).choice([88, 92, 95]))])
    lines = []
    for label, pts in objs:
        lines.append(str(cls[label]) + " " + " ".join(f"{min(max(v, 0), 1):.6f}" for v in (pts / [w, h]).reshape(-1)))
    (lbl_dir / (name + ".txt")).write_text("\n".join(lines), encoding="utf-8")
    return path


def file_hash(path):
    return hashlib.sha1(Path(path).read_bytes()).hexdigest()


def generate(src_dir, out_root, n_train, n_test, seed, events, img_path=None, json_path=None, keep_touching=False):
    src_dir = Path(src_dir)
    out_root = Path(out_root)
    jp = Path(json_path) if json_path else sorted(src_dir.glob("*.json"))[0]
    ip = Path(img_path) if img_path else next(p for p in sorted(src_dir.glob(jp.stem + ".*")) if p.suffix.lower() in (".bmp", ".png", ".jpg", ".jpeg"))
    img = cv2.imread(str(ip), cv2.IMREAD_COLOR)
    if img is None:
        raise SystemExit("Cannot read " + str(ip))
    objs = load_polygons(jp)
    classes = sorted({o["label"] for o in objs})
    cls = {c: i for i, c in enumerate(classes)}
    bg_plate = estimate_background(img, objs)
    masks = all_mattes(img, objs, bg_plate)
    usable = objs
    if not keep_touching:  # a screw that touches another one is partly occluded in the photo: its matte would carry a cut edge, so it is not used
        usable = [o for o in objs if o['id'] not in touching_ids(objs, masks)]
        print(f'clean screws: {len(usable)}/{len(objs)} (touching ones dropped: {[o[chr(105)+chr(100)] for o in objs if o not in usable]})', file=sys.stderr)
    if keep_touching:
        thr, train_o, test_o = split_objects(usable, 6, 4, GAP)
    else:  # few isolated screws with overlapping label extents: hold out the two right-most screws as whole objects (only matte pixels are ever pasted)
        by_x = sorted(usable, key=lambda o: o["poly"][:, 0].mean())
        n_hold = 2 if len(by_x) >= 6 else 1
        train_o, test_o = by_x[:-n_hold], by_x[-n_hold:]
        if len(train_o) < 3:
            raise SystemExit("Too few isolated screws; use --keep-touching.")
        thr = (train_o[-1]["poly"][:, 0].mean() + test_o[0]["poly"][:, 0].mean()) / 2
    gap_o = [o for o in objs if o not in train_o and o not in test_o]
    train_patches = [cut_patch(img, o, bg=bg_plate, mask_full=masks.get(o["id"])) for o in train_o]
    test_patches = [cut_patch(img, o, bg=bg_plate, mask_full=masks.get(o["id"])) for o in test_o]
    n_fallback = sum(1 for p in train_patches + test_patches if not p["matted"])
    print(f"matting: {len(train_patches) + len(test_patches) - n_fallback}/{len(train_patches) + len(test_patches)} objects matted, {n_fallback} fell back to the label shape", file=sys.stderr)

    for sub in ("images/train", "images/val", "labels/train", "labels/val"):
        (out_root / sub).mkdir(parents=True, exist_ok=True)
    events.emit("gen_start", n_train=n_train, n_test=n_test, train_objects=[o["id"] for o in train_o], test_objects=[o["id"] for o in test_o],
                gap_objects=[o["id"] for o in gap_o], cut_x=round(float(thr), 1))

    meta = {"train": [], "test": []}
    hashes = {"train": set(), "test": set()}
    for split_name, count, patches, families, base_seed in (
            ("train", n_train, train_patches, TRAIN_BG, seed * 1000 + 17),
            ("test", n_test, test_patches, TEST_BG, seed * 1000 + 777001)):
        sub = "val" if split_name == "test" else "train"
        for i in range(count):
            rng = np.random.default_rng(base_seed + i)
            kind = families[i % len(families)] if split_name == "train" else (UNSEEN_BG[i % 3] if i % 2 == 0 else TRAIN_BG[(i // 2) % len(TRAIN_BG)])
            image, objs_i, info = compose(patches, kind, rng, random.Random(base_seed + i))
            name = f"{split_name}_{i:05d}"
            path = write_pair(out_root / "images" / sub, out_root / "labels" / sub, name, image, objs_i, cls)
            hashes[split_name].add(file_hash(path))
            info.update({"name": name, "path": str(path), "seed": int(base_seed + i)})
            meta[split_name].append(info)
            events.emit("gen_image", split=split_name, index=i + 1, total=count, path=str(path), count=info["count"], bg=info["bg"], mode=info["mode"])
    names = "\n".join(f"  {i}: {c}" for c, i in cls.items())
    (out_root / "data.yaml").write_text(f"path: {out_root.as_posix()}\ntrain: images/train\nval: images/val\nnames:\n{names}\n", encoding="utf-8")

    train_src = {s for m in meta["train"] for s in m["sources"]}
    test_src = {s for m in meta["test"] for s in m["sources"]}
    train_seeds = {m["seed"] for m in meta["train"]}
    test_seeds = {m["seed"] for m in meta["test"]}
    leak = {
        "objects_disjoint": not (train_src & test_src),
        "train_object_ids": sorted(train_src),
        "test_object_ids": sorted(test_src),
        "image_hashes_disjoint": not (hashes["train"] & hashes["test"]),
        "seeds_disjoint": not (train_seeds & test_seeds),
        "unseen_background_families_in_test": sorted({m["bg"] for m in meta["test"]} - {m["bg"] for m in meta["train"]}),
    }
    if not (leak["objects_disjoint"] and leak["image_hashes_disjoint"] and leak["seeds_disjoint"]):
        raise SystemExit("Leakage check failed: " + json.dumps(leak))
    summary = {
        "source_image": str(ip), "cut_x": float(thr), "classes": classes,
        "train": {"images": n_train, "objects_total": sum(m["count"] for m in meta["train"]),
                  "per_mode": {k: sum(1 for m in meta["train"] if m["mode"] == k) for k in ("sparse", "medium", "dense")},
                  "per_bg": {k: sum(1 for m in meta["train"] if m["bg"] == k) for k in sorted({m["bg"] for m in meta["train"]})}},
        "test": {"images": n_test, "objects_total": sum(m["count"] for m in meta["test"]),
                 "per_mode": {k: sum(1 for m in meta["test"] if m["mode"] == k) for k in ("sparse", "medium", "dense")},
                 "per_bg": {k: sum(1 for m in meta["test"] if m["bg"] == k) for k in sorted({m["bg"] for m in meta["test"]})}},
        "leakage": leak,
    }
    (out_root / "gen_meta.json").write_text(json.dumps({"summary": summary, "images": meta}, ensure_ascii=False, indent=1), encoding="utf-8")
    events.emit("gen_done", summary=summary)
    return summary, meta


def contact_sheet(out_root, meta, path, per_split=12, cell=(320, 256)):
    rows = []
    for split_name, sub in (("train", "train"), ("test", "val")):
        items = meta[split_name][:per_split]
        tiles = []
        for m in items:
            im = cv2.imread(str(Path(m["path"])))
            im = cv2.resize(im, cell)
            cv2.putText(im, f"{split_name} n={m['count']} {m['bg']}", (6, 18), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 255), 1, cv2.LINE_AA)
            tiles.append(im)
        while len(tiles) % 6:
            tiles.append(np.zeros((cell[1], cell[0], 3), np.uint8))
        for r in range(0, len(tiles), 6):
            rows.append(np.hstack(tiles[r:r + 6]))
    cv2.imwrite(str(path), np.vstack(rows), [cv2.IMWRITE_JPEG_QUALITY, 88])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True, help="folder with the labelled image + labelme json")
    ap.add_argument("--out", required=True, help="dataset output folder")
    ap.add_argument("--n-train", type=int, default=300)
    ap.add_argument("--n-test", type=int, default=100)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--keep-touching", action="store_true", help="also use screws that touch others in the source photo (dirtier mattes)")
    ap.add_argument("--events", default="", help="events.jsonl path")
    ap.add_argument("--sheet", default="", help="write a contact sheet jpg of the first images")
    a = ap.parse_args()
    summary, meta = generate(a.dir, a.out, a.n_train, a.n_test, a.seed, Events(a.events), keep_touching=a.keep_touching)
    if a.sheet:
        contact_sheet(a.out, meta, a.sheet)
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    main()




