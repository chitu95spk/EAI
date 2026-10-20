"""Box tightening for Economy oriented_rectangle proposals (offline, no agent).
refine_box(gray_image, proposal_pts(4x2), neighbours_pts(list of 4x2)) -> 4x2 float32 pts.
OpenCV calls: GaussianBlur, percentile (sort; C# Array.Sort), Sobel x2, magnitude, fillPoly,
watershed (marker based), connectedComponentsWithStats, findContours, convexHull,
drawContours, moments. Everything else is plain array math.
Method: per object marker watershed on the Sobel gradient (object marker = proposal shrunk to 0.55,
neighbours = other label, background = bright panel pixels + outside proposal grown), convex hull of
the object region, orientation = corner direction (farthest hull point from centroid) - 45 deg, box = extents
of the region along that frame. Run twice (second pass seeds from first result, tighter limits).
Safe gate: result with IoU vs proposal < GATE keeps the proposal."""
import numpy as np,cv2
PASSES=((0.55,1.45),(0.55,1.3))   # (marker shrink, outer grow) per pass
AXIS_TOL=1.0; BRIGHT=0.85; GATE=0.45; PAD=0.0; EX=0.6
def _shrink(q,f):
    c=q.mean(0); return c+(q-c)*f
def _fill(q,val,img): cv2.fillPoly(img,[np.round(q).astype(np.int32)],val)
def _silhouette(gray,p,nb,sh,grow):
    ext=p.max(0)-p.min(0)
    x0,y0=np.maximum(0,p.min(0)-EX*ext).astype(int); x1,y1=np.minimum([gray.shape[1],gray.shape[0]],p.max(0)+EX*ext).astype(int)
    if x1-x0<8 or y1-y0<8: return None,(x0,y0)
    wb=cv2.GaussianBlur(gray[y0:y1,x0:x1],(5,5),0); off=np.array([x0,y0],np.float32)
    bgl=np.percentile(wb,90)
    gx=cv2.Sobel(wb,cv2.CV_32F,1,0,ksize=3);gy=cv2.Sobel(wb,cv2.CV_32F,0,1,ksize=3)
    mag=cv2.GaussianBlur(cv2.magnitude(gx,gy),(0,0),1.5)
    mag=np.clip(mag/max(np.percentile(mag,99.5),1)*255,0,255).astype(np.uint8)
    mk=np.zeros(wb.shape,np.int32)
    allow=np.zeros(wb.shape,np.uint8); _fill(_shrink(p-off,grow),255,allow)
    core=np.zeros(wb.shape,np.uint8); _fill(_shrink(p-off,0.8),255,core)
    mk[allow==0]=1; mk[(wb>bgl*BRIGHT)&(core==0)]=1
    for q in nb: _fill(_shrink(q-off,sh),3,mk)
    _fill(_shrink(p-off,sh),2,mk)
    cv2.watershed(cv2.merge([mag,mag,mag]),mk)
    m=(mk==2).astype(np.uint8)*255
    n,lab,st,_=cv2.connectedComponentsWithStats(m)
    if n<2: return None,(x0,y0)
    k=1+np.argmax(st[1:,cv2.CC_STAT_AREA]); m=(lab==k).astype(np.uint8)*255
    cs,_=cv2.findContours(m,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_NONE)
    c=cv2.convexHull(max(cs,key=cv2.contourArea)); f=np.zeros_like(m); cv2.drawContours(f,[c],-1,255,-1)
    return f,(x0,y0)
def _corner_angle(f):
    cs,_=cv2.findContours(f,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_NONE)
    c=max(cs,key=cv2.contourArea)[:,0,:].astype(np.float64)
    M=cv2.moments(f,True); ce=np.array([M['m10']/M['m00'],M['m01']/M['m00']])
    d=np.hypot(*(c-ce).T); mask=d>=d.max()*0.97
    a=np.arctan2(*(c[mask]-ce).T[::-1])
    return np.arctan2(np.sin(a).mean(),np.cos(a).mean())
def _axis_aligned(p):
    e=p[1]-p[0]; a=abs(np.degrees(np.arctan2(e[1],e[0])))%90
    return min(a,90-a)<AXIS_TOL
def _once(gray,p,nb,sh,grow,axis=False):
    f,(x0,y0)=_silhouette(gray,p,nb,sh,grow)
    if f is None: return None
    th=0.0 if axis else _corner_angle(f)-np.pi/4
    u=np.array([np.cos(th),np.sin(th)]);v=np.array([-np.sin(th),np.cos(th)])
    ys,xs=np.nonzero(f); pts=np.stack([xs,ys],1).astype(np.float64); a=pts@u;b=pts@v
    a0,a1,b0,b1=a.min()-PAD,a.max()+1+PAD,b.min()-PAD,b.max()+1+PAD
    return (np.array([a0*u+b0*v,a1*u+b0*v,a1*u+b1*v,a0*u+b1*v])+[x0,y0]).astype(np.float32)
def _iou(a,b):
    lo=np.floor(np.minimum(a.min(0),b.min(0))).astype(int)-2; hi=np.ceil(np.maximum(a.max(0),b.max(0))).astype(int)+2
    s=(hi-lo)[::-1]; m1=np.zeros(s,np.uint8);m2=np.zeros(s,np.uint8)
    _fill(a-lo,1,m1);_fill(b-lo,1,m2); u=(m1|m2).sum(); return (m1&m2).sum()/u if u else 0
def refine_box(gray,p,nb,return_info=False,style='oriented'):
    p=np.asarray(p,np.float32); nb=[np.asarray(q,np.float32) for q in nb]; cur=p
    # image-level style: axis-aligned labels (OK-type images) vs corner-oriented labels (NG-type)
    axis=(style=='axis')   # caller decides: 'axis' = bbox of silhouette, 'oriented' = aligned to the squared corner
    for sh,grow in PASSES:
        r=_once(gray,cur,nb,sh,grow,axis)
        if r is None: break
        cur=r
    ok=cur is not p and _iou(cur,p)>=GATE
    out=cur if ok else p
    return (out,ok) if return_info else out
