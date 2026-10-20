# Skill: detect

Find objects and propose NEW shapes in ONE pass.

1. Use the image facts of this prompt (size, classes, existing shapes). Skip an object that already has a shape.
2. Turn 1: one `get_crop` overview. Never read exact coordinates from it.
3. Turn 2: for EVERY object make all independent calls together: `grid_crop` on its region, or `sam2_segment` with a `box`. Never propose from the overview alone. Back-map with the scale and offset in each result text (`grid_crop` ticks are already image pixels).
4. Build the shapes (`find-objects`, `label-placement`) and send ONE `propose_shapes` with all of them (action `add`, no `target`); `[]` when none. Resend a rejected item with its `id`.
5. Re-check with `render_overlay` only when the effort is high, xhigh or max, or the accuracy is hard.
6. Use class names exactly (or the label the task names). Skip doubtful objects. Final answer: one line, shapes per label.
