from pathlib import Path
import json, sys, time
from seg_native import train_model
OUT=Path(r"C:\Demo\LucGiac.segai-tune")
manifest=OUT/"dataset"/"manifest.json"
deadline=time.time()+7200
while not manifest.exists():
 if time.time()>deadline:raise TimeoutError("dataset manifest not completed within 2 hours")
 print("waiting for frozen manifest",flush=True);time.sleep(10)
m=json.loads(manifest.read_text(encoding="utf-8"))
print("BASELINE_TRAIN_BEGIN",flush=True)
model=train_model(m,OUT/"baseline")
print("BASELINE_TRAIN_SAVED",model,flush=True)
