---
name: feedback-git-line-level-staging-verify-head
description: "Dirty-worktree repo: line-level `git apply --cached --unidiff-zero` staging broke commits twice; build committed HEAD in a clean worktree to verify"
metadata:
  node_type: memory
  type: feedback
  originSessionId: a8090c57-2ecd-4188-b5e2-43c5fb31a8db
  modified: 2026-09-29T18:20:07.526Z
---

The worktree here is permanently dirty (500+ files, big pending edits in shared files like tests/BeeCore.Persistence.Tests/Program.cs, CODEX_HISTORY.md, *.csproj, ProjectContainerStore.cs). I committed V148-V154 by staging only my hunks, and that produced real defects: (1) `git apply --cached --unidiff-zero` placed a 137-line test hunk AFTER the file's closing braces (commit 2a4c289 did not compile; fixed in aedda81); (2) a line-level csproj hunk produced a malformed BeeInterface.csproj (cd077ae); (3) files that committed code depends on (LabelInspectionRuntime.cs, ToolLabelParameters.cs) had never been committed. Verifiers built the WORKING TREE, so none of this showed until a clean-checkout build was made.

**Why:** working-tree builds prove nothing about what was committed.
**How to apply:**
- For shared dirty files build the staged content explicitly (HEAD blob + insert the exact text blocks, then `git hash-object -w` + `git update-index --cacheinfo`) instead of patch hunks; for pure appends (CODEX_HISTORY.md) use HEAD + section. After staging, sanity-check: same method-name set as the working file, brace/class structure intact, `git diff HEAD` of the file shows only the user's pre-existing dirt.
- After a series of commits, have a verifier build HEAD in a detached `git worktree` OUTSIDE the repo (needs a read-only `packages` junction, PLC_Communication `-t:Restore`; remove junctions with `cmd /c rmdir` BEFORE removing the worktree; "Filename too long" needs robocopy /MIR from an empty dir). This is a contract-authorised verification task (verifier agents refuse to run without an authorization block from a `.codex/task-contract.<ID>.toml`, so write one first).
- The user pre-approved checkpoint commits of their pending Segment AI work ("tôi commit checkpoint giúp") but NOT other features' dirt (e.g. EnsureProgramSidecarAuthority in ProjectContainerStore.cs stays uncommitted).
- Never commit artifacts/V149..V15x scratch (GBs, untracked).
See [[segai-v2-trainer-facts]], [[persistence-tests-hijack-bin-release]], [[feedback_delegate_to_agents]].
