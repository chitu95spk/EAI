import cv2,numpy as np,glob,os,re,json
R='E:/Training/TrainLOGONEW/images/'
names=[os.path.basename(f)[:-4] for f in glob.glob(R+'*.png') if re.match(r'(NHAN_PHONG|TRAY \(|Phong_L1|NHAN_PHONG_CHONG)',os.path.basename(f))]
key=lambda s:(re.sub(r'\s*\(\d+\)','',s),int(re.findall(r'\((\d+)\)',s)[0]));names.sort(key=key)
def cr(n):
    im=cv2.imread(R+n+'.png');cx,cy,w,h=3200,2450,1000,700
    c=im[cy-h//2:cy+h//2,cx-w//2:cx+w//2].copy()
    lab=cv2.cvtColor(c,cv2.COLOR_BGR2LAB);lab[:,:,0]=cv2.createCLAHE(3.0,(4,4)).apply(lab[:,:,0]);c=cv2.cvtColor(lab,cv2.COLOR_LAB2BGR)
    c=cv2.resize(c,(600,420),interpolation=cv2.INTER_AREA);cv2.putText(c,n,(5,18),0,0.55,(0,255,255),2);return c
for k in range(0,len(names),12):
    t=[cr(n) for n in names[k:k+12]]
    while len(t)%3:t.append(np.zeros_like(t[0]))
    cv2.imwrite('sc/s%02d.jpg'%(k//12),np.vstack([np.hstack(t[i:i+3]) for i in range(0,len(t),3)]),[cv2.IMWRITE_JPEG_QUALITY,88])
print(len(names),(len(names)+11)//12)
