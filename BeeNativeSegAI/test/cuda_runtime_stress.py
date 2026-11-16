"""Independent-handle CUDA probe/note race stress; no shared-handle Predict calls.

python -S -X utf8 cuda_runtime_stress.py NATIVE_DIR [threads=12] [iterations=1000]
Set PYTHONPATH to the Python site-packages directory when using -S.
Run separately against a complete runtime and a fixture missing a required CUDA DLL.
This exercises races; it is not a ThreadSanitizer proof.
"""
import os, sys, ctypes as C, concurrent.futures as F, threading, json, time
from pathlib import Path
if sys.flags.no_site:sys.path.append(str(Path(sys.executable).parent/'Lib/site-packages'))
os.environ['SEGAI_NATIVE_DIR']=sys.argv[1]
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'tools/segai_tune'))
import seg_native as S
workers=int(sys.argv[2]) if len(sys.argv)>2 else 12
iterations=int(sys.argv[3]) if len(sys.argv)>3 else 1000
probe=S.bind('InferGetCudaAvailable',[])
barrier=threading.Barrier(workers)
def worker(index):
    h=S.icreate(); assert h
    try:
        barrier.wait(timeout=60)
        available=set(); notes=set()
        for j in range(iterations):
            rc=probe(); assert rc in (0,1); available.add(rc)
            buf=C.create_string_buffer(2048)
            n=S.backend_note(None,buf,len(buf)); assert 0<=n<len(buf)
            note=buf.value.decode('utf-8',errors='strict'); assert n==len(buf.value)
            assert not rc or not note,(rc,note)
            assert rc or note,'unavailable probe did not report a reason'
            notes.add(note)
            S.check(S.set_gpu_backend(h,index%2),h)
            S.check(S.gpu(h,index%2),h)
            assert S.note(h)=='','unloaded per-handle note leaked process-global data'
        return dict(thread=index,probes=iterations,available=sorted(available),notes=sorted(notes))
    finally:S.idestroy(h)
t=time.perf_counter()
with F.ThreadPoolExecutor(max_workers=workers) as pool:results=list(pool.map(worker,range(workers)))
print(json.dumps(dict(ok=True,threads=workers,total_probes=workers*iterations,seconds=time.perf_counter()-t,results=results)),flush=True)
