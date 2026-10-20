# EAI-5 Inspect review: ACCEPT (fixes applied)
| # | Sev | Decision | Reason |
|---|---|---|---|
| 1 | minor failed run wiped previous model results and named a model that never ran | fix | results/model only replaced when the run returned results |
| 2 | minor non-string path in inspect.json threw an uncaught cast error (list silently empty) | fix | rethrown as InvalidDataException |
| 3 | minor Persist writes synchronously on the UI thread | accepted-risk | small lists |
| 4 | minor ScanFolder has no cancel/symlink-cycle guard | accepted-risk | bounded by path length |
| 5 | note parity test only at FloorThreshold | accepted | intended difference |
Unverified: vi smoke (harness), non-active candidate selection and 'Inspect with the new model' in UI, CSV via UI, cancel during Run all, drag-drop, vi column clipping.
