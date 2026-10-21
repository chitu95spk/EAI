# V175 — Barcode "Print quality" section + tile map (handoff, partial continuation)

Status 2026-10-03: **resumed with bounded plan V175-r1-resume; algorithm decisions remain pending.**
Read the Codex continuation and audit below for current evidence. Original stopped-session state:
code was edited but **uncommitted**;
`BeeCore` builds, `BeeInterface` could **not** be built (an unrelated file edited by another session broke it).
Nothing of V175 is in `CODEX_HISTORY.md` yet.

Already committed earlier in this thread (do not redo): V172 Streak Check (`6974ccae`, `6e95d3ed`, `8e686cf2`),
V174 Contrast Check (`1dad6864`). Contracts: `.claude/task-contract.V172.toml`, `.claude/task-contract.V174.toml`
(untracked). **The original stopped session had no V175 contract** — the user authorized, in chat
(AskUserQuestion), "Build + test offline + commit". The current Codex continuation records that
authorization in `.codex/task-contract.V175.toml`; do not create a parallel Claude V175 packet.

## User requests covered here
1. UI per the new agent rules (`CLAUDE.md` 0.2a, `.agents/skills/easyvision-winforms-ui|layout`): gather the print-quality
   checks (Blur, Streak, Contrast) into ONE big section and move it from the Advanced accordion to **Setup**
   (`parameterAccordion1`); backend-advanced parameters stay in Advanced.
2. Image `E:\Data\IK\Image_20261002181243593.bmp` (ink smeared over the top rows, locator finds no box, no decode) must be judged
   **NG "mờ"**, and **all blurred / heavy (đậm) / faded (nhạt) NG areas must be drawn with the Global opacity**
   (`Global.ParaShow.Opacity`).
3. User answers: NG rule = **area share above a % limit** (reuse the existing per-option limits); build + offline test + commit authorized.

## What is in the working tree (all uncommitted)
| File | Change |
| --- | --- |
| `BeeInterface/Tool/ToolBarcode.cs` | Blur/Streak/Contrast no longer create accordion sections. Each builds a block (`CreatePrintQualityBlock`: tinted caption band + `CreateAdvancedGroupRow` rows) `_blockBlurCheck/_blockStreakCheck/_blockContrastCheck`. `ConfigurePrintQualitySection()` creates `sectionPrintQuality` (TabIndex 4, key `tool.barcode.setup.printquality.title`, vi "Đánh giá chất lượng in", en "Print quality") in `parameterAccordion1`, first row = shared **area limit** bar (`_adjQualityAreaLimit`, 1–50 %, default 4, `SyncQualityAreaLimit`, Single-only), header summary `DescribePrintQuality` ("Blur · Streak · Contrast" or "OFF"). Sync* methods use block visibility (Blur + Contrast blocks Single-only, Streak both modes). New unique captions "Bật kiểm tra mất nét / Blur check", "Ngưỡng mất vạch tối đa / Missing-stripe limit", "Bật kiểm tra vệt sọc / Streak check", "Streak min length" (the generic "Min length" collides with `ui.common.minlength.text`). |
| `BeeCore/Algorithm/BarcodePrintQualityMap.cs` (new) + 1 Compile line in `BeeCore/BeeCore.csproj` | `BarcodePrintQualityMap.Analyze(gray, areaWorld, darkLevel, lightLevel, blurOn, fadedOn, fadedLimit, heavyOn, heavyLimit, areaLimit)` → immutable `PrintQualityMapResult` (tiles in Area-Check patch pixels). Area warped to an upright native-scale patch (`WarpAffine`, world = center + R(angle)·(patch − size/2), same chain as `AreaLimitToWorld`/DrawResult). ~36×36 tiles (≥16 px). Per tile (histogram): uniform mid-grey → **Blur**; non-uniform with ≥55 % mid-grey → **Blur**; else ink-class median > dark+faded% of contrast → **Faded**; paper-class median < light−heavy% → **Heavy** (or ink darker than sample−heavy%). 4-connected clusters < 4 tiles are dropped. Share = tiles / "active" tiles (ink or edge tiles). NG if an enabled category exceeds the limit. Does **not** need a decode or box. |
| `BeeCore/Unit/BarCode.cs` | Fields `_sampleDarkLevel/_sampleLightLevel` (taught ink/paper medians, `[OptionalField]`), `_qualityAreaLimitPercent` (property `QualityAreaLimitPercent`, default 4), `[NonSerialized] _qualityMap/LastQualitySummary/_qualityNg`. `TryMeasureInkLevels` (+ `TryMeasureInkContrast` wrapper). Levels taught in `RebuildSinglePreview` next to `_contrastSample`. **`ClearBlurSample` no longer clears the contrast/levels** (it is called from the lazy `BuildBlurSample`, which wiped the sample whenever Blur ran with positioning — real bug found in testing); new `ClearTeachSamples()` used at Detect / ResetTeachSelection / AutoTemp. `DoWorkSingle`: new block just before `listRegionOk.Add(ok)` runs the map when Single and (Blur or a contrast option) is ON, even if the decode failed; NG sets `ok=false`. `DrawQualityMap` fills tiles (Blur DarkOrange, Heavy RoyalBlue, Faded Gold) with alpha = `ParaShow.Opacity*2.55`; `DrawContrastStatus` also prints `LastQualitySummary`. |
| `docs/architecture/tasks/2026-10-03-V175-assets/` | Harness sources and the tile-map prototype (see Evidence). |

