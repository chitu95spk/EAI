import json,glob,os,sys,time
import numpy as np,cv2,onnxruntime as ort
M='C:/Users/chitu/AppData/Local/EasyAI/models/sam2/'
so=ort.SessionOptions(); so.intra_op_num_threads=2; so.log_severity_level=3; so.graph_optimization_level=ort.GraphOptimizationLevel.ORT_DISABLE_ALL
enc=ort.InferenceSession(M+'encoder.onnx',so,providers=['CPUExecutionProvider'])
dec=ort.InferenceSession(M+'decoder.onnx',so,providers=['CPUExecutionProvider'])
mean=np.array([0.485,0.456,0.406],np.float32);std=np.array([0.229,0.224,0.225],np.float32)
def encode(gray):
    rgb=cv2.cvtColor(gray,cv2.COLOR_GRAY2RGB) if gray.ndim==2 else cv2.cvtColor(gray,cv2.COLOR_BGR2RGB)
    x=cv2.resize(rgb,(1024,1024),interpolation=cv2.INTER_LINEAR).astype(np.float32)/255
    x=((x-mean)/std).transpose(2,0,1)[None].astype(np.float32)
    return enc.run(None,{'image':x})  # h0,h1,embed
def decode(emb,W,H,box,pt=None):
    sx,sy=1024/W,1024/H
    c=[[box[0]*sx,box[1]*sy],[box[2]*sx,box[3]*sy]];l=[2,3]
    if pt is not None: c.append([pt[0]*sx,pt[1]*sy]);l.append(1)
    out=dec.run(None,{'image_embed':emb[2],'high_res_feats_0':emb[0],'high_res_feats_1':emb[1],
      'point_coords':np.array([c],np.float32),'point_labels':np.array([l],np.float32),
      'mask_input':np.zeros((1,1,256,256),np.float32),'has_mask_input':np.zeros(1,np.float32)})
    masks,iou=out; b=int(np.argmax(iou[0]))
    m=cv2.resize(masks[0,b],(W,H),interpolation=cv2.INTER_LINEAR)
    return (m>0).astype(np.uint8),float(iou[0,b])
def fit(mask):
    cs,_=cv2.findContours(mask,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_SIMPLE)
    if not cs: return None
    c=max(cs,key=cv2.contourArea)
    if cv2.contourArea(c)<500: return None
    return cv2.boxPoints(cv2.minAreaRect(c))
def poly_iou(a,b,shape):
    m1=np.zeros(shape,np.uint8);m2=np.zeros(shape,np.uint8)
    cv2.fillPoly(m1,[np.round(a).astype(np.int32)],1);cv2.fillPoly(m2,[np.round(b).astype(np.int32)],1)
    i=(m1&m2).sum();u=(m1|m2).sum();return i/u if u else 0
def aabb(p): return [p[:,0].min(),p[:,1].min(),p[:,0].max(),p[:,1].max()]
if __name__=='__main__':
    run=sys.argv[1]; tag=sys.argv[2]; N=int(sys.argv[3]) if len(sys.argv)>3 else 18
    res=[];te=[];td=[]
    for g in sorted(glob.glob('E:/EAI_TEST/*.json'))[:N]:
        n=os.path.basename(g)[:-5]
        G=[np.array(s['points']) for s in json.load(open(g))['shapes']]
        P=[np.array(s['points']) for s in json.load(open(f'{run}/{n}.json'))['shapes'] if len(s['points'])==4]
        gray=cv2.imread(f'E:/EAI_TEST/{n}.png',0);H,W=gray.shape
        t=time.time();emb=encode(gray);te.append(time.time()-t)
        for gp in G:
            j=int(np.argmin([np.linalg.norm(p.mean(0)-gp.mean(0)) for p in P])); p=P[j]
            r=[poly_iou(p,gp,gray.shape)]; gates=[]
            for pt in (None,p.mean(0)):
                t=time.time();m,s=decode(emb,W,H,aabb(p),pt);td.append(time.time()-t)
                f=fit(m); r.append(poly_iou(f,gp,gray.shape) if f is not None else r[0]); gates.append(poly_iou(f,p,gray.shape) if f is not None else 0)
            res.append(r+gates)
        print(n,len(G),'enc %.1fs'%te[-1],flush=True)
    R=np.array(res)
    print('n',len(R),'orig %.3f box %.3f box+pt %.3f'%tuple(R[:,:3].mean(0)))
    print('enc mean %.2f s, dec mean %.3f s (n=%d)'%(np.mean(te),np.mean(td),len(td)))
    for k,col in ((1,3),(2,4)):
        for t in (0.6,0.7,0.8):
            use=np.where(R[:,col]>=t,R[:,k],R[:,0]); print('variant',k,'gate',t,'iou %.3f used %.2f'%(use.mean(),(R[:,col]>=t).mean()))
    np.save(f'tmp/EAI-LABEL/refine-C/res_{tag}.npy',R)
