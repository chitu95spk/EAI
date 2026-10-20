import json,cv2,numpy as np,sys
C=json.load(open('cands.json'));T=int(sys.argv[1])
cache={}
def tile(c):
    n,sc,pts=c;pts=np.array(pts)
    if n not in cache: cache.clear();cache[n]=cv2.imread('E:/Training/TrainLOGONEW/images/'+n)
    im=cache[n];cx,cy=pts.mean(0);w=max(np.ptp(pts[:,0]),np.ptp(pts[:,1]));half=int(max(w*0.8,350))
    x0=int(max(0,min(im.shape[1]-2*half,cx-half)));y0=int(max(0,min(im.shape[0]-2*half,cy-half)))
    cr=im[y0:y0+2*half,x0:x0+2*half].copy();s=T/cr.shape[1];raw=cv2.resize(cr,(T,int(cr.shape[0]*s)))
    q=((pts-[x0,y0])*s).astype(np.int32);o=raw.copy();cv2.polylines(o,[q],True,(0,255,0),1)
    t=np.hstack([raw,o]);cv2.putText(t,'%s %.2f'%(n.replace('.png',''),sc),(4,18),0,0.6,(255,255,0),2);return t
tiles=[tile(c) for c in C]
for k in range(0,len(tiles),6):
    ch=tiles[k:k+6];h=max(t.shape[0] for t in ch);ch=[cv2.copyMakeBorder(t,0,h-t.shape[0],0,0,0,0) for t in ch]
    while len(ch)%2: ch.append(np.zeros_like(ch[0]))
    cv2.imwrite('sheet%d.jpg'%(k//6),np.vstack([np.hstack(ch[i:i+2]) for i in range(0,len(ch),2)]),[cv2.IMWRITE_JPEG_QUALITY,85])
print(len(tiles))
