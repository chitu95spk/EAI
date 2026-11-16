#!/usr/bin/env python
"""Huan luyen mang nho FastNet (models/fastnet.pt) cho fast_counter.py.

Nhan = file *_points.csv (cot id,x,y = tam tung soi, toa do anh GOC). Co 3 cach co nhan:
    1. cellpose_counter.py          -> tu dong (Cellpose), co the sua tay bang --review
    2. label_points.py              -> dan nhan/sua nhan bang chuot (khuyen dung de lam nhan "that")
    3. tu viet CSV theo dung dinh dang tren

Tien xu ly + kich thuoc dung CHUNG voi fast_counter.py (import truc tiep) nen luc huan luyen va
luc dem luon giong nhau. Co the dung bang dong lenh (duoi day) hoac bang giao dien: fastnet_gui.py.

Cach dung:
    # 1) kiem tra du lieu truoc (khong huan luyen)
    python train_fastnet.py --images .. --labels output_v1 --check

    # 2) huan luyen, giu 2 anh lam tap kiem tra
    python train_fastnet.py --images .. --labels output_v1 --holdout 2 --iters 1200 --out models/fastnet_new.pt

    # 2b) huan luyen bang iGPU/GPU Intel (can torch ban XPU); --list-devices de xem + do toc do
    python train_fastnet.py --images .. --labels output_v1 --device xpu
    python train_fastnet.py --list-devices

    # 3) huan luyen tiep (fine-tune) tu trong so cu khi them anh moi
    python train_fastnet.py --images anh_moi --labels nhan_moi --init models/fastnet.pt --iters 400

Mac dinh ghi ra models/fastnet_new.pt de khong de len fastnet.pt dang dung.
Dung thu:  python fast_counter.py .. --weights models/fastnet_new.pt --threshold <gia tri goi y>
"""
import argparse
import csv
import os
import sys
import time
from pathlib import Path

import cv2
import numpy as np
import torch

import fast_counter as fc
from app_paths import app_dir

# iGPU/GPU Intel (XPU) phai bien dich kernel o lan chay dau (co the mat vai phut); bat cache tren dia de cac lan sau nhanh.
# Dat TRUOC khi PyTorch khoi tao thiet bi XPU lan dau.
os.environ.setdefault("SYCL_CACHE_PERSISTENT", "1")

HERE = app_dir(__file__)                         # canh file .exe khi da dong goi
SIGMA = 3.0          # do rong dinh trong ban do nhiet (pixel o do phan giai SCALE)
THRESHOLDS = (0.5, 0.6, 0.7, 0.8, 0.9)


# --------------------------------------------------------------------------- du lieu
def read_points(csv_path):
    with open(csv_path, newline="") as fh:
        pts = [(float(r["x"]), float(r["y"])) for r in csv.DictReader(fh)]
    return np.array(pts, np.float32).reshape(-1, 2)


def find_pairs(images, labels, log=print):
    pairs = []
    for f in fc.collect_images([images]):
        c = Path(labels) / f"{f.stem}_points.csv"
        if c.is_file():
            pairs.append((f, c))
        else:
            log(f"  (bo qua {f.name}: khong co {c.name})")
    return pairs


def pairwise(pts):
    d = np.hypot(pts[:, None, 0] - pts[None, :, 0], pts[:, None, 1] - pts[None, :, 1])
    np.fill_diagonal(d, np.inf)
    return d


