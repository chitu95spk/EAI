"""Reconcile verified raw artifacts into a short final reviewable report."""
from pathlib import Path
import json,time,hashlib,shutil,subprocess
import cv2,numpy as np
R=Path(r"D:\CODE\EAI");O=Path(r"C:\Demo\LucGiac.segai-tune");D=R/"docs/segai-tune"
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def summarize(rows):
 tp=sum(r["tp"] for r in rows);fp=sum(r["fp"] for r in rows);fn=sum(r["fn"] for r in rows)
 return dict(images=len(rows),objects=sum(r["gt"] for r in rows),precision=tp/max(1,tp+fp),recall=tp/max(1,tp+fn),
             count_exact=sum(r["prediction"]["count"]==r["gt"] for r in rows),tp=tp,fp=fp,fn=fn)
def main():
 deadline=time.time()+21600
 while not (O/"verified_completion.json").exists():
  if time.time()>deadline:raise TimeoutError("verified native experiment did not finish")
  time.sleep(10)
 v=json.loads((O/"verified_completion.json").read_text());s=v["results"]
 base=s["baseline_verified"];tuned=s["tuned_verified"];sel=v["model"]
 raw={name:json.loads((O/"metrics"/(name+"_final.json")).read_text()) for name in ["baseline_verified","tuned_verified"]}
 rawreal={name:json.loads((O/"metrics"/(name+"_real.json")).read_text()) for name in ["baseline_verified","tuned_verified"]}
 split={}
 for name,rr in raw.items():
  split[name]={g:summarize([r for r in rr["raw"] if (r["bg"] in ["wood","dots","carbon"])==unseen])
               for g,unseen in [("seen_background",False),("unseen_background",True)]}
 (O/"background_results.json").write_text(json.dumps(split,indent=2),encoding="utf-8")
 manifest=json.loads((O/"dataset/manifest.json").read_text())
 # Visual artifact after selection: thin GT green, prediction red, no metrics/selection change.
 assets=D/"assets";assets.mkdir(exist_ok=True)
 for name,rr in rawreal.items():
  row=rr["raw"][0];im=cv2.imread(row["image"]);gt=[r for r in manifest["records"] if r["split"]=="real"][0]["boxes"]
  for pts in gt:cv2.polylines(im,[np.asarray(pts,np.int32)],True,(0,220,0),2)
  for pts in row["prediction"]["boxes"]:cv2.polylines(im,[np.asarray(pts,np.int32)],True,(0,0,255),3)
  scale=min(800/im.shape[1],800/im.shape[0])
  im=cv2.resize(im,(int(im.shape[1]*scale),int(im.shape[0]*scale)))
  cv2.imwrite(str(assets/(name+"_real.jpg")),im)
 # Verify saved copy by native API and exact file hash, with frozen settings and affinity.
 import sys;sys.path.insert(0,str(R/"tools/segai_tune"))
 from seg_native import SegNative
 import run_experiment as ex
 assert sha(O/"tuned/model.segai")==sel["sha256"]
 eng=SegNative(O/"tuned/model.segai",**sel["settings"]);ex.affinity(sel["affinity"])
 try:smoke=eng.run(cv2.imread(rawreal["tuned_verified"]["raw"][0]["image"]))
 finally:eng.close();ex.affinity(ex.FULL)
 assert smoke["count"]==rawreal["tuned_verified"]["raw"][0]["prediction"]["count"]
 native_diff=subprocess.run(["git","diff","--numstat","--","BeeNativeSegAI"],cwd=R,capture_output=True,text=True).stdout.strip()
 assert not native_diff,"Unexpected native source diff"
 script_hash={str(p):sha(p) for p in (R/"tools/segai_tune").glob("*.py")}
 (O/"script_hashes.json").write_text(json.dumps(script_hash,indent=2),encoding="utf-8")
 all_real=rawreal["tuned_verified"]["raw"][0]
 fmt=lambda x:"N/A" if x is None else f"{x:.6f}"
 lines=["# SegAI vs Count (hex) - verified report 2026-11-10","",
 "## Status","",
 "**CHUA DAT TOAN BO / CHUA NGHIEM THU.** Native Segment AI training, tuning and paired measurements completed. Native Couter Count cannot be measured from this checkout because its implementation/runtime is absent. No completion ticks, commit or push.",
 "",
 "## Final comparison - same frozen 100-image manifest","",
 "|Method|Train input|Exact count /100|Precision IoU .5|Recall IoU .5|Mean score|Cycle p50 / p95 ms|Native p50 / p95 ms|",
 "|---|---|---|---|---|---|---|---|",
 "|Count / native Couter|N/A|N/A|N/A|N/A|N/A|N/A|N/A|"]
 for name,label in [("baseline_verified","SegAI baseline"),("tuned_verified","SegAI tuned")]:
  a=s[name];lines.append("|"+label+"|640x640|"+str(a["count_exact"])+"/100|"+fmt(a["precision"])+"|"+fmt(a["recall"])+"|"+fmt(a["mean_score"])+f'|{a["p50_ms"]:.3f} / {a["p95_ms"]:.3f}|{a["native_p50_ms"]:.3f} / {a["native_p95_ms"]:.3f}|')
 lines+=["",f'Paired verification speedup: **{v["speedup"]:.4f}x end-to-end**, {v["native_speedup"]:.4f}x native only. Requested speed condition end-to-end >=2x: **{v["speedup"]>=2}**. Do not round 1.95x into 2x.',
 "Score increased: "+str(tuned["mean_score"]>base["mean_score"])+". Precision non-decrease: "+str(tuned["precision"]>=base["precision"])+". Recall non-decrease: "+str(tuned["recall"]>=base["recall"])+".",
 "",
 "## Real photo - diagnostic, 20 labelled screws","",
 "|Method|Detected / truth|TP / FP / FN IoU .5|Cycle p50 / p95 ms|","|---|---|---|---|"]
 for name,label in [("baseline_verified","Baseline"),("tuned_verified","Tuned")]:
  rr=rawreal[name];a=rr["summary"];row=rr["raw"][0]
  lines.append("|"+label+"|"+str(row["prediction"]["count"])+"/20|"+f'{a["tp"]} / {a["fp"]} / {a["fn"]}|{a["p50_ms"]:.3f} / {a["p95_ms"]:.3f}|')
 lines+=["","The real photo supplied the source crops, so it is NOT independent validation. Synthetic holdout improvement is not a production/generalization guarantee.",
 "",
 "## Baseline and unchanged input evidence","",
 "- No original LucGiac Segment AI model existed in C:/Demo/LucGiac/.easyai/models. A native-default baseline was trained rather than substituting YOLO or a different product's model.",
 "- BeeNativeSegAI C API: TrainerCreate, ConfigureV2, AddSample, Train, Save, InferLoad/Predict, GetConfidence and InferGetPhaseTimings. Instance-head factor additionally used AddInstances/PredictInstances.",
 "- Fixed Side640 / 640x640 is checked from the actual saved native YAML payload; originalInput=0 and V2SpeedScale=1.0 throughout. No resizing shortcut was used.",
 "- Default baseline settings: epochs200 cap, lr0.015, regularization0.001, rotation15, scaleVariation0.15, brightness0.15, augmentation on, earlyStop on. Train backend CPU: the default Intel GPU request returned -8 (Intel GPU unavailable); Quadro P5000 is not an Intel OpenVINO GPU.",
 "- Baseline native saved metadata: "+str(O/"baseline/model.json")+". Comparison is relative to this newly trained default baseline, not to an unavailable historical deployment model.",
 "",
 "## Data isolation","",
 "- 120 train +30 internal validation +40 tuning +100 final synthetic images. Training and validation intentionally share source identities; tune and final identities are separately held out.",
 "- Source groups: "+json.dumps(manifest["source_groups"])+".",
 "- Fixed seeds and frozen SHA256 image hashes; disjoint train/tune/final source identities, seeds and image hashes asserted by dataset.py. Train/val backgrounds use the 11 known families. Final has 50 known +50 unseen wood/dots/carbon backgrounds; tuning never contains those unseen background families.",
 "- Exact selected alpha>128 semantic masks are emitted from a derived in-memory synthgen.compose; original synthgen.py is not edited by this task. Bounding boxes come from the same accepted objects.",
 "- Limits: single source photo, only "+str(len(manifest["source_groups"]["final"]))+" distinct source objects in final and "+str(len(manifest["source_groups"]["tune"]))+" in tuning. This is synthetic object-identity holdout, not fresh-camera validation.",
 "- Manifest: "+str(O/"dataset/manifest.json")+"; SHA256 "+sha(O/"dataset/manifest.json")+".",
 "",
 "## Tuning and model artifacts","",
 "- OFAT from default: priorBalance=1; regularization=.0003; threshold .35/.65/.8; foreground .4/.6/.7; ratio .1/.4/.5; supported morphology0/5; winner-class boundary1; shape cleanup .03/.06; process affinity2/4/8 CPUs.",
 "- Additional declared native factor: instanceHead=1 (default60 head epochs), then centre thresholds .1/.2/.35/.65, quality refinement1, CPU affinity2/4/8. Only 40-frame tuning metrics were used to choose parameters; final files were not inputs to selection.",
 "- Native options morphology1/7 were rejected (actual -2) and were not treated as measured configurations. Supported source contract was then read and used: 0,3,5.",
 "- Process affinity is not claimed to equal OpenVINO inference_num_threads. Native Compile exposes LATENCY only; no precision/thread API. No unsupported FP16/Pascal speed claim. Score values from semantic components and centre heads are not calibrated interchangeable probabilities.",
 "- Selected frozen settings: "+json.dumps(sel)+".",
 "- Original models untouched. New models under "+str(O)+". The tuned copy was reloaded through the native API and its real-frame count matched saved raw output.",
 "- Baseline SHA256: "+sha(O/"baseline/model.segai")+".",
 "- Tuned SHA256: "+sha(O/"tuned/model.segai")+".",
 "- Native DLL SHA256: "+sha(R/"EasyLabel/Lib/native/segai/BeeNativeSegAI.dll")+". git diff BeeNativeSegAI is empty.",
 "- Script hashes: "+str(O/"script_hashes.json")+".",
 "",
 "## Measurement contract / phase bottleneck","",
 "- Same image manifest for baseline/tuned. Five warmups, three serial repeats per final image; real-frame diagnostic uses15 repeats. BGR decode excluded; native inference, confidence extraction and required instance extraction included; IoU matching excluded. One-to-one greedy score-descending convex OBB IoU>=.5.",
 "- Native-only and whole-cycle times are both reported. Do not substitute native-only speedup for the requested whole-cycle target.",
 "- Baseline phase p50 ms: "+json.dumps(base["phase_p50_ms"])+".",
 "- Tuned phase p50 ms: "+json.dumps(tuned["phase_p50_ms"])+".",
 "- Total phase fields overlap subphases. OpenVINO infer is the semantic backbone/head phase; full-resolution confidence/component extraction is visible in whole-cycle minus native.",
 "- PredictInstances currently does not populate InferGetPhaseTimings (queried but zeros are NOT measurements). Instance timing is actual wall-clock native call; no false backbone/head phase attribution.",
 "- Initial semantic-only verification is retained in metrics/segai_*_final.json. Its measured whole-cycle ratio was "+str(json.loads((O/"completion.json").read_text())["ratio"])+". The second paired verification followed a genuinely new native instance-head experiment, not repeated trials to cross2x.",
 "",
 "## Count / Couter blocker (not replaced by a different algorithm)","",
 "The assigned EAI repository is a split-out EasyAI/EasyLabel + BeeNativeSegAI checkout. Verified EasyLabel/Lib/native has only cuda13 and segai; there is no BeeCpp.dll anywhere under EasyLabel, and no Count/Couter implementation under EasyLabel/src. Historical documentation references EasyVision/Pattern/BeeCpp in another codebase, not an executable Couter here. The independent Count worker hit its15-minute runtime limit without producing a wrapper. A real Count result requires the actual Couter runtime/API and its settings; plain connected components would not satisfy this engine requirement. Therefore Count precision, score, and cycle remain N/A, not fabricated.",
 "",
 "## Raw files / reproduce","",
 "- Native models/config/logs: "+str(O)+".",
 "- Per-image raw predictions, matches, timings and summary JSON/JSONL: "+str(O/"metrics")+".",
 "- Final verification bundle: "+str(O/"verified_completion.json")+"; background split "+str(O/"background_results.json")+".",
 "- Scripts: tools/segai_tune/dataset.py, seg_native.py, train_baseline.py, run_experiment.py, instance_experiment.py, finalize_report.py.",
 "- Generate frozen data: python -X utf8 tools/segai_tune/dataset.py",
 "- Native baseline: python -X utf8 tools/segai_tune/train_baseline.py",
 "- OFAT and sealed report: python -X utf8 tools/segai_tune/run_experiment.py; then instance_experiment.py; then finalize_report.py.",
 "- Full chronological attempts and immediately appended results retained in REPORT.history.md. Failed first GPU request and wrapper directory/morphology errors were recovered; old pipeline_error.json is historical, not a claim that verified models failed.",
 "",
 "No commit, push, native edits, original model overwrite or task completion ticks."]
 history=D/"REPORT.history.md"
 if not history.exists():shutil.copy2(D/"REPORT.md",history)
 (D/"REPORT.md").write_text("\n".join(lines)+"\n",encoding="utf-8")
 smoke_out=dict(count=smoke["count"],hash=sha(O/"tuned/model.segai"),native_source_diff=native_diff,
                report=str(D/"REPORT.md"),raw= str(O/"verified_completion.json"))
 (O/"review_verification.json").write_text(json.dumps(smoke_out,indent=2),encoding="utf-8")
 print(json.dumps(smoke_out),flush=True)
 print("FINAL_METRICS",json.dumps(v),flush=True)
if __name__=="__main__":main()
