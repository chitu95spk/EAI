# Task UI2L — ParameterAccordion 2-line header (title/value/button + Tip) & content auto-fill

## Links
- Phase: cross-cutting UI (see section 10.3 `ToolFormBase` layout, 10.4 shared UserControls).
- CLAUDE.md sections: 0 (Hard Rules), 7 (DoD), 10 (GUI standardization).
- Target control: `BeeInterfaceControls/Custom/ParameterAccordion.cs`,
  `ParameterHeaderBinding.cs`, `ParameterAccordionPolicy.cs`,
  `BeeInterface/Custom/ToolParameterAccordionBuilder.cs`, `ParameterHeaderUi.cs`.

## Goal
Every `ParameterAccordion` header (the `BeeInterfaceControls` implementation) is **always 2 lines tall**:
- **Line 1**: title name (left) → … → **value** → **choice/enable button** (far right).
- **Line 2**: a **Tip** explaining the meaning of the title, in a **smaller font**, muted color, ellipsis + tooltip.

Every section has a Tip (3-layer mechanism: explicit → catalog → placeholder). Tips are **bilingual, switchable**
by configuration (no runtime translation calls). The inner content panel **auto-sizes its height exactly** — no
excess, no clipping.

Scope: **only** the `BeeInterfaceControls` implementation. Do NOT touch `EasyScreen/Ui/ParameterAccordion.cs`.

## Execution guidance for Codex 5.5 (READ FIRST — save tokens)
- **Do NOT re-scan the repo.** Every file + anchor (line number) you need is listed in this plan. Open only the
  In-scope files and only the region you must edit. No repo-wide `grep`/`find`, no reading files outside In-scope.
- **Codex authors the tips — do not ask the user.** Seed `ParameterTipCatalog` verbatim from the **Bilingual Tip
  Table** at the end of this plan. For any section not in the table, Codex writes its own VI+EN in the **same
  style** (one short sentence ≤ ~70 chars describing the *meaning / when to use*, not repeating the title). The
  placeholder is only the final safety net.
- **Minimal testing.** Build once at the end of each commit (`/v:minimal`). Smoke = open **one tool only**
  (ToolPattern) and confirm 2-line header + tip + tight content. **Skip** repeat tests (open/close 5×), **skip**
  multi-tool pixel audits, **skip** any multi-tool matrix. No screenshots needed.
- **Work sequentially T1 → T4**, one commit per T. T5 (extra explicit tips) is **optional** — the catalog already
  covers everything, so it may be skipped to save tokens.
- Only `git add` the In-scope files of the current step. Never `git add .`.

## Locked decisions (user, 2026-08-27)
1. Tip source: **3 layers** — explicit `Description` at call site → central catalog by `Key`/`Title` → auto placeholder.
2. Tip language: **bilingual / switchable** (static VI/EN, chosen by a global provider — NO live translation).
3. Scope: **only the main `BeeInterfaceControls` implementation**.

## Preconditions
- [ ] Read the last 3 entries of `CODEX_HISTORY.md` + CLAUDE.md section 0.
- [ ] `git status`: the worktree is dirty in many files (maps/docs) — add ONLY In-scope files, never `git add .`.
- [ ] Baseline build passes (compare against `docs/architecture/baseline_build.md`).
- [ ] Understand the two current header modes: inline (1 line) and `IsValueStacked` (value drops to line 2 on overflow).

## In-scope (files you may edit)
Core control:
- `BeeInterfaceControls/Custom/ParameterAccordion.cs` (class `ParameterAccordion` + `ParameterAccordionSection`)
- `BeeInterfaceControls/Custom/ParameterHeaderBinding.cs` (+ `ParameterHeaderOptions`)
- `BeeInterfaceControls/Custom/ParameterAccordionPolicy.cs` (+ `ParameterAccordionSectionOptions`)
- `BeeInterface/Custom/ToolParameterAccordionBuilder.cs` (+ `ToolParameterSectionSpec`)
- `BeeInterface/Custom/ParameterHeaderUi.cs`
- **NEW files**: `BeeInterfaceControls/Custom/LocalizedText.cs`, `BeeInterfaceControls/Custom/ParameterTipCatalog.cs`

