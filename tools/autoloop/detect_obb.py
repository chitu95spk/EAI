"""Detect oriented boxes (YOLO-OBB) on every image of a folder; used by the EasyAI "Tu hoc" window.

Prints one JSON object per line on stdout (flushed), so the app can show results while the run is going:
  {"type":"start","total":N,"names":{...}}
  {"type":"image","index":i,"image":path,"overlay":path,"count":n,"gt":m|null,"ms":t,"confs":[...]}
  {"type":"done","total":N,"detections":D,"gt":G|null,"images_exact":K|null,"ms_mean":t}
  {"type":"error","message":...}
GT comes from a YOLO label file at <images dir with 'images' replaced by 'labels'>/<name>.txt when it exists; it is only counted
(objects per image), never used to change the prediction. The weights and images are read, nothing else is modified.
"""
import argparse
import json
import sys
import time
from pathlib import Path

EXT = {".bmp", ".png", ".jpg", ".jpeg"}


def emit(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def gt_count(img: Path):
    parts = list(img.parts)
    for i in range(len(parts) - 1, -1, -1):
        if parts[i].lower() == "images":
            parts[i] = "labels"
            lab = Path(*parts).with_suffix(".txt")
            if lab.exists():
                return sum(1 for l in lab.read_text(encoding="utf-8").splitlines() if l.strip())
            return None
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--weights", required=True)
    ap.add_argument("--dir", required=True)
    ap.add_argument("--out", required=True, help="folder for overlay images")
    ap.add_argument("--conf", type=float, default=0.25)
    ap.add_argument("--imgsz", type=int, default=1024)
    ap.add_argument("--max", type=int, default=0, help="0 = all images")
    a = ap.parse_args()
    try:
        import cv2
        import numpy as np
        from ultralytics import YOLO

        folder = Path(a.dir)
        imgs = sorted(p for p in folder.iterdir() if p.suffix.lower() in EXT)
        if a.max:
            imgs = imgs[: a.max]
        if not imgs:
            emit({"type": "error", "message": "No images in " + str(folder)})
            return 2
        out = Path(a.out)
        out.mkdir(parents=True, exist_ok=True)
        model = YOLO(a.weights)
        emit({"type": "start", "total": len(imgs), "names": {str(k): v for k, v in model.names.items()}})
        det_total = gt_total = exact = gt_known = 0
        ms_sum = 0.0
        for i, p in enumerate(imgs):
            t0 = time.perf_counter()
            r = model.predict(str(p), conf=a.conf, imgsz=a.imgsz, verbose=False)[0]
            ms = (time.perf_counter() - t0) * 1000.0
            frame = cv2.imread(str(p), cv2.IMREAD_COLOR)
            confs = []
            if r.obb is not None and len(r.obb) > 0:
                polys = r.obb.xyxyxyxy.cpu().numpy().astype(np.int32)
                confs = [round(float(c), 3) for c in r.obb.conf.cpu().numpy()]
                for poly in polys:
                    cv2.polylines(frame, [poly.reshape(-1, 1, 2)], True, (0, 220, 255), 3, cv2.LINE_AA)
            count = len(confs)
            cv2.putText(frame, f"{count}", (20, 70), cv2.FONT_HERSHEY_SIMPLEX, 2.0, (0, 220, 255), 4, cv2.LINE_AA)
            ov = out / (p.stem + "_det.jpg")
            cv2.imwrite(str(ov), frame, [cv2.IMWRITE_JPEG_QUALITY, 88])
            gt = gt_count(p)
            det_total += count
            ms_sum += ms
            if gt is not None:
                gt_known += 1
                gt_total += gt
                exact += int(gt == count)
            emit({"type": "image", "index": i, "image": str(p), "overlay": str(ov), "count": count, "gt": gt, "ms": round(ms, 1), "confs": confs})
        emit({"type": "done", "total": len(imgs), "detections": det_total, "gt": gt_total if gt_known else None,
              "images_exact": exact if gt_known else None, "ms_mean": round(ms_sum / len(imgs), 1)})
        return 0
    except Exception as ex:  # report to the app instead of dying with a bare traceback
        emit({"type": "error", "message": f"{type(ex).__name__}: {ex}"})
        return 1


if __name__ == "__main__":
    sys.exit(main())
