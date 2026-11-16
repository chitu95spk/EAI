"""Sequential OFAT native SegAI experiment; final split is opened only after freeze."""
from pathlib import Path
import json,time,hashlib,ctypes as C,os,platform,shutil
import numpy as np,cv2
from seg_native import SegNative,train_model
ROOT=Path(__file__).resolve().parents[2];OUT=Path(r"C:\Demo\LucGiac.segai-tune")
REPORT=ROOT/"docs/segai-tune/REPORT.md"
def note(s):
 with REPORT.open("a",encoding="utf-8") as f:f.write("\n"+s+"\n")
 print(s,flush=True)
def dump(p,x):
 p=Path(p);p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(x,indent=1),encoding="utf-8")
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def iou(a,b):
 a=cv2.convexHull(np.asarray(a,np.float32));b=cv2.convexHull(np.asarray(b,np.float32))
 z=float(cv2.intersectConvexConvex(a,b)[0])
 return z/max(cv2.contourArea(a)+cv2.contourArea(b)-z,1e-9)
def evaluate(engine,records,path,repeats=1):
 path=Path(path);path.parent.mkdir(parents=True,exist_ok=True)
 if path.exists():return json.loads(path.read_text())["summary"]
 images=[cv2.imread(r["image"]) for r in records]
 assert all(x is not None for x in images)
 for k in range(5):engine.run(images[k%len(images)])
 raw=[]
 for j,(r,im) in enumerate(zip(records,images)):
  cycles=[];natives=[]
  for k in range(repeats):
   begin=time.perf_counter();p=engine.run(im);cycles.append((time.perf_counter()-begin)*1000)
   natives.append(p.get("native_ms"))
  used=set();pairs=[]
  order=sorted(range(p["count"]),key=lambda x:p["scores"][x] if p["scores"][x] is not None else 0,reverse=True)
  for ix in order:
   choices=[(iou(p["boxes"][ix],g),gi) for gi,g in enumerate(r["boxes"]) if gi not in used]
   if choices:
    v,gi=max(choices)
    if v>=.5:used.add(gi);pairs.append([ix,gi,v])
  row=dict(image=r["image"],sha256=r["sha256"],bg=r["bg"],gt=r["count"],prediction=p,
           tp=len(pairs),fp=p["count"]-len(pairs),fn=r["count"]-len(pairs),pairs=pairs,cycles_ms=cycles,native_ms=natives)
  raw.append(row)
  with path.with_suffix(".jsonl").open("a",encoding="utf-8") as f:f.write(json.dumps(row)+"\n")
  print(path.stem,j+1,len(records),"gt",r["count"],"pred",p["count"],"tp",len(pairs),flush=True)
 tp=sum(r["tp"] for r in raw);fp=sum(r["fp"] for r in raw);fn=sum(r["fn"] for r in raw)
 ps=[s for r in raw for s in r["prediction"]["scores"] if s is not None]
 ts=[r["prediction"]["scores"][pair[0]] for r in raw for pair in r["pairs"] if r["prediction"]["scores"][pair[0]] is not None]
 times=[x for r in raw for x in r["cycles_ms"]];nt=[x for r in raw for x in r["native_ms"] if x is not None]
 precision=tp/max(1,tp+fp);recall=tp/max(1,tp+fn)
 phases={key:float(np.median([r["prediction"]["phases"][key] for r in raw if key in r["prediction"].get("phases",{})]))
         for key in raw[0]["prediction"].get("phases",{})}
 summary=dict(images=len(raw),objects=sum(r["gt"] for r in raw),tp=tp,fp=fp,fn=fn,
   precision=precision,recall=recall,f1=2*precision*recall/max(precision+recall,1e-9),
   count_exact=sum(r["prediction"]["count"]==r["gt"] for r in raw),
   all_objects_matched=sum(r["fp"]==0 and r["fn"]==0 for r in raw),
   mean_score=float(np.mean(ps)) if ps else None,mean_tp_score=float(np.mean(ts)) if ts else None,
   p50_ms=float(np.median(times)),p95_ms=float(np.percentile(times,95)),
   native_p50_ms=float(np.median(nt)) if nt else None,native_p95_ms=float(np.percentile(nt,95)) if nt else None,
   phase_p50_ms=phases)
 dump(path,dict(summary=summary,raw=raw,warmup=5,repeats=repeats,
     timing="resident BGR; native+required extraction; excludes disk decode and IoU",
     manifest_sha256=sha(OUT/"dataset/manifest.json"),iou_threshold=.5))
 note("### Measurement "+path.stem+"\nRaw: "+str(path)+"\n"+json.dumps(summary))
 return summary
kernel=C.WinDLL("kernel32",use_last_error=True)
kernel.GetCurrentProcess.restype=C.c_void_p
proc=kernel.GetCurrentProcess()
kernel.GetProcessAffinityMask.argtypes=[C.c_void_p,C.POINTER(C.c_size_t),C.POINTER(C.c_size_t)]
kernel.SetProcessAffinityMask.argtypes=[C.c_void_p,C.c_size_t]
pm=C.c_size_t();sm=C.c_size_t()
assert kernel.GetProcessAffinityMask(proc,C.byref(pm),C.byref(sm))
FULL=pm.value
def affinity(mask):
 assert kernel.SetProcessAffinityMask(proc,mask),C.get_last_error()