i18n scaffolding:
- `BeeGlobal/Enums.cs` (add `enum UiLanguage`)
- `BeeGlobal/Config.cs` (add `UiLanguage` field)
- `BeeCore/Data/LoadData.cs` (validate the new field — next to the `ParameterAccordionMode` validation, ~line 443)
- `BeeUi/Form/FormLoad.cs` (wire `GlobalLanguageProvider`, next to line 127)

Explicit-tip roll-out (T5, one commit per file):
- `BeeInterface/Tool/ToolPattern.AccordionLayout.cs`
- `BeeInterface/Tool/ToolYolo.AccordionLayout.cs`
- `BeeInterface/Tool/ToolPosition_Adjustment.AccordionLayout.cs`
- `BeeInterface/Tool/ToolMatchingShape.AccordionLayout.cs`
- `BeeInterface/Steps/SettingStep1.AccordionLayout.cs`
- `BeeInterface/Steps/SettingStep2.AutoTrigger.cs`
- `BeeInterface/GeneralSetting.cs`
- `BeeInterface/PLC/ProtocolPLC.cs`
- `BeeInterface/Tool/ToolMeasure.ResultPlc.cs`
- `BeeInterface/Tool/ToolSegmentAI.cs`

## Out-of-scope (do NOT touch)
- `EasyScreen/**` (separate accordion — handled later).
- `Pattern/Pattern2.*`, `Pattern/Pitch*` (native, developed in parallel).
- Tool business logic (Run/Save/Load) — only add Tip declarations + re-read layout.
- Do NOT rename `Propety`/`Comunication`… (renaming is Phase 6).
- Do NOT introduce new `Common.PropetyTools[...]` call sites.

## Architecture changes (read carefully before coding)

### A. The Tip belongs to the SECTION, not the binding
Many sections configure their header **multiple times** via `ParameterHeaderUi.Bind*` (each call builds a new
`ParameterHeaderOptions` → would overwrite). So **`ParameterAccordionSection` owns the Tip** and **re-injects** it
on every configuration:
- Add `ParameterAccordionSection.Description` (type `LocalizedText`, resolved on demand).
- In `ParameterAccordionSection.ConfigureHeader(options)`: if `options.Description == null`, set
  `options.Description = this.Description`. Since every `Bind*` funnels through `ConfigureHeader`, the Tip survives.
- Note: the `ParameterHeaderUi.Bind*(Button header, …)` overloads (loose header, not a section) have no Tip;
  that's acceptable — note it in History.

### B. LocalizedText + language provider (mirror `GlobalDisplayModeProvider`)
`BeeInterfaceControls/Custom/LocalizedText.cs`:
```csharp
public enum ParameterUiLanguage { Vietnamese, English }   // or reuse BeeGlobal.UiLanguage as a single enum
public readonly struct LocalizedText
{
    public readonly string Vi; public readonly string En;
    public LocalizedText(string vi, string en) { Vi = vi; En = en; }
    public static LocalizedText Both(string vi, string en) => new LocalizedText(vi, en);
    public static implicit operator LocalizedText(string s) => new LocalizedText(s, s); // single language
    public bool IsEmpty => string.IsNullOrWhiteSpace(Vi) && string.IsNullOrWhiteSpace(En);
    public string Resolve()
    {
        var lang = ParameterAccordion.GlobalLanguageProvider?.Invoke() ?? ParameterUiLanguage.Vietnamese;
        string primary = lang == ParameterUiLanguage.English ? En : Vi;
        return string.IsNullOrWhiteSpace(primary) ? (Vi ?? En ?? string.Empty) : primary;
    }
}
```
- `ParameterAccordion`: add `public static Func<ParameterUiLanguage?> GlobalLanguageProvider { get; set; }`
  (place next to `GlobalDisplayModeProvider`, ~line 51).
- Do NOT call `LibreTranslateClient` on the UI thread (target machine has 2 cores — see memory
  `runtime-target-2core-word`).

