#!/usr/bin/env python
"""Dan nhan / sua nhan tam soi bang chuot, luu thanh <ten_anh>_points.csv (dung cho train_fastnet.py).

Cach dung:
    python label_points.py ..\\Image_20261026164016356.bmp          # mot anh
    python label_points.py ..                                       # ca thu muc, lan luot tung anh
    python label_points.py anh_moi --labels nhan_moi                # chon thu muc luu nhan

Quy trinh nhanh (khuyen dung): neu anh chua co nhan, chuong trinh TU DONG danh dau bang fast_counter
(~0.5 s) roi ban chi viec them cac soi thieu / xoa cac diem thua. Neu da co CSV thi nap lai de sua tiep.
Phai kiem tra het ca anh, vi mang se hoc dung nhung gi ban luu o day.

Phim / chuot:
    chuot trai   them diem (bam vao TAM soi)
    chuot phai   xoa diem gan nhat
    u            hoan tac thao tac gan nhat
    r            nap lai tu dong danh dau (bo het thay doi)
    s / Enter    luu CSV va sang anh tiep theo
    n            bo qua anh nay (khong luu)
    q / Esc      thoat
"""
import argparse
import csv
import sys
from pathlib import Path

import cv2
import numpy as np

import fast_counter as fc
from app_paths import app_dir

HERE = app_dir(__file__)                         # canh file .exe khi da dong goi


class Labeler:
    """Giu danh sach diem + lich su de hoan tac. Tach rieng khoi GUI de de kiem thu."""

    def __init__(self, pts, reach):
        self.pts = [tuple(map(int, p)) for p in pts]
        self.history = []
        self.reach = reach                       # ban kinh toi da de xoa diem (pixel anh goc)

    def add(self, x, y):
        self.history.append(list(self.pts))
        self.pts.append((int(x), int(y)))

    def remove_near(self, x, y):
        if not self.pts:
            return False
        d = [(px - x) ** 2 + (py - y) ** 2 for px, py in self.pts]
        i = int(np.argmin(d))
        if d[i] > self.reach ** 2:
            return False
        self.history.append(list(self.pts))
        self.pts.pop(i)
        return True

    def undo(self):
        if self.history:
            self.pts = self.history.pop()


def read_points(path):
    with open(path, newline="") as fh:
        return [(float(r["x"]), float(r["y"])) for r in csv.DictReader(fh)]


def write_points(path, pts):
    with open(path, "w", newline="") as fh:
        wr = csv.writer(fh)
        wr.writerow(["id", "x", "y"])
        for i, (x, y) in enumerate(pts, 1):
            wr.writerow([i, int(x), int(y)])


def render(base, lab, view, scale, title):
    """Ve diem len anh nen (da cat theo bo day va phong to/thu nho)."""
    x0, y0 = view
    img = base.copy()
    r = max(int(fc.DIAMETER / 2 * scale), 4)
    for x, y in lab.pts:
        c = (int((x - x0) * scale), int((y - y0) * scale))
        cv2.circle(img, c, r, (0, 255, 0), 1)
        cv2.circle(img, c, 3, (0, 0, 255), -1)
    cv2.rectangle(img, (0, 0), (340, 34), (0, 0, 0), -1)
    cv2.putText(img, f"So soi: {len(lab.pts)}  {title}", (8, 24), cv2.FONT_HERSHEY_SIMPLEX,
                0.7, (0, 255, 255), 2, cv2.LINE_AA)
    return img


def label_one(img_path, out_csv, net):
    gray = cv2.imread(str(img_path), cv2.IMREAD_GRAYSCALE)
    if gray is None:
        print(f"Khong doc duoc: {img_path}", file=sys.stderr)
        return "skip"
    auto = [tuple(p) for p in fc.count(gray, net)] if net is not None else []
    start = read_points(out_csv) if out_csv.is_file() else auto
    print(f"{img_path.name}: bat dau voi {len(start)} diem "
          f"({'tu CSV da luu' if out_csv.is_file() else 'tu dong danh dau'})")

    y0, y1, x0, x1 = fc.find_bundle_bbox(gray, margin=int(fc.DIAMETER * 1.1))
    scale = min(1500 / (x1 - x0), 900 / (y1 - y0))
    base = cv2.resize(fc.enhance(gray)[y0:y1, x0:x1], None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    base = cv2.cvtColor(base, cv2.COLOR_GRAY2BGR)

    lab = Labeler(start, reach=fc.DIAMETER * 0.7)
    win = f"dan nhan: {img_path.name}"
    state = {"redraw": True}

    def on_mouse(ev, mx, my, *_):
        x, y = mx / scale + x0, my / scale + y0
        if ev == cv2.EVENT_LBUTTONDOWN:
            lab.add(x, y)
        elif ev == cv2.EVENT_RBUTTONDOWN:
            lab.remove_near(x, y)
        else:
            return
        state["redraw"] = True

    cv2.namedWindow(win, cv2.WINDOW_AUTOSIZE)
    cv2.setMouseCallback(win, on_mouse)
    result = "quit"
    while True:
        if state["redraw"]:
            cv2.imshow(win, render(base, lab, (x0, y0), scale, "[s luu | u hoan tac | r nap lai | n bo qua]"))
            state["redraw"] = False
        k = cv2.waitKey(30) & 0xFF
        if k in (ord("s"), 13):
            write_points(out_csv, lab.pts)
            print(f"  da luu {len(lab.pts)} diem -> {out_csv}")
            result = "saved"
            break
        if k == ord("u"):
            lab.undo(); state["redraw"] = True
        if k == ord("r"):
            lab.history.append(list(lab.pts)); lab.pts = [tuple(map(int, p)) for p in auto]; state["redraw"] = True
        if k == ord("n"):
            result = "skip"
            break
        if k in (ord("q"), 27):
            break
    cv2.destroyWindow(win)
    return result


def main():
    ap = argparse.ArgumentParser(description="Dan nhan tam soi bang chuot",
                                 formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    ap.add_argument("inputs", nargs="+", help="file anh hoac thu muc")
    ap.add_argument("--labels", default=str(HERE / "labels"), help="thu muc luu <ten_anh>_points.csv")
    ap.add_argument("--weights", default=str(fc.DEFAULT_WEIGHTS), help="trong so dung de tu dong danh dau")
    ap.add_argument("--no-auto", action="store_true", help="khong tu dong danh dau, dan tu dau")
    args = ap.parse_args()

    files = fc.collect_images(args.inputs)
    if not files:
        sys.exit("Khong co anh nao.")
    out_dir = Path(args.labels)
    out_dir.mkdir(parents=True, exist_ok=True)
    net = None if args.no_auto else fc.load_net(args.weights)

    for f in files:
        res = label_one(f, out_dir / f"{f.stem}_points.csv", net)
        if res == "quit":
            break
    print(f"Nhan nam trong: {out_dir.resolve()}")


if __name__ == "__main__":
    main()
