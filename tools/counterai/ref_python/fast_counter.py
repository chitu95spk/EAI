#!/usr/bin/env python
"""Dem soi NHANH (< 2 giay / anh), khong ghi CSV.

Mang U-Net nho (models/fastnet.pt) da duoc huan luyen de bat chuoc Cellpose
(xem cellpose_counter.py). Moi soi cho mot dinh sang tren ban do nhiet; so dinh = so soi.

Cach dung:
    python fast_counter.py ..\\Image_20261026164016356.bmp
    python fast_counter.py ..                                  # ca thu muc
    python fast_counter.py .. --expected Image_20261026164016356.bmp=238
    python fast_counter.py .. --save-image ketqua              # doi thu muc luu anh (mac dinh: ./output_fast)
    python fast_counter.py .. --no-save-image                  # khong luu anh

Luu y: mang chi duoc huan luyen tren loai day cap trong thu muc Coil_Nisei. Voi loai day khac
(khac do phong dai, vat lieu...) hay doi chieu voi cellpose_counter.py, hoac huan luyen lai.
"""
import argparse
import sys
import time
from pathlib import Path

import cv2
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

from app_paths import app_dir

HERE = app_dir(__file__)                         # canh file .exe khi da dong goi
DEFAULT_WEIGHTS = HERE / "models" / "fastnet.pt"
IMAGE_EXT = {".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff"}
SCALE = 0.5            # mang chay o nua do phan giai
DIAMETER = 62          # duong kinh soi (pixel, anh goc)


# --------------------------------------------------------------------------- mang
def _block(i, o):
    return nn.Sequential(nn.Conv2d(i, o, 3, padding=1, bias=False), nn.BatchNorm2d(o), nn.ReLU(inplace=True),
                         nn.Conv2d(o, o, 3, padding=1, bias=False), nn.BatchNorm2d(o), nn.ReLU(inplace=True))


class FastNet(nn.Module):
    """U-Net nho: anh xam (da tang sang, thu nho 0.5) -> ban do nhiet tam soi (0..1)."""

    def __init__(self, c=(16, 32, 64, 96)):
        super().__init__()
        self.e1, self.e2, self.e3 = _block(1, c[0]), _block(c[0], c[1]), _block(c[1], c[2])
        self.b = _block(c[2], c[3])
        self.d3 = _block(c[3] + c[2], c[2])
        self.d2 = _block(c[2] + c[1], c[1])
        self.d1 = _block(c[1] + c[0], c[0])
        self.out = nn.Conv2d(c[0], 1, 1)

    def forward(self, x):
        e1 = self.e1(x)
        e2 = self.e2(F.max_pool2d(e1, 2))
        e3 = self.e3(F.max_pool2d(e2, 2))
        b = self.b(F.max_pool2d(e3, 2))
        up = lambda t, r: F.interpolate(t, size=r.shape[-2:], mode="bilinear", align_corners=False)
        d3 = self.d3(torch.cat([up(b, e3), e3], 1))
        d2 = self.d2(torch.cat([up(d3, e2), e2], 1))
        d1 = self.d1(torch.cat([up(d2, e1), e1], 1))
        return torch.sigmoid(self.out(d1))


# --------------------------------------------------------------------------- tien xu ly
def enhance(gray, gamma=0.4):
    """Gamma + CLAHE: lam ro cac soi toi nam trong bong (giong luc huan luyen)."""
    e = ((gray.astype(np.float32) / 255.0) ** gamma * 255).astype(np.uint8)
    return cv2.createCLAHE(clipLimit=3.0, tileGridSize=(16, 16)).apply(e)


def find_bundle_bbox(gray, margin, s=0.25, texture_thresh=0.9):
    """Hop bao quanh bo day, tinh tren anh thu nho 1/4 cho nhanh (~20 ms)."""
    small = cv2.resize(gray, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
    g = np.sqrt(small.astype(np.float32))
    sg = 12 * s
    m = cv2.GaussianBlur(g, (0, 0), sg)
    m2 = cv2.GaussianBlur(g * g, (0, 0), sg)
    roi = (np.sqrt(np.maximum(m2 - m * m, 0)) > texture_thresh).astype(np.uint8)

    def ell(n):
        n = int(n) | 1
        return cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (n, n))

    d = DIAMETER * s
    roi = cv2.morphologyEx(roi, cv2.MORPH_OPEN, ell(d * 1.3))
    roi = cv2.morphologyEx(roi, cv2.MORPH_CLOSE, ell(d))
    n, lab, stats, _ = cv2.connectedComponentsWithStats(roi)
    h, w = gray.shape
    if n <= 1:
        return 0, h, 0, w
    ys, xs = np.nonzero(lab == 1 + np.argmax(stats[1:, 4]))
    return (max(int(ys.min() / s) - margin, 0), min(int(ys.max() / s) + margin, h),
            max(int(xs.min() / s) - margin, 0), min(int(xs.max() / s) + margin, w))


# --------------------------------------------------------------------------- dem
def load_net(weights=DEFAULT_WEIGHTS, device="AUTO"):
    """weights: .pt (PyTorch) hoac .xml (OpenVINO, tao boi fastnet_openvino.py; device: AUTO/GPU/CPU)."""
    weights = Path(weights)
    if not weights.is_file():
        sys.exit(f"Khong tim thay trong so: {weights}")
    if weights.suffix.lower() == ".xml":
        import fastnet_openvino
        return fastnet_openvino.load(weights, device)
    net = FastNet()
    net.load_state_dict(torch.load(weights, map_location="cpu"))
    return net.eval()


