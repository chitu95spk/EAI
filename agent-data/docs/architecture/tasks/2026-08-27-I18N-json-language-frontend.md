# Task I18N — JSON-backed EN|VIE language system + front-end selector + dev editor

## Links
- Builds on: `2026-08-27-UI2L-parameter-accordion-two-line-tip.md` (already implemented: `LocalizedText`,
  `ParameterTipCatalog`, `BeeGlobal.UiLanguage`, `Config.UiLanguage`, `ParameterAccordion.GlobalLanguageProvider`).
- CLAUDE.md sections: 0 (Hard Rules), 7 (DoD).
- New/target files listed under In-scope.

## Goal
Turn the current in-code VI/EN catalog into a real, externalized i18n system:
1. **Two languages EN | VIE**, chosen by the user from a front-end control.
2. **Language strings stored as JSON files** (`en.json`, `vi.json`) — editable/deployable, not hard-coded.
3. **Codex auto-lists all content** (section Titles, Tips, and other accordion labels) into the JSON with **one
   consistent key scheme**, EN and VIE aligned 1:1, no duplicates.
4. **A Development-mode Edit Form** to review/edit strings when the UI changes (add missing translations, spot
   new/unused keys), saving back to the JSON.

## Execution guidance for Codex 5.5 (READ FIRST — save tokens)
- **Do NOT scan the whole repo.** Anchors are given. To harvest keys, prefer the **runtime registry** (below) over
  static grepping. The only allowed narrow scan: reading the tool `*.AccordionLayout.cs` **title string literals**
  when seeding the initial JSON (T2), and only those files.
- **Codex authors the initial JSON content itself** (VI+EN) — do not ask the user. Reuse the existing
  `ParameterTipCatalog` VI/EN pairs verbatim as the seed; write titles + any new tips in the same short style.
- **Minimal testing.** Build once per commit (`/v:minimal`). One smoke each: switch EN↔VIE on ToolPattern; open the
  dev editor, edit one string, save, reload. No matrices, no repeat loops, no screenshots.
- Work **T1 → T4** sequentially, one commit per T. `git add` only In-scope files. Never `git add .`.
- Reuse the existing decoupling pattern (`GlobalLanguageProvider` static Func) so `BeeInterfaceControls` never
  gains a JSON or WinForms dependency.

## Locked decisions
1. Languages: **EN + VIE only** (matches `BeeGlobal.UiLanguage`).
2. Storage: **JSON files on disk**, one per language, same key set.
3. Content: **Codex generates** the initial catalog; unified namespaced keys; EN/VIE 1:1.
4. Editing: **Development-mode Form** only (hidden in production).
5. No live machine translation — static JSON, hand/codex authored.

## In-scope (files you may edit / create)
Infra (no WinForms, no repo-wide deps):
- **NEW** `BeeGlobal/Localization/LanguageStore.cs` — loads/saves JSON, resolves `id → text` by current language.
- **NEW** `BeeGlobal/Localization/LocalizationRegistry.cs` — in-memory `id → {category, defaultVi, defaultEn, seen}`.
- `BeeGlobal/Enums.cs` — (already has `UiLanguage`; add a `LocalizedCategory` enum if useful).
- `BeeGlobal/Config.cs` — add `[OptionalField] public bool DeveloperMode = false;` if no dev flag exists (verify first).

LocalizedText / catalog:
- `BeeInterfaceControls/Custom/LocalizedText.cs` — add optional `Key`, resolve JSON-first via a static resolver.
- `BeeInterfaceControls/Custom/ParameterTipCatalog.cs` — register its entries into the registry + resolve through the store.
- `BeeInterfaceControls/Custom/ParameterAccordion.cs` — add a `static Func<string,UiLanguage,string> GlobalTextResolver`
  (next to `GlobalLanguageProvider`) + a `LanguageChanged`-driven `ReloadLanguage()` that re-runs `RefreshHeader` on
  all sections.

Wiring + front-end:
- `BeeUi/Form/FormLoad.cs` — at startup: init `LanguageStore` (load JSON, create defaults if missing), wire
  `ParameterAccordion.GlobalTextResolver` to the store, then wire `GlobalLanguageProvider` (already there).
- `BeeInterface/GeneralSetting.cs` — add the **EN|VIE selector** bound to `Config.UiLanguage`; on change persist
  Config, set `LanguageStore.CurrentLanguage`, raise `LanguageChanged`.

Dev editor:
- **NEW** `BeeInterface/Localization/FormLanguageEditor.cs` (+ `.Designer.cs`, `.resx`) — dev-only grid editor.
- Entry point to open it (dev-only) — a button in `GeneralSetting` shown only when `Config.DeveloperMode`.