## Evidence so far
* `BeeCore` isolated build: 0 errors (`MSBuild BeeCore\BeeCore.csproj /p:Configuration=Release /p:Platform=x64 /p:EasyVisionIsolatedVerification=true`).
* Offline reflection harness (`harness-teach-run-draw.cs`: locator candidate → `RebuildSinglePreview` → `DoWorkSingle`, then `DrawQualityMap`),
  teach = sharp `181135709`, limit 4 %: sharp `181045057` 0 % / `181135709` 0 % / `181217090` faded 2.7 % → OK;
  blurry `181123605` NG (blur 23.6 %, faded 21.2 %), `181157467` NG (blur 4.5 %, faded 39.1 %); smeared `181243593` **NG blur 5.9 %**.
  Rendered overlay (opacity 45): `artifacts/V172/qualitymap_sheet.png` (smear orange, blurry codes orange/yellow, sharp clean).
* Teach from blurry `181123605`: sharp images → heavy 50–70 %, NG (heavy path works).
* Python prototype `tilemap-prototype.py` (needs scipy) was used to pick the constants; the C# numbers differ slightly (percentiles).

## NOT done / for the next session
1. **Build `BeeInterface`**: blocked at the time by `BeeInterface/LayoutPersistence.cs(54,26) CS0104 'Timer' ambiguous` — another session's in-progress edit
   (not ours, do not touch). Rebuild when it is fixed: `MSBuild BeeInterface\BeeInterface.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /p:EasyVisionIsolatedVerification=true /v:minimal > log`.
   If it stays broken, verify in a clean worktree of HEAD with our 4 files copied in (a worktree attempt was started and removed; it worked as far as checkout).
   The ToolBarcode UI regroup has **never been compiled**.
2. Independent review of V175 was started and **stopped** — run `easyvision-reviewer` (read-only) on the files above. Points to ask: tile/Warp mapping vs draw transform, active-tile denominator,
   `processedGray` must be 8UC1 (the map skips otherwise), legacy `[OptionalField]` safety, Single-only gating, no leftover references to the removed `_section*Check` fields, taught-levels basis (the
   locator candidate box is ~1200 px and includes paper, so "paper level" is the paper median — fine for these frames, check on other layouts).
3. UI smoke (not possible offline): section in Setup, order after Sample; Blur/Contrast blocks + area row hidden in Multi, Streak visible; header summary; vi/en captions; narrow width / 125–150 % DPI;
   status text; tiles at several opacities in edit and run view.
4. `CODEX_HISTORY.md` entry (template in `docs/architecture/refactor-plan.md` 11.2), then commit **only** `BeeCore/Algorithm/BarcodePrintQualityMap.cs`, `BeeCore/BeeCore.csproj`,
   `BeeCore/Unit/BarCode.cs`, `BeeInterface/Tool/ToolBarcode.cs`, `CODEX_HISTORY.md` (+ this doc if wanted). `git add` by name; the tree has many unrelated dirty files and other sessions are editing BeeInterface.
5. Open observations to decide on:
   * Default area limit 4 % is a compromise (OK print `181217090` is 2.7 %, smear is 5.9 %); expose/tune with real OK/NG sets.
   * With positioning ON, the pre-existing Blur mask check NGs even sharp images in the harness (sample rebuilt lazily from the stitched preview `bmRaw`, which contains caption padding). Not caused by V175 but worth a separate look.
   * Faded/Heavy per-option limits are now also the tile-level tolerance (% of contrast); the global contrast-deviation check from V174 is unchanged and still runs on decoded 2D codes.
   * Multi mode has no contrast/blur map. Old projects need the code selected again (no taught levels until `RebuildSinglePreview` runs).
   * The earlier request "the drawn lines/light region are offset in DrawResult" (Streak lines) was **not reproduced** offline; needs a screenshot of Run vs Edit view.
   * Exposure/gain changes read as a contrast deviation.

## Resume checklist
```
git status --short BeeCore BeeInterface/Tool/ToolBarcode.cs      # expect the 4 files above
bash tools/check_propety_tools.sh
# build BeeCore, then BeeInterface (see 1), then rerun harness (teach 181135709 / 181123605) from the assets dir
```
Harness build/run recipe (scratchpad of the original session is gone after cleanup): compile `harness-teach-run-draw.cs` with the Roslyn `csc.exe` (`-langversion:7.3 -platform:x64`,
references BeeCore/BeeGlobal/BeeCpp/OpenCvSharp/OpenCvSharp.Extensions/System.Drawing from `artifacts\verification\Release\x64\bin\BeeCore`), run with `bin\Release` on PATH; args: `<teachSuffix> 25 matrix|pos`.
Images: `E:\Data\IK` (sharp: …181045057, …181135709, …181217090; blurry: …181123605, …181157467; smeared/undecodable: …181243593).

## 2026-10-03 Codex continuation: V175-r1-resume

The current user requested review of this handoff and continued delegation. The existing build,
offline-test and commit authorization above is preserved. The current runtime uses the Codex
backend: `.codex/task-contract.V175.toml` is the V175 packet; no parallel Claude writer or Work
activation was created. The former stopped-state text above remains historical evidence.

Discovery used read-only Explorer and Algorithm agents, followed by the Architect. The exact
bounded plan is **V175-r1-resume**, approved by direct-local scoped review. One Implementer owns
`BarCode.cs`, `BarcodePrintQualityMap.cs`, and the existing V175 harness assets. Root owns this
handoff and authorized tracking. Existing ToolBarcode UI and the V175 csproj compile item stay in
review scope; the unrelated Craft include must not be staged.

Approved corrections: normalize supported BGRA input in Scan, DoWork and positioning; preserve
Mat ownership; invalidate/guard stale map output when options or mode differ (including late worker
publication); allow unchanged Faded/Heavy branches when Blur is disabled. Preserve C# 7.3,
serialized/public/native contracts, enabled-Blur precedence, all thresholds, active denominator,
cluster filtering, default area limit, strict `>` gate, warp/draw coordinates and V172/V174 behavior.

