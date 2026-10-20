from common import *
import refine_x as R
n=sorted(glob.glob('E:/EAI_TEST/*.json'))[int(sys.argv[1])]; n=os.path.basename(n)[:-5]
G=load_shapes(f'E:/EAI_TEST/{n}.json');P=load_shapes(f'{RUNS["run2"]}/{n}.json');gray=img(n)
im=cv2.cvtColor(gray,cv2.COLOR_GRAY2BGR)
for gp in G:
    p=P[int(np.argmin([np.linalg.norm(q.mean(0)-gp.mean(0)) for q in P]))]
    f,(x0,y0)=R.silhouette(gray,p,0.5,1.15,0)
    cs,_=cv2.findContours(f,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_NONE)
    cv2.drawContours(im,cs,-1,(0,0,255),2,offset=(x0,y0))
    cv2.polylines(im,[np.round(gp).astype(np.int32)],True,(0,255,0),2)
    ang,ce=R.corner_angle(f);ce=ce+[x0,y0]
    cv2.line(im,tuple(ce.astype(int)),tuple((ce+200*np.array([np.cos(ang),np.sin(ang)])).astype(int)),(255,0,0),3)
cv2.imwrite('v.png',cv2.resize(im,None,fx=.4,fy=.4))
