import json,glob,os,sys
from PIL import Image
Image.MAX_IMAGE_PIXELS=None
SRC='E:/Training/TrainLOGONEW/images';DST=os.path.abspath('tmp/EAI-ONL-MODEL/tiles');W=int(sys.argv[1]) if len(sys.argv)>1 else 1536
def clip(poly,x0,y0,x1,y1):
    def cl(p,inside,inter):
        out=[]
        for i in range(len(p)):
            a,b=p[i-1],p[i]
            if inside(b):
                if not inside(a): out.append(inter(a,b))
                out.append(b)
            elif inside(a): out.append(inter(a,b))
        return out
    def ix(x):
        return lambda a,b:(x,a[1]+(b[1]-a[1])*(x-a[0])/(b[0]-a[0]))
    def iy(y):
        return lambda a,b:(a[0]+(b[0]-a[0])*(y-a[1])/(b[1]-a[1]),y)
    for ins,it in ((lambda p:p[0]>=x0,ix(x0)),(lambda p:p[0]<=x1,ix(x1)),(lambda p:p[1]>=y0,iy(y0)),(lambda p:p[1]<=y1,iy(y1))):
        if not poly:break
        poly=cl(poly,ins,it)
    return poly
def area(p):
    return abs(sum(p[i-1][0]*p[i][1]-p[i][0]*p[i-1][1] for i in range(len(p))))/2
n=0;ngt=0
for f in sorted(glob.glob(SRC+'/*.json')):
    j=json.load(open(f,encoding='utf-8'));name=os.path.splitext(os.path.basename(f))[0]
    png=os.path.join(SRC,j['imagePath'])
    if not os.path.exists(png):continue
    im=Image.open(png).convert('RGB');IW,IH=im.size
    shapes=[s for s in j['shapes'] if len(s['points'])>=3]
    centers=[]
    for s in shapes:
        if s['label']!='NG':continue
        xs=[p[0] for p in s['points']];ys=[p[1] for p in s['points']]
        cx,cy=sum(xs)/len(xs),sum(ys)/len(ys)
        if any(abs(cx-a)<W*0.4 and abs(cy-b)<W*0.4 for a,b in centers):continue
        centers.append((cx,cy))
    for k,(cx,cy) in enumerate(centers):
        x0=int(min(max(cx-W/2,0),IW-W));y0=int(min(max(cy-W/2,0),IH-W));x1,y1=x0+W,y0+W
        out=[]
        for s in shapes:
            poly=[tuple(p) for p in s['points']]
            full=area(poly)
            c=clip(poly,x0,y0,x1,y1)
            if len(c)<3:continue
            a=area(c)
            if a<max(25,0.02*full):continue
            out.append({'label':s['label'],'points':[[p[0]-x0,p[1]-y0] for p in c],'group_id':None,'shape_type':'polygon' if len(c)!=4 or a<0.98*full else s['shape_type'],'flags':{},'description':None,'direction':None})
        if not any(o['label']=='NG' for o in out):continue
        tn=f'{name}_t{k}'
        im.crop((x0,y0,x1,y1)).save(f'{DST}/{tn}.png')
        json.dump({'version':'5.5.0','flags':j['flags'],'shapes':out,'imagePath':tn+'.png','imageData':None,'imageHeight':W,'imageWidth':W},open(f'{DST}/{tn}.json','w',encoding='utf-8'))
        n+=1;ngt+=sum(1 for o in out if o['label']=='NG')
print('tiles',n,'NG shapes in tiles',ngt)