Language data (content files, copied to output — NOT build artifacts):
- **NEW** `Lang/en.json`, `Lang/vi.json` at the app data location (same folder `LoadData` reads `Config` from —
  Codex must confirm that path from `BeeCore/Data/LoadData.cs`; do not hard-code a random path).

## Out-of-scope
- `EasyScreen/**` (separate app).
- Native projects, tool business logic.
- Renames (`Propety` etc.).
- Translating every string in the whole app in one pass — **scope the content to accordion Titles + Tips + accordion
  labels first**; other UI strings are a later, optional extension (T5).
- Do NOT rebuild `BeeLicense.dll` into Release (project rule).

## Architecture

### JSON schema (one file per language, identical key set)
`Lang/vi.json`:
```json
{
  "version": 1,
  "language": "vi",
  "strings": {
    "section.title.area": "Vùng",
    "section.tip.area": "Vùng ROI để dò tìm hoặc đo trên ảnh.",
    "section.title.featuremode": "Feature Mode",
    "section.tip.featuremode": "Chọn kiểu đặc trưng khớp mẫu: hình dạng, cân bằng hay mức xám.",
    "keyword.tip.roi": "Vùng xử lý trên ảnh."
  }
}
```
Key scheme (unified): `<domain>.<kind>.<name>`
- `section.title.<normalizedKey>`, `section.tip.<normalizedKey>` — from accordion sections.
- `keyword.tip.<keyword>` — from the keyword-fallback group.
- `ui.label.<name>`, `ui.button.<name>` — for the later extension (T5).
`<normalizedKey>` = the existing `NormalizeKey` output (letters+digits, lowercase). EN file has the same keys.

### LanguageStore (BeeGlobal, no UI)
- `Load(baseDir)` reads `Lang/{en,vi}.json`; if a file is missing, create it from registry defaults (bootstrap).
- `CurrentLanguage` (from `Config.UiLanguage`).
- `string Resolve(string id)` → current-language string; fallback: other language → registry default → the raw id.
- `Save()` writes both files (used by the dev editor). Use the project's existing JSON lib (Newtonsoft — confirm
  BeeGlobal can reference it; if not, place the store in `BeeCore` IO layer and expose it to `BeeInterfaceControls`
  only through the static `GlobalTextResolver` Func so no new assembly reference is forced).
- Thread-safe read; edits happen on the UI thread via the editor.

### LocalizationRegistry (BeeGlobal, no UI)
- Every `LocalizedText` that carries a `Key` registers `{id, category, defaultVi, defaultEn}` on first use and marks
  `seen=true` when actually rendered. This is how "when the UI changes" is detected: new keys appear in the registry
  with no JSON entry → the editor flags them as **New/Missing**; JSON keys never seen → **Unused**.

### LocalizedText change (backward compatible)
- Add `public string Key` (optional). Keep `Vi`/`En` as embedded defaults.
- `Resolve()`: if `Key` set and `ParameterAccordion.GlobalTextResolver != null`, return
  `GlobalTextResolver(Key, currentLang)` (which hits the store); else fall back to the current embedded Vi/En logic.
- Add `LocalizedText.Keyed(string id, string viDefault, string enDefault)` — registers the defaults and resolves via
  the store. Existing `LocalizedText.Both(vi,en)` keeps working unchanged.

### ParameterTipCatalog → store-backed
- On init, **register** every existing entry as `section.tip.<key>` (defaults = current VI/EN) and each keyword as
  `keyword.tip.<kw>`. Resolution goes through the store (JSON overrides the embedded default). The embedded table
  stays as the bootstrap/default source, so the app still works if JSON is absent.
- Section titles: when a section is created, also register `section.title.<key>` with the literal title as both
  defaults (EN==VI initially; the editor lets someone localize the title later). Titles then resolve through the
  store too, so a VIE user can get translated headers.

### Front-end selector + live refresh
- In `GeneralSetting`, an EN|VIE control (ComboBox or two RJButtons). On change:
  1. `Global.Config.UiLanguage = chosen; SaveData.Config(...)` (persist).
  2. `LanguageStore.CurrentLanguage = chosen`.
  3. Raise a global `LanguageChanged` event.
- `ParameterAccordion` subscribes to `LanguageChanged` → `ReloadLanguage()` = re-run `RefreshHeaderTitles` +
  `section.RefreshHeader()` on all sections so tips/titles/values re-resolve live. Fallback if live refresh is risky:
  show "reopen the tool to apply language" — but prefer live refresh.

### Development-mode editor (`FormLanguageEditor`)
- Opens only when `Config.DeveloperMode` is true (button hidden otherwise).
- `DataGridView`: columns **Key | Category | VI | EN | Status**. Status = OK / Missing VI / Missing EN / New (in
  registry, not in JSON) / Unused (in JSON, never seen).
