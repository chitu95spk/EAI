import cv2,numpy as np,glob,os,re,json
R='E:/Training/TrainLOGONEW/images/'
lab={f[:-5] for f in os.listdir(R) if f.endswith('.json')}
names=[os.path.basename(f)[:-4] for f in glob.glob(R+'*.png') if re.match(r'(NHAN_PHONG \(|TRAY \(|Phong_L1|NHAN_PHONG_CHONG)',os.path.basename(f))]
key=lambda s:(re.sub(r'\s*\(\d+\)','',s),int(re.findall(r'\((\d+)\)',s)[0]));names.sort(key=key)
X0,Y0,W,H=2500,1900,1500,1100
out={}
for n in names:
    im=cv2.imread(R+n+'.png');c=im[Y0:Y0+H,X0:X0+W];g=cv2.cvtColor(c,cv2.COLOR_BGR2GRAY).astype(np.float32)
    bg=cv2.GaussianBlur(g,(0,0),9);d=g-bg   # bright thin ridges
    sm=cv2.GaussianBlur(d,(0,0),1.2)
    # oriented line filter: open with line kernels
    resp=np.zeros_like(sm)
    for a in range(0,180,12):
        k=np.zeros((31,31),np.uint8);cv2.line(k,(15-int(14*np.cos(np.radians(a))),15-int(14*np.sin(np.radians(a)))),(15+int(14*np.cos(np.radians(a))),15+int(14*np.sin(np.radians(a)))),1,1)
        m=cv2.filter2D(sm,-1,k.astype(np.float32)/k.sum());resp=np.maximum(resp,m)
    mask=(resp>6).astype(np.uint8)
    n_,lbl,st,cen=cv2.connectedComponentsWithStats(mask)
    cands=[]
    for i in range(1,n_):
        x,y,w,h,a=st[i]
        if max(w,h)<40 or a<60: continue
        pts=np.column_stack(np.where(lbl==i))[:,::-1].astype(np.float32);rr=cv2.minAreaRect(pts);L=max(rr[1]);Wd=min(rr[1])
        if L<45 or L/(Wd+1)<3: continue
        cands.append(dict(c=[float(rr[0][0]+X0),float(rr[0][1]+Y0)],L=float(L),W=float(Wd),a=float(rr[2]),str=float(resp[lbl==i].mean())))
    out[n]=cands
json.dump(out,open('ridge.json','w'))
print({k:len(v) for k,v in list(out.items())[:60]})
