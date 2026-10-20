import sys,os,json,time,argparse,ctypes
sys.path.insert(0,'tmp/EAI-LABEL/refine-C')
import numpy as np,cv2,onnxruntime as ort
ap=argparse.ArgumentParser()
ap.add_argument('--intra',type=int,default=2);ap.add_argument('--inter',type=int,default=1)
ap.add_argument('--level',default='DISABLE_ALL');ap.add_argument('--aff',default='');ap.add_argument('--n',type=int,default=6)
ap.add_argument('--skip',default='');ap.add_argument('--prio',default='');ap.add_argument('--tag',default='x');ap.add_argument('--par',type=int,default=0)
ap.add_argument('--seq',default='');ap.add_argument('--noarena',type=int,default=0);ap.add_argument('--first',type=int,default=0)
a=ap.parse_args()
k=ctypes.windll.kernel32;h=k.GetCurrentProcess()
if a.aff: k.SetProcessAffinityMask(h,int(a.aff,0))
if a.prio=='below': k.SetPriorityClass(h,0x4000)
import sam as S  # builds sessions at import; rebuild below
M=S.M
so=ort.SessionOptions();so.intra_op_num_threads=a.intra;so.inter_op_num_threads=a.inter;so.log_severity_level=3
so.graph_optimization_level=getattr(ort.GraphOptimizationLevel,'ORT_'+a.level)
if a.seq: so.execution_mode=ort.ExecutionMode.ORT_SEQUENTIAL
so.enable_mem_pattern=False
if a.noarena: so.enable_cpu_mem_arena=False
for s in a.skip.split(','):
    if s: so.add_session_config_entry('optimization.disable_specified_optimizers',a.skip);break
t=time.time();S.enc=ort.InferenceSession(M+'encoder.onnx',so,providers=['CPUExecutionProvider']);tl=time.time()-t
so2=ort.SessionOptions();so2.intra_op_num_threads=1;so2.log_severity_level=3;so2.graph_optimization_level=ort.GraphOptimizationLevel.ORT_DISABLE_ALL
S.dec=ort.InferenceSession(M+'decoder.onnx',so2,providers=['CPUExecutionProvider'])
import glob
gts=sorted(glob.glob('E:/EAI_TEST/*.json'))[a.first:a.first+a.n]
run='artifacts/EAI-LABEL/s2-eco-par3-run2/project'
te=[];td=[];tdec=[];ious=[];ck=[];fin=True
for g in gts:
    n=os.path.basename(g)[:-5]
    G=[np.array(s['points']) for s in json.load(open(g))['shapes']]
    P=[np.array(s['points']) for s in json.load(open(f'{run}/{n}.json'))['shapes'] if len(s['points'])==4]
    t=time.time();gray=cv2.imread(f'E:/EAI_TEST/{n}.png',0);H,W=gray.shape;tdec.append(time.time()-t)
    t=time.time();emb=S.encode(gray);te.append(time.time()-t)
    f=all(np.isfinite(e).all() for e in emb);fin&=f
    ck.append(float(sum(np.float64(e).sum() for e in emb)))
    for gp in G:
        j=int(np.argmin([np.linalg.norm(p.mean(0)-gp.mean(0)) for p in P]));p=P[j]
        t=time.time();m,s=S.decode(emb,W,H,S.aabb(p),None);td.append(time.time()-t)
        fb=S.fit(m);ious.append(S.poly_iou(fb,gp,gray.shape) if fb is not None else S.poly_iou(p,gp,gray.shape))
r=dict(tag=a.tag,intra=a.intra,inter=a.inter,level=a.level,aff=a.aff,prio=a.prio,skip=a.skip,load_s=round(tl,2),
 enc_mean=round(float(np.mean(te)),2),enc_first=round(te[0],2),enc_rest=round(float(np.mean(te[1:])),2) if len(te)>1 else None,
 dec_mean=round(float(np.mean(td)),3),imgdec_mean=round(float(np.mean(tdec)),3),iou=round(float(np.mean(ious)),4),finite=fin,ck=ck)
print(json.dumps(r),flush=True)
open('artifacts/EAI-LABEL/s4-speed/results.jsonl','a').write(json.dumps(r)+'\n')