def check(pairs, log=print):
    """Kiem tra chat luong du lieu. Tra ve (ok, rows); rows = list dict moi anh."""
    D = fc.DIAMETER
    log(f"Kiem tra {len(pairs)} cap anh/nhan  (duong kinh soi mong doi ~{D}px)")
    log(f"{'anh':<30}{'kich thuoc':>12}{'so nhan':>8}{'NN giua':>9}{'NN min':>8}{'ngoai':>7}{'sang TB':>9}  canh bao")
    ok, rows = True, []
    for img, c in pairs:
        g = cv2.imread(str(img), cv2.IMREAD_GRAYSCALE)
        pts = read_points(c)
        if g is None:
            rows.append(dict(name=img.stem, size="?", n=len(pts), nn_med=0, nn_min=0, out=0, mean=0,
                             flags=["khong doc duoc anh"]))
            ok = False
            continue
        h, w = g.shape
        flags = []
        if len(pts) < 2:
            rows.append(dict(name=img.stem, size=f"{w}x{h}", n=len(pts), nn_med=0, nn_min=0, out=0,
                             mean=float(g.mean()), flags=["nhan rong"]))
            log(f"{img.stem:<30}  nhan rong")
            ok = False
            continue
        nn = pairwise(pts).min(1)
        y0, y1, x0, x1 = fc.find_bundle_bbox(g, margin=int(D * 1.1))
        out = int(((pts[:, 0] < x0) | (pts[:, 0] >= x1) | (pts[:, 1] < y0) | (pts[:, 1] >= y1)).sum())
        if not 0.75 * D <= np.median(nn) <= 1.25 * D:
            flags.append("duong kinh soi lech -> doi ti le anh")
        if nn.min() < 0.55 * D:
            flags.append(f"{int((nn < 0.55 * D).sum())} nhan qua gan nhau (trung?)")
        if out:
            flags.append(f"{out} nhan ngoai bo day")
        if g.mean() < 15 or g.mean() > 200:
            flags.append("anh qua toi/qua sang")
        ok &= not flags
        rows.append(dict(name=img.stem, size=f"{w}x{h}", n=len(pts), nn_med=float(np.median(nn)),
                         nn_min=float(nn.min()), out=out, mean=float(g.mean()), flags=flags))
        log(f"{img.stem[:29]:<30}{f'{w}x{h}':>12}{len(pts):>8}{np.median(nn):>9.1f}{nn.min():>8.1f}"
            f"{out:>7}{g.mean():>9.1f}  {'; '.join(flags)}")
    counts = [r["n"] for r in rows]
    if counts:
        log(f"So nhan moi anh: min {min(counts)}, max {max(counts)}, lech {max(counts) - min(counts)}"
            f"{'  <- cung mot day thi nen bang nhau' if max(counts) != min(counts) else ''}")
    log("DU LIEU OK" if ok else "CO CANH BAO - xem lai cac dong tren")
    return ok, rows


def prepare(img, csv_path):
    """Anh -> (X uint8, Y float32) o do phan giai SCALE, dung cung tien xu ly luc dem."""
    g = cv2.imread(str(img), cv2.IMREAD_GRAYSCALE)
    y0, y1, x0, x1 = fc.find_bundle_bbox(g, margin=int(fc.DIAMETER * 1.1))
    X = cv2.resize(fc.enhance(g[y0:y1, x0:x1]), None, fx=fc.SCALE, fy=fc.SCALE, interpolation=cv2.INTER_AREA)
    pts = (read_points(csv_path) - [x0, y0]) * fc.SCALE
    Y = np.zeros(X.shape, np.float32)
    r = int(3 * SIGMA)
    for x, y in pts:
        xi, yi = int(round(x)), int(round(y))
        xa, xb = max(xi - r, 0), min(xi + r + 1, X.shape[1])
        ya, yb = max(yi - r, 0), min(yi + r + 1, X.shape[0])
        if xa >= xb or ya >= yb:
            continue
        yy, xx = np.mgrid[ya:yb, xa:xb]
        Y[ya:yb, xa:xb] = np.maximum(Y[ya:yb, xa:xb], np.exp(-((xx - x) ** 2 + (yy - y) ** 2) / (2 * SIGMA ** 2)))
    return X, Y


