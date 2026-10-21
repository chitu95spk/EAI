# Task I095 — Windowed rotated-ROI crop (kill the full-frame 20MP warp)

> **PRIMARY root-cause fix for both CT and the E-804 / Axis WorkCounterLoss stalls.** This document is
> the single source of truth. Implement exactly. Do not redesign the crop math beyond what §5 says.
> The output MUST stay bit-for-bit equivalent to the current legacy path.

## 0. PROBLEM (measured + field-confirmed)

For a **rotated** rectangular ROI, `Cropper.CropRotatedRect` falls through to the LEGACY path
(`BeeCore/Func/Crop.cs:611-612` → the private legacy method) which does
`Cv2.WarpAffine(source, warped, M, source.Size(), ...)` at **full frame size**
(`Crop.cs:807`). On a 5472×3648 (20 MP) image this warps ~120 MB per crop even when the ROI is a tiny
fraction of the frame. Consequences proven in `F:\logs.json` and by field observation:

- Width `crop=263–336 ms` (`PERF.Width ... fast=False`), the dominant serial pole in tool `check`.
- Tool batch runs at only **22–41 % CPU** (`PERF.ToolBatch.Done procCpu%`) → the workload is
  **memory-bandwidth bound, not CPU bound**. One program < 400 ms; two programs in parallel spike to
  ~1130 ms even with 12 logical CPUs — no scaling, because both lanes' 20 MP warps saturate the RAM bus.
- **DS5 Axis `WorkCounterLoss` (E-804) persists even after ECAT is pinned to its own core**, because
  memory bandwidth is shared by ALL cores: a saturated bus stalls the ECAT cyclic thread's PDO memory
  access on its isolated core → WKC goes stale → `POSITION_SAFETY_ABORT`.

Fixing the crop to warp only a small window around the ROI cuts memory traffic ~100×, which
simultaneously (a) shrinks CT, (b) lets 2 programs scale, and (c) removes the bandwidth starvation
that trips the Axis. This is a bigger lever than CPU core allocation (see task I094, which stays as a
complementary stability/hygiene change).

## 1. NON-NEGOTIABLE GUARDRAILS

### 1.1 MUST
1. One file in scope: `BeeCore/Func/Crop.cs`. (Tests/smoke harness `tools/RegionCropperSmoke` may be
   used to verify, not required to edit.)
2. The new windowed rotated path MUST produce output **numerically identical** to the current legacy
   full-frame warp for the same ROI (same interpolation, same border handling, same sub-pixel
   sampling). Equivalence is the acceptance bar — see §6.
3. Fall back to the existing legacy path whenever the window cannot be safely computed (ROI touching
   or exceeding the frame border, degenerate size, mask geometry edge cases). Never crash; never
   return wrong pixels.
4. Keep the existing fast paths (`rectangle-direct`, `rectangle-window`, `prepared`,
   `CropRotatedRectUltraFast3`) working exactly as today. You are ADDING a rotated-window path, not
   replacing the axis-aligned ones.
5. Build `Release|x64` (BeeCore + BeeInterface), 0 errors, warnings ≤ baseline.
6. Repo conventions; do not touch files outside scope; do not reformat unrelated code.

### 1.2 MUST NOT
1. Do NOT change public signatures of `CropRotatedRect` / `CropRotatedRectTimed`.
2. Do NOT alter mask semantics — the masked result must match the legacy masked result.
3. Do NOT touch native code, GPU, or the tool scheduler.
4. Do NOT "optimize" by lowering interpolation quality or skipping the mask.

## 2. COMPLETION PROTOCOL (run to done)
1. Implement the rotated-window fast path in `Crop.cs` (§5).
2. Build BeeCore + BeeInterface (§7). Fix any red build in-scope; never leave it red.
3. Verify equivalence (§6): for a representative set of rotated ROIs (varied angle, size, position,
   with and without mask), the new path output equals the legacy output (max abs pixel diff ≤ 1 to
   allow only rounding at borders; ideally 0). Record evidence.
4. Verify perf: crop time for a small rotated ROI drops from hundreds of ms to single-digit/low-tens.
5. Self-verify DoD (§8). Append `CODEX_HISTORY.md`; commit `[I095] Windowed rotated-ROI crop`.
6. Report files touched, build result, equivalence evidence, before/after crop timing, blockers.

## 3. WHY THE FAST PATH IS CURRENTLY SKIPPED (root of the bug)
`TryCropRectangleFast` (`Crop.cs:621`) returns false for ANY rotated rect because of the angle gate at
`Crop.cs:626-630`:
```
double normalizedAngle = rot._rectRotation % 360.0; ...normalize...
if (Math.Abs(normalizedAngle) > 0.000001) return false;   // <-- blocks all rotated ROIs
```
Both the `direct` and the `window` attempts (`Crop.cs:595-609`) go through this same gate, so a rotated
ROI can never reach the small-window `WarpAffine` at `Crop.cs:676-688` and always ends at the legacy
full-frame warp. The window at `:643-646` is also computed axis-aligned (does not enclose a rotated
rect), so it cannot simply be un-gated — it must be widened to the rotated ROI's bounding box.

## 4. GEOMETRY THE FIX NEEDS
For a rotated rectangle with world anchor `A=(worldAnchor.X, worldAnchor.Y)`, size `w×h`
(`rectSize`), and angle `θ = rot._rectRotation`:
- The four ROI corners in world space are `A + R(θ)·(±w/2, ±h/2)` where `R(θ)` is the 2D rotation.
- The **source window** is the axis-aligned bounding box of those four corners, expanded by a small
  margin (≥1 px, use 2 px) on every side for bilinear interpolation neighbourhood — mirroring the
  existing "+1 pixel" reasoning at `Crop.cs:648-654`.
