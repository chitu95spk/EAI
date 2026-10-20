# EAI-DATA commit B review: ACCEPT (fixes applied)
| # | Sev | Decision | Reason |
|---|---|---|---|
| 1 | major export folder allowed inside the project (later scans list copies as images) | fix | ValidateOutput refuses any output inside the project root; test replaced |
| 2 | minor YOLO data.yaml pointed to a missing split | fix | train:/val: lines omitted when the split has no image; test added |
| 3 | minor junctions/symlinks not resolved | accepted-risk | nothing overwritten |
| 4 | minor mask PNG overlaps overwrite silently; decodes every image | accepted-risk | documented |
Unverified: each format through the UI, vi layout, cancel in UI, large projects.