def make_batch(train, rng, bs, S):
    xs, ys = [], []
    for _ in range(bs):
        X, Y = train[rng.integers(len(train))]
        for _ in range(10):                                 # uu tien o cat co chua soi
            y0 = rng.integers(0, max(X.shape[0] - S, 1)); x0 = rng.integers(0, max(X.shape[1] - S, 1))
            if Y[y0:y0 + S, x0:x0 + S].max() > 0.5:
                break
        x = X[y0:y0 + S, x0:x0 + S].astype(np.float32) / 255
        y = Y[y0:y0 + S, x0:x0 + S]
        k = int(rng.integers(4)); x, y = np.rot90(x, k), np.rot90(y, k)
        if rng.random() < 0.5:
            x, y = x[:, ::-1], y[:, ::-1]
        # nhieu do sang / tuong phan de chiu duoc anh sang khac nhau
        x = np.clip(x ** rng.uniform(0.8, 1.25) * rng.uniform(0.85, 1.15) + rng.uniform(-0.05, 0.05), 0, 1)
        xs.append(np.ascontiguousarray(x)); ys.append(np.ascontiguousarray(y))
    return torch.from_numpy(np.stack(xs))[:, None].float(), torch.from_numpy(np.stack(ys))[:, None].float()


# --------------------------------------------------------------------------- danh gia
def evaluate(net, items, thresholds=THRESHOLDS, should_stop=None, status=None, label=""):
    """items: [(ten, anh xam, nhan (N,2))]. Tra ve {nguong: (sai lech so luong, F1 vi tri)}, hoac None neu bi dung.

    Mang chi chay MOT lan moi anh; sau do quet cac nguong tren ban do nhiet (nhanh hon ~5 lan).
    """
    net.eval()
    tol = 0.4 * fc.DIAMETER
    heats = []
    for k, (name, g, lab) in enumerate(items):
        if should_stop and should_stop():
            net.train()
            return None
        if status:
            status(f"Dang danh gia{label}: anh {k + 1}/{len(items)}")
        heats.append(fc.heatmap(g, net))
    res = {}
    for thr in thresholds:
        errs, f1s = [], []
        for (_, _, lab), (heat, x0, y0) in zip(items, heats):
            pts = fc.peaks(heat, x0, y0, thr).astype(np.float32)
            errs.append(len(pts) - len(lab))
            if len(pts) == 0:
                f1s.append(0.0)
                continue
            d = np.hypot(pts[:, None, 0] - lab[None, :, 0], pts[:, None, 1] - lab[None, :, 1])
            p, r = (d.min(1) < tol).mean(), (d.min(0) < tol).mean()
            f1s.append(2 * p * r / max(p + r, 1e-9))
        res[thr] = (np.array(errs), float(np.mean(f1s)))
    net.train()
    return res


def format_eval(title, res):
    lines = [f"  {title}"]
    for thr, (errs, f1) in res.items():
        lines.append(f"    nguong {thr:.2f}: lech so luong TB {np.abs(errs).mean():.2f} (bias {errs.mean():+.2f}, "
                     f"toi da {np.abs(errs).max()}) | F1 vi tri {f1:.3f}")
    return "\n".join(lines)


def best_threshold(res):
    return min(res, key=lambda t: (np.abs(res[t][0]).mean(), -res[t][1]))


# --------------------------------------------------------------------------- thiet bi huan luyen
class _DeviceFailed(RuntimeError):
    """Thiet bi (GPU/iGPU) loi ngay nhung buoc dau -> chuyen ve CPU."""


def available_devices():
    """[(khoa, nhan)] cac thiet bi PyTorch co the huan luyen tren may nay. Luon co 'cpu'."""
    out = [("cpu", f"CPU ({torch.get_num_threads()} luong)")]
    try:
        if torch.cuda.is_available():
            out.append(("cuda", f"CUDA: {torch.cuda.get_device_name(0)}"))
    except Exception:
        pass
    try:
        xpu = getattr(torch, "xpu", None)
        if xpu is not None and xpu.is_available():
            out.append(("xpu", f"Intel GPU (XPU): {xpu.get_device_name(0)}"))
    except Exception:
        pass
    return out


def device_hint():
    """Giai thich vi sao khong co GPU va cach bat (rong neu da co GPU)."""
    keys = [k for k, _ in available_devices()]
    if len(keys) > 1:
        return ""
    ver = torch.__version__
    if "+cpu" in ver or torch.version.cuda is None:
        return (f"PyTorch hien tai ({ver}) la ban CPU. Muon huan luyen bang iGPU/GPU Intel can cai ban XPU, vi du:\n"
                f"  pip install torch=={ver.split('+')[0]}+xpu --index-url https://download.pytorch.org/whl/xpu\n"
                "(can driver Intel moi; mot so iGPU doi cu co the khong duoc ho tro). "
                "Dung 'Do toc do' de kiem tra thiet bi co that su nhanh hon CPU khong.")
    return "PyTorch khong thay GPU nao (kiem tra driver)."


