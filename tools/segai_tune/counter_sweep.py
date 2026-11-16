"""OFAT sweep of native instance-head inference options on the TUNE split only (never final).
Usage: python -X utf8 counter_sweep.py MODEL TAG [thresholds comma list] [quality list] [repeats]
Writes C:\\Demo\\LucGiac.counter\\metrics\\sweep_TAG.json and prints one line per trial."""
import sys, json
from pathlib import Path
import counter_eval as ce
from seg_native import SegNative

model, tag = sys.argv[1], sys.argv[2]
ths = [float(x) for x in (sys.argv[3] if len(sys.argv) > 3 else "0.1,0.2,0.35,0.5").split(",")]
qs = [int(x) for x in (sys.argv[4] if len(sys.argv) > 4 else "0,1").split(",")]
rep = int(sys.argv[5]) if len(sys.argv) > 5 else 2
man = json.loads((ce.SEG / "dataset/manifest.json").read_text())
tune = [r for r in man["records"] if r["split"] == "tune"]
out = []
for q in qs:
    for t in ths:
        opts = {"instance_mode": True, "threshold": t, "quality": q}
        eng = SegNative(model, **opts)
        try:
            s = ce.evaluate(eng, tune, ce.OUT / f"sweep_{tag}_t{t}_q{q}.json", rep)
        finally:
            eng.close()
        out.append(dict(opts=opts, summary=s))
        print(tag, json.dumps(opts), "P=%.3f R=%.3f exact=%d/%d score=%.3f p50=%.2f" % (
            s["precision"], s["recall"], s["count_exact"], s["images"], s["mean_score"] or 0, s["p50_ms"]), flush=True)
(ce.OUT / f"sweep_{tag}.json").write_text(json.dumps(out, indent=1), encoding="utf-8")