Verification: isolated BeeCore and BeeInterface Release/x64 builds with
`EasyVisionIsolatedVerification=true` and `BuildProjectReferences=false`, logs under
`artifacts/V175/`; Roslyn C# 7.3 offline harness with fixture/assembly identity, original six-image
probes, frame-format parity, disabled-Blur contrast and stale-output probes; scoped static checks;
independent Reviewer. No live UI, hardware, native build, deployment, benchmark, branch or worktree.

Full V175 acceptance and commit are held. The active-tile denominator can include non-print dark
background; severe faded classes can cross the taught midpoint and disappear; right/bottom strips
are omitted, and counting tiny remainder tiles equally would misstate area. These need a new exact
semantic plan. A concrete user choice is pending: taught print occupancy (with approved teach/store
expansion), pixel area of the whole Area Check with undecidable quality treated as NG, or safe
integration corrections only. The unrelated LayoutPersistence Timer scope request is independently
pending. Do not mark V175 done from this partial revision.

## Final bounded audit — V175-r1-resume (2026-10-03)

**Task and authority:** V175, direct continuation of the named handoff. Its prior user grant for
build, offline test and commit is preserved; tracking/history were included in the requested
continuation. No Work activation or approval was fabricated. The current original prompt is
preserved in the Codex packet. Independent Reviewer **ACCEPTED the bounded revision** after one
ownership fix cycle. **Overall V175 remains partial/BLOCKED; no commit was performed.**

**Approved scope and invariants:** Finish the existing Setup UI/map integration plus BGRA
normalization, generation-validated stale publication/display, and disabled-Blur fallthrough.
Preserve C# 7.3, existing serialized/public/native contracts, Mat ownership, acquisition lifecycle,
production controls/events/localization, V172/V174, enabled-Blur precedence, thresholds, cluster
rules, active denominator, default 4% area gate, strict greater-than comparison and warp/draw
coordinates. No taught occupancy representation, denominator/midpoint or edge-weighting redesign.

**Exact task source files:** `BeeCore/Algorithm/BarcodePrintQualityMap.cs`;
`BeeCore/BeeCore.csproj` (only the pre-existing V175 compile item is part of this task);
`BeeCore/Unit/BarCode.cs`; `BeeInterface/Tool/ToolBarcode.cs` (existing V175 Setup regroup);
`docs/architecture/tasks/2026-10-03-V175-assets/harness-production-quality.cs`.
This continuation edited only the map, BarCode and the new harness. The csproj and ToolBarcode
retained their initial hashes; the unrelated Craft compile item stays excluded from V175.
Metadata/tracking: this handoff, `.codex/task-contract.V175.toml`, `CODEX_HISTORY.md`,
`.codex-session.md`, and the V175-only row/allocation metadata in `.vision-map.md`.

**Verification commands and results:** all commands used this workspace. MSBuild executable:
`C:/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/MSBuild.exe`.

- `MSBuild.exe BeeCore/BeeCore.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /p:EasyVisionIsolatedVerification=true /p:BuildProjectReferences=false /v:minimal`
  -> `artifacts/V175/build-BeeCore-reviewfix1.log`: exit 0, 0 errors, 144 warnings.
- Same executable/options with `BeeInterface/BeeInterface.csproj`
  -> `artifacts/V175/build-BeeInterface.log`: exit 0, 0 errors, 248 warnings.
  The former Timer blocker was resolved by an external alias edit; V175 never edited or stages
  `LayoutPersistence.cs`. No warning-baseline equivalence is claimed.
- Roslyn executable `C:/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/Roslyn/csc.exe`,
  from `artifacts/verification/Release/x64/bin/BeeCore`, with
  `/nologo /langversion:7.3 /platform:x64 /target:exe`,
  output `V175-harness.exe`, source the harness above, and
  `/reference:BeeCore.dll /reference:BeeGlobal.dll /reference:BeeCpp.dll /reference:OpenCvSharp.dll /reference:OpenCvSharp.Extensions.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll`
  -> `artifacts/V175/harness-build-attempt3.log`: exit 0, 0 errors/warnings.
- Final runtime, working directory `artifacts/V175`:
  `& 'E:/Code/EasyVision_Unisen - NoService/artifacts/verification/Release/x64/bin/BeeCore/V175-harness.exe' 'E:/Data/IK' 'E:/Code/EasyVision_Unisen - NoService/artifacts/verification/Release/x64/bin/BeeCore/BeeCore.dll'`
  -> `harness-production-attempt4.log`: exit 0, 10.356 s, explicit expected-path assertion passed.
  Read-only `bin/Release` was temporarily prepended to process PATH for existing native dependencies,
  then PATH was restored. No Complete/result-send or hardware method was used.
- Scoped `git diff --check`, conflict-marker, language-version/C# 7.3 and declaration/scope inspection:
  PASS. The actual managed builds and harness compiler used C# 7.3; LangVersion was not raised.
- `C:/Program Files/Git/bin/bash.exe tools/check_propety_tools.sh`: exit 1 for six pre-existing
  accesses in unrelated `DataTool.cs`, `Diagnostics/MonitorPanel.cs`, and `Group/View.cs`;
  zero new scoped direct accesses. This global guard is not reported as PASS.

**Offline evidence:** six original sharp/blurry/smeared grayscale fixtures; real public
Scan/Toggle/Refresh/DoWork Gray/BGR/BGRA verdict and summary parity; option/mode ABA and deterministic
late old-publication reinsertion with summary/overlay suppression and current recompute;
disabled-Blur Faded classification and enabled-Blur precedence. Final run loaded the final
isolated BeeCore SHA-256
`2361F15F1DC5BA4304A93B3A7B9860897E924A86F3A28207938F183827569F1B`.
Fixture hashes and all earlier attempts are preserved. Runtime passes cover the bounded revision,
including the final ownership cleanup; they do not establish the held semantic cases.

