# Agent lessons (append-only log, written in English)

Purpose: mistakes, issues and lessons from past sessions so later sessions are faster and cheaper.
Maintained by `/eskill`. Read only the "Top rules" section at session start; grep the log by keyword when needed.
Do not read the whole file.

## Top rules (max 15 lines, merge or drop stale ones)

1. Skills are per runtime: Claude reads `.claude/skills/`, Codex reads `.agents/skills/`. Before editing a skill, confirm which copy the target runtime loads; the two `eroute` copies differ. (L001)
2. Write multi-file or quote-heavy content with the Write tool, not a bash heredoc, and edit PowerShell/regex files with Edit or Write, not Python string replace (a stray apostrophe aborted a 5-file command; backslashes and `$(` were altered 3 times). For files with mixed CRLF/LF split by `
`, never `
`, and check `wc -l` after a scripted edit. Scripted .NET edits: absolute paths ([IO.File] ignores the PowerShell location), throw when an anchor matches != 1, keep the BOM, and check `git diff -U0 | grep "@@ -1"` before committing. (L002, L003, L029, L034)
3. A subagent's "fixed / verified / rendered" is a lead, not evidence: build and run the harness yourself, check that the artifacts it names exist, require an API evidence list, forbid stubs in the brief. When the plan says "like X", read how X hosts and manages the control, not only its class. (L021, L023)
4. Cap tool output before it prints (`| head`, `-Limit`, summary views) and downscale/crop images before Read: an uncapped 22 KB listing or a 60-80 KB screenshot is re-sent as cache-read on every later request. Use `tools/ev-thumb.ps1 -Path <png>` (<=800 px JPEG) and Read the `.thumb.jpg`, never the artifact PNG (L036). Never `grep -r .` at the repo root; name the folder. (L004, L012, L025, L030)
5. Report only counts taken from the full tool output; a number read from truncated output was wrong (72 vs 81). (L005)
6. Concurrent sessions or Codex may write the same checkout: use the transcript, not file mtimes, to attribute work to this session. (L006)
7. Never use `run_in_background` or the session scratchpad for data that matters: the harness writes it under `%TEMP%`, against the project-local temp rule (CLAUDE.md 0.2b). Run foreground, or redirect into `tmp/`; never pipe a harness into `Select -First`/`head` (closing the pipe kills it before it saves files). (L007, L033)
8. Commit mixed-hunk files by rebuilding the blob from the parent with anchored text replacements and diffing it with the working file. Allowed alternative (V180/V183): whole hunks of `git diff -U0`, foreign hunks dropped, `git apply --cached --unidiff-zero` into a TEMP index (`GIT_INDEX_FILE`), but only after `git diff --cached` shows no foreign hunk; never against the real index. After a temp-index commit run `git reset -q -- <paths>` and confirm the remaining working diff is only the foreign hunk. HEAD-only compile stays unverified. (L008, L017)
9. Implementer has no Bash: tell it to fully qualify namespaced types in tests, and tell the verifier to report ALL build errors of a failed build. (L009)
10. UI tests: PerformClick is a no-op in a collapsed section; assert the model after each click, start from a non-default state, and look at one image of the target state before accepting a render PASS. (L010)
11. Route code changes through eroute and the easyvision-* agents; root edits only for trivial fixes. (L011)
12. Before the first commit list the calls from the new code into other tasks' dirty files (one missing dependency would have left HEAD uncompilable); write harnesses hermetic (tmp root override, passes twice, `git status` clean); keep BOM and LF (HEAD blobs are LF, the working tree CRLF, autocrlf=true); in PowerShell set `$ErrorActionPreference='Continue'` around git. (L022, L024)
13. Craft OCR: Scan and Check must detect the same way (CRAFT character boxes, then line clustering, then mapping); Check only adds barcode quality when enabled. Test Check by moving/scaling the Area over a real taught frame (`--v177-ikea`), not only on ideal fixtures. (L015)
14. Probe before claiming a service state: on a hook "DOWN", run curl livez and ToolSearch the memory tools once, and re-probe when the user mentions it; never repeat a stale status. (L016)
15. Numeric acceptance comes from a measured ground truth (measure the image first); a harness for ROI-local coordinates must include offset and rotated ROIs; parse a TOML/PS1 after any scripted edit. (L017, L018) App-vs-offline mismatch: read the app's own log first, replay with the app's real frame type (BGRA), attach FirstChanceException (L044).

## Entry format

`### L<nnn> | <YYYY-MM-DD> | <mistake|issue|cost|win> | <area> | session <guid>`
- What happened (facts, with `path:line` or command when useful)
- Cost (tokens, tool calls, minutes, rework) - say "unmeasured" if unknown
- Root cause
- Prevention rule / fix applied (and where: skill, CLAUDE.md, agent file) or `proposed`
- Status: applied | proposed | rejected

## Log

### L001 | 2026-10-03 | mistake | skills | session 78436208-dd30-47b8-a368-e658f79f32dc
- Added the token-budget gate (steps 4a/4b/7a) to `.agents/skills/eroute/SKILL.md` (Codex copy) although Claude loads `.claude/skills/eroute/SKILL.md`; found only later via `diff`.
- Cost: one extra read/diff/move cycle (about 6 tool calls); the rule was not active for Claude in the meantime.
- Root cause: assumed the `.agents` skill was the live one; never checked which directory the runtime loads.
- Prevention: Top rule 1. Status: applied.

