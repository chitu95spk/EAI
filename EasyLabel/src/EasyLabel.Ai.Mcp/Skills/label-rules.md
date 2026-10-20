# Skill: label rules

1. Prefer a label from `classNames` returned by `get_image_info` and copy it exactly. When the task names another label
   (for example "mark every Pad"), use that name exactly as the user wrote it: it is created as a new label when the user
   accepts the shapes. Never map a new object type onto NG or OK just to fit the list.
2. Call `propose_shapes` once at the end with all shapes together. Nothing is saved until you do.
3. If a shape is rejected, read the reason, fix that item and call `propose_shapes` again with the same `id`
   (an item with an existing `id` replaces the earlier proposal).
4. If you found nothing, call `propose_shapes` with `shapes: []` and say "none found" in your final answer.
5. Keep tool calls minimal: every crop costs tokens. Prefer one overview crop, one crop per uncertain region.
6. Add a short `note` to a shape only when it explains an uncertain decision.
7. Your final answer is a one or two line summary: how many shapes proposed, per label.
