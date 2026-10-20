# Skill: repeat

Use this when the system prompt holds an experience block: the shapes of the first labelled image, as text only (label, shape type, relative size, rounded points). You never see that image.

1. Treat the block as an example of what the user wants, not as coordinates for this image. Positions and sizes may differ.
2. Use the image facts of this prompt; skip objects that already have a shape.
3. Find the same kind of objects here: one `get_crop` overview, then per object `grid_crop` or `sam2_segment` with a `box` (several calls in one turn). Back-map with the scale in the tool text.
4. Match label and shape type to the example (`fromSam2` with `oriented_rectangle` for rotated boxes), then send ONE `propose_shapes` (action `add`).
5. If this image has no such object, send `[]`. Do not copy points from the example.
