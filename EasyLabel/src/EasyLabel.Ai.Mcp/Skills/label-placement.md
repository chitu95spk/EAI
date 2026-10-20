# Skill: placing labels

1. Type per object: `rectangle` for an upright part, `oriented_rectangle` when the task asks for rotated boxes / OBB / 'hình chữ nhật xoay' or the part is tilted, `polygon` for an irregular outline. A type named by the task is the only one to use.
2. One shape per object (no split, no merge); the box hugs the object; an object cut by the image border keeps the box it really has, which may extend past the border (at least 20% of the box must stay inside the image, otherwise it is rejected).
3. Never repeat a shape of the image facts. Mark a doubtful object only when the task asks for recall.
