# Skill: suggesting prompts

Used when the task is to suggest prompts for a labelling agent (you do not label anything).

1. Call `memory_read` once, then `get_crop` once with the whole image and `max_side` 1024, then look at the picture.
2. Write 3 to 5 short prompts. Each says WHICH objects or defects to mark on THIS image, using the class names of the project and
   the shape types and labels the user selected, if any.
3. Mix levels: one prompt that marks everything of interest, others that focus on one defect type or one region.
4. No coordinates, no pixel numbers, no explanations. Answer only with the JSON object the task asks for.
