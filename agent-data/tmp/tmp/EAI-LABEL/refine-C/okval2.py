import sys;sys.path.insert(0,'tmp/EAI-LABEL/refine-C')
import sam,cv2,numpy as np,json,glob,os,time
run=sys.argv[1];N=int(sys.argv[2])
def rect(pts):
    pts=np.array(pts,np.float32); return pts if len(pts)==4 else cv2.boxPoints(cv2.minAreaRect(pts))
L=lambda p:json.load(open(p,encoding='utf-8'))
names=[os.path.basename(x)[:-5] for x in sorted(glob.glob('artifacts/EAI-LABEL/s0-baseline/project/*_OK.json'))]
names=names[::max(1,len(names)//N)][:N]
rows=[];tt=[];nsh=0
for n in names:
    B=[rect(s['points']) for s in L(f'artifacts/EAI-LABEL/s0-baseline/project/{n}.json')['shapes']]
    P=[rect(s['points']) for s in L(f'{run}/{n}.json')['shapes']]
    g=cv2.imread(f'E:/EAI_TEST/{n}.png',0);H,W=g.shape
    t=time.time();e=sam.encode(g);te=time.time()-t;t=time.time()
    ious0=[];ious1=[];gates=[];ious2=[]
    for p in P:
        m,s=sam.decode(e,W,H,sam.aabb(p));f=sam.fit(m)
        j=int(np.argmin([np.linalg.norm(b.mean(0)-p.mean(0)) for b in B]));b=B[j]
        i0=sam.poly_iou(p,b,g.shape);i1=sam.poly_iou(f,b,g.shape) if f is not None else i0
        if f is not None:
            ed=p[1]-p[0];ang=abs(np.degrees(np.arctan2(ed[1],ed[0])))%90;ang=min(ang,90-ang)
            if ang<3:
                x,y,w,h=cv2.boundingRect(cv2.findNonZero(m));f2=np.array([[x,y],[x+w,y],[x+w,y+h],[x,y+h]],np.float32)
            else: f2=f
            i2=sam.poly_iou(f2,b,g.shape)
        else: i2=i0
        ious2.append(i2)
        ious0.append(i0);ious1.append(i1);gates.append(sam.poly_iou(f,p,g.shape) if f is not None else 0)
    td=time.time()-t;tt.append((te,td,len(P)))
    rows+=list(zip(ious0,ious1,gates,ious2))
    print(n[-14:],'base',len(B),'eco',len(P),'prop %.3f refined %.3f enc %.1f dec %.1f'%(np.mean(ious0),np.mean(ious1),te,td),flush=True)
R=np.array(rows);print('shapes',len(R),'prop %.3f refined %.3f axis-aware %.3f'%(R[:,0].mean(),R[:,1].mean(),R[:,3].mean()),'refined<.8 frac %.2f'%(R[:,1]<.8).mean(),'gate>=.7 frac %.2f'%(R[:,2]>=.7).mean())
for t in (0.6,0.7): print('gate',t,'%.3f'%np.where(R[:,2]>=t,R[:,1],R[:,0]).mean())
