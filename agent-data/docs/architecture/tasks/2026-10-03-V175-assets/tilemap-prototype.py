import cv2,numpy as np,glob,sys
def levels(g):
    t,_=cv2.threshold(g,0,255,cv2.THRESH_BINARY+cv2.THRESH_OTSU)
    d=g[g<=t]; l=g[g>t]
    return float(np.median(d)),float(np.median(l)),t
from scipy import ndimage
def tilemap(g,dark,light,f=0.25,h=0.25,mid=0.55,tiles=36,uni=0.35,minc=4):
    C=light-dark; H,W=g.shape; ts=max(16,int(round(min(H,W)/tiles)))
    rows=H//ts; cols=W//ts
    cls=np.zeros((rows,cols),np.uint8); act=np.zeros((rows,cols),bool)
    lo=dark+0.3*C; hi=light-0.3*C; midl=(dark+light)/2
    for r in range(rows):
        for c in range(cols):
            t=g[r*ts:(r+1)*ts,c*ts:(c+1)*ts].ravel().astype(np.float32)
            p10,p50,p90=np.percentile(t,[10,50,90]); spread=p90-p10; midf=np.mean((t>=lo)&(t<=hi))
            ink=t[t<=midl]; pap=t[t>midl]; n=len(t)
            k=0
            if spread<uni*C and lo<=p50<=hi: k=1
            elif spread>=uni*C and midf>=mid: k=1
            else:
                if len(ink)>=0.2*n and np.median(ink)>dark+f*C: k=3
                elif len(pap)>=0.2*n and np.median(pap)<light-h*C: k=2
                elif len(ink)>=0.2*n and np.median(ink)<dark-h*C: k=2
            cls[r,c]=k; act[r,c]=(p50<=midl) or spread>=uni*C or k>0
    # cluster filter
    for k in (1,2,3):
        lab,nl=ndimage.label(cls==k)
        for i in range(1,nl+1):
            m=lab==i
            if m.sum()<minc: cls[m]=0
    return cls,act,ts
# sample levels: area crop of sharp 135709
def area(img): return img[280:1480,1250:2450]
samp=cv2.imread('E:/Data/IK/Image_20261002181135709.bmp',0)
sd,sl,t=levels(area(samp)); print('sample dark/light',sd,sl,'C',sl-sd)
for f in sorted(glob.glob('E:/Data/IK/*.bmp')):
    g=area(cv2.imread(f,0)); cls,act,ts=tilemap(g,sd,sl)
    a=act.sum(); n=[(cls==k).sum() for k in (1,2,3)]
    print(f[-12:-4],'active',a,'blur %.1f heavy %.1f faded %.1f'%tuple(100*x/max(a,1) for x in n))
    out=cv2.cvtColor(g,cv2.COLOR_GRAY2BGR); ov=out.copy()
    cols={1:(0,140,255),2:(255,100,0),3:(0,215,255)}
    for r in range(cls.shape[0]):
        for c in range(cls.shape[1]):
            if cls[r,c]: cv2.rectangle(ov,(c*ts,r*ts),((c+1)*ts-1,(r+1)*ts-1),cols[cls[r,c]],-1)
    out=cv2.addWeighted(ov,0.45,out,0.55,0)
    cv2.imwrite('pqmap_'+f[-9:-4]+'.png',cv2.resize(out,None,fx=0.5,fy=0.5))