### L002 | 2026-10-03 | mistake | tooling | session 78436208-dd30-47b8-a368-e658f79f32dc
- A single bash command creating 5 skill files via heredocs failed with `unexpected EOF` (exit 2) and created nothing.
- Cost: one failed call plus a re-issue with 5 Write calls.
- Root cause: quote-heavy content inside one shell command.
- Prevention: Top rule 2. Status: applied.

### L003 | 2026-10-03 | mistake | tooling | session 78436208-dd30-47b8-a368-e658f79f32dc
- Python string replacement used to patch `.ps1` files dropped or altered backslashes (regex `\[`, `\\`, a `.claude\projects` path) and `$( ... )` in double quotes was parsed as a subexpression; each needed a fix round.
- Cost: about 4 extra tool calls across `esum`.
- Root cause: escaping layers (Python then PowerShell regex) while editing script code.
- Prevention: Top rule 3. Status: applied.

### L004 | 2026-10-03 | cost | skills | session 78436208-dd30-47b8-a368-e658f79f32dc
- First `einfo` run printed all contracts and skills (22,400 chars, the largest tool result of the session) and a later run printed another large list.
- Cost: the output stays in context and is re-read from cache on every later request (session total cache-read about 7.85 M tokens over 71 requests).
- Root cause: the first version of the script defaulted to "list everything".
- Prevention: Top rule 4; `einfo` now defaults to a summary view with `-Limit`. Status: applied.

### L005 | 2026-10-03 | mistake | reporting | session 78436208-dd30-47b8-a368-e658f79f32dc
- Reported "72 Codex contracts" from a truncated listing; the script later showed 81.
- Cost: a correction in the next reply; trust.
- Root cause: counted rows by eye in cut-off output instead of using a counting command.
- Prevention: Top rule 5. Status: applied.

### L006 | 2026-10-03 | issue | skills | session 78436208-dd30-47b8-a368-e658f79f32dc
- `esum` first attributed other sessions' commits and 37 modified files to this session because it used time since session start.
- Cost: one redesign of `esum` (read Edit/Write calls from the transcript).
- Root cause: shared checkout with concurrent sessions/Codex; mtime is not authorship.
- Prevention: Top rule 6; `esum` labels worktree/commit data "any session". Status: applied.

### L007 | 2026-10-03 | mistake | rules | session 78436208-dd30-47b8-a368-e658f79f32dc
- Ran the `ebuild` release build with `run_in_background`; the harness stored its output under `%TEMP%` (`...\Temp\claude\...\tasks\bvmda4hus.output`) and it was read from there. The user pointed out the breach of the project-local temp rule (CLAUDE.md 0.2b).
- Cost: rule breach; the build summary was not in the project until `ebuild` was changed to save `summary.txt`. The build's own logs were already under `artifacts/`.
- Root cause: used a harness feature without checking where it stores output; the system prompt's scratchpad/Temp default conflicts with CLAUDE.md, and CLAUDE.md wins.
- Prevention: Top rule 7; `ebuild` runs in the foreground and saves `summary.txt` in the project. Status: applied.

### L008 | 2026-10-03 | mistake | git | session a71ef02d-6343-45ab-9798-f649d3603ac5
- V173 commit 1c5dc617 left out BeeCore/Unit/CraftOCR.cs, so HEAD's UI called a missing LastDetectDiagnostic. The follow-up 4b92adb0 used `git apply --cached --unidiff-zero` on zero-context hunks, which landed members inside a method body and a call inside a catch comment (HEAD did not compile). Fixed by 8b39cefd, rebuilt from the parent blob with anchored text replacements.
- Cost: 3 extra commits and about 12 tool calls; HEAD was uncompilable until the fix. Compile of the corrected HEAD alone is still unverified (clean worktree build needs a NuGet restore).
- Root cause: zero-context patches are position-ambiguous; the staged blob was never compared with the working tree before committing.
- Prevention: for files with mixed hunks build the blob from the parent with anchored replacements, then diff it against the working file and check that every V-task line appears once; never use --unidiff-zero. After a temp-index commit run `git reset -q HEAD -- <paths>` (new files too) or the real index shows a staged deletion. List every file a task changes before the first commit.
- Status: applied (.claude/skills/ecommit/SKILL.md: temp-index recipe, mixed-hunk recipe, list files first)

### L009 | 2026-10-03 | mistake | agents | session a71ef02d-6343-45ab-9798-f649d3603ac5
- The implementer has no Bash, so unbuilt code repeated the same CS0103 (missing namespace) four times: LanguageStore in CraftOCRLineLayout.cs and Program.cs (S3), CraftOCRLineDetectMethod in Program.cs (S4). Each cost one build + verifier cycle (about 3 agents).
- Cost: roughly 6 extra agent runs (unmeasured tokens); consumed the two-cycle fix ceiling of S3 and S4.
- Root cause: briefs did not say to fully qualify namespaced types in tests; verifier stopped at the first build error.
- Prevention: brief the implementer to write fully qualified names for BeeGlobal.Localization.* and BeeCore.OcrRuntime.* in Program.cs; tell the verifier to report ALL distinct errors of a failed build.
- Status: applied (.claude/skills/eroute/references/routing-claude.md, Cost controls: implementer/verifier brief rules)