def resolve_device(name="cpu"):
    """'auto' -> cuda > xpu > cpu. Ten khong kha dung -> ValueError kem huong dan."""
    name = (name or "cpu").lower()
    avail = [k for k, _ in available_devices()]
    if name == "auto":
        return next((k for k in ("cuda", "xpu") if k in avail), "cpu")
    if name not in ("cpu", "cuda", "xpu"):
        raise ValueError(f"Thiet bi khong hop le: {name} (cpu | cuda | xpu | auto)")
    if name not in avail:
        raise ValueError(f"Thiet bi '{name}' khong kha dung tren may nay. {device_hint()}")
    return name


def _sync(dev):
    t = dev.type
    if t == "cuda":
        torch.cuda.synchronize()
    elif t == "xpu":
        torch.xpu.synchronize()


def benchmark_devices(batch=6, crop=192, steps=12, log=print, should_stop=None):
    """Do toc do huan luyen (buoc/giay) tren tung thiet bi bang du lieu gia. Tra ve list dict."""
    rows = []
    for key, label in available_devices():
        if should_stop and should_stop():
            break
        try:
            dev = torch.device(key)
            net = fc.FastNet().to(dev).train()
            opt = torch.optim.AdamW(net.parameters(), 1e-3)
            x = torch.rand(batch, 1, crop, crop, device=dev)
            y = torch.rand(batch, 1, crop, crop, device=dev) * 0.1

            def one():
                loss = ((1 + 30 * y) * (net(x) - y) ** 2).mean()
                opt.zero_grad(); loss.backward(); opt.step()
                return loss

            for _ in range(2):                                   # khoi dong / bien dich kernel
                one()
            _sync(dev)
            t = time.perf_counter()
            for _ in range(steps):
                one()
            _sync(dev)
            sps = steps / (time.perf_counter() - t)
            rows.append(dict(key=key, label=label, sps=sps, error=None))
            log(f"  {label}: {sps:.2f} buoc/giay  (batch {batch}, crop {crop})")
        except Exception as e:
            rows.append(dict(key=key, label=label, sps=None, error=str(e).splitlines()[0][:120]))
            log(f"  {label}: LOI - {str(e).splitlines()[0][:120]}")
    base = next((r["sps"] for r in rows if r["key"] == "cpu" and r["sps"]), None)
    if base:
        for r in rows:
            r["speedup"] = (r["sps"] / base) if r["sps"] else None
    return rows


