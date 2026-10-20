# Skill: read the image

1. The image facts block of this prompt gives the size, class names and existing shapes: do not call `get_image_info` or `list_shapes` for them (only when the block is missing or you need an `fp`).
2. `get_crop` with no arguments shows the whole image downscaled: an overview only.
3. `get_crop` with `x`, `y`, `width`, `height` (image pixels) zooms in. Each result text gives `scale` and the back-mapping: image_x = x + crop_x / scale, image_y = y + crop_y / scale.
4. `grid_crop` (`x`, `y`, `w`, `h`) draws a grid whose tick labels are original image pixels.
5. `render_overlay` shows shapes: existing `e<id>`, proposals `p:<id>`.
6. Every coordinate you send is in original image pixels, never crop pixels. Independent calls go in the same turn.
