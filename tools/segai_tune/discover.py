from pathlib import Path
import re, os, ctypes as C, sys
root=Path(r"D:\CODE\EAI")
p=root/"BeeNativeSegAI"/"SegV2Core.cpp"
a=p.read_text(encoding="utf-8-sig").splitlines()
keys=["inference_num_threads","INFERENCE_NUM_THREADS","inference_precision","INFERENCE_PRECISION","EASYAI","void SegV2Model::Save","void SegV2Model::Load","priorBalanced","write(fs","p.Side"]
hits=[(i+1,s) for i,s in enumerate(a) if any(k in s for k in keys)]
print("\n".join(str(i)+": "+s[:200] for i,s in hits)[-16000:])
print("MANAGED")
pat=re.compile(r"instanceHead|ConfigureV2|InputSize|OriginalInput|backbone|Side =|Side=")
found=[]
for part in ["EasyLabel/Services","EasyLabel/ViewModels","EasyLabel/Models"]:
 p=root/part
 if p.exists():
  for f in p.rglob("*.cs"):
   if any(w in f.name.lower() for w in ["seg","train","native"]):
    for i,s in enumerate(f.read_text(encoding="utf-8-sig",errors="replace").splitlines()):
     if pat.search(s): found.append(str(f.relative_to(root))+":"+str(i+1)+": "+s.strip()[:190])
print("\n".join(found)[:14000])
native=root/"EasyLabel/Lib/native/segai"
handle=os.add_dll_directory(str(native))
dll=C.CDLL(str(native/"BeeNativeSegAI.dll"))
for name in ["SEGAI_GetVersion","SEGAI_GetBuildInfo","SEGAI_TrainerConfigureV2","SEGAI_TrainerAddInstances","SEGAI_InferPredictInstances","SEGAI_InferPredictDiagnosticV1"]:
 print(name,hasattr(dll,name))
dll.SEGAI_GetBuildInfo.restype=C.c_char_p
print("BUILD",dll.SEGAI_GetBuildInfo())
print("SOURCE models",list(Path(r"C:\Demo\LucGiac\.easyai\models").glob("*")))
