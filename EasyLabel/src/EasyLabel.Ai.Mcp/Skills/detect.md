# Skill: detect

Find objects and propose NEW shapes in ONE pass.

1. Use the image facts of this prompt (size, classes, existing shapes). Skip an object that already has a shape.
2. Look at the picture you already have (the attached preview, else ONE `get_crop` overview). A preview carries a numbered coordinate grid: read the position of every object from the grid numbers (preview pixels, interpolate between lines) instead of estimating it; the centre of an object is the middle of its whole length. Never read exact coordinates from a downscaled picture without converting them with its scale.
3. When the prompt lists numbered SAM2 candidates (outlines already drawn on the picture), do ONE `pick_candidates` call: each object is the list of candidate numbers that together form it (a screw = head + shaft), leave out the ones the task does not ask for, then finish. Use `detect_points` only for an object that has no candidate.
3b. Many similar objects (more than 3) and no candidates: make ONE `detect_points` call with the centre `x`,`y` of EVERY object (read from the picture you have, or from the blob table of the prompt when it lists the objects; if it is a downscaled preview pass its `scale`, otherwise image pixels; add a rough `box` only when objects touch). It runs SAM2 for all of them, fits the rotated box and proposes everything. Then finish: do NOT call `propose_shapes` again, do not re-check, do not zoom.
4. Only a few objects, or objects `detect_points` reported as failed: `sam2_segment` with a `box`, or `grid_crop` on the region, then `propose_shapes` (action `add`, no `target`); `[]` when none. Resend a rejected item with its `id`.
5. Re-check with `render_overlay` only when the effort is high, xhigh or max, or the accuracy is hard.
6. Use class names exactly (or the label the task names). Skip doubtful objects. Final answer: one line, shapes per label.