# --------------------------------------------------------------------------- huan luyen
def run_training(images, labels, val=(), holdout=0, iters=1200, batch=6, crop=192, lr=3e-3,
                 init=None, out=None, seed=0, log=print, progress=None, should_stop=None, status=None,
                 device="cpu"):
    """Huan luyen FastNet. Dung duoc tu dong lenh lan giao dien.

    device: "cpu" | "cuda" | "xpu" (iGPU/GPU Intel, can torch ban XPU) | "auto". Loi o vai buoc dau tren GPU ->
    tu chuyen sang CPU. Danh gia luon chay tren CPU (giong luc su dung that).

    progress(it, iters, loss) goi sau moi buoc; should_stop() -> True de dung som (ca luc dang danh gia);
    status(text) bao giai doan hien tai. Tra ve dict: out, stopped, best, train_res, val_res, n_train, n_val.
    Neu bi dung giua chung: luu <ten>_partial.pt, KHONG danh gia (best/train_res/val_res = None).
    Neu bi dung luc danh gia: mo hinh da luu day du, danh gia bi bo (eval_skipped = True).
    """
    out = Path(out) if out else HERE / "models" / "fastnet_new.pt"
    pairs = find_pairs(images, labels, log)
    if not pairs:
        raise ValueError("Khong co cap anh/nhan nao. Ten nhan phai la <ten_anh>_points.csv")
    val_names = set(val)
    if holdout:
        val_names |= {p[0].stem for p in pairs[-holdout:]}
    train_pairs = [p for p in pairs if p[0].stem not in val_names]
    val_pairs = [p for p in pairs if p[0].stem in val_names]
    if not train_pairs:
        raise ValueError("Khong con anh nao de huan luyen.")
    log(f"Huan luyen: {len(train_pairs)} anh | kiem tra: {len(val_pairs)} anh "
        f"{[p[0].stem[-9:] for p in val_pairs]}")

    rng = np.random.default_rng(seed)
    torch.manual_seed(seed)
    train = [prepare(i, c) for i, c in train_pairs]
    val_items = [(i.stem, cv2.imread(str(i), cv2.IMREAD_GRAYSCALE), read_points(c)) for i, c in val_pairs]

    dev_key = resolve_device(device)
    labels_by_key = dict(available_devices())

    def train_loop(key):
        """Huan luyen tren thiet bi `key`. Tra ve (net tren CPU, stopped). Nem _DeviceFailed neu GPU loi o buoc dau."""
        dev = torch.device(key)
        net = fc.FastNet()
        if init:
            net.load_state_dict(torch.load(init, map_location="cpu"))
            log(f"Tiep tuc tu {init}")
        try:
            net.to(dev).train()
        except Exception as e:                                       # driver/thiet bi khong dung duoc
            if dev.type != "cpu":
                raise _DeviceFailed(str(e).splitlines()[0][:160] if str(e) else type(e).__name__)
            raise
        opt = torch.optim.AdamW(net.parameters(), lr, weight_decay=1e-4)
        sched = torch.optim.lr_scheduler.OneCycleLR(opt, lr, total_steps=iters)
        log(f"Thiet bi huan luyen: {labels_by_key.get(key, key)}")
        if key == "xpu":
            log("  (iGPU Intel: lan chay dau phai bien dich kernel, co the mat vai phut; cac lan sau dung cache nen nhanh)")
        t0, stopped = time.time(), False
        for it in range(iters):
            if should_stop and should_stop():
                stopped = True
                log(f"Da dung tai buoc {it}/{iters}")
                break
            x, y = make_batch(train, rng, batch, crop)
            try:
                x, y = x.to(dev), y.to(dev)
                loss = ((1 + 30 * y) * (net(x) - y) ** 2).mean()     # trong so 1+30*y: phat nang sai o tam soi, tranh ra toan 0
                opt.zero_grad(); loss.backward(); opt.step(); sched.step()
                loss_v = float(loss.item())
            except (RuntimeError, NotImplementedError, AssertionError) as e:
                if dev.type != "cpu" and it <= 2:
                    raise _DeviceFailed(str(e).splitlines()[0][:160] if str(e) else type(e).__name__)
                raise
            if progress:
                progress(it + 1, iters, loss_v)
            if it % 100 == 0 or it == iters - 1:
                log(f"  it {it:5d}/{iters}  loss {loss_v:.5f}  {time.time() - t0:.0f}s")
        done = max(it + (0 if stopped else 1), 1)
        log(f"Toc do: {done / max(time.time() - t0, 1e-9):.2f} buoc/giay")
        return net.cpu(), stopped

    try:
        net, stopped = train_loop(dev_key)
    except _DeviceFailed as e:
        log(f"Thiet bi {dev_key} loi ({e}) -> chuyen sang CPU va huan luyen lai tu dau")
        rng = np.random.default_rng(seed)
        torch.manual_seed(seed)
        net, stopped = train_loop("cpu")

    out.parent.mkdir(parents=True, exist_ok=True)
    save_to = out.with_name(out.stem + "_partial.pt") if stopped else out
    torch.save(net.state_dict(), save_to)
    log(f"Da luu {save_to}")

    if stopped:
        return dict(out=str(save_to), stopped=True, eval_skipped=True, best=None, train_res=None, val_res=None,
                    n_train=len(train_pairs), n_val=len(val_pairs))

    train_items = [(i.stem, cv2.imread(str(i), cv2.IMREAD_GRAYSCALE), read_points(c)) for i, c in train_pairs]
    log(f"Danh gia tren {len(train_items)} anh huan luyen + {len(val_items)} anh kiem tra...")
    train_res = evaluate(net, train_items, should_stop=should_stop, status=status, label=" (tap huan luyen)")
    val_res = evaluate(net, val_items, should_stop=should_stop, status=status, label=" (tap kiem tra)") if (
        train_res is not None and val_items) else None
    if train_res is None or (val_items and val_res is None):
        log("Da bo qua buoc danh gia theo yeu cau dung. Mo hinh da luu day du.")
        return dict(out=str(save_to), stopped=False, eval_skipped=True, best=None, train_res=None, val_res=None,
                    n_train=len(train_pairs), n_val=len(val_pairs))
    log(format_eval("TAP HUAN LUYEN (da thay trong luc hoc, chi de tham khao)", train_res))
    best_src = train_res
    if val_res is not None:
        log(format_eval("TAP KIEM TRA (chua thay -> con so dang tin)", val_res))
        best_src = val_res
    best = best_threshold(best_src)
    log(f"Nguong goi y: {best}  ->  python fast_counter.py <anh> --weights {save_to} --threshold {best}")
    if not val_items:
        log("(Khong co tap kiem tra: nguong chon tren anh da hoc nen co the qua lac quan. Dung --holdout 2.)")
    return dict(out=str(save_to), stopped=False, eval_skipped=False, best=best, train_res=train_res,
                val_res=val_res, n_train=len(train_pairs), n_val=len(val_pairs))


