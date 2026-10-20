# Skill: judge

Use this when the task asks whether given shapes are correct (a verdict, not a new labelling).

1. The image facts of this prompt list the shapes (`e<id>`); call `list_shapes` only when you need points or `fp`.
2. Look at them with `render_overlay` (include `existing`) on the region of each shape; use `grid_crop` for a closer look at its edges.
3. For each shape decide: correct, wrong label, wrong outline, duplicate or not an object. Use `measure` for sizes and overlaps when it helps.
4. Answer in your closing message, one line per shape: `id`, verdict and a short reason.
5. Do NOT call `propose_shapes` and do not change any shape unless the task explicitly asks for edits. If it does, follow the `check` skill.
