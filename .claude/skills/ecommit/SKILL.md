---
name: ecommit
description: Commit only what THIS session produced and has not yet committed, grouped per task contract (or [process] for skills/docs/rules), on main following CLAUDE.md section 12. Files owned by other sessions stay untouched for them to commit. Use when the user types /ecommit or says commit, or a session task is complete and checks passed. Never pushes, tags or opens a PR.
---

# EasyVision session commit

Scope: this session's own uncommitted work. Everything else that is dirty (other sessions, Codex, tools) is left in place
for its owner. Preconditions (CLAUDE.md 12.1): current branch is `main` (never switch), no explicit no-commit instruction,
required checks passed or honestly reported `unverified` to the user. Code that needs build evidence which was not
produced is not committed; report that gap instead.

## 1. Plan (read-only)
`powershell -NoProfile -File .claude/skills/ecommit/scripts/plan-session-commit.ps1 -SessionId <guid> [-AlsoFile a,b]`
(`<guid>` = last folder of the scratchpad path in the system prompt, as in `esum`.)
It intersects the files this session edited (Edit/Write calls in the transcript) with `git status`, and prints:
- groups: one per task contract this session edited or whose `in_scope` covers the files (committed or not), `process`
  for skills/docs/rules/tooling scripts, and `UNSCOPED-CODE` for code with no contract (ask the user for the task ID);
- skipped never-commit paths (`bin/ obj/ x64/ Debug/ Release/ tmp/ artifacts/ .vs/ *.pdb *.user settings.local.json`);
- OVERLAP warnings: a file already staged by someone else, or inside the scope of another uncommitted contract;
- the count and top folders of other dirty files that are NOT attributed to this session.
Edits made through Bash/Python or by subagents are invisible to the transcript: compare your own record of such edits with
the "NOT attributed" list and pass the ones that are yours with `-AlsoFile`. Never claim a file that you did not change.

## 2. Decide per group
- Contract group: it needs the verdict from `etest` (or an `unverified` statement) and the contract's `authorization.commit=true`.
  Subject `[<ID>] <summary, 60 chars max>`.
- `process` group: static checks only (`git diff --check`); subject `[process] <summary>`.
- OVERLAP files: do not commit; tell the user which file and why, and let the other session or the user resolve it.
- Mixed hunks in one file: no line-level staging (it broke commits before). If the other hunks belong to another task
  and the user wants this task committed anyway, use the blob method in "Mixed-hunk recipe" below, never
  `git apply --unidiff-zero` (zero-context hunks landed inside a method body, L008); otherwise ask for a scope decision.
- List every file the task changed (`git status`, the contract `in_scope`) BEFORE the first commit: a task commit that
  omits a caller or callee leaves HEAD uncompilable (L008).

## 3. Commit (explicit pathspecs, one commit per group)
1. `git diff --check -- <paths>` for the group's paths.
2. `git add -- <paths>` for exactly those paths, then `git commit -m "<subject>" -m "<body>" -- <paths>`. The `--` pathspec
   keeps files staged by others out of the commit. Never `git add .` / `-A`, never `--no-verify`.
3. End the message with the attribution line the session requires.
4. `git show --stat HEAD`; confirm only the group's files are in it. Report hash, files, and what stayed behind (counts).
Do not push, tag or open a PR. Commit permission grants no build, test, push or deploy permission.

## Temporary-index commit (keeps pre-staged renames out)
When the real index holds other people's staged changes, commit through a copy of the index:
`IDX=$(git rev-parse --git-path index); export GIT_INDEX_FILE=tmp/<task>/index.tmp; cp "$IDX" "$GIT_INDEX_FILE"; git reset -q;
git add -- <paths>; git diff --cached --name-only; git commit ...; unset GIT_INDEX_FILE`.
Afterwards ALWAYS run `git reset -q HEAD -- <paths>` on the real index, new files included: otherwise the real index
still holds the old blob (or lacks the new file) and a later plain `git commit` silently reverts it or shows a staged
deletion (L008). Check `git diff HEAD --name-only -- <paths>` is empty and `git diff --cached --name-only` is unchanged.
Do not hide that reset's stderr (L027): a stale `.git/index.lock` makes it fail silently and leaves the index wrong. Right after it run
`git status --porcelain -- <paths>` (must show nothing for committed paths). Remove `.git/index.lock` only when it is empty, older than a
minute and no git process runs, then repeat the reset.

## Mixed-hunk recipe (only when the user approves committing one task out of a shared file)
1. `git show <parent>:<path>` into a temp file; apply each intended change with an anchored, unique text replacement
   (assert the anchor occurs exactly once); write the result under `tmp/<task>/`.
2. Check that every line of the task appears once in the result and nothing else changed (`diff` against the parent).
3. `git hash-object -w` the file, `git update-index --cacheinfo 100644,<sha>,<path>` in the temp index, commit.
4. Verify the committed blob by reading the touched regions (`git show HEAD:<path> | sed -n ...`) before reporting.
