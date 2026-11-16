"""ctypes binding to the installed BeeNativeSegAI C API, no alternative trainer."""
from pathlib import Path
import os, ctypes as C, time, json, hashlib, re
import cv2
import numpy as np
ROOT=Path(__file__).resolve().parents[2]
NATIVE=ROOT/"EasyLabel/Lib/native/segai"
# SEGAI_NATIVE_DIR lets an experiment load another build (e.g. tmp/EAI-SEGAI-CUDA/integrated-instance) without touching the installed one.
DLLDIR=Path(os.environ.get("SEGAI_NATIVE_DIR") or NATIVE)
dll_directory=os.add_dll_directory(str(DLLDIR))
dll=C.CDLL(str(DLLDIR/"BeeNativeSegAI.dll"))
P=C.c_void_p; I=C.c_int32; F=C.c_float; W=C.c_wchar_p
CALLBACK=C.CFUNCTYPE(None,I,P)
def bind(name,args,ret=I):
 f=getattr(dll,"SEGAI_"+name);f.argtypes=args;f.restype=ret;return f
create=bind("TrainerCreate",[],P)
destroy=bind("TrainerDestroy",[P],None)
configure=bind("TrainerConfigureV2",[P,W])
addsample=bind("TrainerAddSample",[P,P,I,I,I,P,I])
train=bind("TrainerTrain",[P,I,I,I,CALLBACK,P,P])
save=bind("TrainerSave",[P,W,F,C.c_uint32])
terr=bind("TrainerGetLastError",[P,P,I])
icreate=bind("InferCreate",[],P)
idestroy=bind("InferDestroy",[P],None)
iload=bind("InferLoad",[P,W])
ierr=bind("InferGetLastError",[P,P,I])
gpu=bind("InferSetGpu",[P,I])
speed=bind("InferSetV2SpeedScale",[P,F])
opts=bind("InferV2Options",[P,F,F,I])
boundary=bind("InferSetV2BoundaryMode",[P,I])
cleanup=bind("InferSetV2ShapeCleanup",[P,F])
predict=bind("InferPredict",[P,P,I,I,I,I,I,I,I,F,C.POINTER(P),C.POINTER(I),C.POINTER(I),C.POINTER(F)])
confidence=bind("InferGetConfidence",[P,C.POINTER(P),C.POINTER(I),C.POINTER(I)])
free=bind("FreeBuffer",[P],None)
phase=bind("InferGetPhaseTimings",[P,C.POINTER(C.c_double),I])
backend=bind("InferGetLastBackend",[P])
def _optional(name,args,ret=I):
 try:return bind(name,args,ret)
 except AttributeError:return None  # older DLLs predate the CUDA exports
set_gpu_backend=_optional("InferSetGpuBackend",[P,I])
cuda_available=_optional("InferGetCudaAvailable",[])
backend_note=_optional("InferGetBackendNote",[P,C.c_char_p,I])
def note(h):
 if not backend_note:return ""
 buf=C.create_string_buffer(2048);backend_note(h,buf,len(buf));return buf.value.decode("utf-8",errors="replace")
class Instance(C.Structure):
 _fields_=[("structSize",I),("kind",I),("cx",F),("cy",F),("w",F),("h",F),("theta",F),("classIndex",I)]
class Detection(C.Structure):
 _fields_=[("structSize",I),("kind",I),("cx",F),("cy",F),("w",F),("h",F),("theta",F),("score",F),("classIndex",I),("refined",I)]
addinstances=bind("TrainerAddInstances",[P,C.POINTER(Instance),I,I])
ipredict=bind("InferPredictInstances",[P,P,I,I,I,I,I,I,I,F,I,C.POINTER(Detection),I,C.POINTER(I)])
PHASES=["device","prepare","tensor","infer","upsample","compile","ccscore","nearest","v2_total","inferer_tail","inferer_total","export_copy","export_total"]
def check(rc,h,trainer=False):
 if rc!=0:
  buf=C.create_string_buffer(8192);(terr if trainer else ierr)(h,buf,len(buf))
  raise RuntimeError(f"native rc={rc}: "+buf.value.decode("utf-8",errors="replace"))