def main():
    ap = argparse.ArgumentParser(description="Huan luyen FastNet",
                                 formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    ap.add_argument("--images", help="thu muc anh (bat buoc tru khi dung --list-devices)")
    ap.add_argument("--labels", help="thu muc chua <ten_anh>_points.csv (bat buoc tru khi dung --list-devices)")
    ap.add_argument("--check", action="store_true", help="chi kiem tra du lieu roi thoat")
    ap.add_argument("--val", action="append", default=[], metavar="TEN_ANH", help="anh dung de kiem tra (khong huan luyen)")
    ap.add_argument("--holdout", type=int, default=0, help="giu N anh cuoi (theo ten) lam tap kiem tra")
    ap.add_argument("--iters", type=int, default=1200)
    ap.add_argument("--batch", type=int, default=6)
    ap.add_argument("--crop", type=int, default=192, help="canh o cat khi huan luyen (pixel, da thu nho)")
    ap.add_argument("--lr", type=float, default=3e-3)
    ap.add_argument("--init", help="trong so co san de huan luyen tiep (fine-tune)")
    ap.add_argument("--out", default=str(HERE / "models" / "fastnet_new.pt"))
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--device", default="cpu", choices=["cpu", "cuda", "xpu", "auto"],
                    help="thiet bi huan luyen (xpu = iGPU/GPU Intel, can torch ban XPU)")
    ap.add_argument("--list-devices", action="store_true", help="liet ke thiet bi + do toc do huan luyen roi thoat")
    args = ap.parse_args()

    if args.list_devices:
        print("Thiet bi huan luyen:", ", ".join(label for _, label in available_devices()))
        hint = device_hint()
        if hint:
            print(hint)
        benchmark_devices(args.batch, args.crop)
        return
    if not args.images or not args.labels:
        ap.error("can --images va --labels (tru khi dung --list-devices)")

    pairs = find_pairs(args.images, args.labels)
    if not pairs:
        sys.exit("Khong co cap anh/nhan nao. Ten nhan phai la <ten_anh>_points.csv")
    ok, _ = check(pairs)
    if args.check:
        sys.exit(0 if ok else 1)
    if not ok:
        print("Canh bao o tren khong chan huan luyen, nhung nen sua truoc neu la loi that.\n")
    print()
    run_training(args.images, args.labels, args.val, args.holdout, args.iters, args.batch, args.crop,
                 args.lr, args.init, args.out, args.seed, device=args.device)


if __name__ == "__main__":
    main()
