# Codex Handoff — Pattern2 Angle-Scan Parallelism (`PATT`)

> Self-contained work order. Everything Codex needs is in this file. Do NOT explore beyond the
> **Reading List** unless a step explicitly fails and forces it (then stop-and-ask).

---

## 0. EXECUTION CONTRACT (read first, obey strictly)

**Type:** Direct user feature request. OUTSIDE the CLAUDE.md refactor roadmap (Phase 1-6).

**Hard constraints — do not violate:**
1. Work on branch `feature/patt-angle-parallel` only. Do NOT mix with any refactor task.
2. Edit ONLY files listed in each task's *In-scope*. Touch nothing else. Worktree is dirty — do
   not format/refactor unrelated files even if tempting.
3. The new option is **default OFF**. With OFF, behavior AND timing must be unchanged.
4. With the option ON, match results must be **bit-identical** to OFF (same box count, score,
   angle, position) — only faster. This is a release gate, verified on ≥5 test images.
5. **Forbidden files:** `Pattern/Pitch*`, `Pattern/Pattern2.*` test/sample files, `Pattern.vcxproj`
   (do not change compiler flags), any `*.Designer.cs` outside PATT.5, any GPU path.
6. C# added/modified must compile under **C# 7.3** (no C# 8+ syntax; cast ternary branches to a
   common type when needed). Do not raise `<LangVersion>`.
7. All doc/comment/history/session text in **English**. Final user summary may be Vietnamese.
8. Event subscribe rule: always `X.Event -= H; X.Event += H;` (no exceptions) — relevant in PATT.5.
9. One task = one commit. Message: `[PATT.<n>] <summary>`. Do NOT squash two tasks.
10. After each task: append one block to `docs/architecture/CODEX_HISTORY.md` and one line to
    `.codex-session.md`. Keep both terse.

**Token discipline:**
- Read only the line ranges in the Reading List. Do not open whole 5000-line files.
- Do not run broad `grep`/`find` sweeps; the anchors below are already resolved.
- Do not re-derive the diagnosis; it is verified. Verify only the exact line you are about to edit.

**Stop-and-ask triggers (halt, ask user, do not improvise):**
- PATT.3 state-decoupling refactor would exceed ~150 changed lines or touch a public API signature
  other than adding one bool param.
- Bit-identical check fails and the cause is not an obvious merge-order bug.
- A build error would require editing an out-of-scope file.

**User decisions (2026-08-22) already baked in:**
- Production machine for Pattern = **8+ cores**. So the dominant bottlenecks are the **sequential
  scale loop** and the **angle-count thread cap**. The `<=4`-core BK2 serialization is a minor,
  dev-only concern → lowest priority.
- **Bit-identical is mandatory** → the "increase top-layer angle density" idea is **dropped**.

---

## 1. Reading List (open ONLY these, in this order)

| # | File | Lines | Why |
|---|---|---|---|
| 1 | `Pattern/Pattern2.h` | 589-672 | `Pattern2StableConfig` struct + ctor (add field here) |
| 2 | `Pattern/Pattern2.cpp` | 3348-3360 | `Pattern2::Match(...)` signature (add bool param here) |
| 3 | `Pattern/Pattern2.cpp` | 3440-3475 | top-layer angle list `vecAngles` build (do NOT change the set) |
| 4 | `Pattern/Pattern2.cpp` | 3513-3592 | thread budget + BK2 policy + coarse dispatch |
| 5 | `Pattern/Pattern2.cpp` | 3628-3652 | refine dispatch |
| 6 | `Pattern/Pattern2.cpp` | 3775-3966 | `RunStableMatchOnFeatures`: scale loop + shared-state writes + `Match()` call |
| 7 | `BeeCore/Algorithm/PatternStableConfigFactory.cs` | 179-279 | `Create(...)` overloads (add param) |
| 8 | `BeeCore/Unit/Patterns.cs` | 2679-2739 | tool fields + `BuildStableConfig()` |