def sha(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def train_model(manifest,folder,overrides=None):
 folder=Path(folder);folder.mkdir(parents=True,exist_ok=True)
 model=folder/"model.segai"
 if model.exists():return model
 records=[r for r in manifest["records"] if r["split"] in ("train","val")]
 # Explicit defaults from SegV2Core.cpp Impl and Configure. No originalInput or speed scale.
 cfg=dict(backbone=str(NATIVE/"Models/DefectV2/mobilenetv2-12.onnx").replace("\\","/"),
          logs=str(folder).replace("\\","/"),side=640,epochs=200,learningRate=.015,
          regularization=.001,rotation=15.,scaleVariation=.15,brightness=.15,
          augment=1,gpu=0,originalInput=0,earlyStop=1,earlyStopMinGain=.02,
          validation=[i for i,r in enumerate(records) if r["split"]=="val"])
 cfg.update(overrides or {})
 cp=folder/"config.json";cp.write_text(json.dumps(cfg,indent=2),encoding="utf-8")
 h=create();assert h
 try:
  check(configure(h,str(cp)),h,True)
  for i,r in enumerate(records):
   im=cv2.imread(r["image"]);mask=cv2.imread(r["mask"],cv2.IMREAD_GRAYSCALE)
   assert im is not None and mask is not None
   check(addsample(h,im.ctypes.data,im.shape[1],im.shape[0],im.strides[0],mask.ctypes.data,mask.strides[0]),h,True)
   if cfg.get("instanceHead",0):
    labels=[]
    for pts in r["boxes"]:
     v=np.asarray(pts,np.float32)
     center=v.mean(axis=0);edge=v[1]-v[0]
     labels.append(Instance(32,0,float(center[0]),float(center[1]),float(np.linalg.norm(edge)),float(np.linalg.norm(v[3]-v[0])),float(np.arctan2(edge[1],edge[0])),0))
    arr=(Instance*len(labels))(*labels)
    check(addinstances(h,arr,len(labels),32),h,True)
   print("add",i+1,len(records),flush=True)
  started=time.time()
  @CALLBACK
  def progress(p,_):print("train_progress",p,"seconds",round(time.time()-started,1),flush=True)
  flag=I(0)
  check(train(h,100,20,2,progress,None,C.byref(flag)),h,True)
  check(save(h,str(model),.5,20),h,True)
  # Native save embeds its real model Side in the YAML payload; validate, don't infer from image dimensions.
  m=re.search(rb"\bside:\s*(\d+)",model.read_bytes())
  assert m and int(m.group(1))==640,"Saved native payload missing fixed Side 640"
  metadata=dict(model=str(model),sha256=sha(model),input_size=[640,640],
                train_seconds=time.time()-started,config=cfg,native_sha256=sha(NATIVE/"BeeNativeSegAI.dll"))
  (folder/"model.json").write_text(json.dumps(metadata,indent=2),encoding="utf-8")
  with (ROOT/"docs/segai-tune/REPORT.md").open("a",encoding="utf-8") as f:
   f.write("\n### Native model saved: "+folder.name+"\n"+json.dumps(metadata,indent=1)+"\n")
  return model
 finally:destroy(h)

class SegNative:
 def __init__(self,model,threshold=.5,foreground=.5,ratio=.25,morphology=3,boundary_mode=0,shape_cleanup=0.,min_area=20,instance_mode=False,quality=0):
  self.h=icreate();assert self.h
  check(iload(self.h,str(model)),self.h)
  # SEGAI_BACKEND=cuda selects ORT CUDA (useGpu=1, gpuBackend=1); anything else keeps the historical OpenVINO CPU behaviour.
  self.want_cuda=os.environ.get("SEGAI_BACKEND","cpu").lower()=="cuda"
  if self.want_cuda:
   assert set_gpu_backend,"this BeeNativeSegAI.dll has no CUDA exports"
   check(set_gpu_backend(self.h,1),self.h);check(gpu(self.h,1),self.h)
  else:check(gpu(self.h,0),self.h)
  check(speed(self.h,1.),self.h) # invariant: never reduce the trained input
  check(opts(self.h,foreground,ratio,morphology),self.h)
  check(boundary(self.h,boundary_mode),self.h)
  check(cleanup(self.h,shape_cleanup),self.h)
  self.threshold=threshold;self.min_area=min_area;self.instance_mode=instance_mode;self.quality=quality
 def close(self):
  if self.h:idestroy(self.h);self.h=None
 def run(self,im):
  if self.instance_mode:return self.run_instances(im)
  im=np.ascontiguousarray(im);ptr=P();w=I();h=I();score=F()
  start=time.perf_counter()
  check(predict(self.h,im.ctypes.data,im.shape[1],im.shape[0],im.strides[0],0,0,im.shape[1],im.shape[0],self.threshold,C.byref(ptr),C.byref(w),C.byref(h),C.byref(score)),self.h)
  try:mask=np.ctypeslib.as_array(C.cast(ptr,C.POINTER(C.c_uint8)),shape=(h.value,w.value)).copy()
  finally:
   if ptr:free(ptr)
  native_ms=(time.perf_counter()-start)*1000
  cp=P();cw=I();ch=I()
  check(confidence(self.h,C.byref(cp),C.byref(cw),C.byref(ch)),self.h)
  try:conf=np.ctypeslib.as_array(C.cast(cp,C.POINTER(C.c_uint8)),shape=(ch.value,cw.value)).copy() if cp else np.zeros_like(mask)
  finally:
   if cp:free(cp)
  binary=(mask>0).astype(np.uint8)
  # Native returns accepted score maps; derive instance boxes using connected components (semantic baseline has no instance head).
  n,labels,stats,centres=cv2.connectedComponentsWithStats(binary,8)
  boxes=[];scores=[]
  for c in range(1,n):
   if stats[c,cv2.CC_STAT_AREA]<self.min_area:continue
   x,y,rw,rh=stats[c,:4]
   ys,xs=np.where(labels[y:y+rh,x:x+rw]==c)
   ys=ys+y;xs=xs+x
   if len(xs)<3:continue
   pts=np.column_stack([xs,ys]).astype(np.float32)
   boxes.append(cv2.boxPoints(cv2.minAreaRect(pts)).tolist())
   scores.append(float(conf[ys,xs].max()/255.))
  timings=(C.c_double*13)();written=phase(self.h,timings,13)
  return dict(boxes=boxes,scores=scores,count=len(boxes),native_ms=native_ms,
              cycle_ms=(time.perf_counter()-start)*1000,phases=dict(zip(PHASES,list(timings)[:written])),
              backend=backend(self.h),frame_score=score.value,mask_values=np.unique(mask).tolist())


 def run_instances(self,im):
  im=np.ascontiguousarray(im);out=(Detection*2048)();count=I()
  begin=time.perf_counter()
  check(ipredict(self.h,im.ctypes.data,im.shape[1],im.shape[0],im.strides[0],0,0,im.shape[1],im.shape[0],self.threshold,self.quality,out,2048,C.byref(count)),self.h)
  native_ms=(time.perf_counter()-begin)*1000
  boxes=[];scores=[]
  for d in list(out)[:count.value]:
   if d.classIndex!=0:continue
   boxes.append(cv2.boxPoints(((d.cx,d.cy),(d.w,d.h),float(np.degrees(d.theta)))).tolist());scores.append(float(d.score))
  vals=(C.c_double*13)();phase(self.h,vals,13)
  # Builds with the instance CUDA work fill the V2 slots for PredictInstances: prepare/tensor/infer/compile/v2_total as named, and
  # ccscore = decode phase (sigmoid + DecodeInstances + refine + un-letterbox). Older DLLs leave them 0 (all-zero = not populated).
  populated=any(v>0 for v in list(vals)[:9])
  phases=dict(zip(PHASES,list(vals))) if populated else {}
  if populated:phases["decode"]=phases.pop("ccscore")
  return dict(boxes=boxes,scores=scores,count=len(boxes),native_ms=native_ms,cycle_ms=(time.perf_counter()-begin)*1000,
   phases=phases,backend=backend(self.h),backend_note=note(self.h),detections=[(d.cx,d.cy,d.w,d.h,d.theta,d.score,d.classIndex) for d in list(out)[:count.value]])
