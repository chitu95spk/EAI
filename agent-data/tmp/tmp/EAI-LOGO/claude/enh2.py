import cv2,numpy as np,sys
R='E:/Training/TrainLOGONEW/images/'
out=sys.argv[1];names=sys.argv[2:]
tiles=[]
for n in names:
    im=cv2.imread(R+n+'.png');s=1100/im.shape[1];im=cv2.resize(im,None,fx=s,fy=s,interpolation=cv2.INTER_AREA)
    lab=cv2.cvtColor(im,cv2.COLOR_BGR2LAB);lab[:,:,0]=cv2.createCLAHE(4.0,(8,8)).apply(lab[:,:,0]);e=cv2.cvtColor(lab,cv2.COLOR_LAB2BGR)
    cv2.putText(e,n,(6,24),0,0.8,(0,255,255),2);tiles.append(e)
rows=[np.hstack(tiles[i:i+2]) if len(tiles[i:i+2])==2 else np.hstack([tiles[i],np.zeros_like(tiles[i])]) for i in range(0,len(tiles),2)]
cv2.imwrite(out,np.vstack(rows),[cv2.IMWRITE_JPEG_QUALITY,85])
