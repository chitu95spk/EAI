from common import *
import refine_x as R
def run(iters,grow,runs=('run2','full')):
    out=[]
    for run in runs:
        res=[]
        for g in real_gt_files():
            n=os.path.basename(g)[:-5]; G=load_shapes(g); P=load_shapes(f'{RUNS[run]}/{n}.json'); gray=img(n)
            for gp in G:
                j=int(np.argmin([np.linalg.norm(p.mean(0)-gp.mean(0)) for p in P])); p=P[j]
                r=R.refine_box(gray,p,[],iters,grow); res.append(poly_iou(r,gp))
        out.append(np.mean(res))
    return out
for it in (1,2,3):
    for gr in (1.1,1.2):
        print(it,gr,np.round(run(it,gr),3))
