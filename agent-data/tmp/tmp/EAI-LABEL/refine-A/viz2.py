from common import *
import refine_g as R

n=os.path.basename(sorted(glob.glob('E:/EAI_TEST/*.json'))[int(sys.argv[1])])[:-5]
G=load_shapes(f'E:/EAI_TEST/{n}.json');P=load_shapes(f'{RUNS["run2"]}/{n}.json');gray=img(n)
im=cv2.cvtColor(gray,cv2.COLOR_GRAY2BGR)
for j,p in enumerate(P):
    f,(x0,y0)=R.silhouette(gray,p,[q for k,q in enumerate(P) if k!=j])
    cs,_=cv2.findContours(f,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_NONE)
    cv2.drawContours(im,cs,-1,(0,0,255),3,offset=(x0,y0))
    ang,ce=R.corner_angle(f);ce=ce+[x0,y0]
    cv2.line(im,tuple(ce.astype(int)),tuple((ce+200*np.array([np.cos(ang),np.sin(ang)])).astype(int)),(255,0,0),4)
    cv2.polylines(im,[np.round(R.refine_box(gray,p,[q for k,q in enumerate(P) if k!=j])).astype(np.int32)],True,(0,255,255),3)
for gp in G: cv2.polylines(im,[np.round(gp).astype(np.int32)],True,(0,255,0),2)
xs,ys=np.concatenate(P).min(0).astype(int),np.concatenate(P).max(0).astype(int)
cv2.imwrite('v.png',cv2.resize(im[max(0,ys[1]-1000):0+ys[1]+100,max(0,xs[0]-100):xs[0]+1400][:,:] if False else im[max(0,xs[1]-100):ys[1]+100,max(0,xs[0]-100):ys[0]+100],None,fx=.5,fy=.5))