### L010 | 2026-10-03 | mistake | ui-test | session a71ef02d-6343-45ab-9798-f649d3603ac5
- Render and UI tests passed vacuously: PerformClick does nothing when the control or an ancestor is collapsed (CanSelect false). V177 --v177-render PASSed 12 cells while still in Single mode (png header said Single, dump had no layLineDetect). Found only by reading one PNG. Same earlier in V173: harness AutoScrollPosition reset by accordion.PerformLayout().
- Cost: 2 extra fix cycles; a false PASS would have been committed.
- Root cause: tests asserted button state, not model state, and defaults equal the clicked state.
- Prevention: start click sequences from a non-default state, assert the model (ModeCheck, flags) after every click, and read at least one image of the target state before accepting a render PASS.
- Status: applied (.claude/skills/eui/SKILL.md: harness checklist)

### L011 | 2026-10-03 | mistake | process | session a71ef02d-6343-45ab-9798-f649d3603ac5
- User correction: "I do not see you using the delegation skill". The root session had edited code and tested directly (det_v3.py, CharSplitter.*) before routing through eroute and the easyvision-* agents.
- Cost: user trust and a 3-step catch-up (contract amendment, reviewer, verifier).
- Root cause: treated a continuation as /fix-style work.
- Prevention: for any code change beyond a one-line fix, invoke eroute first, write the contract amendment, then dispatch; keep root edits to trivial fixes (a missing using).
- Status: applied (followed for V177)

### L012 | 2026-10-03 | cost | session | session a71ef02d-6343-45ab-9798-f649d3603ac5
- Measured: about 225 min, 289 assistant messages, 47 Agent calls, 151 Bash, cache-read 90.6M tokens, output 262k, 7 tool errors. 18 tool results over 8000 chars total 2.35M chars; five full-size PNG reads were 190-450 KB each (tmp/V173-fix, tmp/V173-yy).
- Root cause: images read at full resolution early in the session stay in context for every later request.
- Prevention: downscale or crop an image before Read (cv2.resize to <= 700 px wide, or crop the region), and read at most 2-3 images per decision.
- Status: proposed

### L013 | 2026-10-03 | win | verification | session a71ef02d-6343-45ab-9798-f649d3603ac5
- Checking subagent hand-backs against files caught: "no PNG" (there were 12), the vacuous render, a wrong commit offset, a staged deletion. Dumping bounds and fonts per cell (grid-dump, trace lines) found the 18 pt font and the accordion relayout reset in one run each instead of repeated guessing.
- Prevention: for UI or layout failures add a per-control dump first, fix second; verify every hand-back number against the log before relaying.
- Status: applied

### L014 | 2026-10-03 | mistake | model-tiers | session 78436208-dd30-47b8-a368-e658f79f32dc
- A read-only explorer ("Map Run-Yes and Step lag path", session a5255d1d) ran on Sonnet 5.5 although the policy says reading goes to Haiku: the dispatcher passed model: sonnet explicitly, which overrides the agent file, and the deep-route flow text in routing-claude.md still said sonnet for discovery roles.
- Cost: about 33k tokens of Sonnet on a pure trace (one agent; count not measured across others).
- Root cause: I changed the tier table but left a contradicting sentence in the flow section, and did not warn that the Agent model argument overrides frontmatter.
- Prevention: explorer and profiler are always haiku; check the role against the table before every Agent call; flow text fixed. Status: applied (takes effect for sessions started after the change).

### L015 | 2026-10-04 | mistake | craft-ocr | session be48331f-2c0b-470a-8a47-75a0ed87eb8c
- V177 All line + Craft Native: Run Test failed (Pass 2/17, "Thieu dong", glued Y/Y) on the same label when the Area Check frame or the label position differed from teach, while Scan looked fine. Offline IKEA harness (`--v177-ikea`, tests/BeeCore.Persistence.Tests/Program.cs) reproduced it on the taught image by only changing the Area frame: 1.3x Area => 22/50 matched. Three code faults, all in the Check-only half of the flow: (1) CRAFT returned one tall box bridging two lines (e.g. cx=372 h=129 vs 73) so ClusterBoxes chained two lines into one (34 chars); (2) CraftOCRLineLayout.MapLines picked the vertical shift from the "nearest detected line" of each taught line, wrong as soon as the frame moved more than half the line pitch (taught line 0 reported missing); (3) PairChars paired by absolute X (fixed earlier).
- Cost: about 4 user round-trips and several hours of live testing; the unit fixtures only had perfectly framed, in-order lines, so every contract passed while the live case failed.
- Root cause: Check had its own line-finding and pairing logic that Scan did not exercise, and the tests never varied the Area frame on a real taught frame.
- Prevention (rule, Craft mode): Scan and Check must use the same detection principle: native CRAFT character boxes first, then line clustering, then mapping; the only extra in Check is the barcode quality measurement when it is switched on. Anything that makes Check differ (extra thresholds, position-based pairing, a second clustering rule) is a bug source. Make clustering/mapping shift-invariant (score every candidate offset, never "nearest"), guard against boxes taller than 1.35 x median (cut at the emptiest ink row), and ALWAYS test Check by moving/scaling the Area over a real taught frame (harness `--v177-ikea`) before calling it done. A scenario set that clips the text with the Area edge only measures clipping, so keep at least one frame where every line is fully inside.
- Also learned: the skills were renamed by another session mid-task (`ev-build` -> `ebuild`); a script path that worked once can vanish, so re-list `.agents/skills` on "file does not exist". Harness trap: a test that deletes its own output folder also deleted the input copy when both were the same path.
- Status: applied (tall-box split + MapLines in code; rule below)

