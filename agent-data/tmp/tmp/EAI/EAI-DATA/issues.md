# EAI-DATA commit A review: FIX_REQUIRED -> fixed
| # | Sev | Decision | Reason |
|---|---|---|---|
| 1 | major notice lacked the one-way warning and backup path; body English only | fix | MigrationOneWay sentence (en+vi) appended to the notice |
| 2 | minor images without LabelMe JSON lose mode in live layout | fix | skipped count shown in the Summary; mode kept in backup/seg |
| 3 | minor migration rewrites the LabelMe JSON (reformat; imageData re-encode) and no test round-trips unknown keys + imageData | accepted-risk | atomic write; ExtensionData keeps unknown keys; owed test |
| 4 | minor stale doc comments | fix | updated |
| 5 | note SaveEditedShapes/App Write strip imageData when embed off | accepted-risk | pre-existing |
| 6 | note a.jpg and a.png share one meta/cache stem | accepted-risk | legacy behaviour |
Coordinator changes: backups gathered in .easyai/backup/<utc>; ReadReviewStatus via SidecarIO; real-project migration + idempotent second open verified. Owed: kill-mid-migration, read-only folder, thousands-of-images timing.
