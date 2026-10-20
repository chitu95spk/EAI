import sys, os, shutil
from ultralytics import YOLOWorld
classes = [c.strip() for c in sys.argv[1].split(",") if c.strip()]
m = YOLOWorld("yolov8s-worldv2.pt")
m.set_classes(classes)
p = m.export(format="onnx", imgsz=640, nms=False, simplify=False, opset=17)
os.makedirs("out/yolo-world", exist_ok=True)
shutil.copy(p, "out/yolo-world/yolov8s-worldv2.onnx")
open("out/yolo-world/yolov8s-worldv2.classes.txt", "w", encoding="utf-8").write("\n".join(classes) + "\n")
print("EXPORTED", p, classes)
