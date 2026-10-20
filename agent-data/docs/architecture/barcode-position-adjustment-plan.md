# Barcode as a Positioning Tool

Status: implemented and targeted acceptance verified (V129).
Prepared for: Codex 5.6.
Date: 2026-09-17.

## Handoff instructions

- Implement the agreed behavior below in `E:\Code\EasyVision_Unisen - NoService` only. Do not edit, build, test, or run files from the older sibling workspace.
- Read repository `AGENTS.md`, the relevant `CLAUDE.md` rules, and only the last 120 lines of `.codex-session.md` before implementation. The latest user request was to save this plan, not to implement it yet.
- Continue on the current branch. The user explicitly approved preserving existing uncommitted changes and implementing on top of the current working tree. Review relevant existing diffs; do not stash, reset, or overwrite unrelated changes.
- Build and executable tests are not authorized. Use source inspection and static diff/whitespace checks. Do not infer authorization from this plan or its acceptance scenarios.
- Keep all added or modified C# compatible with C# 7.3. Use existing tool-list resolvers and paired event unsubscribe/subscribe patterns.
- On implementation, register the feature in the appropriate map using its then-current next free ID, and update required session/history records. Do not claim acceptance scenarios have passed without actually verifying them.

## Summary

Add **Advanced → Enable Position Adjustment** to Barcode. The selected detected rotated box becomes the positioning reference for other tools in the same flowchart.

Enable this only when PosAdj is absent or Unused. Each flowchart may have exactly one positioning provider. While Barcode positioning is enabled, block activating PosAdj or enabling a second positioning Barcode.

Existing behavior remains unchanged when this option is off.

## Configuration and positioning

- Persist the enable flag, reference box in image coordinates, and positioning-box index. Existing projects default to disabled. Keep detected poses and cycle validity transient.
- Single uses its selected box. Multiple retains its reading-box selection and requires the operator to designate one selected box as the positioning anchor.
- Initialize the reference from the existing saved sample box. If unavailable, require selecting a box on the master image before training a new image.
- Use the detector's actual rotated box, before display normalization or Area Check expansion. Apply translation and rotation without scaling, using the existing rigid-transform helper.
- Keep the positioning Barcode's Area Scan fixed as its search region. Transform its inspection regions and other eligible tools' Area Scan, Area Check, and masks from their taught coordinates.
- Calculate every adjustment from the saved reference; never accumulate previous-cycle adjustments. Normalize equivalent rectangle angles relative to the reference to avoid artificial angle jumps.
- Validate provider conflicts in editing, Used/Unused changes, program loading/synchronization, and runtime entry. Conflicting configurations must report an error rather than silently choose a provider.

## Training workflow

Implement a Barcode-provider branch in `BeeCore/Func/AutoTrainingCoordinator.cs`, retaining the existing PosAdj branch.

**Pinned frame → Scan Barcode boxes → Select reading boxes and anchor → Transform OCR Area Scan → Scan OCR boxes → Select OCR boxes → Inspect Barcode → Inspect OCR → Evaluate → Result → Complete**

### Selection rules

- **Manual:** explicitly select every Barcode/OCR box; Multiple also explicitly selects the Barcode anchor.
- **Auto Single:** use the saved index. Do not silently substitute another index through positional matching.
- **Auto Multiple:** request manual selection, including the positioning anchor.
- Missing boxes or unavailable indexes open Area Scan editing, then rescan and request selection on the same pinned frame. Cancellation restores the previous recipe.

### Learning and evaluation

- Separate acquiring new readings/samples from evaluating them against the recipe. Old content mismatches must not prevent teaching new content.
- Barcode Single replaces its taught content and Text Matching with the newly decoded value, including previously typed Text Matching.
- Barcode Multiple replaces the taught content list and uses the existing exact-list comparison mode.
- CraftOCR replaces selected image templates and sample previews; this feature does not add character-to-string recognition.
- Refresh these samples as a required part of this training branch, regardless of the general optional reteach setting. Missing required decoded content or invalid templates enters recovery instead of accepting an empty sample.
- Evaluate using the new samples and existing score criteria. Publish Result only after that evaluation.
- Commit adjusted regions and the new Barcode reference together, making the final verification frame the new zero pose. Preserve Save Program as the persistence action.
- Recovery after changing the Barcode anchor invalidates downstream OCR selections and restarts their scan from the original staged geometry, preventing double adjustment.
- Auto launches must surface required selection/recovery UI. Without an interactive callback, return an actionable failure.

## Runtime and implementation boundaries

- In `BeeCore/Unit/BarCode.cs`, extract reusable candidate detection that does not clear saved selections, samples, or content. Existing `Scan()` clears training state and must not be reused directly for production positioning.
- Add a shared provider resolver and pose result containing validity, reference pose, and current pose. Reuse existing detector/native interfaces; no P/Invoke changes.
- In `BeeCore/Checking.cs`, resolve and locate the provider before scheduling dependent tools, using the same cycle image.
- With a valid pose, inspect Barcode and downstream tools. A Barcode content NG still allows OCR to run; normal configured result aggregation applies.
- With no valid pose/reference, mark the cycle NG and finish dependent tools as skipped/invalid. Clear previous results and pose state; never reuse a previous-cycle position or leave scheduler tasks pending.
- Extend existing Advanced controls, training callbacks, canvas overlays, and localized messages to show the anchor, selection stages, and recovery.
- Deep-copy mutable Barcode reference, selection, and sample data used by staged training so cancellation cannot modify the original recipe.

## Acceptance criteria and constraints

These are acceptance scenarios, not authorization to run builds or executable tests:

- PosAdj absent/Unused permits enabling; active PosAdj and duplicate Barcode providers are rejected.
- Translation and rotation move dependent regions correctly without repeated-training drift.
- Manual, Auto Single, and Auto Multiple follow the agreed selection rules.
- Missing candidates support edit/rescan/reselect; cancellation restores samples, regions, and reference.
- New Barcode content and CraftOCR templates are adopted before evaluation.
- Runtime content NG continues OCR; missing positioning produces NG without stale results or a stalled cycle.
- Save/reopen preserves configuration; older recipes remain disabled; flowcharts remain independent.

Implement on the current branch in `E:\Code\EasyVision_Unisen - NoService`, preserving existing uncommitted changes. Maintain C# 7.3 and update the applicable map/session records upon implementation.

Verification completed after explicit user authorization: Release/x64 targeted builds for
`BeeCore`, `BeeInterface`, `BeeUi`, `BeeMain`, and `AutoTrainingSmoke` passed; the smoke executable
passed provider-conflict, rigid-region, fixed-search-area, content-adoption, persistence, and
WinForms surface checks. The full solution build remains unavailable because the existing solution
references missing `Update/TestCPlus.csproj` and the unrelated EtherCAT linker output is locked.
