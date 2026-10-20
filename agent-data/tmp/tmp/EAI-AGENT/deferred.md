# Deferred work: EAI-AGENT

Written in English by /epause. Resume with /eresume EAI-AGENT. Append new sections; never rewrite earlier ones.

## Pause 2026-10-11 12:35 - User ran /epause; work was on EAI-PROJ P4, EAI-AGENT is next in the queue

### Facts (filled by script)
- session: 102413dc-1a16-463a-99a4-ace6a1c0fa60 (started 2026-10-10 19:49)
- usage: snapshot age 11469 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: EAI-AGENT, EAI-CACHE, EAI-CAM, EAI-PROJ
- commits since session start (any session): 17
  - 644f928ae [EAI-PROJ] P3: recent projects (max 10, newest first, case-insensitive de-dupe, missing entries kept and marked), persisted in AppSettings.RecentProjects
  - b2d276311 [EAI-PROJ] P2: named project manifest (<Name>.easyai + .easyai.data), ProjectManager Create/OpenFile/SaveManifest/Reload/SaveAs, name rules, classes.txt in the metadata folder for named projects
  - 49f57eda3 [EAI-PROJ] P1: split the image root from the metadata directory (ProjectPaths); legacy <root>\.easyai placement unchanged
  - 7e6a5b5a2 [EAI-AGENT] S4c: check / X icon buttons on every suggestion box (accept or reject exactly that suggestion)
  - 169f7a4d1 [EAI-AGENT] S1 fix: retry the temp-file replace in AgentOutcomeLog (scanner/indexer made ConcurrentAppends test fail with access denied once)
  - 00579b891 [EAI-AGENT] S1: agent outcome log (agent-outcomes.jsonl) and deterministic 'Learned from review' digest
- files edited by this session: 13; still uncommitted: 4
  - .claude\task-contract.EAI-AGENT.toml [??]
  - .claude\task-contract.EAI-CACHE.toml [??]
  - .claude\task-contract.EAI-CAM.toml [??]
  - .claude\task-contract.EAI-PROJ.toml [??]

### Task and scope
EAI-AGENT (.claude/task-contract.EAI-AGENT.toml, plan tmp/EAI-AGENT/plan-v1.md including the plan-v2 amendments, approved by the user 2026-10-11 with edit/build/test/commit, no push). Goal: EasyLabel Auto-label Agent as a chat popup opened from a round button at the bottom of the left tool strip, Effort next to the prompt box and Run, Mode All/Current/Remaining images, a learning project memory (Accept/Reject outcomes), real skills and app-owned MCP tools, optional external MCP servers approved one by one, and a Check button that proposes fixes as dashed suggestions. Scope EasyLabel/** only.

### Done (verified)
S1 00579b891 plus fix 169f7a4d1 (Core Io/AgentOutcomeLog.cs, AgentOutcomeDigest.cs, ProjectLayout.AgentOutcomesFile; 15 tests; Core 175 at that time; evidence artifacts/EAI-AGENT/s1, s4c). S4c 7e6a5b5a2 (check and X icon buttons on every suggestion box: Canvas/SuggestionButtonLayout.cs, AnnotationCanvas, CanvasViewModel.AcceptSuggestion/RejectSuggestion; 18 layout tests; evidence artifacts/EAI-AGENT/s4c; on-screen rendering UNVERIFIED). Also committed earlier in this session under EAI-PLUS: oriented_rectangle proposals (e44908ffd, 94a088b27) and backend-name texts (de7261936). Discovery maps: tmp/EAI-AGENT/ui-map.md, backend-map.md, mcp-survey.md.

### Remaining (in order)
Order agreed with the user: EAI-PROJ (P4 first, see tmp/EAI-PROJ/deferred.md), then EAI-CACHE (plan not approved yet), then EAI-AGENT. Slices from tmp/EAI-AGENT/plan-v1.md: 1. (start here after PROJ/CACHE) S2 wire outcomes (decision-aware event in CanvasViewModel, LabelStepViewModel records agent-sourced only, AgentRunner/AgentRunFiles inject the digest and add it to the cache-key hash list). 2. S3 review protocol (list_shapes ids and fp, propose_shapes target/action/note, SegAiSuggestion optional Action/TargetIndex/TargetHash). 3. S4 applying a review (DocumentEditor.ApplyReview, stale check, dashed drawing; UI smoke). 4. S5 app-owned tools grid_crop, render_overlay, measure. 5. S6 skill files plus the Settings Enable-toggle fix (EasyAI run skills, ~/.claude/skills read-only; UI smoke). 6. S7 batch runner and Stick modes (Remaining runs WITHOUT pausing; caps editable in Settings, default 2.00 USD and 50 images). 7. S8 chat history store. 8. S9 chat popup UI (AgentChatWindow, pinned round button in ToolStripView, effort and mode in the popup, old bar removed; UI smoke vi and en). 9. S10 external MCP registry with per-server approval dialog (UI smoke). S11 yolo_detect deferred.

### Decisions already made
Popup = owned non-modal FluentWindow with chat history (not AvalonDock, not Popup). Memory also learns from Accept/Reject. External open-source MCP servers allowed only after per-server approval; app-owned tools by default. Check proposes fixes as dashed suggestions, never overwrites silently. Remaining mode runs without pausing (user chose this against the architect recommendation). Settings skills page switches to EasyAI run skills with ~/.claude/skills read-only. Chat history per project; the agent does not see earlier turns. 'Stick Still Images' read as 'Remaining images'.

### Do NOT redo
The three haiku discovery reports and the architect plan; S1 and S4c builds and tests. Harness facts: see tmp/EAI-PROJ/deferred.md (isolated OutDir with a Windows absolute path, TEMP and TMP under tmp, never /e/ style paths).

### In-flight agents and background work
None for EAI-AGENT. (The P4 implementer of EAI-PROJ was stopped; see tmp/EAI-PROJ/deferred.md.)

### Next commands
Authorization: edit_code, build, run_tests, commit true; push false; paid real-agent runs only after asking the user (they cost account tokens); UI smoke only on an idle PC with EasyAI.exe closed. First step when it is EAI-AGENT's turn: dispatch easyvision-implementer (sonnet) for S2 with the plan section A5 and the S1 API (AgentOutcomeLog.Append/AppendMany/ReadAll, AgentOutcomeDigest.Build, ProjectPaths.AgentOutcomesFile; Append can throw IOException, catch it in S2).

### Risks and unverified
UNVERIFIED: S4c rendering, hover, tooltip and Undo on screen; all later slices not started. Real agent results are model-dependent (oriented_rectangle run: 2 of 5 boxes rotated, the rest near axis-aligned). EasyAI.exe may be running on the user's PC and lock bin. Other worktree changes belong to other sessions.

### Acceptance still owed
Everything in the contract acceptance list except the icon buttons' code: popup, effort and mode controls, memory from Accept/Reject, skills and tools, MCP approval, Check.

