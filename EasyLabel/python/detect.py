"""EasyLabel in-process detection bridge.

The C# side calls detect_json(request_json) through pythonnet. Keeping the
boundary JSON-shaped lets a later subprocess backend reuse the same contract.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

_MODEL_CACHE: dict[str, Any] = {}


def detect_json(request_json: str) -> str:
    request = json.loads(request_json)
    model_path = str(request["model"])
    image_paths = list(request.get("images") or [])
    task = str(request.get("task") or "detect").lower()
    conf = float(request.get("conf", 0.25))
    iou = float(request.get("iou", 0.7))
    classes = request.get("classes")

    model = _load_model(model_path)
    results = model.predict(
        source=image_paths,
        conf=conf,
        iou=iou,
        classes=classes,
        verbose=False,
    )

    payload = []
    for image_path, result in zip(image_paths, results):
        height, width = _result_shape(result)
        payload.append(
            {
                "image": image_path,
                "width": width,
                "height": height,
                "detections": _detections_for_result(result, task),
            }
        )

    return json.dumps(payload)


def _load_model(model_path: str) -> Any:
    key = str(Path(model_path).resolve())
    model = _MODEL_CACHE.get(key)
    if model is None:
        from ultralytics import YOLO

        model = YOLO(model_path)
        _MODEL_CACHE[key] = model
    return model


def _result_shape(result: Any) -> tuple[int, int]:
    shape = getattr(result, "orig_shape", None)
    if shape is None:
        return 0, 0
    return int(shape[0]), int(shape[1])


def _detections_for_result(result: Any, task: str) -> list[dict[str, Any]]:
    if task == "obb" and getattr(result, "obb", None) is not None:
        return _obb_detections(result)
    if task == "segment" and getattr(result, "masks", None) is not None:
        return _segment_detections(result)
    return _box_detections(result)


def _box_detections(result: Any) -> list[dict[str, Any]]:
    boxes = getattr(result, "boxes", None)
    if boxes is None or boxes.xyxy is None:
        return []

    points = boxes.xyxy.cpu().numpy().tolist()
    classes = boxes.cls.cpu().numpy().tolist()
    confidences = boxes.conf.cpu().numpy().tolist()
    return [
        _detection(
            result,
            int(class_id),
            float(confidence),
            "rectangle",
            [[float(x1), float(y1)], [float(x2), float(y2)]],
        )
        for (x1, y1, x2, y2), class_id, confidence in zip(points, classes, confidences)
    ]


def _obb_detections(result: Any) -> list[dict[str, Any]]:
    obb = result.obb
    corners = obb.xyxyxyxy.cpu().numpy().reshape(-1, 4, 2).tolist()
    classes = obb.cls.cpu().numpy().tolist()
    confidences = obb.conf.cpu().numpy().tolist()
    return [
        _detection(
            result,
            int(class_id),
            float(confidence),
            "rotation",
            [[float(x), float(y)] for x, y in corner_set],
        )
        for corner_set, class_id, confidence in zip(corners, classes, confidences)
    ]


def _segment_detections(result: Any) -> list[dict[str, Any]]:
    masks = result.masks
    polygons = masks.xy or []
    boxes = getattr(result, "boxes", None)
    classes = boxes.cls.cpu().numpy().tolist() if boxes is not None else [0] * len(polygons)
    confidences = boxes.conf.cpu().numpy().tolist() if boxes is not None else [0.0] * len(polygons)
    return [
        _detection(
            result,
            int(class_id),
            float(confidence),
            "polygon",
            [[float(x), float(y)] for x, y in polygon.tolist()],
        )
        for polygon, class_id, confidence in zip(polygons, classes, confidences)
    ]


def _detection(
    result: Any,
    class_id: int,
    confidence: float,
    shape_type: str,
    points: list[list[float]],
) -> dict[str, Any]:
    names = getattr(result, "names", {}) or {}
    return {
        "classId": class_id,
        "label": str(names.get(class_id, class_id)),
        "confidence": confidence,
        "shapeType": shape_type,
        "points": points,
    }


def main() -> int:
    import sys

    request_json = sys.stdin.read()
    sys.stdout.write(detect_json(request_json))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
