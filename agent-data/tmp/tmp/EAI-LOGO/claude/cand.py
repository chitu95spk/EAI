import json,os,cv2,numpy as np
d=json.load(open('../../../artifacts/EAI-LOGO/claude-pred1/run/predictions.json'))
lab={f[:-5]+'.png' for f in os.listdir('../project') if f.endswith('.json')}
def inter_min(a,b):
    a=np.array(a,np.float32);b=np.array(b,np.float32)
    ar=cv2.contourArea(a);br=cv2.contourArea(b)
    r,p=cv2.intersectConvexConvex(a,b)
    return r/max(1,min(ar,br))
C=[]
for n,v in sorted(d.items()):
    if n in lab: continue
    keep=[]
    for x in sorted(v,key=lambda x:-x['score']):
        if x['score']<0.25: continue
        if any(inter_min(x['points'],k['points'])>0.3 for k in keep): continue
        keep.append(x)
    for x in keep: C.append((n,x['score'],x['points']))
json.dump(C,open('cands.json','w'))
print(len(C),len({c[0] for c in C}))
