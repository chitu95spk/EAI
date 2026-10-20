import cv2,numpy as np,glob,os,re,json
R='E:/Training/TrainLOGONEW/images/'
lab={f[:-5] for f in os.listdir(R) if f.endswith('.json')}
fam=[os.path.basename(f)[:-4] for f in glob.glob(R+'*.png') if re.match(r'(NHAN_PHONG|TRAY \(|Phong_L1)',os.path.basename(f)) and os.path.basename(f)[:-4] not in lab]
key=lambda s:(re.sub(r'\s*\(\d+\)','',s),int(re.findall(r'\((\d+)\)',s)[0]))
fam.sort(key=key)
regs={'A':(4300,3130,1000,800),'B':(3515,2800,1000,800),'C':(3250,2100,1300,1000),'D':(1450,1650,1000,1300)}
T=450
def tile(im,r):
    cx,cy,w,h=regs[r];x0=max(0,min(im.shape[1]-w,cx-w//2));y0=max(0,min(im.shape[0]-h,cy-h//2))
    c=im[y0:y0+h,x0:x0+w];s=T/max(w,h);c=cv2.resize(c,None,fx=s,fy=s,interpolation=cv2.INTER_AREA)
    o=np.zeros((T,T,3),np.uint8);o[:c.shape[0],:c.shape[1]]=c;cv2.putText(o,r,(4,20),0,0.7,(0,255,255),2);return o
rows=[]
for n in fam:
    im=cv2.imread(R+n+'.png');row=np.hstack([tile(im,r) for r in 'ABCD']);cv2.putText(row,n,(60,20),0,0.7,(255,255,0),2);rows.append(row)
for k in range(0,len(rows),4):
    cv2.imwrite('rv/s%02d.jpg'%(k//4),np.vstack(rows[k:k+4]),[cv2.IMWRITE_JPEG_QUALITY,88])
json.dump(fam,open('rv/fam.json','w'));print(len(fam),(len(fam)+3)//4)