### C. Catalog + 3-layer resolver
`BeeInterfaceControls/Custom/ParameterTipCatalog.cs`:
```csharp
public static class ParameterTipCatalog
{
    // normalized key (letters+digits only, see MakeKey/NormalizeKey) -> LocalizedText
    private static readonly Dictionary<string, LocalizedText> _byKey = ...;
    public static bool TryGet(string key, string title, out LocalizedText tip); // key first, title second
    public static LocalizedText Resolve(LocalizedText explicitTip, string key, string title)
    {
        if (!explicitTip.IsEmpty) return explicitTip;              // layer 1
        if (TryGet(key, title, out var dict)) return dict;         // layer 2
        return Placeholder(title);                                 // layer 3
    }
    private static LocalizedText Placeholder(string title) =>
        LocalizedText.Both("Cấu hình " + title, "Configure " + title);
}
```
- **Codex seeds the catalog with the full VI+EN content from the "Bilingual Tip Table" at the end of this plan**
  (use verbatim, do not ask the user). Missing keys → Codex writes them in the same style. The placeholder is
  only the final safety net.
- Keep the catalog in `BeeInterfaceControls` so the control resolves it itself; call sites pass an explicit
  `Description` only when they want to override.

### D. Binding: header ALWAYS 2 lines
Modify `ParameterHeaderBinding`:
- Add `ParameterHeaderOptions.Description` (type `LocalizedText`).
- Add a `_tipLabel` child (Label, `AutoEllipsis=true`, `Visible=true`), font **smaller** than the title
  (≈ `header.Font.Size * 0.62f`, min ~7.5f), muted color `Color.FromArgb(96,104,112)`, bottom line.
- Change `ApplyLayout` to a **two fixed zones** model (no longer driven by value overflow):
  - **topLine** = upper half: title (RJButton text) + `_valueLabel` (right, just left of the choice button) +
    `_choiceHost` + `_enableControl` (far right).
  - **bottomLine** = lower half: `_tipLabel` (left, full width).
  - The value **no longer** drops to line 2; if too long → shrink font (reuse `CalculateFittedFontSize`) then
    ellipsis. Remove/disable the `stackValue → value to bottom` branch; keep `IsValueStacked` for compat but it
    stays `false` for value (the bottom line is now the tip).
  - Compute the header's `ContentPadding.Bottom`/`Top` to reserve exactly the tip height (generalize the existing
    `stackedEdge`/`stackedValueHeight` logic).
- `RefreshValue`/`ApplyAppearance` also set `_tipLabel.Text = options.Description.Resolve()` and its tooltip.
- Update `Dispose` to remove `_tipLabel`, dispose the new font caches, and unhook events — respect `-=`/`+=` pairing.

### E. Accordion: header height always fits 2 lines
Modify `ParameterAccordion`:
- `GetHeaderHeight`/`MeasureStackedHeaderHeight`: **drop the `IsHeaderValueStacked` condition**, always compute
  `twoLine = lineBox(titleFont) + lineBox(tipFont) + padding` and use it as the **floor** for both collapsed &
  expanded.
- Update the defaults in `ParameterAccordionPolicy.ApplyDefaults` (currently `Collapsed=32`, `Expanded=40`) to fit
  2 lines at the base font (suggest `Collapsed≈44`, `Expanded≈50`) to reduce first-paint jump; final values are
  decided by actual measurement (measurement remains the source of truth).

### F. Content auto-size, tight (no excess/clipping)
Root cause: `MeasureContentHeight` (measured with the custom `MeasureTableRows`/`MeasureNaturalControlHeight`)
drifts from the real rendered height of `_contentLayout` (AutoSize, Dock=Top) → `_contentClip`
(= `CurrentContentHeight`) is too tall (blank gap) or too short (clipped). Requirements:
- Prefer `_contentLayout.GetPreferredSize(new Size(width, 0)).Height` **measured at the true render width** as the
  source of truth; measure at the same width used in `ApplyLayoutState` (`ClientSize.Width`), including
  `_contentLayout`/`_contentClip` padding.
