from common import *
import refine_g as R
import itertools
cache={}
def data():
    out=[]
    for run in ('run2','full'):
        for g in real_gt_files():
            n=os.path.basename(g)[:-5]; G=load_shapes(g); P=load_shapes(f'{RUNS[run]}/{n}.json'); gray=img(n)
            for gp in G:
                j=int(np.argmin([np.linalg.norm(p.mean(0)-gp.mean(0)) for p in P]))
                out.append((gray,P[j],[q for k,q in enumerate(P) if k!=j],gp))
    return out
D=data()
def ev(**kw):
    for k,v in kw.items(): setattr(R,k,v)
    r=[poly_iou(R.refine_box(g,p,nb),gp) for g,p,nb,gp in D]
    return np.mean(r[:90]),np.mean(r[90:])
if __name__=='__main__':
    base=dict(SH=0.55,GROW=1.3,BRIGHT=0.92)
    print('base',ev(**base))
    for k,vals in (('SH',(0.4,0.7)),('GROW',(1.2,1.45)),('BRIGHT',(0.85,0.97))):
        for v in vals:
            d=dict(base);d[k]=v;print(k,v,ev(**d))