That is the entire surface. Nothing else needs reading to implement PATT.0-PATT.6.

---

## 2. Verified Diagnosis (do not re-investigate)

Runtime path: `Patterns.cs.BuildStableConfig()` → `Pattern2::MatchStable` →
`RunStableMatchOnFeatures` → **sequential scale loop** → per scale calls legacy `Pattern2::Match()`
→ coarse scan parallel over angles + refine parallel over candidates.

Three bottlenecks:
1. **Coarse threads capped by angle count** (`Pattern2.cpp:3543`
   `coarseThreadCount = min(cpuThreadBudget, vecAngles.size())`). At the TOP pyramid layer the
   template is shrunk, so `dAngleStepAuto = atan(2/max(w,h))*R2D` is coarse → few angles → few
   threads even on 8-16 cores when the angle range is narrow.
2. **Sequential scale loop** (`Pattern2.cpp:3867` `for (const auto& item : stableScaleBank)`).
   With `EnableScaleSearch=true`, N scales run one after another. Largest untapped parallelism.
3. **BK2 / `<=4`-core policy** (`Pattern2.cpp:3534`) grabs a process-wide `runtimeLock` and pins
   nested cv threads to 1. Only fires at `hostLogicalCores <= 4` → dev-only per user decision.

Config plumbing already exists: `EnableCpuMultiThread` + `CpuThreads` (`Pattern2.h:621-622`),
C# sets them via `PatternStableConfigFactory.Create(..., UseGpu, UseCpu && !UseGpu, 0)`
(`Patterns.cs:2729-2730`, `cpuThreads=0`=auto=all cores).

**Critical race note for PATT.3:** the scale loop body writes shared instance state
`img->matRaw = srcFeature; img->matSample = tplGray; img->m_TemplData = item.templData;`
(`Pattern2.cpp:3907-3910`) then calls `Match()` which reads `img->m_TemplData`. Parallelizing
scales as-is WILL race. PATT.3 must pass template+source as local parameters instead.

---

## 3. New Option Design

- **C++ (`Pattern2StableConfig`):** add `bool AggressiveAngleParallel;`, init `false` in the
  `Pattern2StableConfig(bool)` ctor. Reuse existing `CpuThreads` as the thread ceiling
  (0 = auto = all logical cores).
- **C# (`Patterns.cs`):** add `[OptionalField] public bool AggressiveAngleParallel = false;`
  (`[OptionalField]` is mandatory so old serialized projects still load).
- **Factory:** add `bool aggressiveAngleParallel` param to both `Create(...)` overloads; set
  `cfg.AggressiveAngleParallel = aggressiveAngleParallel;`.

**Semantics when ON (and `EnableCpuMultiThread` true, non-GPU):**
- **P1 (PATT.3):** parallelize the scale loop when `stableScaleBank.size() > 1`. Each scale runs
  `Match()` **single-threaded internally** (pass `useMultiThread=false`) to avoid nested
  oversubscription. Results go to per-index buckets, merged in index order (mirror the existing
  refine-bucket pattern), then global NMS as today. Requires the state-decoupling refactor.
- **P2 (PATT.4):** when a single scale, lift the BK2 cap so
  `coarseThreadCount = min(budget, #angles)` and refine likewise. **Never** change `vecAngles`
  or `dAngleStepAuto` (bit-identical).
- **P3 (PATT.2):** allow bypassing BK2 serialization on `<=4`-core hosts when ON (dev-only benefit).

Priority (post user decision): **PATT.3 = focus**, then PATT.4, then PATT.2.

---

## 4. Task Cards

### PATT.0 — Baseline measurement (mandatory first)
- **In-scope:** none (measurement only) + append to `CODEX_HISTORY.md`.
- **Steps:** run one Pattern tool for 3 cases — (a) narrow range ±10°, (b) wide ±180°,
  (c) scale-search ON — capture `workers/coarseMs/refineMs/totalMs` from `GetLastMatchPerf()`
  and `Environment.ProcessorCount`.
