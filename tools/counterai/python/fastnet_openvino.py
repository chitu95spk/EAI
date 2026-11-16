#!/usr/bin/env python
"""Chuyen FastNet (PyTorch) sang OpenVINO IR va dung IR do thay mang torch trong fast_counter.

Dong lenh:
    python fastnet_openvino.py models/fastnet.pt                 # chuyen FP16 + do toc do cac thiet bi
    python fastnet_openvino.py models/fastnet.pt --fp32
    python fast_counter.py .. --weights models/openvino_fastnet/fastnet_fp16.xml --device GPU

IR duoc luu voi kich thuoc dong (H, W bat ky). Luc chay, anh duoc don them cho H, W la boi so cua 64
de chi phai bien dich vai kich thuoc (cac ban bien dich duoc nho lai trong bo nho + cache tren dia).
"""
import argparse
import os
import sys
import tempfile
import threading
import time
from pathlib import Path

import numpy as np
import torch

import fast_counter as fc
from app_paths import app_dir

HERE = app_dir(__file__)                         # canh file .exe khi da dong goi
DEFAULT_OUT_DIR = HERE / "models" / "openvino_fastnet"
PAD = 64


def available_devices():
    try:
        import openvino as ov
    except ImportError:
        return []
    return list(ov.Core().available_devices)


def device_name(core, d):
    try:
        return core.get_property(d, "FULL_DEVICE_NAME")
    except Exception:
        return d


def convert(weights, out_xml=None, fp16=True, log=print):
    """FastNet .pt -> OpenVINO IR (.xml + .bin). Tra ve duong dan .xml."""
    import openvino as ov
    weights = Path(weights)
    out_xml = Path(out_xml) if out_xml else DEFAULT_OUT_DIR / f"{weights.stem}_{'fp16' if fp16 else 'fp32'}.xml"
    out_xml.parent.mkdir(parents=True, exist_ok=True)
    net = fc.load_net(weights)
    t = time.perf_counter()
    with torch.no_grad():
        ir = ov.convert_model(net, example_input=torch.zeros(1, 1, 256, 256),
                              input=ov.PartialShape([1, 1, -1, -1]))
    ov.save_model(ir, str(out_xml), compress_to_fp16=fp16)
    size = sum(p.stat().st_size for p in (out_xml, out_xml.with_suffix(".bin")) if p.exists())
    log(f"Da chuyen {weights.name} -> {out_xml.name} ({'FP16' if fp16 else 'FP32'}, "
        f"{size / 1e6:.2f} MB, {time.perf_counter() - t:.1f} s)")
    return out_xml


class OVFastNet:
    """Thay the fc.FastNet: nhan tensor (1,1,H,W) tra ve tensor (1,1,H,W), chay bang OpenVINO."""

    def __init__(self, xml, device="AUTO", precision="f32"):
        import openvino as ov
        self.core = ov.Core()
        self.xml = Path(xml)
        self.device = device
        cache = self.xml.parent / "cache"
        if not os.access(self.xml.parent, os.W_OK):          # vd cai trong Program Files: cache sang thu muc tam
            cache = Path(tempfile.gettempdir()) / "fastnet_ov_cache"
        self.cfg = {"PERFORMANCE_HINT": "LATENCY", "CACHE_DIR": str(cache)}
        if precision:                            # iGPU mac dinh chay f16 -> lech so dinh; ep f32 cho khop PyTorch
            self.cfg["INFERENCE_PRECISION_HINT"] = precision
        self._compiled = {}                      # (H, W) -> mo hinh da bien dich
        self._lock = threading.Lock()            # InferRequest khong dung chung duoc giua cac luong

    def _get(self, h, w):
        if (h, w) not in self._compiled:
            m = self.core.read_model(str(self.xml))
            m.reshape([1, 1, h, w])
            self._compiled[(h, w)] = self.core.compile_model(m, self.device, self.cfg)
        return self._compiled[(h, w)]

    def eval(self):
        return self

    def __call__(self, x):
        a = x.detach().cpu().numpy().astype(np.float32)
        h, w = a.shape[-2:]
        H, W = (h + PAD - 1) // PAD * PAD, (w + PAD - 1) // PAD * PAD
        if (H, W) != (h, w):
            a = np.pad(a, ((0, 0), (0, 0), (0, H - h), (0, W - w)), mode="edge")
        with self._lock:
            out = self._get(H, W)(a)[0][:, :, :h, :w]
        return torch.from_numpy(np.ascontiguousarray(out))


def load(xml, device="AUTO", precision="f32"):
    xml = Path(xml)
    if not xml.is_file():
        sys.exit(f"Khong tim thay IR OpenVINO: {xml}")
    return OVFastNet(xml, device, precision)


def benchmark(weights, xml, devices=None, size=768, runs=5, log=print):
    """Do toc do + sai lech so voi PyTorch cho tung thiet bi. Tra ve list dict."""
    import openvino as ov
    core = ov.Core()
    devices = devices or available_devices()
    rng = np.random.default_rng(0)
    x = torch.from_numpy(rng.random((1, 1, size, size), dtype=np.float32))
    net = fc.load_net(weights)
    with torch.no_grad():
        ref = net(x)
        net(x)
        t = time.perf_counter()
        for _ in range(runs):
            net(x)
        t_torch = (time.perf_counter() - t) / runs * 1000
    rows = [dict(device="PyTorch CPU", name="torch", compile_s=0.0, ms=t_torch, diff=0.0)]
    log(f"PyTorch CPU: {t_torch:.0f} ms / anh {size}x{size}")
    for d in devices:
        try:
            t = time.perf_counter()
            net_ov = OVFastNet(xml, d, "f32")
            y = net_ov(x)                                   # bien dich + chay lan dau
            tc = time.perf_counter() - t
            t = time.perf_counter()
            for _ in range(runs):
                net_ov(x)
            ms = (time.perf_counter() - t) / runs * 1000
            diff = float((y - ref).abs().max())
            rows.append(dict(device=d, name=device_name(core, d), compile_s=tc, ms=ms, diff=diff))
            log(f"OpenVINO {d} ({device_name(core, d)}): bien dich {tc:.1f} s | {ms:.0f} ms | "
                f"lech toi da {diff:.4f} | nhanh gap {t_torch / ms:.1f}x so voi PyTorch")
        except Exception as e:
            rows.append(dict(device=d, name=d, compile_s=0.0, ms=float("nan"), diff=float("nan"),
                             error=str(e).splitlines()[0][:120]))
            log(f"OpenVINO {d}: loi - {str(e).splitlines()[0][:120]}")
    return rows


def main():
    ap = argparse.ArgumentParser(description="Chuyen FastNet sang OpenVINO")
    ap.add_argument("weights", help="file fastnet .pt")
    ap.add_argument("--out", help="duong dan .xml dau ra")
    ap.add_argument("--fp32", action="store_true", help="luu FP32 (mac dinh FP16)")
    ap.add_argument("--no-benchmark", action="store_true")
    args = ap.parse_args()
    xml = convert(args.weights, args.out, fp16=not args.fp32)
    if not args.no_benchmark:
        benchmark(args.weights, xml)


if __name__ == "__main__":
    main()
