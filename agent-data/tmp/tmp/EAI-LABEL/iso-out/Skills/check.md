# Skill: check

Review the EXISTING shapes of the image and propose fixes in ONE pass.

1. The image facts list the shapes (`e<id>`, label, type, [x,y,w,h]); use the `id` as `target`. Call `list_shapes` only when you need an `fp` or the exact points.
2. Turn 1: make these independent calls together: `render_overlay` (include `existing`), `measure` (duplicates, shapes inside shapes, odd sizes), and `grid_crop` on each doubtful shape.
3. Decide per shape: keep it (propose nothing), correct it, or remove it. Do not touch correct shapes.
4. Send ONE `propose_shapes`: `target` = shape `id`, `action` `replace` with corrected `points` (or `fromSam2`) or `remove` with no points. One item per target, each with a short `note`. A shape the image really misses: `action` `add`, no `target`. Send `[]` when nothing needs a fix.
5. Call `render_overlay` (include `both`) to re-check only when the effort is high, xhigh or max, or the accuracy is hard.
