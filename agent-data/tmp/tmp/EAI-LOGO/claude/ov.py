import sys,json,cv2,numpy as np,os
pred=json.load(open('../../../artifacts/EAI-LOGO/claude-pred1/run/predictions.json'))
thr=float(sys.argv[1]);names=sys.argv[3:];out=sys.argv[2]
tiles=[]
for n in names:
    im=cv2.imread('E:/Training/TrainLOGONEW/images/'+n);s=900/im.shape[1];im=cv2.resize(im,None,fx=s,fy=s)
    for x in pred[n]:
        if x['score']<thr: continue
        p=(np.array(x['points'])*s).astype(np.int32);cv2.polylines(im,[p],True,(0,255,0),2);cv2.putText(im,'%.2f'%x['score'],tuple(p[0]),0,0.7,(0,255,255),2)
    cv2.putText(im,n,(5,25),0,0.8,(255,255,0),2);tiles.append(im)
h=max(t.shape[0] for t in tiles);tiles=[cv2.copyMakeBorder(t,0,h-t.shape[0],0,0,0,0) for t in tiles]
rows=[np.hstack(tiles[i:i+2]) for i in range(0,len(tiles),2)]
cv2.imwrite(out,np.vstack(rows),[cv2.IMWRITE_JPEG_QUALITY,82])
