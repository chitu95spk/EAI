import json,glob,os,sys,math
import numpy as np
run=sys.argv[1]
def rect(sh):
    p=np.array(sh['points'],float)
    c=p.mean(0); e1=p[1]-p[0]; e2=p[3]-p[0]
    return c,e1,e2,p
rows=[]
for g in sorted(glob.glob('E:/EAI_TEST/*.json'))[:18]:
    n=os.path.basename(g); pr=os.path.join(run,n)
    G=[s for s in json.load(open(g))['shapes'] if len(s['points'])==4]
    P=[s for s in json.load(open(pr))['shapes'] if len(s['points'])==4]
    used=set()
    for s in G:
        c,e1,e2,_=rect(s)
        best=None
        for i,t in enumerate(P):
            if i in used: continue
            d=np.linalg.norm(rect(t)[0]-c)
            if best is None or d<best[0]: best=(d,i)
        if best is None: continue
        used.add(best[1]); t=P[best[1]]
        c2,f1,f2,_=rect(t)
        # align pred edges to gt edges
        u=e1/np.linalg.norm(e1); v=e2/np.linalg.norm(e2)
        gw,gh=np.linalg.norm(e1),np.linalg.norm(e2)
        cands=[]
        for a,b in((f1,f2),(f2,f1)):
            pw=abs(a@u)+abs(b@u)*0; 
            cands.append((a,b))
        # choose pairing where first edge is closer to u axis
        a,b=(f1,f2) if abs(f1@u)/np.linalg.norm(f1)>=abs(f2@u)/np.linalg.norm(f2) else (f2,f1)
        pw,ph=np.linalg.norm(a),np.linalg.norm(b)
        ang=math.degrees(math.atan2((u[0]*(a/pw)[1]-u[1]*(a/pw)[0]),u@(a/pw)))
        ang=(ang+90)%180-90
        d=c2-c
        rows.append((pw/gw,ph/gh,ang,d@u,d@v,gw,gh))
R=np.array(rows)
print('n',len(R))
for i,nm in enumerate(['wratio','hratio','angdeg','du','dv','gw','gh']):
    print(nm,'mean %.3f med %.3f std %.3f'%(R[:,i].mean(),np.median(R[:,i]),R[:,i].std()))