def score(s):return (s["count_exact"],s["f1"],s["mean_tp_score"] or 0,-s["p50_ms"])
def no_loss(s,ref):return s["precision"]>=ref["precision"]-1e-12 and s["recall"]>=ref["recall"]-1e-12 and s["count_exact"]>=ref["count_exact"]
def main():
 deadline=time.time()+21600
 while not (OUT/"baseline/model.json").exists():
  if time.time()>deadline:raise TimeoutError("baseline model metadata never completed")
  print("waiting baseline save",flush=True);time.sleep(15)
 manifest=json.loads((OUT/"dataset/manifest.json").read_text())
 tune=[r for r in manifest["records"] if r["split"]=="tune"]
 baseline=OUT/"baseline/model.segai";selected=baseline;settings={}
 trials=[]
 note("## Baseline ready / OFAT begins\nNative CPU; fixed 640x640, speed scale1.0. Tune40 and final100 have distinct source identities, seeds and hashes. Final is not consulted for choices.")
 def trial(name,model,opts,mask=FULL):
  affinity(mask)
  e=SegNative(model,**opts)
  try:s=evaluate(e,tune,OUT/"metrics"/(name+".json"))
  finally:e.close();affinity(FULL)
  row=dict(name=name,model=str(model),sha256=sha(model),settings=opts,process_affinity_mask=mask,summary=s)
  trials.append(row);dump(OUT/"trials.json",trials);return s
 ref=trial("baseline_tune",baseline,{})
 best=ref;bestmask=FULL
 # Train only ONE changed factor per candidate, from baseline defaults.
 for name,overrides in [("prior_balance",{"priorBalance":1}),("regularization_0003",{"regularization":.0003})]:
  m=train_model(manifest,OUT/name,overrides)
  s=trial(name+"_tune",m,{})
  if no_loss(s,ref) and score(s)>score(best):selected=m;best=s;settings={};bestmask=FULL
 # Each inference trial changes one factor from the currently selected configuration.
 for key,values in [("threshold",[.35,.65,.8]),("foreground",[.4,.6,.7]),
                    ("ratio",[.1,.4,.5]),("morphology",[0,5]),("boundary_mode",[1]),
                    ("shape_cleanup",[.03,.06])]:
  parent=dict(settings);roundbest=best;chosen=dict(settings)
  for v in values:
   opts=dict(parent);opts[key]=v
   s=trial("ofat_"+key+"_"+str(v).replace(".","p"),selected,opts)
   if no_loss(s,ref) and score(s)>score(roundbest):roundbest=s;chosen=opts
  settings=chosen;best=roundbest
 # Process affinity is measured honestly, NOT claimed as exact OpenVINO thread property.
 bits=[1<<i for i in range(64) if FULL&(1<<i)]
 for n in [2,4,8]:
  if n>=len(bits):continue
  mask=sum(bits[:n]);s=trial("cpu_affinity_"+str(n),selected,settings,mask)
  if no_loss(s,ref) and s["f1"]>=best["f1"]-1e-12 and s["p50_ms"]<best["p50_ms"]:
   best=s;bestmask=mask
 note("### Precision/thread limits\nNative Compile exposes LATENCY only, no thread/precision API. CPU affinity is an OS scheduling experiment, not proof of an INFERENCE_NUM_THREADS setting. No NVIDIA FP16 path exists in this V2 backend; no unsupported precision claim or input reduction is used.")
 frozen=dict(model=str(selected),sha256=sha(selected),settings=settings,affinity=bestmask,
             baseline_sha256=sha(baseline),input=[640,640],speed_scale=1.,tuning_only=True)
 dump(OUT/"frozen_selection.json",frozen)
 note("### Selection frozen before final evaluation\n"+json.dumps(frozen))
 tuned=OUT/"tuned";tuned.mkdir(exist_ok=True)
 shutil.copy2(selected,tuned/"model.segai");dump(tuned/"settings.json",frozen)
 final=[r for r in manifest["records"] if r["split"]=="final"]
 real=[r for r in manifest["records"] if r["split"]=="real"]
 results={}
 for label,model,opts,mask in [("segai_baseline",baseline,{},FULL),("segai_tuned",selected,settings,bestmask)]:
  affinity(mask);e=SegNative(model,**opts)
  try:
   results[label]=evaluate(e,final,OUT/"metrics"/(label+"_final.json"),3)
   results[label+"_real"]=evaluate(e,real,OUT/"metrics"/(label+"_real.json"),15)
  finally:e.close();affinity(FULL)
 dump(OUT/"segai_results.json",results)
 b=results["segai_baseline"];t=results["segai_tuned"]
 ratio=b["p50_ms"]/t["p50_ms"];native_ratio=b["native_p50_ms"]/t["native_p50_ms"]
 note("## SegAI final (selection locked)\n|Method|Exact count /100|Precision|Recall|Mean score|p50 ms|p95 ms|\n|---|---|---|---|---|---|---|\n"+
 "\n".join("|"+name+"|"+str(s["count_exact"])+"/"+str(s["images"])+"|"+f'{s["precision"]:.6f}|{s["recall"]:.6f}|{s["mean_score"]:.6f}|{s["p50_ms"]:.3f}|{s["p95_ms"]:.3f}|' for name,s in results.items())+
 "\nSpeed ratio baseline/tuned="+str(ratio)+"; native-only="+str(native_ratio)+
 "; 2x achieved="+str(ratio>=2 and no_loss(t,b))+
 "\nPhase bottleneck: "+str(sorted(t["phase_p50_ms"].items(),key=lambda z:z[1],reverse=True))+
 "\nReal photo is source-derived diagnostic, NOT independent test evidence. Count comparison is in separate Count lane until common-manifest measurement is joined. No commit or native changes.")
 dump(OUT/"completion.json",dict(status="segai measured; Count join pending",ratio=ratio,native_ratio=native_ratio,results=results))
if __name__=="__main__":
 try:main()
 except Exception:
  import traceback
  error=traceback.format_exc()
  note("## Pipeline exception (actual evidence)\n\n"+error)
  dump(OUT/"pipeline_error.json",dict(traceback=error))
  raise
