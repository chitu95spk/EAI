import json,glob,os,sys
import numpy as np,cv2
run=sys.argv[1]; EX=float(sys.argv[2]) if len(sys.argv)>2 else 0.25
def poly_iou(a,b,shape):
    m1=np.zeros(shape,np.uint8);m2=np.zeros(shape,np.uint8)
    cv2.fillPoly(m1,[np.round(a).astype(np.int32)],1);cv2.fillPoly(m2,[np.round(b).astype(np.int32)],1)
    i=(m1&m2).sum();u=(m1|m2).sum();return i/u if u else 0
def refine(gray,p):
    c=p.mean(0); x0,y0=np.maximum(0,p.min(0)-EX*(p.max(0)-p.min(0))).astype(int); x1,y1=np.minimum([gray.shape[1],gray.shape[0]],p.max(0)+EX*(p.max(0)-p.min(0))).astype(int)
    w=gray[y0:y1,x0:x1]
    _,th=cv2.threshold(cv2.GaussianBlur(w,(5,5),0),0,255,cv2.THRESH_BINARY_INV+cv2.THRESH_OTSU)
    th=cv2.morphologyEx(th,cv2.MORPH_CLOSE,np.ones((15,15),np.uint8))
    # keep only pixels inside the proposal grown 20% so touching neighbours/border do not leak in
    pm=np.zeros(th.shape,np.uint8); q=(p-[x0,y0]); cc=q.mean(0); q=cc+(q-cc)*1.15
    cv2.fillPoly(pm,[np.round(q).astype(np.int32)],255); th=th&pm
    n,lab,st,cen=cv2.connectedComponentsWithStats(th)
    cx,cy=(c-[x0,y0]).astype(int)
    k=lab[min(max(cy,0),lab.shape[0]-1),min(max(cx,0),lab.shape[1]-1)]
    if k==0:
        if n<2: return None
        k=1+np.argmax(st[1:,cv2.CC_STAT_AREA])
    ys,xs=np.nonzero(lab==k)
    if len(xs)<500: return None
    pts=np.stack([xs,ys],1).astype(np.float32)
    box=cv2.boxPoints(cv2.minAreaRect(pts))+[x0,y0]
    return box
import itertools
SC=[0.9,0.95,1.0,1.05,1.1]
res=[]
for g in sorted(glob.glob('E:/EAI_TEST/*.json'))[:18]:
    n=os.path.basename(g)[:-5]
    G=[np.array(s['points']) for s in json.load(open(g))['shapes']]
    P=[np.array(s['points']) for s in json.load(open(f'{run}/{n}.json'))['shapes'] if len(s['points'])==4]
    gray=cv2.imread(f'E:/EAI_TEST/{n}.png',0); sh=gray.shape
    for gp in G:
        j=int(np.argmin([np.linalg.norm(p.mean(0)-gp.mean(0)) for p in P])); p=P[j]
        r=refine(gray,p)
        i0=poly_iou(p,gp,sh); i1=poly_iou(r,gp,sh) if r is not None else i0
        gate=poly_iou(r,p,sh) if r is not None else 0
        res.append([i0]+[poly_iou(r.mean(0)+(r-r.mean(0))*f,gp,sh) if r is not None else i0 for f in SC])
R=np.array(res);print(R.mean(0))
