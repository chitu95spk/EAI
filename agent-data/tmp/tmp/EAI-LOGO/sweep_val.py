import json,sys,os
exec(open('score_boxes.py').read().split("pred=json.load")[0])
pred=json.load(open(sys.argv[1])); imgdir=r'E:\Training\TrainLOGONEW\images'
val=[]
for name,preds in pred.items():
    d=json.load(open(os.path.join(imgdir,os.path.splitext(name)[0]+'.json'),encoding='utf8'))
    if d.get('flags',{}).get('val'): val.append((name,preds,[s['points'] for s in d['shapes']]))
print('val images',len(val),'GT boxes',sum(len(g) for _,_,g in val))
for min_iou in (0.3,0.5):
    print('--- match IoU >=',min_iou)
    for thr in (0.05,0.1,0.15,0.2,0.3,0.45,0.6):
        tp=fp=cand=0;gt=0
        for name,preds,g in val:
            ps=sorted([p for p in preds if p['score']>=thr],key=lambda p:-p['score']); used=set(); gt+=len(g); cand+=len(ps)
            for p in ps:
                best,bi=0,-1
                for i,b in enumerate(g):
                    if i in used: continue
                    v=iou(p['points'],b)
                    if v>best: best,bi=v,i
                if best>=min_iou: used.add(bi); tp+=1
                else: fp+=1
        print('thr %.2f cand/img %.1f recall %.2f precision %.2f FP/img %.1f'%(thr,cand/len(val),tp/gt,tp/max(1,cand),fp/len(val)))
