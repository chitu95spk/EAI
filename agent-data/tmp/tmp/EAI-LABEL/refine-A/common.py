import json,glob,os,sys
import numpy as np,cv2
ROOT='E:/Code/EasyVision_Unisen - NoService/'
def poly_iou(a,b,shape=(2048,2448)):
    a=np.asarray(a);b=np.asarray(b)
    x0,y0=np.floor(np.minimum(a.min(0),b.min(0))).astype(int)-2; x1,y1=np.ceil(np.maximum(a.max(0),b.max(0))).astype(int)+2
    h,w=y1-y0,x1-x0
    m1=np.zeros((h,w),np.uint8);m2=np.zeros((h,w),np.uint8)
    cv2.fillPoly(m1,[np.round((a-[x0,y0])).astype(np.int32)],1);cv2.fillPoly(m2,[np.round(b-[x0,y0]).astype(np.int32)],1)
    i=(m1&m2).sum();u=(m1|m2).sum();return i/u if u else 0
def load_shapes(f):
    return [np.array(s['points'],np.float32) for s in json.load(open(f))['shapes'] if len(s['points'])==4]
def real_gt_files(): return sorted(glob.glob('E:/EAI_TEST/*.json'))[:18]
RUNS={'run2':ROOT+'artifacts/EAI-LABEL/s2-eco-par3-run2/project','full':ROOT+'artifacts/EAI-LABEL/s2-eco-par3-full/project'}
PSEUDO=ROOT+'artifacts/EAI-LABEL/s0-baseline/project'
def img(n): return cv2.imread(f'E:/EAI_TEST/{n}.png',0)
