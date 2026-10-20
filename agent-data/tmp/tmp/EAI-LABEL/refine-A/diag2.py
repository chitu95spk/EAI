from common import *
import refine_g as R

rows=[]
for g in real_gt_files():
    n=os.path.basename(g)[:-5]; G=load_shapes(g); P=load_shapes(f'{RUNS["run2"]}/{n}.json'); gray=img(n)
    for gp in G:
        j=int(np.argmin([np.linalg.norm(q.mean(0)-gp.mean(0)) for q in P])); p=P[j]
        f,(x0,y0)=R.silhouette(gray,p,[q for k,q in enumerate(P) if k!=j])
        ang,ce=R.corner_angle(f); th=ang-np.pi/4
        e=gp[1]-gp[0]; ga=np.arctan2(e[1],e[0])
        err=(np.degrees(th-ga)+45)%90-45
        t=ga;u=np.array([np.cos(t),np.sin(t)]);v=np.array([-np.sin(t),np.cos(t)])
        ys,xs=np.nonzero(f);pts=np.stack([xs,ys],1)+[x0,y0];a=pts@u;b=pts@v
        box=np.array([a.min()*u+b.min()*v,a.max()*u+b.min()*v,a.max()*u+b.max()*v,a.min()*u+b.max()*v])
        rows.append((err,poly_iou(box,gp),len(rows)))
A=np.array(rows);print('orient mean abs %.1f med %.1f >8deg %d/90; oracle IoU %.3f'%(np.abs(A[:,0]).mean(),np.median(np.abs(A[:,0])),(np.abs(A[:,0])>8).sum(),A[:,1].mean()))
bad=A[np.abs(A[:,0])>8]; print(np.round(bad[:,[2,0,1]],2).tolist())