**First distinct errors and fix response:** no compiler/root errors found. Harness attempt 1
used a doubled timestamp prefix; corrected the shared path helper. Attempt 2 omitted re-teaching
after ChangeTeachMode cleared selection; restored the real lifecycle on the same Barcode.
Attempt 3 passed but lacked the explicit expected-assembly argument; attempt 4 supplied it and
passed on the final Core. Reviewer cycle 1 found the conversion helper could leak Mat on a
CvtColor exception before returning ownership; fixed by disposing/rethrowing inside the helper,
then focused Core compile and independent recheck passed.

**Preservation and residual risks:** live Core/Interface hashes matched the initial snapshot
after the first isolated builds, then drifted during concurrent external work before attempt 2.
All V175 build commands remained isolated; final live-binary equality is not claimed and no
external change was reverted. No native/solution build, branch/worktree, deployment, benchmark,
hardware, commit or push occurred. Live UI, DPI/font/vi-en checks, rotated overlay runtime parity,
legacy real-project smoke and conversion exception injection remain unverified. A direct external
ModeCheck field ABA can bypass ChangeTeachMode. Existing recipe levels must be re-taught when absent.

**Next assignment gate:** remain on V175. Pending user choice defines printed/taught occupancy
versus the pixel area of the entire Area Check (undecidable quality becomes NG), or permits only
this completed bounded patch. Architect must produce the next exact revision before changing
teach/storage, denominator, severe-Faded midpoint behavior or right/bottom remainder weighting.
Do not mark V175 done or commit from the limited fixture evidence. The earlier Timer scope
question is obsolete because the external fix compiled; no Timer edit permission is needed here.

## User clarification and approved V175-r2-sample

The user clarified that the reference region is the real selected Barcode Sample for Single or
Multi. This resolves the denominator choice: use the complete sample footprint, including its
paper pixels, not all Area Check pixels or an intensity-selected subset. Reuse the production
persisted listRotScan geometry; no exact barcode-bit occupancy mask or parallel sample type.

Read-only Explorer/Algorithm discovery and Architect decision produced V175-r2-sample. Direct-local
scoped review approves Single implementation in BarCode.cs, BarcodePrintQualityMap.cs and the
existing harness assets; root owns packet/audit/tracking. Single selects exactly one Center-anchored
listRotScan sample and applies the existing ParentLocalToWorld(selected,rotAreaRun) transform.
Decode failure does not erase that footprint. Invalid/ambiguous/out-of-frame footprint is unchecked
with a note, without guessing a box or adding a new fail-closed policy.

Use whole-footprint pixel denominator, ceiling grid, true clipped tile pixel numerators and
matching clipped draw extents. Connected clusters retain the old four-full-tile equivalent minimum
as pixel area. Enabled Blur remains first with unchanged predicates. Otherwise use reliable
tile-local Otsu class medians (minimum five-level separation and existing class fractions), retaining
taught-level single-class fallback if degenerate. Entirely uninformative samples without an
affirmative enabled defect return unchecked, not assessed OK. Disabled Faded/Heavy cannot hide
another enabled category. Retain the 4% strict greater-than gate, option tolerances, tile sizing,
C# 7.3, existing persistence/native/API/Mat ownership and r1 no-lock publication protections.

Mandatory offline probes: outside-background invariance, real selected-sample transforms/overlay,
local severe Faded/Heavy amid sharp printing, changed crisp content, exact area/cluster boundaries,
full edge coverage with weighted percentages/drawing, uniform-input indeterminate state, r1
format/cache regressions and all six real fixtures. The specified smear must remain NG Blur at
unchanged 4%; if fixture acceptance fails, stop for diagnosis/new plan, never secretly retune.
Use authorized focused isolated managed builds with BuildProjectReferences=false; the newly
available build helper forces project references and cannot run within this native-excluded packet.

Multi association remains a separate pending user choice: evaluate taught positions under the
existing parent transform, or only boxes located during runtime. The current production Multi path
uses selected samples for expected content, not decode-independent quality association. Do not
add Multi maps, UI exposure, aggregate calibration or new stored sample associations without that
decision and an exact next revision. A coherent checked Single part may be committed under the
current repository completed-part rules; full V175 must remain partial while Multi is held.

### r2 first verification and bounded harness correction

Focused isolated Core and Interface builds passed with zero compiler errors (144 and 248
warnings respectively); r2 binlogs exist. C# 7.3 harness compilation passed. The first runtime
attempt stopped at sharp fixture 181045057 because its map was unchecked, with default result
geometry. No later probe ran and no semantic acceptance is claimed.

Read-only diagnosis found that the harness replaced rotArea directly after ToggleDetectedCandidate
had rebased the selected sample into its production-selected parent. Architect approved a
harness-only correction within V175-r2-sample: remove that post-toggle parent overwrite, retain
initial scan setup and real RefreshSinglePreview, log local/parent/world geometry, taught levels
and map Note, and assert usable taught contrast. Reviewer must confirm the bounded correction
before recompiling/rerunning against the same verified isolated assemblies. No algorithm,
threshold, selected candidate or region-size change is authorized by this correction.
### r2 coverage diagnosis and pending user policy

