from common import *
import refine_g as R
for run in ('run2','full'):
    res=[]
    for g in real_gt_files():
        n=os.path.basename(g)[:-5]; G=load_shapes(g); P=load_shapes(f'{RUNS[run]}/{n}.json'); gray=img(n)
        for gp in G:
            j=int(np.argmin([np.linalg.norm(p.mean(0)-gp.mean(0)) for p in P])); p=P[j]
            nb=[q for k,q in enumerate(P) if k!=j]
            r=R.refine_box(gray,p,nb)
            res.append((poly_iou(p,gp),poly_iou(r,gp),poly_iou(r,p)))
    A=np.array(res);print(run,'orig %.3f ref %.3f'%(A[:,0].mean(),A[:,1].mean()),'min %.2f'%A[:,1].min(), np.round(np.sort(A[:,1])[:8],2))
