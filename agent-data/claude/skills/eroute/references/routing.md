# EasyVision Dispatch Policy

## Route matrix

| Route | Typical categories | Coordinator | Astra | Default verification |
| --- | --- | --- | --- | --- |
| `light` | `docs-config`, `ui-cosmetic` | `gpt-5.6-terra` | No | Static and diff checks; build normally not recommended |
| `standard` | `ui-behavior`, `managed-bug`, `managed-refactor` | `gpt-5.6-sol` | No | Recommend the narrowest build/test for executable state or logic changes |
| `deep` | `vision-algorithm`, `native-interop`, `concurrency-lifecycle`, `performance`, `hardware-control`, `cross-subsystem` | `gpt-5.6-sol` | `easyvision_architect` on `gpt-6-astra` High | Recommend focused build/test/benchmark only when it proves an identified risk |

Use `light` only for documentation, metadata, UI copy, icons, spacing, color, localization, or
layout-only work with no state, persistence, SDK, threading, native, or algorithm effect.

Use `standard` for a localized managed-code bug, refactor, or UI behavior change contained in one
known subsystem with stable interfaces and bounded call paths.

Use `deep` for vision accuracy, geometry, calibration, or algorithm choices; native C++/CLI,
P/Invoke, ABI, or unmanaged ownership; camera lifecycle; async, concurrency, or UI-thread behavior;
performance-sensitive pipelines; persisted, serialized, or public contracts; multiple subsystems;
PLC, EtherCAT, DS5, motion, safety, or hardware control; unclear invariants; high risk; or conflicting
specialist evidence.

## Agent flows

- `light`: optional `easyvision_explorer` -> approved plan -> `easyvision_implementer` ->
  `easyvision_reviewer`.
- `standard`: `easyvision_explorer` -> approved plan -> `easyvision_implementer` ->
  `easyvision_verifier` when authorized -> `easyvision_reviewer`.
- `deep`: launch only the relevant read-only `easyvision_explorer`, `easyvision_algorithm`, and
  `easyvision_profiler` roles, at most three concurrently. Then run `easyvision_architect` to resolve
  evidence and produce the plan. After Work approves that revision, run `easyvision_implementer`,
  authorized `easyvision_verifier`, and `easyvision_reviewer` sequentially.

The deep coordinator remains `gpt-5.6-sol`; Astra is a conditional architect, not the root. Algorithm
work therefore receives Astra analysis while Terra remains the production-code implementer.
Work must pass the coordinator model to task creation and set reasoning to medium for `light` or high
for `standard` and `deep`. An unsupported selection is a dispatch mismatch, not permission to use an
implicit default.

## Automatic escalation

- Escalate `light` to `standard` when the change affects executable behavior, state, persistence, or
  more than cosmetic files.
- Escalate any route to `deep` when discovery finds vision math, native or unmanaged boundaries,
  threading or lifecycle constraints, performance-critical behavior, hardware control, public or
  persisted contracts, cross-subsystem scope, unclear high-risk invariants, or conflicting evidence.
- Record the new `dispatch.route`, `dispatch.astra_required`, reason, affected plan revision, and any
  newly unavailable acceptance evidence. Do not silently de-escalate a deep route.
- Escalation never expands scope or authorization. Stop when it requires out-of-scope files, reaches
  a repository stop condition, or needs a user decision.

## Task Contract rules

- `category`: one canonical category from the route matrix.
- `route`: exactly `light`, `standard`, or `deep` in a filled contract.
- `normalized_request`: a testable restatement containing current and expected behavior, context,
  scope, assumptions, constraints, and acceptance evidence.
- `selected_model`: coordinator model; it is not a permission grant.
- `astra_required`: true only for a deep trigger or recorded escalation.
- `build_recommended`: whether executable verification would materially reduce an identified risk.
- `build_reason`: empty when not recommended; otherwise name the exact risk and narrowest useful
  build, test, or benchmark.

If explicit current-task user authorization already exists, record its provenance and do not ask
again. Otherwise, when `build_recommended=true`, Work asks exactly once before dispatch. If the user
declines, dispatch with the flags false and make the Reviewer distinguish verified evidence from
unverified runtime behavior. A deep route does not itself authorize any executable command.

## Completion safeguards

Preserve the original prompt beside the normalized request. Validate the exact workspace before
dispatch. Never claim a task was created when tooling is unavailable. Reviewer `ACCEPT` is required,
but safety and authorization gates take priority; missing evidence must not be fabricated as a pass.
On `FIX_REQUIRED`, reuse Implementer, Verifier, and Reviewer for at most two bounded cycles, then
report `BLOCKED` with the remaining condition.