- Buttons: **Reload**, **Save** (writes both JSON files via `LanguageStore.Save`), **Harvest** (pull current
  registry so keys added since last run appear), **Filter: problems only**.
- Editing a cell updates the in-memory store; Save persists. Keep it simple; no fancy validation beyond
  missing/unused flags.

## Steps (each T = one commit)

**T1 — Infra: store + registry + LocalizedText.Key + resolver wiring** (`[I18N.1]`)
1. `LanguageStore` + `LocalizationRegistry` in `BeeGlobal/Localization/`.
2. `LocalizedText.Key` + `Keyed(...)` + JSON-first `Resolve()`; add `ParameterAccordion.GlobalTextResolver`.
3. `FormLoad.cs`: init store (load/bootstrap JSON at the confirmed data dir), wire `GlobalTextResolver`.
4. Add `Config.DeveloperMode` if absent.
- Verify: build passes; app still runs with existing (embedded) tips even before JSON exists.

**T2 — Seed JSON + move catalog/titles to store-backed** (`[I18N.2]`)
1. `ParameterTipCatalog`: register all tips (`section.tip.*`) and keywords (`keyword.tip.*`); resolve via store.
2. Register `section.title.*` when sections are created (default = literal title).
3. Generate initial `Lang/en.json` + `Lang/vi.json`: port every `ParameterTipCatalog` VI/EN pair; add `section.title.*`
   entries harvested from the tool `*.AccordionLayout.cs` title literals (narrow read of those files only). EN/VIE 1:1,
   no duplicate keys, unified scheme.
- Verify: build passes; delete JSON → app falls back to embedded defaults; restore JSON → values come from JSON.

**T3 — Front-end EN|VIE selector + live refresh** (`[I18N.3]`)
1. Selector in `GeneralSetting` bound to `Config.UiLanguage`; persist + set store language + raise `LanguageChanged`.
2. `ParameterAccordion.ReloadLanguage()` on `LanguageChanged`; subscribe/unsubscribe with `-=` before `+=`.
- Verify (once): open ToolPattern, switch VIE↔ENG → section tips/titles change live (or after reopening the tool).

**T4 — Development-mode language editor** (`[I18N.4]`)
1. `FormLanguageEditor` (grid + Reload/Save/Harvest/Filter), dev-only entry from `GeneralSetting`.
2. Wire Save → `LanguageStore.Save()` (writes both JSON files).
- Verify (once): with `DeveloperMode=true`, open editor, edit one VI cell, Save, Reload → change persisted in
  `vi.json`; a newly added section shows as New/Missing after Harvest.

**T5 — (OPTIONAL, later) extend harvest to more UI strings** — skip unless asked.
- Add `ui.label.*` / `ui.button.*` keys for tab titles, common buttons, etc., via the same registry+editor flow.

## Verify commands (minimal)
| Command | Expected |
|---|---|
| `MSBuild EasyVision.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal` (once per commit) | pass, warnings ≤ baseline |
| Smoke: ToolPattern, switch EN↔VIE | tips/titles re-resolve from JSON |
| Smoke: dev editor edit + Save + Reload | change written to `vi.json`/`en.json`; New/Unused flags work |
- No repeat/matrix testing, no full-repo scan. Do NOT rebuild BeeLicense.dll into Release.

## Rollback
- Any T fails: `git reset --hard HEAD~1` (unpushed) or `git revert <sha>`.
- JSON absent/corrupt at runtime must never crash — store falls back to embedded defaults; verify this in T1.

## Exit criteria (DoD)
- [ ] Build passes, warnings ≤ baseline.
- [ ] `Lang/en.json` + `Lang/vi.json` exist with a unified key scheme, EN/VIE 1:1, seeded by Codex (tips + titles).
- [ ] User can pick EN|VIE on the front end; tips/titles switch (live or on reopen); choice persists in Config.
- [ ] Missing JSON/keys degrade to embedded defaults, never crash.
- [ ] Dev-mode `FormLanguageEditor` lists/edit/saves keys and flags New/Missing/Unused; hidden in production.
- [ ] `-=` before `+=` on all new event subscriptions; each commit only its In-scope files.
- [ ] `CODEX_HISTORY.md` new section (template 11.2), noting JSON location + store assembly choice.

## Notes / Stop-and-ask
- Confirm the app data directory from `LoadData`/`SaveData` before choosing the `Lang/` path; don't invent one.
- If BeeGlobal cannot reference the JSON lib cleanly, keep the store in the IO layer and expose it to
  `BeeInterfaceControls` only via `GlobalTextResolver` (no new assembly reference from BeeInterfaceControls).
- If live language refresh risks breaking accordion expansion/selection state, fall back to "reopen tool to apply"
  and stop-and-ask.
- Keep the editor minimal — it is a dev tool, not a shipped feature.
