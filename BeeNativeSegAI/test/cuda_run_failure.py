"""Behaviour test for the dedicated SEGAI_TEST_INSTANCE_CUDA_RUN_FAILURE build.
Uses the real ORT CUDA session, injects only at Run, verifies same-call CPU recovery,
visible reason and sticky fallback, and compares every detection with CPU reference.
python -S -X utf8 cuda_run_failure.py FAULT_NATIVE_DIR MODEL IMAGE
Never run against or deploy a production DLL with the test macro enabled.
"""
import os, sys, json
from pathlib import Path
if sys.flags.no_site:sys.path.append(str(Path(sys.executable).parent/'Lib/site-packages'))
os.environ['SEGAI_NATIVE_DIR']=sys.argv[1]
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'tools/segai_tune'))
import cv2
from seg_native import SegNative
im=cv2.imread(sys.argv[3]); assert im is not None
os.environ['SEGAI_BACKEND']='cpu'
cpu=SegNative(sys.argv[2],instance_mode=True,threshold=.25,quality=0)
try:reference=cpu.run(im)
finally:cpu.close()
os.environ['SEGAI_BACKEND']='cuda'
eng=SegNative(sys.argv[2],instance_mode=True,threshold=.25,quality=0)
try:
    first=eng.run(im); second=eng.run(im)
    assert first['backend']==second['backend']==0,(first,second)
    assert 'SEGAI_TEST_INSTANCE_CUDA_RUN_FAILURE' in first['backend_note'],first['backend_note']
    assert 'CUDA Run failed' in first['backend_note']
    assert first['backend_note']==second['backend_note']
    assert first['detections']==second['detections']==reference['detections'],'fallback altered detections'
    print(json.dumps(dict(ok=True,count=first['count'],backend=[first['backend'],second['backend']],note=first['backend_note'],same_call_cpu_parity=True,sticky_cpu_parity=True)),flush=True)
finally:eng.close()
