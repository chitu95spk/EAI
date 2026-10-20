from eval import *
import refine as RR
ok=sorted(f for f in glob.glob(PSEUDO+'/*.json') if os.path.basename(f)[:-5].endswith('_OK'))
axf=lambda f: np.mean([RR._axis_aligned(p) for p in load_shapes(f)])
ax=[f for f in ok if axf(f)>=.5]; ori=[f for f in ok if axf(f)<.5]
print(len(ok),'OK imgs; pseudo axis-style',len(ax),'oriented-style',len(ori))
if __name__=="__main__":
 for name in ('run2','full'):
   for lab,grp,st in (('axis-pseudo',ax[::2][:10],'axis'),('axis-pseudo-oriented-mode',ax[::2][:10],'oriented'),('oriented-pseudo',ori,'oriented')):
     files=[f for f in grp if os.path.exists(f'{RUNS[name]}/{os.path.basename(f)}')]
     A=run(files,RUNS[name],pseudo=PSEUDO,style=st)
     print(name,lab,len(files),'imgs',len(A),'shapes: orig %.3f refined %.3f gated-out %.2f ms/shape %.0f improved %.2f'%(A[:,0].mean(),A[:,1].mean(),1-A[:,2].mean(),A[:,3].mean(),(A[:,1]>A[:,0]).mean()))
 