import sys,os;sys.path.insert(0,'tmp/EAI-LABEL/refine-C')
import onnxruntime as ort,cv2,numpy as np
mode=sys.argv[1]
so=ort.SessionOptions(); so.log_severity_level=3
if mode=='t1': so.intra_op_num_threads=1
if mode=='t2': so.intra_op_num_threads=2
if mode=='noopt': so.intra_op_num_threads=2; so.graph_optimization_level=ort.GraphOptimizationLevel.ORT_DISABLE_ALL
if mode=='nomem': so.intra_op_num_threads=2; so.enable_mem_pattern=False; so.enable_cpu_mem_arena=False
s=ort.InferenceSession('C:/Users/chitu/AppData/Local/EasyAI/models/sam2/encoder.onnx',so,providers=['CPUExecutionProvider'])
g=cv2.imread('E:/EAI_TEST/MODEL_FAN_XOAN_PO_1_134609_598_NG.png',0)
rgb=cv2.cvtColor(g,cv2.COLOR_GRAY2RGB)
x=cv2.resize(rgb,(1024,1024)).astype(np.float32)/255
x=((x-[0.485,0.456,0.406])/[0.229,0.224,0.225]).transpose(2,0,1)[None].astype(np.float32)
print(mode,float(x.sum()),float(s.run(None,{'image':x})[2].sum()))
