---
name: eui
description: Audit the frontend (WinForms UI) of a task contract that this session is working on against its approved plan, then fix findings until the UI matches the plan. Use when the user says /eui, check the UI of this contract, or fix UI to match the plan. Current session's contract only; excludes EasyScreen, performance-only work, and other sessions' contracts.
---

# EasyVision UI audit and fix loop (Claude backend)

Scope: the contract this session is working on. Never audit or edit another session's contract.
Companion to the Codex `.agents/skills/eui` (control discovery list) and `elayout` (responsive rules). `CLAUDE.md`
section 0.2a and `AGENTS.md` govern; this skill grants no build, runtime, commit or push permission.

## 1. Pin the contract and the plan (cheap, no agents)
1. Run `esum` (`.claude/skills/esum/scripts/summarize-remaining.ps1 -SessionId <guid>`; guid = scratchpad folder
   name). Take the contract this session edited or the one the user names with `-Id`. If this session has no
   contract, or the named ID is not part of this session's work, stop and ask which contract to use.
2. Read only that contract (`.claude/task-contract.<ID>.toml`): goal, `in_scope`, `out_of_scope`,
   `acceptance.behavior`, `acceptance.evidence_required`, `[authorization]`, and the approved plan revision.
   The plan text lives in the contract/handoff for that revision; if the plan is not approved
   (`plan_approved=false`) stop and report it. Do not invent requirements.
3. Run the token-budget gate from `eroute` step 4a before dispatching agents.

## 2. Audit (read-only, one pass)
Dispatch `easyvision-explorer` (trace) then `easyvision-reviewer` (judge), or do both yourself in `/fix` mode for a
light contract. Brief with `.claude/invariants.md`, the contract slice, budget and report cap. Check the real
production control tree, not a mockup, and record each item as MATCH / GAP / UNVERIFIED with `path:line`:
- Plan conformance: every UI-related acceptance item and `in_scope` file is implemented as the plan says; nothing
  outside scope was changed.
- Controls (0.2a): reuse existing custom controls (RJButton, CustomNumericEx, AdjustBarEx, ParameterAccordion,
  TouchNumberPad, ParameterOptionStyle); no stock `CheckBox`/`ComboBox`/`NumericUpDown` where a custom one serves
  the role; no mock, placeholder or parallel screen.
- Layout: adapts to window size, DPI/font scale and Vietnamese/English text; nothing clipped, overlapped or
  unreachable; scrolling owned by the right container. Use `elayout` rules.
- State and lifecycle: save/reload round trip, disabled/running states, event `-=` before `+=`, UI-thread safety,
  localization keys (`tools/Check-UiLocalizationCatalog.ps1` when authorized), no new static UI state.
- Rendered behavior is UNVERIFIED unless runtime UI verification was explicitly authorized and run.

### Harness checklist for hosted-Form UI tests and render matrices (L010)
A green harness proves only what it asserted. Before accepting a UI PASS:
1. Expand the owning accordion section BEFORE any `PerformClick` (it is a no-op when the control or an ancestor is
   collapsed or invisible: `CanSelect` is false) and assert `CanSelect`, or click through the production handler.
2. Start every click sequence from a NON-default state and assert the MODEL (e.g. `ModeCheck`, mode flags, stored
   option) after each click, not only the button's lit state; defaults equal the clicked state otherwise.
3. Scroll AFTER all layout and never call `accordion.PerformLayout()` after scrolling (it resets the scroll to 0).
4. Give every new control an explicit `Font` and a `MinimumSize` that does not scale past its cell (an inherited 18 pt
   font and a DPI-scaled MinimumSize caused overlapping presets and buttons).
5. On a layout failure add a per-control dump (Bounds, Visible, Font, text measure) first, fix second.
6. Read ONE image of the target state (downscaled or cropped, L012) before accepting a render PASS; a matrix that never
   entered the target state passes vacuously.
7. Render new editor UI the way the app builds it, at the real panel width (470-600 px, not a 1100 px host): construct the
   tool, run `ToolParameterAccordionBuilder.Apply` then `ToolEditorUiRules.Apply` (the DataTool.New sequence: Prepare, Builder,
   AttachPlaceholder, Rules), THEN look at the PNG. The builder wraps unknown tab content into an auto-size accordion section
   that collapses a Fill layout to nothing; a bare-control harness cannot see that (L035). Harness mode example: `--v186-narrow`.

## 3. Fix loop (bounded)
1. GAP list -> one bounded fix request per finding to the single `easyvision-implementer` (the only writer).
2. Re-audit only the changed declarations with `easyvision-reviewer`; reuse the same agents.
3. Repeat until every plan item is MATCH or an explicit UNVERIFIED with the reason. Limit: two `FIX_REQUIRED`
   cycles per plan revision. If gaps remain, stop and report; a third cycle needs a new revision approved by
   the user. Escalate to `deep` if a fix would leave the plan's scope or touch native/safety code.
4. Build/test only if `[authorization].build` / `run_tests` is true for this task and the user authorized it
   (use `ebuild`). Otherwise record `unverified`.

## 4. Report
Per plan item: status and evidence (`path:line`), what changed, what is still UNVERIFIED (build, live UI, DPI,
language), and the next action. Commit only through `ecommit` when the contract allows it and checks passed.
Stop agents that run far past budget or outside scope (`eroute` step 7a).