- Handle scrollbar reflow: if measuring flips the vertical scrollbar state → measure once more at the new width
  (`GetSectionWidth`).
- Keep `MeasureTableRows`/DataGridView only as a **fallback** for AutoSize-unreliable controls (DataGridView).
- After `LayoutSections` (when not animating and the section is open): if
  `_contentLayout.Height != CurrentContentHeight` → reconcile (reset + relayout once, guarded by `_layingOut`
  against recursion).

## Steps (each T = one commit)

**T1 — i18n scaffolding** (`[UI2L.1]`)
1. `BeeGlobal/Enums.cs`: `public enum UiLanguage { Vietnamese, English }`.
2. `BeeGlobal/Config.cs`: `[OptionalField] public UiLanguage UiLanguage = UiLanguage.Vietnamese;` (next to
   `ParameterAccordionMode`, line 412).
3. `BeeCore/Data/LoadData.cs` (~443): validate `Enum.IsDefined(typeof(UiLanguage), config.UiLanguage)` → default
   `Vietnamese`.
4. `LocalizedText.cs` + `ParameterAccordion.GlobalLanguageProvider` (map `BeeGlobal.UiLanguage` ↔
   `ParameterUiLanguage`, or use one shared enum to avoid mapping — decide while coding, note in History).
5. `BeeUi/Form/FormLoad.cs` (next to line 127):
   `ParameterAccordion.GlobalLanguageProvider = () => Global.Config?.UiLanguage == UiLanguage.English ? ParameterUiLanguage.English : ParameterUiLanguage.Vietnamese;`
   (null-guard appropriately; use if/else if the project's C# version lacks `?.`/`??`).
- Verify: build passes, no UI change yet.

**T2 — Tip data channel + catalog (VI/EN) + resolver** (`[UI2L.2]`)
1. Add `Description` (LocalizedText) to `ParameterHeaderOptions`, `ToolParameterSectionSpec`,
   `ParameterAccordionSectionOptions`, `ParameterAccordionSection`.
2. `ParameterTipCatalog.cs`: **seed the "Bilingual Tip Table" at the end of this plan verbatim** (VI+EN for every
   key) + the 3-layer resolver.
3. `ParameterAccordionPolicy.AddSection`/`ToolParameterAccordionBuilder.AddSection`: resolve the Tip by
   `Key`/`Title` (layers 2+3) and assign it to `section.Description` if the spec has no explicit one.
4. `ParameterAccordionSection.ConfigureHeader`: re-inject `Description` into options if empty.
- Verify: build passes. (No need to run — the tip becomes visible when you open a tool in T3.)

**T3 — Header always 2 lines** (`[UI2L.3]`)
1. `ParameterHeaderBinding`: add `_tipLabel`, change `ApplyLayout` to the two-zone model (D).
2. `ParameterAccordion.GetHeaderHeight`/`MeasureStackedHeaderHeight`: always 2 lines (E).
3. `ApplyDefaults`: raise the default heights.
- Verify (once): open **ToolPattern** — 2-line header, smaller tip, value sits just left of the choice button.

**T4 — Content auto-fill, tight** (`[UI2L.4]`)
1. Fix `MeasureContentHeight` + reconcile after layout (F).
- Verify (once, eyeball): open ToolPattern, the inner panel has no leftover blank space / no clipping.

**T5 — (OPTIONAL) explicit tip refinement** — skip to save tokens.
- The VI/EN catalog from T2 already covers every section. Only do this if the user asks to reword specific tools;
  in that case set `section.Description = LocalizedText.Both("<vi>","<en>")` right after `AddSection`, one commit
  per file.

## Round-2 feedback (2026-08-27, from screenshot) — implement as T6 + T7

Observed on the built accordion: (a) the right-side buttons (ON / More·Equal·Less) sit only on the top row and
leave a gap to the right edge; the user wants them **flush to the right edge and as tall as both rows**. (b) Building
a tool's `ParameterAccordionSection` list is **very slow to load** — optimize it.

**T6 — Right control column: flush-right, full 2-row height** (`[UI2L.6]`)
Change only `ParameterHeaderBinding.ApplyLayout`. Target layout per header:
```
┌───────────────────────────────┬────────────┐
│ (row 1) title ........ value   │            │
├───────────────────────────────┤  buttons   │   ← enable + choice column:
│ (row 2) tip                    │ (full H)   │     flush to right edge, spans BOTH rows
└───────────────────────────────┴────────────┘
```
1. The **enable control + choice host** form the right column. Give them height = **full `clientHeight`** (y=0),
   not `topControlHeight`. Place them **flush to the right edge**: start `int right = clientWidth;` (remove the
   `- edge` right inset for this column; keep the left `edge` inset for title/tip). A 0px right inset = "sát mép phải".
2. Capture the column's left boundary **before** the value label consumes width, e.g. `int rightColumnLeft = right;`
   after placing enable + choice (and their gaps). The value label still lives on **row 1 only** and is placed to the
   left of the column using the running `right` (its current behaviour).
3. **Tip width must stop at the column**, not span full width: set the tip bounds right edge to `rightColumnLeft`
   (minus a small gap), i.e. width = `Math.Max(0, rightColumnLeft - edge - gap)`. Otherwise the tip underlaps the
   full-height buttons.
4. Keep `reserveRight` = `clientWidth - right` so the title text still avoids the value + column on row 1.
5. `_valueLabel` stays top-row (height = `topControlHeight`), vertically centred — unchanged.
- Verify (once): open ToolYolo/ToolPattern — ON / More·Equal·Less buttons touch the right edge and are as tall as
  the 2-line header; tip on row 2 ends before the buttons (no overlap); value still on row 1 left of the buttons.

**T7 — Performance: ParameterAccordionSection loads slowly** (`[UI2L.7]`)
Root causes (mostly introduced or amplified by the 2-line work): per-section `Font` allocation + `TextRenderer`
measures on every style refresh, `GetPreferredSize` called very often, and per-section layout cascades while a tool
adds ~10–15 sections. Apply these, cheapest-first; re-measure load time after each:
1. **Cache the two-line header height.** `MeasureTwoLineHeaderHeight`/`GetHeaderHeight` allocate a `Font` and run
   `TextRenderer.MeasureText("Ag", …)` twice **per section, per `RefreshHeaderStyles`** — and it only depends on the
   header font + dpi. Memoize by a `(fontFamily,size,style,unit,dpi)` key (static dictionary or a small cache);
   dispose the temp tip `Font` you create for measuring, or cache it too. This removes N× font allocs per relayout.
2. **Stop full `GetPreferredSize` on every `CanExpand`.** `CanExpand` calls `MeasureContentHeight(Width)` and
   `IsSectionExpandable` (which reads `CanExpand`) is called in many per-section loops
   (`RefreshHeaderTitles`, `RefreshHeaderStyles`, `LayoutSections`, `IsSectionExpanded`…). For the boolean
   "can this expand" use a **cheap** check (has ≥1 locally-visible content control + `IsExpansionEnabled`) instead
   of a full preferred-size pass; keep the expensive height measure only where the actual pixel height is needed.
3. **Batch the build.** Add a lightweight suspend/resume to `ParameterAccordion` (e.g. `BeginBulkUpdate()` /
   `EndBulkUpdate()` that sets a `_bulkUpdating` flag): while set, `OnControlAdded` and `RequestSectionLayout` just
   mark dirty and return; `EndBulkUpdate` runs **one** `RefreshHeaderStyles` + `LayoutSections`. Wrap each tool's
   section-building (ToolPattern `BuildPatternAccordionUi`, ToolYolo `BuildYoloAccordionUi`, etc.) — but do it
   generically: expose the two methods and have `ToolParameterAccordionBuilder`/`ParameterAccordionPolicy` bracket
   their add-loops so all tools benefit without editing each tool file. Fallback: `accordion.SuspendLayout()/ResumeLayout(false)` around the add-loop plus a single deferred layout.
4. **Coalesce `RequestSectionLayout`.** During load many events fire synchronously; collapse them: if a layout is
   already pending (a `_layoutPending` flag + single `BeginInvoke`), don't run another synchronous pass.
5. **Run typography normalization once per real content change.** `NormalizeContentTypography` does a binary-search
   `FitControlFont` (with `TextRenderer.MeasureText`) over **all** content controls on every `ApplyLayoutState`.
   Guard it with a dirty flag so it runs on content add/remove/text/font change only, not on every layout tick.
6. **Skip the T4 reconcile pass during bulk update** and for collapsed sections (only expanded sections need it).
- Constraint: keep behaviour identical (same final sizes, tip, 2-line header). This is pure throughput.
- Verify (once): open ToolYolo (the heaviest) and confirm the parameter tab opens noticeably faster with the same
  visual result. No repeat/matrix timing — one before/after eyeball is enough.
- Stop-and-ask if a batch/suspend change risks breaking initial expansion/selection state.

**T8 — Header-only sections: selected style + non-clickable** (`[UI2L.8]`)
Context (from screenshot): sections whose inputs are ALL hoisted into the header (e.g. `Feature Mode` = Shape/
Balanced/Gray, `Execution Mode` = Normal/HighSpeedBk2) have an **empty body** — there is nothing to expand. They
currently render in the plain collapsed (grey) style. Requirement: such "header-only" sections must render
**permanently in the selected style (`IsClick = 1`)** and stay **non-clickable** (no expand/collapse toggle).
Change `ParameterAccordion` only:
1. Define **header-only** = `section.IsSectionVisible && section.IsExpansionEnabled && !section.CanExpand`
   (visible, NOT gated off by an enable toggle, and no expandable body ⇒ content height 0). Reuse the existing
   `CanExpand`/`IsExpansionEnabled` — do not add a new measurement pass (respect T7).
2. In `RefreshHeaderStyles`, make such a section selected:
   `bool selected = (IsSectionExpanded(section) && IsSectionExpandable(section)) || headerOnly;`
   so `ApplyHeaderStyle(selected: true, …)` sets `_headerButton.IsCLick = true` + the selected back/text colors.
3. Non-clickable is already true: `SectionHeader_Click` returns early for non-expandable sections
   (header-only ⇒ `CanExpand` false ⇒ `IsSectionExpandable` false), and `RefreshHeaderTitles` already suppresses the
   ▶/▼ indicator for non-expandable sections. Optionally give the header a non-interactive look (no hover/press
   change) — minor, only if trivial.
4. Do **NOT** apply this to sections that are collapsed because an enable toggle is OFF (those have
   `IsExpansionEnabled == false` and real content) — the `IsExpansionEnabled` guard in the definition already
   excludes them, so they keep their normal/off look.
5. Header height for header-only sections is unchanged (still the 2-line title+tip height).
- Verify (once): open ToolPattern/ToolYolo — `Feature Mode` / `Execution Mode` render highlighted (IsClick=1) and do
  nothing on click, while `Angle Range` (has a body) still expands/collapses normally.

## Verify commands (minimal — save tokens)
| Command | Expected |
|---|---|
| `MSBuild EasyVision.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal` (once at the end of each commit) | pass, warnings ≤ baseline |
| Smoke once: open app → open **ToolPattern** | 2-line header (title+value+button / tip); tight content; no exception |
| (if convenient) Toggle `Config.UiLanguage` VI↔EN → reopen ToolPattern | tip switches language |
- Do NOT run repeat tests, do NOT run multi-tool pixel audits, do NOT scan the repo. Skip `check_propety_tools.sh`
  (this task adds no `PropetyTools[]` access).

## Rollback
- Build/smoke fails in T1–T4: `git reset --hard HEAD~1` (if not pushed) or `git revert <sha>`.
- T5 fails on one file: revert just that file's commit; the others stand (the fallback catalog/placeholder still
  covers everything).

