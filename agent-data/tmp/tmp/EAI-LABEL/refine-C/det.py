import sys;sys.path.insert(0,'tmp/EAI-LABEL/refine-C')
import sam,cv2,numpy as np,json
n='MODEL_FAN_XOAN_PO_1_134609_598_NG'
g=cv2.imread(f'E:/EAI_TEST/{n}.png',0);H,W=g.shape
P=[np.array(s['points']) for s in json.load(open(f'artifacts/EAI-LABEL/s2-eco-par3-run2/project/{n}.json'))['shapes']]
e=sam.encode(g)
print(float(e[2].sum()),[ (int(sam.decode(e,W,H,sam.aabb(p))[0].sum())) for p in P])
