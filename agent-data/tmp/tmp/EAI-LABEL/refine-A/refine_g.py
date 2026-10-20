import numpy as np,cv2
from refine_w import shrink,fill,corner_angle
SH=0.55;GROW=1.3;BRIGHT=0.92;CORE_FG=0.0
def silhouette(gray,p,nb,ex=0.6):
    ext=p.max(0)-p.min(0)
    x0,y0=np.maximum(0,p.min(0)-ex*ext).astype(int); x1,y1=np.minimum([gray.shape[1],gray.shape[0]],p.max(0)+ex*ext).astype(int)
    w=gray[y0:y1,x0:x1]; wb=cv2.GaussianBlur(w,(5,5),0)
    off=np.array([x0,y0],np.float32)
    bgl=np.percentile(wb,90)
    gx=cv2.Sobel(wb,cv2.CV_32F,1,0,ksize=3);gy=cv2.Sobel(wb,cv2.CV_32F,0,1,ksize=3)
    mag=cv2.GaussianBlur(cv2.magnitude(gx,gy),(0,0),1.5)
    mag=np.clip(mag/ max(np.percentile(mag,99.5),1)*255,0,255).astype(np.uint8)
    mk=np.zeros(w.shape,np.int32)
    allow=np.zeros(w.shape,np.uint8); fill(0,shrink(p-off,GROW),255,allow)
    core=np.zeros(w.shape,np.uint8); fill(0,shrink(p-off,0.8),255,core)
    mk[(allow==0)]=1
    mk[(wb>bgl*BRIGHT)&(core==0)]=1
    for q in nb: fill(0,shrink(q-off,SH),3,mk)
    fill(0,shrink(p-off,SH),2,mk)
    cv2.watershed(cv2.merge([mag,mag,mag]),mk)
    m=(mk==2).astype(np.uint8)*255
    n,lab,st,cen=cv2.connectedComponentsWithStats(m)
    if n<2: return None,(x0,y0)
    k=1+np.argmax(st[1:,cv2.CC_STAT_AREA]); m=(lab==k).astype(np.uint8)*255
    cs,_=cv2.findContours(m,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_NONE)
    c=cv2.convexHull(max(cs,key=cv2.contourArea)); f=np.zeros_like(m); cv2.drawContours(f,[c],-1,255,-1)
    return f,(x0,y0)
def refine_box(gray,p,nb):
    f,(x0,y0)=silhouette(gray,p,nb)
    if f is None: return p
    ang,ce=corner_angle(f); th=ang-np.pi/4
    u=np.array([np.cos(th),np.sin(th)]);v=np.array([-np.sin(th),np.cos(th)])
    ys,xs=np.nonzero(f); pts=np.stack([xs,ys],1).astype(np.float64); a=pts@u;b=pts@v
    box=np.array([a.min()*u+b.min()*v,a.max()*u+b.min()*v,a.max()*u+b.max()*v,a.min()*u+b.max()*v])+[x0,y0]
    return box.astype(np.float32)