## Exit criteria (DoD — concise)
- [ ] Build passes, warnings ≤ baseline.
- [ ] Open ToolPattern: EVERY section is 2 lines (line 1 title+value+button / line 2 smaller tip), content tight,
      no exception.
- [ ] 3-layer tip works: the VI/EN catalog covers every visible section; no section has an empty line 2.
- [ ] Event subscribes are `-=` before `+=` for `_tipLabel` and any new handler.
- [ ] Each commit contains only the In-scope files of that step.
- [ ] `CODEX_HISTORY.md` has a new section (template 11.2), noting the language-enum decision (single enum vs map).

## Notes / Stop-and-ask
- If the 2-line measurement causes a large first-paint jump → tune the default height, do NOT disable measurement.
- If a single shared `BeeGlobal.UiLanguage` enum forces `BeeInterfaceControls` to reference `BeeGlobal` (already
  referenced — verify), prefer the shared enum to avoid mapping.
- If you find a section using `ParameterHeaderBinding.Attach(header,…)` directly (loose header) that needs a tip →
  ask the user whether that group should be covered.
- T3 (two-zone binding) is the highest-risk step; if it exceeds 2 days → stop-and-ask.

## Bilingual Tip Table (Codex seeds verbatim into `ParameterTipCatalog` — do NOT ask the user)
> `key` = normalized title (letters+digits only, case-insensitive — use the existing `MakeKey`/`NormalizeKey`).
> Look up by `Key` first, then `Title`. Sections not in the table → Codex writes VI+EN in the same style (one
> short sentence, describes meaning/when-to-use, does not repeat the title). Seed code shape:
> `_byKey["area"] = LocalizedText.Both("Vùng ROI để dò tìm/đo trên ảnh.", "ROI region to search / measure on the image.");`

