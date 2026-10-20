from common import *
import refine as R,time
def run(files,runsrc,pseudo=None,style='oriented'):
    res=[]
    for g in files:
        n=os.path.basename(g)[:-5]; G=load_shapes(g if pseudo is None else f'{pseudo}/{n}.json'); P=load_shapes(f'{runsrc}/{n}.json'); gray=img(n)
        for gp in G:
            j=int(np.argmin([np.linalg.norm(p.mean(0)-gp.mean(0)) for p in P]))
            if np.linalg.norm(P[j].mean(0)-gp.mean(0))>80: continue
            t=time.perf_counter(); r,ok=R.refine_box(gray,P[j],[q for k,q in enumerate(P) if k!=j],True,style); dt=(time.perf_counter()-t)*1e3
            res.append((poly_iou(P[j],gp),poly_iou(r,gp),ok,dt))
    return np.array(res)
if __name__=='__main__':
    for name in ('run2','full'):
        A=run(real_gt_files(),RUNS[name]); print('REAL',name,len(A),'orig %.3f refined %.3f gated-out %.2f  min %.2f  ms/shape %.0f'%(A[:,0].mean(),A[:,1].mean(),1-A[:,2].mean(),A[:,1].min(),A[:,3].mean()))