The corrected harness retained real selected geometry and taught levels (dark 38, light 142),
but attempt 2 again stopped at sharp fixture 181045057: selected sample outside the frame.
All six BMP fixtures are 2448x2048. The selected sample world geometry is center (1850,982),
size 1228.42x1165.011, angle 179.344 degrees; rounded patch 1228x1165. Its sampled corners
are approximately (2470.629,1557.432), (1243.709,1571.480), (1230.383,407.556),
(2457.302,393.508). The first and fourth corners exceed the last valid x=2447 by 23.629 and
10.302 pixels. Architect and Reviewer independently confirm true incomplete coverage, not an
incorrect containment check. Teach constant-border crop can still yield nonzero taught levels.

No production correction, sample shrink/change, border fabrication, assertion weakening or
threshold retuning is approved. Full r2 acceptance is BLOCKED. A user question is pending:
retain full-sample coverage and report clipped samples unchecked, or approve a visible-intersection
coverage mask/denominator through an exact new r3 plan. The current fixture setup remains intact.
Root authorized only existing independent synthetic probes as explicitly partial evidence;
they do not replace the failed real-fixture gate. Multi association remains separately pending.
## V175-r2-sample audit (2026-10-03 05:20 UTC)

**Scope and invariants:** Direct user clarification defines the actual selected Barcode Sample.
Deep Architect approved Single-only r2; Multi association remains pending. Exact changed source:
BeeCore/Unit/BarCode.cs, BeeCore/Algorithm/BarcodePrintQualityMap.cs, and
 docs/architecture/tasks/2026-10-03-V175-assets/harness-production-quality.cs.
Existing V175-only csproj compile item and ToolBarcode UI diff remain review scope, unchanged by
r2; unrelated Craft csproj item and other concurrent work are excluded. Root updated the existing
V175 contract/handoff/tracking. Preserve C# 7.3, public/persisted/native contracts, r1 BGRA/no-lock
publication/ownership, V172/V174, actual selected geometry, Blur precedence, default 4% strict >
and option tolerances. No threshold retuning or sample manipulation occurred.

**Verification commands and results:** All execution used the permitted workspace, isolated
managed outputs, task-local TEMP/TMP and restored process environment. Commands below run from
repository root unless stated otherwise.

```powershell
& 'C:/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/MSBuild.exe' BeeCore/BeeCore.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /p:EasyVisionIsolatedVerification=true /p:BuildProjectReferences=false /v:minimal '/bl:E:/Code/EasyVision_Unisen - NoService/artifacts/V175/r2/BeeCore-r2.binlog' > artifacts/V175/r2/build-BeeCore-r2.log 2>&1
& 'C:/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/MSBuild.exe' BeeInterface/BeeInterface.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /p:EasyVisionIsolatedVerification=true /p:BuildProjectReferences=false /v:minimal '/bl:E:/Code/EasyVision_Unisen - NoService/artifacts/V175/r2/BeeInterface-r2.binlog' > artifacts/V175/r2/build-BeeInterface-r2.log 2>&1
```

Core PASS: exit 0, zero errors, 144 warnings; Interface PASS: exit 0, zero errors, 248 warnings.
Binlogs exist (668556 and 719796 bytes). These counts are observed, not a new warning baseline.
No native/reference/solution build ran. From artifacts/verification/Release/x64/bin/BeeCore:

```powershell
& 'C:/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/Roslyn/csc.exe' /nologo /langversion:7.3 /platform:x64 /target:exe '/out:E:/Code/EasyVision_Unisen - NoService/artifacts/verification/Release/x64/bin/BeeCore/V175-harness-r2-synthetic.exe' 'E:/Code/EasyVision_Unisen - NoService/docs/architecture/tasks/2026-10-03-V175-assets/harness-production-quality.cs' /reference:BeeCore.dll /reference:BeeGlobal.dll /reference:BeeCpp.dll /reference:OpenCvSharp.dll /reference:OpenCvSharp.Extensions.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll > 'E:/Code/EasyVision_Unisen - NoService/artifacts/V175/r2/harness-build-r2-synthetic.log' 2>&1
```

All three r2 harness compiles passed with zero errors/warnings. First full run used
V175-harness-r2.exe; corrected full run used V175-harness-r2-attempt2.exe. From artifacts/V175/r2,
with resolved bin/Release temporarily prepended to PATH and restored afterward:

```powershell
& 'E:/Code/EasyVision_Unisen - NoService/artifacts/verification/Release/x64/bin/BeeCore/V175-harness-r2-attempt2.exe' 'E:/Data/IK' 'E:/Code/EasyVision_Unisen - NoService/artifacts/verification/Release/x64/bin/BeeCore/BeeCore.dll' > harness-production-r2-attempt2.log 2>&1
& 'E:/Code/EasyVision_Unisen - NoService/artifacts/verification/Release/x64/bin/BeeCore/V175-harness-r2-synthetic.exe' 'E:/Data/IK' 'E:/Code/EasyVision_Unisen - NoService/artifacts/verification/Release/x64/bin/BeeCore/BeeCore.dll' --synthetic-only > harness-production-r2-synthetic.log 2>&1
```

Both full runs STOPPED at the first assertion mismatch. Initial harness had an invalid post-toggle
parent overwrite; Architect/Reviewer approved its harness-only lifecycle correction. Corrected
run proved valid sample/taught levels but stopped at true outside-frame coverage, as documented
above. No later full-run groups executed. Synthetic-only PASS (exit 0, 0.493s) is explicit partial
evidence: disabled-Blur Faded fallthrough/enabled-Blur precedence, uniform paper unchecked,
30/70 local Faded/Heavy 1024 pixels of 4096, changed crisp content, strict 4% pass/5% NG,
65x64 clipped-edge actual area (1040 pixels and one-pixel edge), outside-background invariance.
Real six fixtures, Gray/BGR/BGRA and fixture-dependent ABA/publication/overlay probes were skipped
in that mode. Their old r1 passes do not prove revised r2 geometry behavior.

