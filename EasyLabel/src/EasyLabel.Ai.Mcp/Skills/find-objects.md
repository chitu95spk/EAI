# Skill: find objects

1. Per region call `sam2_segment` with a `box` [x1, y1, x2, y2] in image pixels (`points` with `positive` true/false fix a mask). Several regions = several calls in one turn.
2. Prefer `fromSam2` in `propose_shapes`; add `shapeType` `oriented_rectangle` for a rotated box (the app fits it to the outline; `rectangle` gives the bounds).
3. SAM2 unavailable: type the shape. `rectangle` = 2 axis-aligned corners. A tilted part = `oriented_rectangle` with 8-16 outline points in order (or its 4 corners); never send a tilted part as `rectangle`.
4. Never invent labels.
