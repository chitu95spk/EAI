import json,glob,os,csv,itertools
import numpy as np, cv2
A='E:/Code/EasyVision_Unisen - NoService/artifacts/EAI-LABEL/'
def poly(s): return np.array(s['points'],np.float32)
def iou(a,b):
    ar=cv2.contourArea(a);br=cv2.contourArea(b)
    i,_=cv2.intersectConvexConvex(a,b); r=i
    return i/(ar+br-i) if r>0 else 0
def load(d,n):
    p=os.path.join(d,n)
    if not os.path.exists(p): return None
    return [poly(s) for s in json.load(open(p))['shapes'] if len(s['points'])==4]
def match(P,Q):
    # greedy by iou; returns list of (i,j,iou)
    pairs=sorted(((iou(p,q),i,j) for i,p in enumerate(P) for j,q in enumerate(Q)),reverse=True)
    ui=set();uj=set();out=[]
    for v,i,j in pairs:
        if v<=0.3: break
        if i in ui or j in uj: continue
        ui.add(i);uj.add(j);out.append((i,j,v))
    return out
def fuse(p,q):
    best=None
    for rev in (0,1):
        qq=q[::-1] if rev else q
        for k in range(4):
            r=np.roll(qq,k,0); d=np.linalg.norm(p-r,axis=1).sum()
            if best is None or d<best[0]: best=(d,r)
    return (p+best[1])/2
def meaniou(P,G):
    m=match(P,G); 
    return (sum(v for _,_,v in m)/len(G)) if G else 0
names=[os.path.basename(f)[:-5] for f in sorted(glob.glob(A+'s0-baseline/project/*.json'))]
GTN=[os.path.basename(f)[:-5] for f in sorted(glob.glob('E:/EAI_TEST/*.json'))][:18]
R1=A+'s2-eco-par3-full/project/';R2=A+'s2-eco-par3-run2/project/';B=A+'s0-baseline/project/'
def rows(d): return {r['rel'][:-4]:r for r in csv.DictReader(open(A+d+'/per-image.csv'))}
c1=rows('s2-eco-par3-full');c2=rows('s2-eco-par3-run2')
res={}
for n in names:
    G=load('E:/EAI_TEST',n+'.json') if n in GTN else None
    b=load(B,n+'.json')
    p1=load(R1,n+'.json');p2=load(R2,n+'.json')
    if p1 is None or p2 is None or not p1 or not p2: res[n]=None;continue
    r={'n':n,'gt':G is not None,'nb':len(b)}
    ref=G if G else b
    r['i1']=meaniou(p1,ref);r['i2']=meaniou(p2,ref)
    m=match(p1,p2)
    r['dis']=1-np.mean([v for _,_,v in m]) if m else 1
    r['nmis']=abs(len(p1)-len(p2))
    fused=[fuse(p1[i],p2[j]) for i,j,_ in m]
    r['ifuse']=meaniou(fused,ref) if fused else 0
    # recall of fused
    r['cost1']=float(c1[n]['cost_usd']);r['turns1']=int(c1[n]['turns']);r['wall1']=float(c1[n]['wall_seconds']);r['np1']=len(p1)
    r['cost2']=float(c2[n]['cost_usd']);r['turns2']=int(c2[n]['turns'])
    # size variance (within image, short side cv)
    s=[min(np.linalg.norm(q[1]-q[0]),np.linalg.norm(q[3]-q[0])) for q in p1]
    r['szcv']=np.std(s)/np.mean(s)
    res[n]=r
R=[r for r in res.values() if r]
print('valid',len(R),'of',len(res))
G=[r for r in R if r['gt']]
print('GT n',len(G),'run1 %.3f run2 %.3f fused %.3f'%tuple(np.mean([r[k] for r in G]) for k in('i1','i2','ifuse')))
print('pseudo (all, vs baseline) run1 %.3f run2 %.3f fused %.3f'%tuple(np.mean([r[k] for r in R]) for k in('i1','i2','ifuse')))
NG=[r for r in R if r['n'].endswith('_NG')];OK=[r for r in R if not r['n'].endswith('_NG')]
for nm,S in(('NG',NG),('OK',OK)):
    print(nm,len(S),'pseudo run1 %.3f run2 %.3f fused %.3f'%tuple(np.mean([r[k] for r in S]) for k in('i1','i2','ifuse')),'cost1 %.3f turns %.2f np %.1f'%(np.mean([r['cost1'] for r in S]),np.mean([r['turns1'] for r in S]),np.mean([r['np1'] for r in S])))
# pseudo-GT noise: baseline vs GT on 18 (baseline agrees .975)
# correlations with loss (1-i1) on pseudo-gt
from numpy import corrcoef
for k in('cost1','turns1','wall1','np1','szcv','dis','nmis'):
    x=[r[k] for r in R];y=[1-r['i1'] for r in R]
    xg=[r[k] for r in NG];yg=[1-r['i1'] for r in NG]
    xo=[r[k] for r in OK];yo=[1-r['i1'] for r in OK]
    print('%-6s corr loss all %.2f  NG %.2f  OK %.2f   (GT18 %.2f)'%(k,corrcoef(x,y)[0,1],corrcoef(xg,yg)[0,1],corrcoef(xo,yo)[0,1],corrcoef([r[k] for r in G],[1-r['i1'] for r in G])[0,1]))
# oracle / selector sim
NORM=0.97; ncost=0.073; ecost=2.6/84
def sim(sel,label):
    out=[]
    for f in(0.2,0.3,0.4,0.5):
        k=int(round(f*len(R)))
        order=sorted(R,key=sel,reverse=True)[:k]
        ch={r['n'] for r in order}
        mi=np.mean([NORM if r['n'] in ch else r['i1'] for r in R])
        out.append('%d%%: IoU %.3f'%(f*100,mi))
    print('%-14s'%label,' | '.join(out))
sim(lambda r:1-r['i1'],'oracle')
sim(lambda r:r['cost1'],'cost')
sim(lambda r:r['turns1'],'turns')
sim(lambda r:r['np1'],'nshapes')
sim(lambda r:r['szcv'],'sizecv')
sim(lambda r:r['dis'],'run-disagree*')
import random
random.seed(1)
vals=[]
for _ in range(200):
    rr=random.sample(R,len(R)); 
    vals.append(rr)
sim(lambda r:random.random(),'random')
json.dump(R,open('res.json','w'),default=float)