Loaded expected isolated Core path and SHA256 were asserted:
C3F261ABBD4DCA5B832507AB297830AF30D14A1A5DE4A568A0F32BD6326A2CA8.
Scoped diff/whitespace, conflict-marker, edited-scope/declaration and C# 8+ candidate checks PASS;
compiler evidence confirms C# 7.3 harness and managed projects. First compiler/root errors:
none found. Runtime first distinct roots: inconsistent harness parent (fixed); true outside-frame
sample causes unchanged sharp-fixture Checked/OK assertion to fail (unresolved semantic gate).
No parser cascades occurred. Global property guard was not rerun; r1's six unrelated failures
remain separate. Final live binary preservation is not claimed amid external concurrent changes.

**Residual risks/status:** Full V175 acceptance BLOCKED; no commit/push/deploy/hardware/benchmark
or live GUI run. Rotation/draw parity, complete real fixtures, r2 format/cache regression,
legacy project smoke and Multi maps remain unverified. Footprint tile labels approximate defect
pixel area; completely missing ink cannot be inferred from content-free footprint alone. Hold
until user coverage policy and Multi association decisions produce an exact approved next plan.

**Independent final review:** BLOCKED; no separate production defect found. The true coverage conflict prevents mandatory real-fixture acceptance. No commit or done tracking. Partial synthetic results cannot replace real/format/cache evidence.

## Pending clarification: no box must be NG

The user added: "khi k co khung nao NG lun" (when there is no box, immediately NG).
Root requested the precise meaning: no runtime barcode box, no valid selected Barcode Sample
including an out-of-frame sample, or both. Read-only Flow and Architect are reviewing the
existing Single/Multi verdict paths. No source edits or new plan approval follow from an
unanswered interpretation. In particular, an existing box partly outside the image is not
necessarily an absent box; r2 coverage and Multi association decisions are not silently resolved.

## Approved V175-r3-no-runtime-box scoped plan

User instruction: when there is no box, immediately NG. Root applies the literal runtime barcode
box meaning after source discovery; the optional precision question was unanswered. This does
not approve blanket quality-unchecked NG or resolve an existing sample extending beyond the image.
Flow found nonempty native payloads can pass managed matching without reported rectangles.
Architect approved the minimal geometry gate: Single needs any valid positioning/ZXing/matrix
runtime rectangle; Multi returns NG for null/empty/all-invalid rectangle list. Valid means finite
coordinates/dimensions/angle and positive size; thin scan bands and partly out-of-frame boxes
remain present. Preserve existing content rules and retry/reset lifecycle. No per-payload pairing,
new state/locks, serialization/public/native/API changes or sample/padding/threshold changes.

Sole writer: BarCode.cs and existing production-quality harness; scratch artifacts/V175/r3.
A small private Single decision helper is allowed to retain existing content branches plus the
geometry guard and directly verify them without native result injection. Harness adds an explicit
focused no-box mode with exact isolated identity; default r2 fixture gates remain unchanged.
Test missing/empty/nonfinite/nonpositive runtime geometry despite payload, valid geometry with
normal matching, old/current Single/Multi modes, and existing independent synthetic map gates.
Prior authorized focused isolated builds and offline tests continue with BuildProjectReferences=false
and binlogs; no native/hardware/live GUI/benchmark/deployment. Independent review required.
Full V175 remains partial while r2 coverage and Multi quality association are unresolved.

## V175-r3-no-runtime-box code audit

**Approved scope and preserved invariants:** Current user instruction requires NG when no runtime
barcode box exists. Architect approved the direct-local r3 plan. Single now requires at least one
finite positive positioning/ZXing/matrix rectangle before its existing content rules; Multi NGs
null/empty/all-invalid native rectangle lists before its existing matching/count rules. Thin
positive and partially out-of-frame rectangles remain present. Taught/drawing boxes cannot satisfy
the gate. Preserve C# 7.3, public/persisted/native API, ownership, camera lifecycle, no-new-lock
publication, r1 BGRA/generation and all r2 map classification/4%/sample coverage rules.

**Exact changed files:** BeeCore/Unit/BarCode.cs and
 docs/architecture/tasks/2026-10-03-V175-assets/harness-production-quality.cs.
Root metadata changes: existing V175 contract, handoff, History/session and V175 map row.
Map/csproj/ToolBarcode hashes match r3 initial snapshots; no new native/UI/storage edits.
The writer inspected its scoped declarations/diff and supplied the handoff before verification.
Private production Single decision helper is used by the actual DoWorkSingle path; focused
harness reflects that helper and EvaluateMulti, not a duplicate matching implementation.

**Commands/results:** Focused builds, all logs redirected; task-local TEMP/TMP and restored PATH;
no native/reference build. Run from repository root:

```powershell
& 'C:/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/MSBuild.exe' BeeCore/BeeCore.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /p:EasyVisionIsolatedVerification=true /p:BuildProjectReferences=false /v:minimal '/bl:E:/Code/EasyVision_Unisen - NoService/artifacts/V175/r3/BeeCore-r3.binlog' > artifacts/V175/r3/build-BeeCore-r3.log 2>&1
& 'C:/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/MSBuild.exe' BeeInterface/BeeInterface.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /p:EasyVisionIsolatedVerification=true /p:BuildProjectReferences=false /v:minimal '/bl:E:/Code/EasyVision_Unisen - NoService/artifacts/V175/r3/BeeInterface-r3.binlog' > artifacts/V175/r3/build-BeeInterface-r3.log 2>&1
```

Core PASS, exit0, zero errors/144 warnings; Interface PASS, exit0, zero errors/248 warnings.
Binlogs exist: 668957 and720540 bytes. Counts are observed rather than a new baseline.
Harness compile from artifacts/verification/Release/x64/bin/BeeCore:

```powershell
& 'C:/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/Roslyn/csc.exe' /nologo /langversion:7.3 /platform:x64 /target:exe '/out:E:/Code/EasyVision_Unisen - NoService/artifacts/verification/Release/x64/bin/BeeCore/V175-harness-r3.exe' 'E:/Code/EasyVision_Unisen - NoService/docs/architecture/tasks/2026-10-03-V175-assets/harness-production-quality.cs' /reference:BeeCore.dll /reference:BeeGlobal.dll /reference:BeeCpp.dll /reference:OpenCvSharp.dll /reference:OpenCvSharp.Extensions.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll > 'E:/Code/EasyVision_Unisen - NoService/artifacts/V175/r3/harness-build-r3.log' 2>&1
```

Compile PASS, zero errors/warnings, explicit C#7.3. From artifacts/V175/r3 with read-only
repository bin/Release prepended to PATH temporarily and restored:

```powershell
& 'E:/Code/EasyVision_Unisen - NoService/artifacts/verification/Release/x64/bin/BeeCore/V175-harness-r3.exe' 'E:/Data/IK' 'E:/Code/EasyVision_Unisen - NoService/artifacts/verification/Release/x64/bin/BeeCore/BeeCore.dll' --no-runtime-box-only > harness-no-runtime-box-r3.log 2>&1
& 'E:/Code/EasyVision_Unisen - NoService/artifacts/verification/Release/x64/bin/BeeCore/V175-harness-r3.exe' 'E:/Data/IK' 'E:/Code/EasyVision_Unisen - NoService/artifacts/verification/Release/x64/bin/BeeCore/BeeCore.dll' --synthetic-only > harness-synthetic-r3.log 2>&1
```

Both runtime modes PASS, exit0 (0.829s and0.963s). Explicit expected isolated Core path and
SHA256 asserted in both:
27F6B3B6864BB563F6AFAEA6BD5A8EDA15DB04CAD154F9F149298A673E07B97E.
Focused gates cover nonempty payload with null/zero/NaN/infinite geometry NG, thin/partly-outside
positive boxes present, valid Single positioning/fallback/matrix with unchanged exact/contains/
readable modes, mismatch/empty content NG, and Multi null/empty/all-invalid NG plus one valid box
preserving existing matching rules. Retained independent map synthetics pass (Blur precedence,
local Faded/Heavy, uniform unchecked, crisp content, strict4%/5%, clipped edge, background invariance).

**Static/compiler assumptions and root errors:** Scoped diff/whitespace PASS; conflict and C#8+
candidate searches no matches; edited declaration/brace scope self-review PASS. Managed projects
compile with current configured language; explicit harness /langversion:7.3; no LangVersion change.
First distinct compiler or runtime root errors: none found in r3 focused verification.

**Residual risks and exclusions:** Payload-only decodes intentionally become NG. Rectangle
presence does not establish payload association or localization accuracy. Public blank-frame
production inspection was not run; focused helpers provide deterministic managed verdict evidence.
Real six fixtures, Gray/BGR/BGRA, fixture-dependent ABA/publication/overlay, rendered UI/rotation,
legacy load, hardware/native/liveGUI/benchmark remain unverified for r3. R2 real selected sample
coverage remains blocked and Multi quality association remains held. Existing default fixture
assertions were not weakened. Full V175 is partial; no combined unfinished V175 commit, push or
deployment. External staged skill changes and unrelated dirty files are preserved.

### r3 independent review cycle 1: primary 1D box propagation

Reviewer returned FIX_REQUIRED: DecodeSingleRoi receives a primary 1D `located` rectangle from
TryDecodeOnce but drops it when returning the successful payload. The new Single gate sees only
positioning/fallback rectangles and can incorrectly reject a valid 1D code with positioning off.
Existing helper-only verdict probes did not exercise this production propagation path.

Root approves the smallest in-scope correction under V175-r3-no-runtime-box: thread the primary
runtime rectangle into the presence decision, preserving decoder priority and existing drawing
semantics. Add a real primary-1D production-path regression probe with positioning off. No new
stored state, native call/API, map change, threshold, geometry manipulation or UI edit. Same
Implementer performs the fix, authorized focused verification follows, and the same r3 Reviewer
must recheck. Prior PASS runs establish the tested helpers, not the missing production path.

### Cycle 1 verification fixture approval