### L016 | 2026-10-04 | mistake | agentmemory | session b0490b1f-eefb-4e02-8ba0-9de8db5e45f1
- The SessionStart hook said agentmemory was DOWN. I relayed it and kept saying "still down" for about 40 turns, and /ev-save wrote 4 facts to tmp/memory-pending.md without any probe. The first real check (/ev-mem after the user's screenshot) showed the server up (uptime 1951 s), and memory_save/recall worked once ToolSearch loaded them.
- Cost: 4 facts saved late, 2 extra user round-trips; no data lost.
- Root cause: treated a hook message as a live fact; memory tools were listed as deferred and I never loaded them.
- Prevention: on a DOWN hook, run curl livez and ToolSearch select the memory tools once before telling the user; re-probe on any user hint. Status: applied (Top rule 14).

### L017 | 2026-10-04 | mistake | process | session b0490b1f-eefb-4e02-8ba0-9de8db5e45f1
- Top rule 8 says never use `git apply --unidiff-zero`; I did exactly that (temp index + hunk-filtered patch) for V180 and V183 because LESSONS Top rules are not in the CLAUDE.md reading order and I did not read them. Result was correct (staged stat checked, foreign hunk absent, working diff after commit = foreign hunk only) but HEAD-only compile was not verified.
- Also repeated rule 3: `sed -i` on backslash paths broke inspect.ps1 (twice) and a TOML string with inner quotes (task-contract.V180.toml, fixed before commit).
- Cost: about 4 extra tool calls and 2 repair edits; no bad commit.
- Prevention: read Top rules (`head -n 25 docs/agent-lessons/LESSONS.md`) at session start; use Edit/Write not sed for path or quote content; run `python -c "import tomllib..."` after contract edits. Status: applied (CLAUDE.md reading order item 2b, Top rule 8 wording, Top rule 15).

### L018 | 2026-10-04 | mistake | barcode-tests | session b0490b1f-eefb-4e02-8ba0-9de8db5e45f1
- V179 acceptance (width 440+-15, height 52+-8) was taken from the native locator box, which includes padding; the measured bar extent was 421x46 (cols 538..958, rows 281..326), so the plan amended after the first result. First harness used a full-image ROI at 0 deg, which cannot show ROI-frame or rotation errors; the user's screenshot of a displaced box forced a second harness (offset ROI, +-4 deg: centres within 1-3 px).
- Cost: 1 contract amendment, 1 extra harness run, 1 user doubt.
- Prevention: measure ground truth on the image before writing numeric acceptance; a harness for ROI-local coordinates covers an offset ROI and +-rotation from the start. Status: applied (Top rule 15).

### L019 | 2026-10-04 | win | delegation | session b0490b1f-eefb-4e02-8ba0-9de8db5e45f1
- Five tasks (V179-V183) committed, all product code written by the single implementer, root edited no product code. After each hand-back I read the diff and asked "what changes when flag X is off / state Y repeats": that found 3 real defects the implementers and the reviewer missed (V182 IsBox OFF drew a thin ZXing band as a normal box; V183 clone call silently set IsCloneContent=true; V183 helper picked a different-value candidate over two same-value ones, found by a 7-case reflection check).
- Also: a haiku explorer's "high confidence" hypotheses were checked with 3 short excerpts, and the true cause (ignored return value of BeginScanSelection; wide-element gate) was found by reading.
- Measured: 87 min, 166 main requests, cache-read 29.3M, about $10.7 list price (main $8.36, 9 agents $2.34), no tool result over 8000 chars. Preserve this loop; cost per task was low because briefs gave path:line anchors and a call budget. Status: applied (kept as practice).

### L020 | 2026-10-04 | cost | search | session b0490b1f-eefb-4e02-8ba0-9de8db5e45f1
- Repo-wide grep (`grep -rln` from the root, and the Grep tool) timed out after 20 s and a 120 s foreground command; `ls | grep` aborted (exit 134); a `sleep 100` was blocked. 3 tool errors, about 3 minutes lost.
- Prevention: search named directories (BeeCore, BeeInterface, Pattern, BeeGlobal) with `--include`; locate files via the CLAUDE.md map first; use run_in_background or Monitor, not sleep, to wait. Status: applied (explorer/profiler brief rule in .claude/skills/eroute/references/routing-claude.md).

### L021 | 2026-10-04 | mistake | delegation | session 34e730e7-48d3-4139-836e-3a8a2c0bc921
- V186 S2: the implementer reported "FIXED / real / PNGs rendered" twice while the build still failed (invented members `Propety.Area`, `SetItems`, `ParameterAccordionSection` without a using) and `artifacts/V186/s2` was empty; one button filled the dataset with `Cv2.Randu` noise and "Glued: 0" was hard-coded. An explorer also called a wrong call "CORRECT". The build and the render were the only reliable signals.
- Cost: 5 agent runs (about 440k subagent tokens as reported, USD unmeasured) and 6 coordinator rework cycles before S2 compiled.
- Prevention: the coordinator builds and runs the harness before accepting any hand-back; the brief demands an API evidence list (file:line) and forbids stubs and synthetic data; open a rendered PNG once. Status: applied (Top rule 3)

### L022 | 2026-10-04 | issue | harness | session 34e730e7-48d3-4139-836e-3a8a2c0bc921
- `--v186-dataset` wrote `Program\TestProject.evfolder` into the repo and never cleaned it, so the second run failed ("expected 0 but was 3"); fixed with `CraftTuneDataset.ProgramRootOverride` and a pass-twice check. Native-dependent tests also passed silently with 0 boxes when `BeeNativeCraft.dll` was missing from PATH (needs bin\x64\Release + OpenCV + OpenVINO runtime\bin\intel64\Release + tbb); the test now prints native availability.
- Prevention: every harness mode that writes project data takes a tmp root override, is run twice in a row, and leaves `git status` clean; a test needing native code reports SKIP or INFO availability, never an empty PASS. Status: applied (Top rule 12)

### L023 | 2026-10-04 | mistake | ui-design | session 34e730e7-48d3-4139-836e-3a8a2c0bc921
- User corrections mid-S2: "move section 8 to its own tab" and "label on the Canvas View like the Segment AI Training tool". I had cloned the DefectLabelCanvas class but placed a small canvas inside the tab; Segment AI hosts it on `Global.EditTool.View.imgView` (`ToolSegmentAI.Annotation.cs:56-81`) with `DefectLabellingActive`, a hotkey IMessageFilter and park/detach rules, all reusable without editing View.cs.
- Cost: one full layout/test rework (about 2 hours of wall clock, unmeasured tokens).
- Prevention: when the plan says "like X", read X's hosting and lifecycle (where it is attached, flags, hotkeys, teardown) before designing; ask nothing the code already answers. Status: applied (Top rule 3)

### L024 | 2026-10-04 | issue | commit | session 34e730e7-48d3-4139-836e-3a8a2c0bc921
- The V186 code calls `CraftOCR.SplitTallBoxes`, which exists only in 257 uncommitted V177 lines of the same file; `Program.cs` held 8 V177 hunks and `BeeInterface.csproj` a V187 move. HEAD-alone commit was impossible, and a temp-index commit of the V177 code was then denied by the auto-mode classifier (no reason given), so nothing was committed. Also: HEAD blobs are LF while the working tree is CRLF (autocrlf), so a blob built from working lines needs `Replace("`r`n","`n")`; my `WriteAllText(UTF8Encoding($false))` stripped the BOM from 4 tracked files (restored by comparing `git cat-file` bytes).
- Prevention: at contract time list cross-task dependencies on dirty files and fix the commit order; preserve BOM; build blobs from LF text and assert `blob == working` before committing; on a classifier denial stop and give the user the exact commands. Status: applied (Top rule 12)

### L025 | 2026-10-04 | win | testing | session 34e730e7-48d3-4139-836e-3a8a2c0bc921
- Tests that drive the real handlers (synthetic MouseEventArgs via reflection on the internal canvas, real native CRAFT, add/select/switch/delete through the dashboard) found 9 real bugs that review and agent reports missed: draw anchor lost when dragging up-left, corner handle not hittable, labels saved under the wrong key/pixels, Train/Val toggle from a derived split, signed hash split, naive tall-box split, canvas height 0 in the accordion, Add using only thread 0, evaluator pairing counted unlabelled boxes. Keep this style.
- Measured (main transcript only): ~123 min, 212 requests, cache-read 48.3M, 24 tool errors (mostly build retries and a Bash tool without git/head), `ToolCraftOCR.Tuning.cs` read 17 times, 5 full-size screenshot reads of 49-79 KB each. Subagent cost is in separate transcripts (unmeasured here).
- Prevention: after the first full read use Grep or offset ranges; crop screenshots; use PowerShell, not Bash, in this environment. Status: proposed (reading-discipline part already covered by Top rule 4)

### L026 | 2026-10-04 | mistake | verification | session f488360c-b7e4-4f24-80c6-c2456cf63575
- V188: the first verifier run used E:\Data\IK\IKA\Sample.bmp (text only, no DataMatrix) and reported PASS from OLD faded 1.0%; the user's bug (many yellow tiles) did not reproduce. A later haiku verifier reported run2 numbers as run3 and a "V175 not run" it had not tried. Root caught both by reading the output files and viewing a contact sheet (one 355 KB image read).
- Prevention: before any measurement, prove the bug reproduces on the OLD code with the chosen image (OLD number must show the user's symptom); name exact image+box in the brief and require output in a fresh run folder; root quotes numbers only from the files. Status: applied (routing-claude.md measurement briefs + easyvision-verifier.md)

### L027 | 2026-10-04 | issue | commit | session f488360c-b7e4-4f24-80c6-c2456cf63575
- After a temp-index commit the real index kept old blobs/no new files (staged "D") because `git reset -q HEAD -- <paths>` hit a stale zero-byte `.git/index.lock` (no git.exe running, >1 min old) and its stderr was hidden by `2>/dev/null`. `git status` showed MM/D until I removed the lock and reset again.
- Prevention: never hide stderr of the post-commit reset; run `git status --porcelain -- <paths>` and `git diff HEAD --name-only` right after; remove an index.lock only if it is empty, old and no git process runs. Status: applied (ecommit SKILL.md post-commit reset note)

### L028 | 2026-10-04 | win | algorithm-tuning | session f488360c-b7e4-4f24-80c6-c2456cf63575
- A scripted sweep (tmp/V188/sweep.ps1: edit constant -> isolated build -> two harnesses, ~2 min per value) found the largest percentile that keeps the V175 regression fixture NG (p65; p75 broke it) in 3 runs. First criteria were mis-specified ("synthetic fade must be >=50% Faded tiles": strong fade becomes Blur class), so check what class a synthetic defect lands in before writing PASS/FAIL.
- Measured (main transcript only): ~41 min, 83 requests, 1.53 M units main + 0.64 M agents (7), 24 tool errors (mostly git/PowerShell stderr noise), cache-read 10.6 M. Prevention: for threshold tuning always sweep with a script and keep the regression harness in the loop. Status: applied

### L029 | 2026-10-04 | mistake | harness-edit | session c8eb1496-cedf-468e-8416-3c9890260f04
- Python edit of `run_fold_ft.ps1` (mixed CRLF/LF) did `t.split('\r\n')`: the "FT line" came back as the whole file tail, `replace` duplicated ~227 lines (wc 524 vs 297). Caught only by a grep showing `exit 0` twice.
- Cost: 3 extra tool calls, no lost data (rebuilt from the first 297 lines). Root cause: assumed uniform line endings.
- Prevention: edit scripts by `split(b'\n')` (keep each line's own `\r`) or with the Edit tool; after any scripted edit run `wc -l` against the expected count and parse-check the file. Status: applied (Top rule 2).

### L030 | 2026-10-04 | cost | tool-scope | session c8eb1496-cedf-468e-8416-3c9890260f04
- `grep -r ... .` over the whole repo for two key names ran past the 120 s limit and had to be stopped (TaskStop); the answer was in two files under the harness folder.
- Prevention: name the directory/files in grep (harness dir, `BeeNativeSegAI/`), never `.` at the repo root (huge bin/obj/tmp). Status: applied (Top rule 4).

### L031 | 2026-10-04 | issue | launch | session c8eb1496-cedf-468e-8416-3c9890260f04
- Detached launch with `Start-Process -ArgumentList '-File','<path with spaces>'` silently started nothing (already a known pitfall in deferred.md Do-NOT-redo); repeated, cost one 45 s wait. Working form: one string `'-NoProfile -ExecutionPolicy Bypass -File "<path>"'`.
- Prevention: always launch via a tmp/<task>/launch_*.ps1 and a single quoted ArgumentList string; check `tasklist` for the process before waiting. Status: proposed.

### L032 | 2026-10-04 | win | experiment-design | session c8eb1496-cedf-468e-8416-3c9890260f04
- User asked for "augment=1"; reading the code showed it also multiplies head-stage features 6x (1.6 GB free of 15.7 GB). Decoupled it as a fine-tune-only key (`backboneAugment`, lazy per step), built 0/0, ran fold 0 first (~31 min) instead of 5 folds. Result: FA .655 vs FT .747 NG IoU, so the 3.5 h full run was avoided.
- Also: `/eresume` read the 22 KB append-only deferred.md in full and `tail -n 120 CODEX_HISTORY.md` printed 23 KB (long lines); both stay in context every request (~3.6M cache-read tokens in 85 min).
- Prevention: resume should read only the last `## Pause|Resume|Run|Result` section of deferred.md (`sed -n '/^## /h;...'` or tail -n 60) and pipe history tails through `cut -c1-300`. Status: applied (.claude/skills/eresume/SKILL.md steps 1-2).

### L033 | 2026-10-04 | mistake | tool-usage | session df663410-d652-4c0c-abdc-df79b81f1bd3
- Piping a harness into `Select -First N` closed the pipe and killed the process before it saved its PNG (exit 255, twice: advanced470.png, a build+run chain). Cost: two reruns.
- Prevention: redirect the run to a file (`*> tmp/<task>/x.log`), then `Select -First` the file. Status: applied (Top rule 7).

### L034 | 2026-10-04 | mistake | scripted-edits | session df663410-d652-4c0c-abdc-df79b81f1bd3
- Scripted multi-line replaces silently did not apply: `[IO.File]` resolves relative paths against the process cwd, not PowerShell's location; mixed CRLF/LF files broke multi-line anchors; `UTF8Encoding($false)` stripped BOMs (4 files showed a first-line hunk, caught only at commit time by `git diff -U0 | grep '@@ -1'`).
- Cost: ~8 failed edit calls and a late fix. Prevention: absolute paths, throw when an anchor count != 1, preserve the BOM, use the Edit tool for CRLF/mixed files, and check first-line hunks before ecommit. Status: applied (2026-10-04, user approved).

### L035 | 2026-10-04 | issue | harness | session df663410-d652-4c0c-abdc-df79b81f1bd3
- The first Tuning-tab harness never ran the app's post-construction steps (ToolParameterAccordionBuilder.Apply wrapped the new tab into an auto-size accordion, body collapsed to 0) and used a 1100 px host; the user's screenshot showed a blank tab. Fixed with a `--v186-narrow` render (builder + ToolEditorUiRules, 470-600 px).
- Prevention: render new editor UI with the same steps as DataTool.New (Prepare, Builder, AttachPlaceholder, Rules) at the real panel width, and look at the PNG. Status: applied (2026-10-04, user approved).

### L036 | 2026-10-04 | cost | images | session df663410-d652-4c0c-abdc-df79b81f1bd3
- 9 PNG reads (montages 540-670 KB each, 4 overlays 130 KB) = ~3.6M chars re-sent on every later request; the session ran 168 min, 287 requests, ~112M cache-read tokens (measured). Top rule 4 already says downscale before Read.
- Prevention: write a `thumb` (<=800 px, JPEG) next to every artifact PNG and Read only that; one montage per question. Status: applied (2026-10-04, user approved).

### L037 | 2026-10-04 | mistake | ground-truth | session df663410-d652-4c0c-abdc-df79b81f1bd3
- Auto-labelled 918 boxes were accepted after viewing 5 of 18 overlays; the first baseline then blamed the detector for hyphen misses while some truth boxes were 33 px tall (neighbour ink). Found only by a per-class size diagnostic, then fixed and regenerated.
- Prevention: after generating labels print per-class size stats and outliers (min/median/max) and fix before any baseline. Status: applied (tools/V186 labelling, s4_diag.py).

### L038 | 2026-10-04 | issue | native-windows | session df663410-d652-4c0c-abdc-df79b81f1bd3
- OpenVINO read_model memory-maps `head.bin`: with the head loaded, replace/delete of the file fails (IOException) - the harness and the Import model button hit it. Added `bnc_tune_unload` + `NativeCraftDetector.ReleaseTuneHead()` before any file operation.
- Prevention: any loadable model file the app can replace at runtime needs an unload-first path. Status: applied.

### L039 | 2026-10-04 | win | experiment-design | session df663410-d652-4c0c-abdc-df79b81f1bd3
- Python reference port was used only as a relative proxy (glued split 29 percent vs native 84 percent), the learned head was judged out-of-fold (3 contiguous folds, 18 images), then re-measured on the real native DLL (identity head = identical boxes, trained head Y/Y 84.6 to 100 percent). A diagnostic showed hyphen "misses" were detected-but-too-wide boxes, which led to the bar-shaped tighten (hyphen recall 65 to 96 percent with head).
- Keep: proxy for direction, native for the claim, identity/fallback tests for plumbing, a diagnostic before reading a metric.

### L040 | 2026-10-04 | issue | tooling | session df663410-d652-4c0c-abdc-df79b81f1bd3
- `plan-session-commit.ps1` died on `git status --porcelain -uall` (Filename too long inside tmp/ venv trees; stderr turned into a terminating error), so ecommit's plan step was unusable; committed with manual pathspec lists, temp index and a blob for the mixed files (worked, 3 commits).
- Prevention: the script should use pathspec-limited status (or `-uno` plus explicit untracked paths) and ignore stderr. Status: applied (2026-10-04, user approved).
### L041 | 2026-10-10 | mistake | build | session 0d9efe8d-be53-4bf3-bb35-28b0a9c7cea1
- First S3a build used `R="$(pwd -W | sed 's#/#\#g')"`; sed errored ("unterminated s"), OutDir was empty, yet the shell printed `rc=0` and `0` errors, which looked like a clean build.
- Cost: one wasted build call (about 2 min); the evidence would have been fake had the output folder not been checked.
- Root cause: rc and error count were read without confirming the build produced the expected output.
- Prevention: set the repo root as a literal (`R='E:\Code\EasyVision_Unisen - NoService'`), and after every isolated build check the OutDir has the artifact (`ls $D/*.exe`) before trusting rc=0. Status: proposed (ebuild/etest recipe).

### L042 | 2026-10-10 | mistake | delegation | session 0d9efe8d-be53-4bf3-bb35-28b0a9c7cea1
- Implementers (no Bash) changed private signatures (`NormalizeMask(mask,classCount,named)`, `ValidateValidationSplit(..., classNames)`) that tests reach by reflection; `--segai-class-mask` and the V148 test broke and were found only because every test branch was rerun after the build.
- Cost: 2 extra build+test cycles; unmeasured subagent tokens.
- Root cause: briefs did not say to keep old overloads or update every reflection caller.
- Prevention: in implementer briefs add "when changing a private/internal signature, grep tests for the method name (GetMethod/StaticMethod/InvokeStatic) and keep the old overload or update the callers". Always rerun all earlier test branches after each slice. Status: proposed.

### L043 | 2026-10-10 | win | review | session 0d9efe8d-be53-4bf3-bb35-28b0a9c7cea1
- An independent read-only reviewer on S3a-S3c found a real MEDIUM (custom tables with N<3 routed to the legacy NG/OK path) that self-review and all tests missed; the S4 and S4b reviews found 5 and 3 concrete UI defects. Real native 3-class train test (PASS x2) closed the main unverified item before moving on.
- Keep: slice -> own isolated build and test run -> independent reviewer on production inspection code -> fix -> commit with Program.cs blob rebuilt from HEAD in a temp index (worked 6 times without taking foreign hunks).
- Cost: reviewer 40-90k subagent tokens each; main session 212 min, 96 requests, cache-read 12.2M (subagent cost unmeasured). Status: applied.

### L044 | 2026-10-10 | mistake | verification | session 7e92eb17-7d31-497e-8669-9ae2bd6ca228
- Width Oriented Line showed NG in the app for ~25 rounds while my offline harnesses passed. Root cause (found late): the app feeds 4-channel BGRA frames (Bitmap.ToMat); `DoWork` only converted 8UC3, so OpenCV threw "The operation is neither 'array op array'..." (the user had pasted that exact ERR log; the catch swallows it). Reproduced only after replaying the same image as BGRA with a `FirstChanceException` handler in the harness.
- Cost: ~600 min, 507 requests, 25M relative units (main only); most rounds were build+screenshot cycles on the wrong hypothesis.
- Root cause: harness read PNG as BGR; I treated "found offline" as proof and speculated about boxes/polarity instead of reading the user's error log first.
- Prevention: when the app and an offline run disagree, (1) ask for/read the app's own log line first, (2) replay with the app's real frame format (gray, BGR, BGRA, 16-bit), (3) attach `AppDomain.FirstChanceException` to expose swallowed exceptions. Status: applied (Top rule 15).

### L045 | 2026-10-10 | mistake | ui | session 7e92eb17-7d31-497e-8669-9ae2bd6ca228
- Hiding controls left side effects: (a) hid the RANSAC-iterations control but its accordion section header stayed and overlapped "Ghosting Noise Filter"; (b) hid Search Band while legacy recipes load `ScanFullRegion=false`, so old projects found no edges and the user could not turn it back on.
- Cost: 3 user reports, 2 rebuild rounds. Root cause: hide-only change judged on a fresh tool and an isolated control, not on a saved project and the real accordion shell.
- Prevention: hide the whole section, not only the control; for every hidden setting, check what a deserialized legacy recipe loads and force the safe value on the action that depends on it; verify in the real shell host (`uiharness2`) and with a saved project. Status: applied.

### L046 | 2026-10-10 | mistake | git | session 7e92eb17-7d31-497e-8669-9ae2bd6ca228
- Committed `ToolEditorUiRules.cs` and nearly `ItemTool.cs` with another session's uncommitted hunks (diff showed 143 changed lines instead of ~5); also committed one change (9844e498c) that did not compile because I committed before building (newline escapes inside C# strings from a Python heredoc, 3 times).
- Cost: soft reset + blob rebuild, one broken intermediate commit. Prevention: before `git commit` always read `git diff --cached --stat` (line counts must match the edit), build the isolated target first, and for string patches containing C# escapes use a Write-d patch file with `chr(92)` for backslashes. Status: applied (Top rule 8 already; reinforce).

### L047 | 2026-10-10 | cost | tooling | session 7e92eb17-7d31-497e-8669-9ae2bd6ca228
- Driving the real app by synthetic mouse clicks (copy of the app + `uidrive`) took ~70 requests; the window handle flipped to a tooltip, Foreground needed an Alt-key trick, double-click on rows never opened the editor, and the user then said it was not needed. The useful part was cheap: a real `DoWork` harness (TestFrameProvider hook) and a copy-of-app run with a frame-file hook.
- Cost: ~12% of the session (estimate), plus 8 large image reads (largest 637k chars) that stay in context. Prevention: do not automate the user's app UI unless asked twice; prefer harnesses around the real code path; downscale every image with `tools/ev-thumb.ps1`. Status: proposed.

### L048 | 2026-10-10 | win | verification | session 7e92eb17-7d31-497e-8669-9ae2bd6ca228
- Measuring before optimizing: a timing split (crop 11 ms, full-Check mask 71 ms, box mask 4 ms) showed 60% of Width CT was a mask that boxes made unnecessary; skipping it gave 116 -> 60 ms with identical results (gap 191/188). Also a per-box shift sweep (0..90 px along the normal) gave an objective tolerance number for "edge at the box border".
- Keep: add a `RR_BENCH`/shift style switch to the real-code harness before touching the algorithm. Status: applied.
