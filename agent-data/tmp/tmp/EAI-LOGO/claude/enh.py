import sys,cv2,numpy as np
src,out,w=sys.argv[1],sys.argv[2],int(sys.argv[3])
im=cv2.imread(src);h=int(im.shape[0]*w/im.shape[1]);im=cv2.resize(im,(w,h),interpolation=cv2.INTER_AREA)
lab=cv2.cvtColor(im,cv2.COLOR_BGR2LAB);l=lab[:,:,0]
cl=cv2.createCLAHE(3.0,(8,8)).apply(l);lab[:,:,0]=cl
e=cv2.cvtColor(lab,cv2.COLOR_LAB2BGR)
cv2.imwrite(out,np.hstack([im,e]) if len(sys.argv)>4 else e,[cv2.IMWRITE_JPEG_QUALITY,85])
