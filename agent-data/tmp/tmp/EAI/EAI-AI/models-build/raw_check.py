import numpy as np, onnxruntime as ort, cv2
img = cv2.imread(r"E:\Training\FANXOAN\images\001_FAN3_S98.6_OK_OBB+110.5_A+110.5.png")
h, w = img.shape[:2]; s = 640/max(h, w)
r = cv2.resize(img, (round(w*s), round(h*s)))
canvas = np.full((640, 640, 3), 114, np.uint8)
dy, dx = (640-r.shape[0])//2, (640-r.shape[1])//2
canvas[dy:dy+r.shape[0], dx:dx+r.shape[1]] = r
x = canvas[:, :, ::-1].transpose(2, 0, 1)[None].astype(np.float32)/255
s_ = ort.InferenceSession("out/yolo-world/yolov8s-worldv2.onnx", providers=["CPUExecutionProvider"])
o = s_.run(None, {"images": x})[0][0]
sc = o[4:].max(0); i = sc.argmax()
names = open("out/yolo-world/yolov8s-worldv2.classes.txt").read().split()
print("max class score", sc.max(), names[o[4:, i].argmax()], "box cxcywh", o[:4, i], "count>=0.25", (sc >= 0.25).sum(), "count>=0.1", (sc >= 0.1).sum())
