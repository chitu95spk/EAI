"""Native instance-head OFAT extension declared before reading any final metrics."""
from pathlib import Path
import json,time,shutil,traceback,hashlib
import run_experiment as ex
from seg_native import train_model,SegNative
O=ex.OUT
def main():
 deadline=time.time()+21600
 while not (O/"completion.json").exists():
  if time.time()>deadline:raise TimeoutError("semantic pipeline did not complete")
  print("waiting semantic pipeline (no concurrent timing or training)",flush=True);time.sleep(10)
 # Final metric files deliberately never read to guide this extra predeclared factor.
 manifest=json.loads((O/"dataset/manifest.json").read_text())
 tune=[r for r in manifest["records"] if r["split"]=="tune"]
 ex.note("## Additional native factor: instanceHead=1\nDeclared before viewing final metrics. Selection uses only the separate 40-frame tuning split. Adds native instance head (default60 epochs) at unchanged 640x640; no native changes. Existing final files are not consulted for choices.")
 model=train_model(manifest,O/"instance_head",{"instanceHead":1})
 ref=json.loads((O/"metrics/baseline_tune.json").read_text())["summary"]
 frozen=json.loads((O/"frozen_selection.json").read_text())
 e=SegNative(frozen["model"],**frozen["settings"]);ex.affinity(frozen["affinity"])
 try:best=ex.evaluate(e,tune,O/"metrics/selected_semantic_recheck.json",2)
 finally:e.close();ex.affinity(ex.FULL)
 chosen=dict(frozen);headbest=None;headopts={"instance_mode":True};headmask=ex.FULL
 candidates=[]
 def trial(name,opts,mask=ex.FULL):
  ex.affinity(mask);eng=SegNative(model,**opts)
  try:s=ex.evaluate(eng,tune,O/"metrics"/(name+".json"),2)
  finally:eng.close();ex.affinity(ex.FULL)
  candidates.append(dict(name=name,settings=opts,affinity=mask,summary=s))
  ex.dump(O/"instance_trials.json",candidates)
  return s
 parent=trial("instance_default",headopts);headbest=parent
 for threshold in [.1,.2,.35,.65]:
  opts={"instance_mode":True,"threshold":threshold}
  s=trial("instance_threshold_"+str(threshold).replace(".","p"),opts)
  if ex.no_loss(s,ref) and ex.score(s)>ex.score(headbest):headbest=s;headopts=opts
 opts=dict(headopts);opts["quality"]=1
 s=trial("instance_quality1",opts)
 if ex.no_loss(s,ref) and ex.score(s)>ex.score(headbest):headbest=s;headopts=opts
 bits=[1<<i for i in range(64) if ex.FULL&(1<<i)]
 for n in [2,4,8]:
  if n>=len(bits):continue
  mask=sum(bits[:n]);s=trial("instance_affinity_"+str(n),headopts,mask)
  if ex.no_loss(s,ref) and s["f1"]>=headbest["f1"]-1e-12 and s["p50_ms"]<headbest["p50_ms"]:
   headbest=s;headmask=mask
 if ex.no_loss(headbest,ref) and ex.score(headbest)>ex.score(best):
  chosen.update(model=str(model),sha256=ex.sha(model),settings=headopts,affinity=headmask)
  best=headbest
 chosen["additional_instance_factor_examined"]=True
 ex.dump(O/"frozen_selection_extended.json",chosen)
 ex.note("### Extended selection frozen (tuning only)\n"+json.dumps(chosen))
 final=[r for r in manifest["records"] if r["split"]=="final"]
 real=[r for r in manifest["records"] if r["split"]=="real"]
 results={}
 for label,m,opts,mask in [("baseline_verified",O/"baseline/model.segai",{},ex.FULL),
                           ("tuned_verified",Path(chosen["model"]),chosen["settings"],chosen["affinity"])]:
  ex.affinity(mask);eng=SegNative(m,**opts)
  try:
   results[label]=ex.evaluate(eng,final,O/"metrics"/(label+"_final.json"),3)
   results[label+"_real"]=ex.evaluate(eng,real,O/"metrics"/(label+"_real.json"),15)
  finally:eng.close();ex.affinity(ex.FULL)
 ex.dump(O/"verified_results.json",results)
 shutil.copy2(chosen["model"],O/"tuned/model.segai");ex.dump(O/"tuned/settings.json",chosen)
 b=results["baseline_verified"];t=results["tuned_verified"]
 speed=b["p50_ms"]/t["p50_ms"];native=b["native_p50_ms"]/t["native_p50_ms"]
 scoreup=(t["mean_score"] or 0)>(b["mean_score"] or 0)
 achieved=speed>=2 and ex.no_loss(t,b) and scoreup
 files=list((ex.ROOT/"tools/segai_tune").glob("*.py"))
 hashes={str(p):ex.sha(p) for p in files}
 ex.dump(O/"script_hashes.json",hashes)
 ex.note("## FINAL VERIFIED SUMMARY (not accepted / no task ticks)\n"+
 "|Method|Input|Count exact|Precision IoU.5|Recall IoU.5|Mean score|p50 ms|p95 ms|\n|---|---|---|---|---|---|---|---|\n"+
 "|Count native Couter|N/A|N/A|N/A|N/A|N/A|N/A|N/A|\n"+
 "\n".join("|"+name+"|640x640|"+str(s["count_exact"])+"/"+str(s["images"])+f'|{s["precision"]:.6f}|{s["recall"]:.6f}|{s["mean_score"]:.6f}|{s["p50_ms"]:.3f}|{s["p95_ms"]:.3f}|' for name,s in results.items())+
 "\nSpeedup p50="+str(speed)+" native-only="+str(native)+"; score increased="+str(scoreup)+"; all requested targets="+str(achieved)+
 "\nCount blocker verified: assigned EAI checkout has only Lib/native/{cuda13,segai}; no BeeCpp.dll under EasyLabel and no Count/Couter implementation in EasyLabel/src. Native Couter belongs to the separate EasyVision/Pattern lineage, not this checkout. Do not substitute connected-components and call it Couter. Independent Count worker timed out after15min without a harness."+
 "\nModels and hashes: baseline="+ex.sha(O/"baseline/model.segai")+"; tuned="+ex.sha(O/"tuned/model.segai")+
 "\nRaw data: "+str(O/"metrics")+"; frozen selection: "+str(O/"frozen_selection_extended.json")+"; script hashes: "+str(O/"script_hashes.json")+
 "\nTiming excludes disk decode and IoU; includes native call and mandatory detection extraction; 5 warmups,3 repeats/frame (real15). Native instance head scores and semantic component scores are not calibrated probabilities. Precision/thread limitations remain as documented; no FP16/Pascal claim."+
 "\nProfiling semantic baseline/tuned: "+json.dumps({k:v["phase_p50_ms"] for k,v in results.items()})+
 "\nNative PredictInstances does not populate InferGetPhaseTimings: zeros are not claimed as measurements. Real image is source-derived diagnostic, not independent evidence; holdout100 is synthetic and has only2 source screws, tune40 only1 source screw. No real-world/general production claim."+
 "\nNo original models overwritten, no native changes, no commit/push or completed task ticks.")
 ex.dump(O/"verified_completion.json",dict(speedup=speed,native_speedup=native,all_targets=achieved,count_runtime="unavailable",results=results,model=chosen))
if __name__=="__main__":
 try:main()
 except Exception:
  error=traceback.format_exc();ex.note("## Instance extension actual exception\n"+error);ex.dump(O/"instance_error.json",dict(traceback=error));raise
