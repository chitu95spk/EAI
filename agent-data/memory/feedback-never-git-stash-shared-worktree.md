---
name: feedback-never-git-stash-shared-worktree
description: "Never run git stash here; the worktree holds hundreds of other sessions' dirty files and a failed pop leaves tracked files reverted"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 25496681-3c1c-4779-9622-8df1cccdb487
  modified: 2026-10-13T02:39:24.380Z
---

Never run `git stash` (or any reset/checkout that rewrites the worktree) in this repo.

**Why:** 2026-10-13 a stray `git stash -q` in an EAI-LABEL session stashed ~180 tracked files of other sessions; `git stash pop` restored all but 3 csproj files (BeeCore/BeeGlobal/BeeInterface), which had to be recovered by hand with `git show stash@{0}:<path>`. A stash entry (stash@{0}, "WIP on main: 13f172eb0") was left behind as a safety copy.

**How to apply:** to compare against a baseline use `git diff`, `git show <rev>:<path>` or a clean worktree; never stash. Also: writing python via a bash heredoc corrupts `\n`/`\t` escapes in this harness; write the script with the Write tool, then run it.
