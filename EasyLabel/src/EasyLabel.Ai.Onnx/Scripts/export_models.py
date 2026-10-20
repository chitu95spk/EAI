# Re-export YOLO-World with your own class list (the list is baked into the ONNX file).
# Run inside a Python 3.12 venv that has: torch torchvision (cpu), ultralytics, onnx, onnxruntime.
#   python export_yolo_world.py "scratch,dent,crack,fan"
# Output: out/yolo-world/yolov8s-worldv2.onnx + .classes.txt -> copy both into %LOCALAPPDATA%\EasyAI\models\yolo-world
# Licence: Ultralytics code and weights are AGPL-3.0 (download/export only; do not redistribute).
# SAM2.1 tiny (Apache-2.0) was exported with samexporter 0.5.0 + facebookresearch/sam2:
#   python -m samexporter.export_sam2 --checkpoint sam2.1_hiera_tiny.pt --output_encoder encoder.onnx --output_decoder decoder.onnx --model_type sam2.1_hiera_tiny --opset 17
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
