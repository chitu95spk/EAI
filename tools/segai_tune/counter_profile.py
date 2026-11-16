"""Phase / bottleneck profile of a SegAI instance-head model without changing native code.
Usage: python -X utf8 counter_profile.py MODEL TAG [threshold] [mask-hex,mask-hex,...]
Uses tune-split images only, 40 images x 3 repeats per variant. Writes metrics/profile_TAG.json."""
import sys, json, time, ctypes as C
import numpy as np, cv2
import counter_eval as ce
from seg_native import SegNative

model, tag = sys.argv[1], sys.argv[2]
thr = float(sys.argv[3]) if len(sys.argv) > 3 else 0.35
masks = [int(x, 16) for x in sys.argv[4].split(",")] if len(sys.argv) > 4 else []
man = json.loads((ce.SEG / "dataset/manifest.json").read_text())
tune = [r for r in man["records"] if r["split"] == "tune"]
images = [cv2.imread(r["image"]) for r in tune]
k32 = C.WinDLL("kernel32", use_last_error=True)
k32.GetCurrentProcess.restype = C.c_void_p
k32.GetProcessAffinityMask.argtypes = [C.c_void_p, C.POINTER(C.c_size_t), C.POINTER(C.c_size_t)]
k32.SetProcessAffinityMask.argtypes = [C.c_void_p, C.c_size_t]
proc = k32.GetCurrentProcess()
pm, sm = C.c_size_t(), C.c_size_t()
assert k32.GetProcessAffinityMask(proc, C.byref(pm), C.byref(sm)), C.get_last_error()
FULL = pm.value

def time_engine(eng, reps=3, keys=("native_ms",)):
    for k in range(8): eng.run(images[k % len(images)])
    nat, cyc, ph = [], [], {}
    for im in images:
        for _ in range(reps):
            b = time.perf_counter(); p = eng.run(im); cyc.append((time.perf_counter() - b) * 1000)
            nat.append(p["native_ms"])
            for k, v in (p.get("phases") or {}).items(): ph.setdefault(k, []).append(v)
    return dict(native_p50=float(np.median(nat)), native_p95=float(np.percentile(nat, 95)),
                cycle_p50=float(np.median(cyc)), cycle_p95=float(np.percentile(cyc, 95)),
                phases_p50={k: float(np.median(v)) for k, v in ph.items()})

res = {}
def run(name, opts, mask=FULL):
    assert k32.SetProcessAffinityMask(proc, C.c_size_t(mask)), C.get_last_error()
    eng = SegNative(model, **opts)
    try: res[name] = dict(opts=opts, mask=hex(mask), **time_engine(eng))
    finally: eng.close(); k32.SetProcessAffinityMask(proc, C.c_size_t(FULL))
    r = res[name]
    print(tag, name, "native p50=%.2f p95=%.2f cycle p50=%.2f p95=%.2f" % (r["native_p50"], r["native_p95"], r["cycle_p50"], r["cycle_p95"]), r["phases_p50"] or "", flush=True)

run("semantic_head_phases", {"threshold": 0.5})                        # shared backbone + semantic head, phase timers populated
run("instance_q0", {"instance_mode": True, "threshold": thr, "quality": 0})
run("instance_no_peaks", {"instance_mode": True, "threshold": 0.999, "quality": 0})   # ~ letterbox + infer + sigmoid, almost no decode/NMS
run("instance_q1", {"instance_mode": True, "threshold": thr, "quality": 1})
for m in masks:
    run("instance_q0_mask_%x" % m, {"instance_mode": True, "threshold": thr, "quality": 0}, m)
ce.OUT.mkdir(parents=True, exist_ok=True)
(ce.OUT / f"profile_{tag}.json").write_text(json.dumps(res, indent=1), encoding="utf-8")
