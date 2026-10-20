import numpy as np,cv2
def shrink(q,f):
    c=q.mean(0); return c+(q-c)*f
def fill(shape,q,val,img):
    cv2.fillPoly(img,[np.round(q).astype(np.int32)],val)
TH=None
HULL=True
def silhouette(gray,p,nb,ex=0.6,grow=1.35,sh=0.5,wk=1):
    ext=p.max(0)-p.min(0)
    x0,y0=np.maximum(0,p.min(0)-ex*ext).astype(int); x1,y1=np.minimum([gray.shape[1],gray.shape[0]],p.max(0)+ex*ext).astype(int)
    w=gray[y0:y1,x0:x1]
    wb=cv2.GaussianBlur(w,(5,5),0)
    if TH is None: _,th=cv2.threshold(wb,0,255,cv2.THRESH_BINARY_INV+cv2.THRESH_OTSU)
    else:
        bgl=np.percentile(wb,90); _,th=cv2.threshold(wb,bgl*TH,255,cv2.THRESH_BINARY_INV)
    fg=cv2.morphologyEx(th,cv2.MORPH_CLOSE,np.ones((15,15),np.uint8))
    off=np.array([x0,y0],np.float32)
    mk=np.zeros(fg.shape,np.int32)
    allow=np.zeros(fg.shape,np.uint8); fill(0,shrink(p-off,grow),255,allow)
    mk[(allow==0)|(fg==0)]=1
    for q in nb:
        qq=q-off
        if (qq.max(0)<0).any() or (qq.min(0)>[fg.shape[1],fg.shape[0]]).any(): continue
        fill(0,shrink(qq,sh),3,mk)
    fill(0,shrink(p-off,sh),2,mk)
    dt=cv2.distanceTransform(fg,cv2.DIST_L2,5)
    land=255-np.clip(dt/ max(dt.max(),1)*255,0,255).astype(np.uint8)
    cv2.watershed(cv2.merge([land,land,land]),mk)
    m=((mk==2)&(fg>0)).astype(np.uint8)*255
    m=cv2.morphologyEx(m,cv2.MORPH_OPEN,np.ones((5,5),np.uint8))
    n,lab,st,cen=cv2.connectedComponentsWithStats(m)
    if n<2: return None,(x0,y0)
    k=1+np.argmax(st[1:,cv2.CC_STAT_AREA]); m=(lab==k).astype(np.uint8)*255
    cs,_=cv2.findContours(m,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_NONE)
    c=max(cs,key=cv2.contourArea)
    if HULL: c=cv2.convexHull(c)
    f=np.zeros_like(m); cv2.drawContours(f,[c],-1,255,-1)
    return f,(x0,y0)
def corner_angle(filled):
    cs,_=cv2.findContours(filled,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_NONE)
    c=max(cs,key=cv2.contourArea)[:,0,:].astype(np.float64)
    M=cv2.moments(filled,True); ce=np.array([M['m10']/M['m00'],M['m01']/M['m00']])
    d=np.hypot(*(c-ce).T); mask=d>=d.max()*0.97
    a=np.arctan2(*(c[mask]-ce).T[::-1])
    return np.arctan2(np.sin(a).mean(),np.cos(a).mean()),ce
def refine_box(gray,p,nb):
    f,(x0,y0)=silhouette(gray,p,nb)
    if f is None: return p
    ang,ce=corner_angle(f); th=ang-np.pi/4
    u=np.array([np.cos(th),np.sin(th)]);v=np.array([-np.sin(th),np.cos(th)])
    ys,xs=np.nonzero(f); pts=np.stack([xs,ys],1).astype(np.float64); a=pts@u;b=pts@v
    box=np.array([a.min()*u+b.min()*v,a.max()*u+b.min()*v,a.max()*u+b.max()*v,a.min()*u+b.max()*v])+[x0,y0]
    return box.astype(np.float32)