- **DoD:** baseline table recorded; confirms scale-loop and/or angle-cap dominate on this host.

### PATT.1 — Add config field end-to-end (no logic change)
- **In-scope:** `Pattern/Pattern2.h`, `BeeCore/Unit/Patterns.cs`,
  `BeeCore/Algorithm/PatternStableConfigFactory.cs`.
- **Steps:** add field (C++ + ctor init false), C# `[OptionalField]` field, factory param + wire in
  `BuildStableConfig()` (pass `AggressiveAngleParallel`).
- **Verify:** build passes (Release|x64); load an existing sample project → no deserialize error.
- **DoD:** flag flows C#→native, behavior unchanged (still OFF everywhere).

### PATT.3 — Parallelize scale loop (FOCUS, hardest)
- **In-scope:** `Pattern/Pattern2.cpp` (`RunStableMatchOnFeatures`, and `Match()` if a bool param
  is added to thread the flag down).
- **Precondition:** remove shared-state writes at `3907-3910` from the parallel region — pass
  `s_TemplData` + source features as local params to the coarse routine (reuse
  `MatchTopLayerContext`; ensure it reads no mutable `img->` template state).
- **Steps:** gate on `AggressiveAngleParallel && stableScaleBank.size() > 1`; `cv::parallel_for_`
  over scale index; each scale internal single-thread; per-index buckets; merge in order; global
  NMS unchanged. Ceiling = `CpuThreads>0 ? CpuThreads : hostLogicalCores`.
- **Verify:** scale-search ON → `totalMs` drops materially; results **bit-identical** to OFF on
  ≥5 images.
- **Rollback:** if not bit-identical, revert PATT.3, keep scale loop sequential; ship PATT.4 only.
- **Stop-and-ask:** if refactor > ~150 lines or touches another public signature.

### PATT.4 — Lift angle-thread cap for single-scale
- **In-scope:** `Pattern/Pattern2.cpp` (`coarseThreadCount`/`refineThreadCount`, BK2 gate).
- **Steps:** when ON and single scale, do not cap via BK2; keep `min(budget, #angles)`. Do NOT
  alter `vecAngles`/`dAngleStepAuto`.
- **Verify:** wide range → `workers ≈ min(cores, #angles)`; results bit-identical to OFF.

### PATT.2 — Bypass BK2 serialization when ON (dev-only, low priority)
- **In-scope:** `Pattern/Pattern2.cpp` `useBk2Policy` at 3534 (thread the flag via the `Match()`
  param from PATT.3).
- **Steps:** when `AggressiveAngleParallel`, set `useBk2Policy=false` for single-tool runs.
- **Verify:** on a `<=4`-core box, ON → `workers>1`; results bit-identical.

### PATT.5 — UI wiring
- **In-scope:** the Pattern tool editor under `BeeInterface/Tool/` that already exposes
  `UseCpu/UseGpu/ExecutionMode` (locate the existing binding, add one checkbox
  "Advanced multi-thread (Angle)"). Bind to `Patterns.AggressiveAngleParallel`. Use `-=` before
  `+=` on the handler. C# 7.3 only.
- **Verify:** toggle persists through save/load; smoke-run one inspection with it ON.

### PATT.6 — Build, perf compare, close
- **Verify:** `MSBuild EasyVision.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  passes; warnings ≤ baseline in `docs/architecture/baseline_build.md`.
- Record ON-vs-OFF perf table (using PATT.0 numbers) in `CODEX_HISTORY.md`.

---

## 5. Definition of Done (whole feature)
- [ ] Option flows UI → C# → native, default OFF.
- [ ] OFF: behavior + timing unchanged (esp. results).
- [ ] ON: bit-identical to OFF on ≥5 images; `totalMs` lower for many-angle / scale-search cases.
- [ ] Build passes, warnings ≤ baseline.
- [ ] `CODEX_HISTORY.md` has an entry with ON/OFF perf table; `.codex-session.md` updated.
- [ ] Commits split per PATT.x, messages `[PATT.<n>] <summary>`.