- Clip test: if the expanded window `[left,top,right,bottom]` is fully inside `[0, source.W/H)`,
  proceed; otherwise return false → legacy fallback (§1.1 rule 3).

## 5. IMPLEMENTATION (add a rotated-window branch — keep it equivalent)

In `TryCropRectangleFast`, replace the unconditional angle gate with logic that:
1. Still uses the existing axis-aligned `direct` path when `|θ| ≈ 0` (unchanged behaviour).
2. When `|θ| > 0`, does NOT return false; instead computes the rotated bounding window (§4). If the
   window is in-bounds:
   - `windowLeft/top/right/bottom` = expanded rotated bbox (integer floor/ceil + 2 px margin).
   - `windowW = right-left+1`, `windowH = bottom-top+1`.
   - `transform = Cv2.GetRotationMatrix2D(anchor, rot._rectRotation, 1.0);`
     `transform[0,2] -= windowLeft; transform[1,2] -= windowTop;`  (same offset trick as `:676-678`).
   - `Cv2.WarpAffine(source, localWarp, transform, new Size(windowW, windowH), InterpolationFlags.Linear,
     BorderTypes.Constant, new Scalar(0,0,0));`  (Linear + Constant(0) = same as legacy `:807-810`).
   - `GetRectSubPixAllChannels(localWarp, patchSize, new Point2f(anchor.X - windowLeft, anchor.Y - windowTop), patch);`
     (same sub-pixel extraction as the axis-aligned window path `:692-696` and legacy `:817-819`).
   - Then apply the EXISTING mask composition code (the `rotMask != null` block starting `:716`) — do
     not change it; it already works on the `patch` in patch-local coordinates.
   - If any step fails or window is out of bounds → return false (legacy fallback).
3. Because the crop is now a small `windowW×windowH` warp instead of `source.Size()`, memory traffic
   drops from ~120 MB to ~(window area) — typically <1 MB for a small ROI.

Keep `direct` mode restricted to `|θ| ≈ 0` (GetRectSubPix alone cannot rotate). Only the WINDOW path
gains rotation support.

> Equivalence note: legacy warps the whole frame with `GetRotationMatrix2D(anchor, θ)` then
> `GetRectSubPix` at `anchor`. The windowed path applies the SAME rotation matrix (only translated by
> the window origin) and the SAME `GetRectSubPix` at the translated anchor, over a super-set of the
> sampled neighbourhood. With Linear interpolation and Constant(0) border, the sampled pixels are
> identical, so the patch is identical (borders may differ only if the window clips the interpolation
> neighbourhood — the 2 px margin + in-bounds check prevent that).

## 6. EQUIVALENCE VERIFICATION (acceptance bar)
Add a temporary debug comparison (or use `tools/RegionCropperSmoke`): for N rotated ROIs
(θ ∈ {5,15,30,45,90,137}°, sizes small/medium, positions away from border, mask null and non-null),
compute both `legacy` and `window` results and assert `Cv2.Norm(a, b, NormTypes.INF) <= 1`. Ideally 0.
Remove the temporary comparison before commit (or gate behind a debug flag). Report the measured max
diff and per-ROI crop time before/after.

## 7. VERIFY (build)
```
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" "BeeCore\BeeCore.csproj" /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" "BeeInterface\BeeInterface.csproj" /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo
```
Expected: `-> ...dll`, 0 errors, warnings ≤ baseline.

## 8. DEFINITION OF DONE
- [ ] Rotated ROI uses the small-window warp; legacy full-frame warp only as a documented fallback.
- [ ] Output equivalence verified (max abs diff ≤ 1) for the §6 ROI set, mask and no-mask.
- [ ] Crop time for a small rotated ROI drops from hundreds of ms to ≤ ~tens of ms.
- [ ] Existing axis-aligned / prepared / ultrafast paths unchanged and still pass.
- [ ] BeeCore + BeeInterface build `Release|x64`, 0 errors, warnings ≤ baseline.
- [ ] `git diff --name-only` = `BeeCore/Func/Crop.cs` + this doc + `CODEX_HISTORY.md`.
- [ ] `CODEX_HISTORY.md` appended; commit `[I095] Windowed rotated-ROI crop`.

## 9. ROLLBACK
`git checkout HEAD -- BeeCore/Func/Crop.cs`. The change is isolated to one method's branch; the legacy
path remains as the fallback, so reverting only removes the fast path.

## 10. EXPECTED IMPACT (state, then confirm on hardware)
- Width `crop` 263–336 ms → single-digit/low-tens ms.
- 2-program parallel CT scales (memory bus no longer saturated by 2× full-frame warps).
- DS5 Axis `WorkCounterLoss` under "2 programs + CCD read" should stop, because the ECAT cyclic
  thread's memory access is no longer starved. Verify on the real 4P+6E DS5 rig together with I093
  (WKC tolerance) and I094 (core hygiene). Pulling the EtherCAT cable must still Quick Stop immediately.

## 11. RELATION TO OTHER TASKS
- I093 (done): configurable WKC watchdog — absorbs residual short jitter.
- I094 (plan): demand-driven CPU core auto-tune — hygiene/stability; NOT sufficient alone (field-proven).
- I095 (this): removes the memory-bandwidth root cause — the primary fix.
Secondary CT levers to investigate AFTER I095 (separate, not in scope here): Learning ONNX runs on the
full 5472×3648 frame (consider downscale-before-infer); cam=0 read ≈ 2× cam=1 (exposure/link/ROI);
`PERF.PLC.DoneCCDToResult` 343 ms handshake; `PERF.PLC.WriteIOProcessing ChangeMode` 451–548 ms stall.
Related: [[e804-workcounterloss-camera-starvation]].
