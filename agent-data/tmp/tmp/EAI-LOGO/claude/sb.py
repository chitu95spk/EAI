import cv2,numpy as np,math,json
R='E:/Training/TrainLOGONEW/images/'
def box(c,L,W,deg):
    a=math.radians(deg);ux,uy=math.cos(a),math.sin(a);vx,vy=-uy,ux
    return [[c[0]+sx*L/2*ux+sy*W/2*vx,c[1]+sx*L/2*uy+sy*W/2*vy] for sx,sy in((1,-1),(1,1),(-1,1),(-1,-1))]
# name: list of (cx,cy,L,W,deg)
B={
'NHAN_PHONG (14)':[(2796,2562,170,50,104)],
'NHAN_PHONG (15)':[(2809,2617,120,50,100)],
'NHAN_PHONG (16)':[(2892,2425,130,50,-25)],
'NHAN_PHONG (17)':[(2911,2323,110,45,-12)],
'NHAN_PHONG (18)':[(2892,2275,190,55,-20),(2796,2585,100,45,100)],
'NHAN_PHONG (22)':[(2905,2323,120,45,-10)],
'NHAN_PHONG (41)':[(2930,2425,300,50,60)],
'NHAN_PHONG (43)':[(2930,2348,260,50,45)],
'TRAY (1)':[(3200,2437,500,60,-16)],'TRAY (2)':[(3200,2437,500,60,-16)],'TRAY (5)':[(3200,2437,500,60,-16)],'TRAY (6)':[(3200,2437,500,60,-16)],'TRAY (9)':[(3200,2437,500,60,-16)],
}
if __name__=='__main__':
    t=[]
    for n,bs in B.items():
        if n.startswith('TRAY'):continue
        im=cv2.imread(R+n+'.png');c=bs[0];x0,y0=int(c[0]-250),int(c[1]-250);cr=im[y0:y0+500,x0:x0+500].copy()
        for b in bs:cv2.polylines(cr,[(np.array(box(b[:2],*b[2:]))-[x0,y0]).astype(np.int32)],True,(0,255,0),1)
        cv2.putText(cr,n,(5,18),0,0.55,(0,255,255),2);t.append(cr)
    while len(t)%3:t.append(np.zeros_like(t[0]))
    cv2.imwrite('sb.jpg',np.vstack([np.hstack(t[i:i+3]) for i in range(0,len(t),3)]),[cv2.IMWRITE_JPEG_QUALITY,90])
