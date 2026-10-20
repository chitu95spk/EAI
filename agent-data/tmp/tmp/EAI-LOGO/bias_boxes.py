import json,sys,os,math,statistics as st
exec(open('score_boxes.py').read().split("pred=json.load")[0])
def dims(p):
    a=math.dist(p[0],p[1]); b=math.dist(p[1],p[2]); ang=math.degrees(math.atan2(p[1][1]-p[0][1],p[1][0]-p[0][0]))
    return a,b,ang
def center(p): return (sum(x for x,_ in p)/4,sum(y for _,y in p)/4)
pred=json.load(open(sys.argv[1])); thr=float(sys.argv[2]); imgdir=r'E:\Training\TrainLOGONEW\images'
rows=[]
for name,preds in pred.items():
    d=json.load(open(os.path.join(imgdir,os.path.splitext(name)[0]+'.json'),encoding='utf8'))
    gt=[s['points'] for s in d['shapes']]; used=set()
    for p in sorted([q for q in preds if q['score']>=thr],key=lambda q:-q['score']):
        best,bi=0,-1
        for i,g in enumerate(gt):
            if i in used: continue
            v=iou(p['points'],g)
            if v>best:best,bi=v,i
        if best>=.5:
            used.add(bi); g=gt[bi]; pa=dims(p['points']); ga=dims(g)
            ar=(pa[0]*pa[1])/(ga[0]*ga[1])
            c1,c2=center(p['points']),center(g); off=math.dist(c1,c2)/math.sqrt(ga[0]*ga[1])
            # long-side ratios (sort sides)
            ps=sorted(pa[:2]);gs=sorted(ga[:2])
            rows.append((best,ar,off,ps[1]/gs[1],ps[0]/gs[0]))
n=len(rows)
print('matched',n)
for k,name in ((1,'area ratio pred/gt'),(2,'center offset / sqrt(gt area)'),(3,'long side ratio'),(4,'short side ratio')):
    v=sorted(r[k] for r in rows); print(name,'median %.3f p10 %.3f p90 %.3f'%(v[n//2],v[n//10],v[9*n//10]))
