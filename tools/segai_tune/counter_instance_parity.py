"""CPU (OpenVINO) vs CUDA (ONNX Runtime) parity of the SegAI instance head on the Counter test images.
Needs a BeeNativeSegAI.dll that exports SEGAI_InferGetLastInstanceTensor (SEGAI_NATIVE_DIR=...integrated-instance).
Compares the raw network output tensor per value ({classes+7, grid, grid}: heat logits + 7 regression channels), the sigmoid heat,
and the decoded detections, then writes one JSON summary to CE_OUT (never into the original project).
Usage: python -X utf8 counter_instance_parity.py MODEL OUT.json [split,...]   (default split: final,real)
"""
import os, sys, json, time
os.environ["EASYAI_SEGAI_INSTANCE_TENSOR"] = "1"  # must be set before the engines load the model
from pathlib import Path
import ctypes as C
import numpy as np, cv2
import seg_native as sn

SEG = Path(r"C:\Demo\LucGiac.segai-tune")
getter = sn.bind("InferGetLastInstanceTensor", [sn.P, C.POINTER(C.c_float), sn.I, C.POINTER(sn.I), C.POINTER(sn.I)])

def tensor(engine):
    ch = sn.I(); gr = sn.I()
    n = getter(engine.h, None, 0, C.byref(ch), C.byref(gr))
    assert n in (-2,), f"instance tensor not captured (rc={n})"
    buf = (C.c_float * (ch.value * gr.value * gr.value))()
    n = getter(engine.h, buf, len(buf), C.byref(ch), C.byref(gr))
    assert n == len(buf), n
    return np.frombuffer(buf, np.float32).reshape(ch.value, gr.value, gr.value).copy()

def make(backend):
    os.environ["SEGAI_BACKEND"] = backend
    return sn.SegNative(sys.argv[1], instance_mode=True, threshold=0.2, quality=0)

def main():
    model, out = sys.argv[1], Path(sys.argv[2])
    splits = (sys.argv[3] if len(sys.argv) > 3 else "final,real").split(",")
    man = json.loads((SEG / "dataset/manifest.json").read_text())
    recs = [r for r in man["records"] if r["split"] in splits]
    t0 = time.perf_counter(); cpu = make("cpu"); cpu_load = time.perf_counter() - t0
    t0 = time.perf_counter(); cuda = make("cuda"); cuda_load = time.perf_counter() - t0
    first = {}
    rows = []
    heat_abs = reg_abs = logit_abs = 0.0
    all_cpu = []; all_cuda = []
    det_dpos = det_dsize = det_dth = det_dscore = 0.0; det_mismatch = 0
    for i, r in enumerate(recs):
        im = cv2.imread(r["image"]); assert im is not None
        t = time.perf_counter(); a = cpu.run_instances(im); ta = time.perf_counter() - t
        t = time.perf_counter(); b = cuda.run_instances(im); tb = time.perf_counter() - t
        if i == 0: first = dict(cpu_first_call_ms=ta * 1000, cuda_first_call_ms=tb * 1000, cuda_backend_after_first=b["backend"], cuda_note=b["backend_note"])
        ta_, tb_ = tensor(cpu), tensor(cuda)
        classes = ta_.shape[0] - 7
        d = np.abs(ta_ - tb_)
        sig = lambda x: 1 / (1 + np.exp(-x))
        dh = np.abs(sig(ta_[:classes]) - sig(tb_[:classes]))
        logit_abs = max(logit_abs, float(d[:classes].max())); reg_abs = max(reg_abs, float(d[classes:].max())); heat_abs = max(heat_abs, float(dh.max()))
        all_cpu.append(ta_.ravel()); all_cuda.append(tb_.ravel())
        da, db = a["detections"], b["detections"]
        if len(da) != len(db): det_mismatch += 1
        else:
            for p, q in zip(da, db):
                det_dpos = max(det_dpos, abs(p[0] - q[0]), abs(p[1] - q[1])); det_dsize = max(det_dsize, abs(p[2] - q[2]), abs(p[3] - q[3]))
                det_dth = max(det_dth, abs(p[4] - q[4])); det_dscore = max(det_dscore, abs(p[5] - q[5]))
        rows.append(dict(image=Path(r["image"]).name, cpu_count=len(da), cuda_count=len(db), max_tensor_abs=float(d.max())))
    ac, bc = np.concatenate(all_cpu), np.concatenate(all_cuda)
    err = np.abs(ac - bc)
    summary = dict(images=len(recs), values_compared=int(ac.size), max_abs_logit_heat=logit_abs, max_abs_regression=reg_abs, max_abs_sigmoid_heat=heat_abs,
                   max_abs_all=float(err.max()), mean_abs_all=float(err.mean()), p99_9_abs=float(np.percentile(err, 99.9)),
                   tensor_range=[float(ac.min()), float(ac.max())], images_with_different_count=det_mismatch,
                   max_det_center_px=det_dpos, max_det_size_px=det_dsize, max_det_theta_rad=det_dth, max_det_score=det_dscore,
                   cpu_load_s=cpu_load, cuda_load_s=cuda_load, **first)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(dict(summary=summary, rows=rows), indent=1), encoding="utf-8")
    print(json.dumps(summary, indent=1))
    cpu.close(); cuda.close()

main()