def heatmap(gray, net):
    """Chay mang. Tra ve (ban do nhiet, x0, y0): toa do goc vung cat (de doi ve anh goc)."""
    y0, y1, x0, x1 = find_bundle_bbox(gray, margin=int(DIAMETER * 1.1))
    x = cv2.resize(enhance(gray[y0:y1, x0:x1]), None, fx=SCALE, fy=SCALE, interpolation=cv2.INTER_AREA)
    h, w = x.shape
    H, W = (h + 7) // 8 * 8, (w + 7) // 8 * 8                   # mang can boi so cua 8
    x = np.pad(x, ((0, H - h), (0, W - w)), mode="edge")
    with torch.no_grad():
        heat = net(torch.from_numpy(x.astype(np.float32) / 255)[None, None])[0, 0].numpy()[:h, :w]
    return heat, x0, y0


def peaks(heat, x0, y0, threshold=0.8, window=11):
    """Dinh cuc dai cuc bo > nguong -> toa do tam (N,2) theo anh goc."""
    peak = cv2.dilate(heat, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (window, window)))
    ys, xs = np.nonzero((heat == peak) & (heat > threshold))
    return np.c_[xs / SCALE + x0, ys / SCALE + y0].astype(np.int32)


def count(gray, net, threshold=0.8, window=11):
    """Tra ve toa do tam (N,2) theo anh goc."""
    heat, x0, y0 = heatmap(gray, net)
    return peaks(heat, x0, y0, threshold, window)


def draw(gray, pts):
    img = cv2.cvtColor(enhance(gray), cv2.COLOR_GRAY2BGR)
    for i, (x, y) in enumerate(pts, 1):
        cv2.circle(img, (int(x), int(y)), 31, (0, 255, 0), 2)
        cv2.circle(img, (int(x), int(y)), 4, (0, 0, 255), -1)
        cv2.putText(img, str(i), (int(x) - 14, int(y) - 8), cv2.FONT_HERSHEY_SIMPLEX,
                    0.6, (0, 255, 255), 2, cv2.LINE_AA)
    cv2.rectangle(img, (0, 0), (520, 70), (0, 0, 0), -1)
    cv2.putText(img, f"So soi: {len(pts)}", (15, 52), cv2.FONT_HERSHEY_SIMPLEX,
                1.6, (0, 255, 255), 4, cv2.LINE_AA)
    return img


def collect_images(paths):
    out = []
    for p in map(Path, paths):
        if p.is_dir():
            out += sorted(f for f in p.iterdir() if f.suffix.lower() in IMAGE_EXT
                          and not f.stem.startswith("counting_manual"))
        elif p.is_file():
            out.append(p)
        else:
            print(f"Khong tim thay: {p}", file=sys.stderr)
    return out


def main():
    ap = argparse.ArgumentParser(description="Dem soi day cap nhanh (khong CSV)",
                                 formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    ap.add_argument("inputs", nargs="+", help="file anh hoac thu muc")
    ap.add_argument("--weights", default=str(DEFAULT_WEIGHTS), help="file trong so fastnet.pt")
    ap.add_argument("--device", default="AUTO", help="chi dung khi --weights la file .xml OpenVINO: AUTO, GPU, CPU")
    ap.add_argument("--threshold", type=float, default=0.8,
                    help="nguong dinh (0-1). Giam de lay them soi mo, tang de chat hon")
    ap.add_argument("--save-image", metavar="THU_MUC", default=str(HERE / "output_fast"),
                    help="thu muc luu anh co danh dau (*_count.png)")
    ap.add_argument("--no-save-image", action="store_true", help="khong luu anh")
    ap.add_argument("--expected", action="append", default=[], metavar="TEN=SO",
                    help="so soi dung de doi chieu, vd anh.bmp=238")
    args = ap.parse_args()

    expected = {}
    for item in args.expected:
        name, _, val = item.rpartition("=")
        expected[Path(name).stem] = int(val)
    files = collect_images(args.inputs)
    if not files:
        sys.exit("Khong co anh nao de xu ly.")
    save_dir = None if args.no_save_image else Path(args.save_image)
    if save_dir:
        save_dir.mkdir(parents=True, exist_ok=True)

    t0 = time.perf_counter()
    net = load_net(args.weights, args.device)
    count(np.zeros((300, 300), np.uint8), net)                  # chay thu 1 lan de khoi dong
    print(f"[TIMER] Load model + khoi dong: {time.perf_counter() - t0:.2f} s", flush=True)

    times = []
    for f in files:
        t_a = time.perf_counter()
        gray = cv2.imread(str(f), cv2.IMREAD_GRAYSCALE)
        if gray is None:
            print(f"Khong doc duoc: {f}", file=sys.stderr)
            continue
        t_b = time.perf_counter()
        pts = count(gray, net, args.threshold)
        t_c = time.perf_counter()
        times.append(t_c - t_b)
        msg = f"{f.name}: {len(pts)} soi"
        if f.stem in expected:
            msg += f" | dung: {expected[f.stem]} | lech {len(pts) - expected[f.stem]:+d}"
        print(msg + f" | [TIMER] imread {t_b - t_a:.3f}s, detect {t_c - t_b:.3f}s", flush=True)
        if save_dir:
            t_d = time.perf_counter()
            cv2.imwrite(str(save_dir / f"{f.stem}_count.png"), draw(gray, pts))
            print(f"          da luu anh: {save_dir / (f.stem + '_count.png')} ({time.perf_counter() - t_d:.2f} s)", flush=True)
    if times:
        print(f"[TIMER] detect trung binh {np.mean(times):.3f} s/anh (lon nhat {max(times):.3f} s) "
              f"tren {len(times)} anh")


if __name__ == "__main__":
    main()