| key | Title (reference) | VI (tip line) | EN (tip line) |
|---|---|---|---|
| area | Area / ROI | Vùng ROI để dò tìm hoặc đo trên ảnh. | ROI region to search or measure on. |
| featuremode | Feature Mode | Chọn kiểu đặc trưng khớp mẫu: hình dạng, cân bằng hay mức xám. | Matching feature type: shape, balanced or gray. |
| executionmode | Execution Mode | Chế độ chạy: Normal chính xác, HighSpeed ưu tiên tốc độ. | Run mode: Normal for accuracy, HighSpeed for speed. |
| anglerange | Angle Range | Khoảng góc xoay cho phép khi dò mẫu. | Allowed rotation angle range when searching. |
| resizetemplate | Resize Template | Phóng/thu mẫu để khớp vật kích thước khác. | Scale the template to fit differently sized parts. |
| patternmethod | Pattern Method | Kiểu nhãn (OK/NG, multi) và phạm vi tìm kiếm. | Label mode (OK/NG, multi) and search-area scope. |
| setsample | Set Sample | Dạy/chọn mẫu chuẩn để so khớp. | Teach or pick the reference sample to match. |
| checkcolor | Check Color | Bật kiểm tra màu bổ sung cho vùng đã khớp. | Extra color check on the matched region. |
| scoretolerance | Score / Tolerance | Ngưỡng điểm khớp tối thiểu để coi là đạt. | Minimum match score to pass. |
| resultlimit | Result Limit | Điều kiện số lượng kết quả (<, =, >) để OK/NG. | Count condition (<, =, >) for OK/NG. |
| maximumobject | Maximum Object | Số vật tối đa được trả về mỗi lần dò. | Max number of objects returned per search. |
| stepangle | Step Angle | Bước góc khi quét xoay — nhỏ thì chính xác, chậm hơn. | Angle step in rotation scan — smaller = finer, slower. |
| searchalgorithm | Search Algorithm | Chọn lấy vật tốt nhất hay tất cả vật. | Return best object or all objects. |
| options | Options | Bật/tắt các tuỳ chọn phụ (sub-pixel, bitwise-not…). | Toggle minor options (sub-pixel, bitwise-not…). |
| difficulty | Difficulty | Mức khó khớp: Easy/Normal/Hard đổi độ nhạy. | Match difficulty: Easy/Normal/Hard sensitivity. |
| validation | Validation | Bật các bộ lọc kiểm định kết quả khớp. | Enable validators that filter matches. |
| scalesearch | Scale Search | Dò thêm theo tỉ lệ phóng để khớp vật to/nhỏ. | Also search across scale for larger/smaller parts. |
| compute | Compute | Chọn CPU/GPU và số luồng tính toán. | Choose CPU/GPU and thread count. |
| colorcheckfilter | Color Check Filter | Lọc nhiễu hình thái cho vùng kiểm màu. | Morphology noise filter for the color check. |
| colorngoffset | Color NG Offset | Co/giãn biên vùng màu trước khi đánh giá NG. | Shrink/grow color region before NG judging. |
| contrast | Contrast | Mức tăng tương phản khi trích hình dạng. | Contrast boost level for shape extraction. |
| denoise | Denoise | Mức khử nhiễu trước khi dò. | Denoise level before detection. |
| edgesensitivity | Edge Sensitivity | Độ nhạy phát hiện cạnh của bộ lọc hình dạng. | Edge-detection sensitivity of the shape filter. |
| minimumshapesize | Minimum Shape Size | Bỏ qua hình dạng nhỏ hơn ngưỡng (px). | Ignore shapes smaller than this (px). |
| grayweight | Gray Weight | Tỉ trọng thông tin mức xám khi khớp. | Weight of gray information in matching. |
| positioncheck | Position Check | Kiểm dung sai vị trí X/Y/góc (và W/H) của vật. | Tolerance check on X/Y/angle (and W/H). |
| positionresult | Position Result | Sai lệch vị trí đo được so với mẫu. | Measured position deviation vs. reference. |
| plcaddress | PLC Address | Địa chỉ PLC nơi ghi kết quả ra. | PLC address where results are written. |
| setpositionzero | Set Position Zero | Đặt gốc toạ độ và chiều trục cho vị trí. | Set origin corner and axis directions. |
| setareascale | Set AreaScale | Hiệu chuẩn tỉ lệ pixel → mm thực. | Calibrate pixel → real mm scale. |
| calibposition | Calib Position | Lưới hiệu chuẩn bù sai lệch vị trí. | Calibration mesh compensating position error. |
| sendpositionangle | Send Position Angle | Bật gửi vị trí/góc ra PLC theo mapping. | Send position/angle to PLC by mapping. |
| output | Output | Cấu hình xuất kết quả (PLC/limit/communication). | Result output config (PLC/limit/communication). |
| basic | Basic | Tham số cơ bản của công cụ. | Core parameters of the tool. |
| extension | Extension | Tham số mở rộng, ít dùng hơn. | Extended, less-common parameters. |
| filtershape | Filter Shape | Bộ lọc tiền xử lý hình dạng trước khi khớp. | Shape pre-processing filters before matching. |
| model | Model | Cấu hình/chọn model AI. | Configure or select the AI model. |
| training | Training | Dữ liệu và tiến trình huấn luyện. | Training data and progress. |
| parameters | Parameters | Tham số của mục này. | Parameters for this section. |

### Keyword fallback group (when key/title doesn't match the table above — substring match, like `SemanticOrder`)
| Contains keyword | VI | EN |
|---|---|---|
| roi / crop / region | Vùng xử lý trên ảnh. | Image region to process. |
| mode / method / direction / source | Chọn chế độ/phương pháp xử lý. | Select processing mode/method. |
| sample / target / template | Mẫu chuẩn dùng để so khớp. | Reference sample used for matching. |
| score / tolerance / comparison | Ngưỡng/dung sai để đánh giá đạt. | Threshold/tolerance to judge pass. |
| result / limit / count | Điều kiện số lượng/kết quả. | Count/result condition. |
| filter / threshold / noise / morph / mask | Bộ lọc tiền xử lý ảnh. | Image pre-processing filter. |
| scale / geometry / ransac / calibration | Hiệu chuẩn tỉ lệ/hình học. | Scale/geometry calibration. |
| output / plc / communication | Xuất kết quả ra PLC/giao tiếp. | Output results to PLC/communication. |
| dataset / class / training / progress | Dữ liệu/huấn luyện AI. | AI dataset/training. |

Final placeholder (no match at all): VI `"Cấu hình " + title`, EN `"Configure " + title`.
