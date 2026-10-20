import numpy as np,cv2
def silhouette(gray,p,ex=0.5,grow=1.4,op=0):
    ext=p.max(0)-p.min(0)
    x0,y0=np.maximum(0,p.min(0)-ex*ext).astype(int); x1,y1=np.minimum([gray.shape[1],gray.shape[0]],p.max(0)+ex*ext).astype(int)
    w=gray[y0:y1,x0:x1]
    _,th=cv2.threshold(cv2.GaussianBlur(w,(5,5),0),0,255,cv2.THRESH_BINARY_INV+cv2.THRESH_OTSU)
    th=cv2.morphologyEx(th,cv2.MORPH_CLOSE,np.ones((15,15),np.uint8))
    pm=np.zeros(th.shape,np.uint8); q=p-[x0,y0]; cc=q.mean(0); q=cc+(q-cc)*grow
    cv2.fillPoly(pm,[np.round(q).astype(np.int32)],255); th=th&pm
    n,lab,st,cen=cv2.connectedComponentsWithStats(th)
    cx,cy=cc.astype(int); cx=min(max(cx,0),th.shape[1]-1); cy=min(max(cy,0),th.shape[0]-1)
    k=lab[cy,cx]
    if k==0:
        if n<2: return None,(x0,y0)
        k=1+np.argmax(st[1:,cv2.CC_STAT_AREA])
    m=(lab==k).astype(np.uint8)*255
    cs,_=cv2.findContours(m,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_NONE)
    c=max(cs,key=cv2.contourArea)
    filled=np.zeros_like(m); cv2.drawContours(filled,[c],-1,255,-1)
    return filled,(x0,y0)
def corner_angle(filled):
    cs,_=cv2.findContours(filled,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_NONE)
    c=max(cs,key=cv2.contourArea)[:,0,:].astype(np.float64)
    dt=cv2.distanceTransform(filled,cv2.DIST_L2,5); _,rmax,_,loc=cv2.minMaxLoc(cv2.GaussianBlur(dt,(0,0),5)); ce=np.array(loc,np.float64)
    d=np.hypot(*(c-ce).T); 
    mask=d>=d.max()*0.97
    a=np.arctan2(*(c[mask]-ce).T[::-1])
    ang=np.arctan2(np.sin(a).mean(),np.cos(a).mean())
    return ang,ce
def refine_once(gray,p,nb,SP=(0.5,1.15,0)):
    f,(x0,y0)=silhouette(gray,p,*SP)
    if f is None: return p
    ang,ce=corner_angle(f)
    th=ang-np.pi/4   # side axis
    u=np.array([np.cos(th),np.sin(th)]);v=np.array([-np.sin(th),np.cos(th)])
    ys,xs=np.nonzero(f); pts=np.stack([xs,ys],1).astype(np.float64)
    a=pts@u;b=pts@v
    a0,a1,b0,b1=a.min(),a.max(),b.min(),b.max()
    box=np.array([a0*u+b0*v,a1*u+b0*v,a1*u+b1*v,a0*u+b1*v])+[x0,y0]
    return box.astype(np.float32)

ITER=3;GROW=1.15
def refine_box(gray,p,nb,iters=None,grow=None):
    iters=iters or ITER;grow=grow or GROW
    for _ in range(iters):
        p=refine_once(gray,p,nb,(0.5,grow,0))
    return p
