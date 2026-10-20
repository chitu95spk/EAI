import json,sys,glob,os
sys.path.insert(0,os.path.dirname(__file__))
def signed_area(p): return sum(p[i][0]*p[(i+1)%len(p)][1]-p[(i+1)%len(p)][0]*p[i][1] for i in range(len(p)))/2 if p else 0
def clip(subject,cl):
    out=list(subject); o=1 if signed_area(cl)>=0 else -1
    for i,a in enumerate(cl):
        b=cl[(i+1)%len(cl)]; inc,out=out,[]
        if not inc: break
        s=inc[-1]
        for e in inc:
            def side(q): return o*((b[0]-a[0])*(q[1]-a[1])-(b[1]-a[1])*(q[0]-a[0]))
            si,ei=side(s)>=-1e-9,side(e)>=-1e-9
            if ei!=si:
                d1=side(s);d2=side(e);t=d1/(d1-d2)
                out.append((s[0]+(e[0]-s[0])*t,s[1]+(e[1]-s[1])*t))
            if ei: out.append(tuple(e))
            s=e
    return out
intersection=clip
def area(p): return abs(signed_area(p))
def iou(a,b):
    inter=intersection(a,b)
    ia=area(inter) if len(inter)>=3 else 0
    u=area(a)+area(b)-ia
    return ia/u if u>0 else 0
pred=json.load(open(sys.argv[1])); thr=float(sys.argv[2]) if len(sys.argv)>2 else 0.45
imgdir=r'E:\Training\TrainLOGONEW\images'
res={'train':[], 'val':[]}; fp={'train':0,'val':0}; gtn={'train':0,'val':0}; tp={'train':0,'val':0}
for name,preds in pred.items():
    js=os.path.join(imgdir,os.path.splitext(name)[0]+'.json')
    d=json.load(open(js,encoding='utf8')); fl=d.get('flags',{})
    split='val' if fl.get('val') else 'train'
    gt=[s['points'] for s in d.get('shapes',[]) if s.get('shape_type')=='oriented_rectangle']
    ps=sorted([p for p in preds if isinstance(preds,list) and p['score']>=thr],key=lambda p:-p['score'])
    used=set(); gtn[split]+=len(gt)
    for p in ps:
        best,bi=0,-1
        for i,g in enumerate(gt):
            if i in used: continue
            v=iou(p['points'],g)
            if v>best: best,bi=v,i
        if best>=0.5: used.add(bi); tp[split]+=1; res[split].append(best)
        else: fp[split]+=1
for s in ('train','val'):
    r=res[s]
    print(s,'GT',gtn[s],'TP',tp[s],'FP',fp[s],'recall %.2f'%(tp[s]/max(1,gtn[s])),'prec %.2f'%(tp[s]/max(1,tp[s]+fp[s])),
      'meanIoU(matched) %.3f'%(sum(r)/max(1,len(r))),'IoU>0.9: %d/%d'%(sum(1 for x in r if x>0.9),len(r)),'IoU>0.75: %d'%sum(1 for x in r if x>0.75))
