from common import *
import refine_x as R
def gt_angle(gp):
    e=gp[1]-gp[0]; return np.arctan2(e[1],e[0])
SP=eval(sys.argv[1]);rows=[]
for g in real_gt_files():
    n=os.path.basename(g)[:-5]; G=load_shapes(g); P=load_shapes(f'{RUNS["run2"]}/{n}.json'); gray=img(n)
    for gp in G:
        p=P[int(np.argmin([np.linalg.norm(q.mean(0)-gp.mean(0)) for q in P]))]
        f,(x0,y0)=R.silhouette(gray,p,*SP)
        ang,ce=R.corner_angle(f); th=ang-np.pi/4
        err=(np.degrees(th-gt_angle(gp))+45)%90-45
        # oracle orientation
        t=gt_angle(gp);u=np.array([np.cos(t),np.sin(t)]);v=np.array([-np.sin(t),np.cos(t)])
        ys,xs=np.nonzero(f);pts=np.stack([xs,ys],1)+[x0,y0];a=pts@u;b=pts@v
        box=np.array([a.min()*u+b.min()*v,a.max()*u+b.min()*v,a.max()*u+b.max()*v,a.min()*u+b.max()*v])
        # gt corner dir: compare
        rows.append((err,poly_iou(box,gp),f.sum()/255,cv2.contourArea(gp)))
A=np.array(rows);print('orient err deg: mean abs %.1f median %.1f  >10: %d/%d'%(np.abs(A[:,0]).mean(),np.median(np.abs(A[:,0])),(np.abs(A[:,0])>10).sum(),len(A)))
print('oracle-orientation silhouette bbox IoU %.3f'%A[:,1].mean()); print('area ratio sil/gt mean %.2f'%(A[:,2]/A[:,3]).mean(), np.round(np.sort(A[:,2]/A[:,3])[::9],2))