Scoped discovery found no suitable 1D fixture/generator in the existing recorded matrix-code
fixtures. Root approved a fixed test-only Code39 bitmap within the existing harness, with no
new dependency, production encoder or native-result injection. The six symbol patterns for
*V175* are 0x094,0x0C1,0x121,0x025,0x130,0x094; render nine alternating runs per symbol,
wide9/narrow3 pixels, quiet margins and inter-symbol gaps. Pattern provenance is the authoritative
[ZXing.Net Code39Reader source](https://github.com/micjahn/ZXing.Net/blob/master/Source/lib/oned/Code39Reader.cs)
(character encodings and start/stop symbol). This is fixture construction, not a new product
encoding capability. Verify actual primary decoder and DoWork with positioning/quality off;
assert primary geometry exists, fallback paths were unnecessary and the existing verdict passes.
A subsequent blank frame on the same object should return NG without Complete/external output.

### Cycle 1 compiler check and in-scope correction

Focused Core reviewfix1 build stopped before harness compilation/runtime: CS0103 for
`hasDecodedContent` at the two existing drawing branches (lines3578/3589 in that revision).
Both errors are one missing caller-local root, not separate parser failures. Binlog and failed
log are preserved at artifacts/V175/r3/BeeCore-r3-reviewfix1.binlog and
build-BeeCore-r3-reviewfix1.log. No new assembly was produced; old SHA was not treated as proof
of the failed source. The same Implementer restored the local after decode and reviewed the
entire DoWorkSingle declaration/drawing scope. This is the same review finding's correction,
not an out-of-scope edit or new geometry/drawing policy. Reverification is pending.

### Final r3 cycle-1 acceptance and completed-part commit scope

Reviewfix2 focused isolated Core build PASS (0 errors/144 warnings), C#7.3 harness compile
PASS (0 errors/warnings), no-runtime-box and independent synthetic modes PASS on final Core
SHA256 916562818781BB3E3DBF45C74206D43C4F7B40BB2D4364419739D7C0CEB3DDEE.
The Code39 test asserted a finite positive primary first-pass rectangle, null retry/matrix outputs,
public positioning-off/quality-off DoWork OK, and subsequent same-object blank-frame NG.
It logs content V175 but does not print rectangle coordinates; do not report coordinates as
runtime log values. The prior missing hasDecodedContent root is fixed; final compiler errors none.

Final commands are the r3 audit commands with these exact substitutions:
Core binlog/log: BeeCore-r3-reviewfix2.binlog / build-BeeCore-r3-reviewfix2.log;
harness output: V175-harness-r3-reviewfix2.exe;
compiler log: harness-build-r3-reviewfix2.log;
runtime logs: harness-no-runtime-box-r3-reviewfix2.log and harness-synthetic-r3-reviewfix2.log.
The same projects, no-reference/isolated switches, C#7.3 references, two identity arguments and
focused third-argument modes were used. Core binlog exists (668257 bytes). Interface was not
rebuilt for private Core-only cycle1 changes; earlier r3 Interface PASS 0 errors/248 warnings
is retained explicitly. Public blank-frame/primary-1D paths are now verified; full real six
fixtures, format/cache/ABA/rendered/legacy evidence remains unverified and r2 clipping blocked.

Independent Reviewer ACCEPT for bounded V175-r3-no-runtime-box after cycle1. Finding withdrawn
because primary geometry reaches the gate through method-local/out values; display priority,
old private wrappers and content matching remain unchanged. No outstanding r3 source finding.

Root isolated the r3-only BarCode delta relative to the pre-r3 snapshot. It applies cleanly to
HEAD, has no references to the unfinished map implementation, and passes staged whitespace.
Only this coherent 85-addition/10-deletion production part may be committed under current main
standing authorization. The full V175 harness and existing audit/tracking remain local for the
unfinished V175 packet; do not commit r2 source/UI/project hunks, unrelated staged skill changes,
or verification artifacts. Full V175 remains partial. The actual shared index is preserved using
a project-local alternate index; no branch/worktree/push/rewritten history.

**Completed-part commit:** 250e2616 `[V175] Require runtime barcode boxes for Single and Multi`.
Commit contains only the isolated r3 BarCode.cs production delta (85 additions/10 deletions),
not the unfinished r2 map/UI/project/harness packet or artifacts. Shared staged entries matched
before/after the alternate-index commit and BarCode index synchronization; unrelated stage preserved.
Git's automatic packing warning was not followed by manual pruning/maintenance. A stray
non-mutating shell token after successful commit/index checks failed harmlessly; subsequent
commit scope and whitespace checks succeeded. No push or branch/worktree was created.

## Approved V175-r4-setup frontend correction

User screenshot and request identify the missing Setup toggles for Streak, Blur and Heavy/Faded.
Explorer confirms current source intentionally hides Blur/Contrast and shared area row in Multi;
this matches the Streak-only image. Architect approves r4 with route deep retained: always show
all production blocks in Setup, preserve functional Single bindings, keep Streak functional in
Multi, and disable unsupported Multi Blur/Contrast/area without resetting stored values.
Add an enabled localized wrapping Single-only notice outside disabled blocks and an effective
localized summary; never represent saved Single flags as active Multi checks. Restore parent
and option-dependent child enablement when switching back to Single. Preserve existing custom
controls/events/localization/AutoSize/scrolling; no backend/native/map/state contract edits.

Sole writer: ToolBarcode.cs plus a focused offline production-control harness under existing
V175 assets if feasible. Existing persistence tests prove finite STA/Config/owner initialization
without Form.Show; reuse that initialization pattern, not imitation controls. Do not edit the
already-dirty giant persistence Program.cs or run its unrelated test suite. Meaningful offline
checks: Single/Multi/Single visibility/Enabled/stored flags, all existing toggle bindings, disabled
Multi immutability, Vietnamese/English notices and effective summaries. Unshown-control bitmap
rendering is not reliable evidence; do not claim rendered/DPI layout verified from it. No live
window/acquisition/hardware/deployment. Prior same-V175 build/offline grant persists: focused
isolated BeeInterface BuildProjectReferences=false with binlog, C#7.3 scoped harness, independent
read-only review. Keep r2 clipping and Multi quality association held and full V175 partial.

### R4 final Setup bridge clarification

Root and Verifier found that DataTool applies ToolEditorUiRules.ApplyStandardLayout after
construction; its unknown-title default routes Print quality to Advanced. Constructor accordion
ownership alone cannot prove Setup placement. Architect confirms the bounded r4 correction:
ToolBarcode.ConfigureAdvancedGroups explicitly reparents/reorders the same existing quality
section into the supplied final Setup accordion. Preserve section identity, controls, events,
settings and repeat-application idempotence. No DataTool/shared bridge source edits or scope
expansion. The offline harness must execute the actual bridge, assert final Setup ancestry and
exactly one section, then check real bindings and Single/Multi/Single state. Initial r4 build/csc
passed but its executable was paused before running; those binaries do not verify the correction.
