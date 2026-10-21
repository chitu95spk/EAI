# Persistence Migration — Binary(.prog) → self-describing JSON (Codex Execution Plan)

> Goal: **Load auto-detects the format** (legacy base64/BinaryFormatter *or* new JSON) and reads
> either; **Save always writes the new JSON format.** Every re-save silently migrates a program.
> Scope is isolated to `BeeCore/Data/*` + one new serializer type — **no tool DTO is rewritten.**
> Companion to `beeglobal-decoupling-plan.md`, `solution-structure-review.md`, and the
> `LegacyProgramBinder` foundation slice. Register slices under `.structure-map.md` (next free `S`
> ids); plan-local names PJ1–PJ6.
> Same global rules (CLAUDE.md §0/§12): one slice = one commit, build-to-file, warnings ≤ baseline,
> no `git add .`, append `CODEX_HISTORY.md`.

---

## 1. Why JSON, and why it also de-risks the structure refactor

Programs today persist as `List<List<PropetyTool>>` via `BinaryFormatter` → base64 → `.prog` file
(`Common\Common.prog`, `Program\<Project>\<Project>.prog`; see `BeeCore/Data/Access.cs`,
`LoadData.cs`, `SaveData.cs`). BinaryFormatter embeds **assembly + type names**, so it is fragile to
the very refactor this repo is planning (moving `Yolo`/`Patterns`/… to `BeeCore.Tools`, enums to
`BeeIOContracts`).

A JSON format that records **short, stable type keys** (`"Yolo"`, not
`BeeCore.Unit.Yolo, BeeCore`) is **immune to assembly/namespace moves forever**. So landing the
JSON write path *before* the big type moves means new/re-saved files never break again; only the
shrinking backlog of old `.prog` binary files needs the `LegacyProgramBinder` (which stays for
reading that backlog). **Recommended order: do PJ1–PJ3 early, alongside/after the binder slice,
before S022/S035.**

## 2. Measured facts that shape the design

- **Newtonsoft.Json 13.0.4** is already referenced by BeeCore (`BeeCore.csproj:82`). No new dep.
- The graph is **polymorphic**: `PropetyTool.Propety` / `Propety2` are `dynamic` (=`object`) slots
  that hold the concrete engine (`Yolo`, `Patterns`, `Edge`, `Circle`, `Width`, `PositionAdj`, …).
  Engines are standalone `[Serializable]` classes, **not** subclasses of `PropetyTool`.
  → JSON **must** emit `$type` on these slots (`TypeNameHandling`), and it must use a **stable
  binder** so the `$type` value is refactor-proof.
- `PropetyTool : ISerializable` with a `protected PropetyTool(SerializationInfo, StreamingContext)`
  ctor + `GetObjectData`, and **tolerant reads** (`GetValueOrDefault`/`GetObjectOrDefault`, missing
  field → default). Newtonsoft honors `ISerializable` and this tolerance carries over to JSON.
- The `[Serializable]` engines serialize **fields** (BinaryFormatter semantics), with many
  `[NonSerialized]` runtime-only fields (`Edge` 18, `BarCode` 16, `CraftOCR` 16, …) holding
  `Mat`/handles. JSON must (a) serialize fields, not properties, and (b) skip `[NonSerialized]`.
  Newtonsoft does both when the contract resolver has `IgnoreSerializableAttribute = false`.
- `LoadBase64` (Access.cs:81-93) **swallows exceptions → returns an empty program.** So any load
  regression is silent data loss. Every gate below asserts **tool count matches**, not merely
  "no exception".

## 3. Format design

### 3.1 On-disk envelope (new files)
Write a small JSON envelope so the format is self-describing and versioned:
```json
{ "fmt": "beeprog/1", "kind": "programs", "savedUtc": "…", "data": <payload> }
```
Keep the **same `.prog` path/extension** (the loader sniffs content, not extension) so nothing
else in the app changes.

### 3.2 Detection (load path)
Read the file, take the first non-whitespace byte:
- `{` (0x7B) → **new JSON** path → parse envelope, deserialize `data`.
- anything else → **legacy** path → the existing base64 → `BinaryFormatter` (with
  `LegacyProgramBinder`) exactly as today.

Base64 of a BinaryFormatter stream never begins with `{` (it begins with `AAEAAAD…`), so the sniff
is unambiguous. Belt-and-braces: if JSON parse throws, fall back to the legacy path before giving up.

## 4. The serializer (crux) — new file `BeeCore/Data/ProgramJson.cs`

```csharp
using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace BeeCore
{
    /// <summary>New JSON persistence for programs. Field-faithful to the [Serializable]/
    /// ISerializable model; polymorphic engine slots carry a STABLE $type key (assembly-move-proof).</summary>
    public static class ProgramJson
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            TypeNameHandling      = TypeNameHandling.Auto,          // $type only where needed (object/dynamic slots)
            SerializationBinder   = StableProgramTypeBinder.Instance, // short, stable type keys
            ContractResolver      = new SerializableFieldsResolver(),  // fields + honor [NonSerialized]
            ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
            PreserveReferencesHandling = PreserveReferencesHandling.Objects, // shared refs like binary
            NullValueHandling     = NullValueHandling.Include,
            DateTimeZoneHandling  = DateTimeZoneHandling.Utc,
            Formatting            = Formatting.Indented,
            // Converters: add gap-fixers found by the golden round-trip test (Color, Mat-holder, etc.)
        };

        public static string Serialize<T>(T value) => JsonConvert.SerializeObject(value, Settings);
        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

        private sealed class SerializableFieldsResolver : DefaultContractResolver
        {
            public SerializableFieldsResolver()
            {
                // [Serializable] types -> serialize fields (incl. private), honor [NonSerialized];
                // ISerializable types (PropetyTool) -> Newtonsoft uses GetObjectData/ctor.
                IgnoreSerializableAttribute = false;
                IgnoreSerializableInterface = false;
            }
        }
    }

    /// <summary>JSON side of the stable type registry. Writes short keys, resolves them back to the
    /// current type wherever it now lives. Shares its map with LegacyProgramBinder (single source).</summary>
    public sealed class StableProgramTypeBinder : ISerializationBinder
    {
        public static readonly StableProgramTypeBinder Instance = new StableProgramTypeBinder();

        // Stable key -> current Type. Key NEVER changes even when the type moves assembly/namespace.
        private static readonly System.Collections.Generic.Dictionary<string, Type> KeyToType =
            TypeRegistry.KeyToType;                 // see PJ2: one shared registry
        private static readonly System.Collections.Generic.Dictionary<Type, string> TypeToKey =
            TypeRegistry.TypeToKey;

        public Type BindToType(string assemblyName, string typeName)
        {
            Type t;
            if (KeyToType.TryGetValue(typeName, out t)) return t;
            // tolerate old files written with a FullName before this binder existed
            return Type.GetType(typeName, throwOnError: false)
                   ?? TypeRegistry.ResolveByFullName(typeName);
        }

        public void BindToName(Type serializedType, out string assemblyName, out string typeName)
        {
            assemblyName = null;
            string key;
            typeName = TypeToKey.TryGetValue(serializedType, out key)
                ? key                               // stable short key
                : serializedType.FullName;          // fallback (log it; add to registry)
        }
    }
}
```

`TypeRegistry` (PJ2) is one shared table used by BOTH `StableProgramTypeBinder` (JSON) and
`LegacyProgramBinder` (binary): `{ "Yolo": typeof(Yolo), "Patterns": typeof(Patterns), … }` plus a
`ResolveByFullName` scan across loaded assemblies. Adding a tool = add one line here.

## 5. Migration mechanics (no surprise writes)

- **Lazy conversion (default):** loading an old `.prog` does **not** rewrite it. The file becomes
  JSON only when the user next **Saves** that program (Save always writes JSON via PJ3). Natural,
  no unexpected disk writes, no surprise for the operator.
- **Backup on first conversion:** the first time a `.prog` is written as JSON over a file that was
  legacy binary, copy the original to `<name>.prog.legacybak` once. (`AtomicWriteAllText` already
  makes a transient `.bak`; add the one-time `.legacybak` for the format switch.)
- **Optional batch tool (PJ5):** a "Convert all projects" action that loads every `.prog` and
  re-saves as JSON, reporting counts and any file that fails the count-match check. Explicit,
  operator-triggered — never automatic.

## 6. Slices

- **PJ1 · medium · detection + read plumbing (flag OFF).** Add the content sniff (§3.2) to the load
  path in `Access.cs`; route `{`→JSON, else→existing binary. Add `ProgramJson.cs` +
  `StableProgramTypeBinder`. Save still binary. Introduce `Global.Config.UseJsonPersistence`
  (default **false**). No behavior change yet; old files still read via binary.
- **PJ2 · medium · shared TypeRegistry.** Extract the stable key↔type map + `ResolveByFullName`
  into `BeeCore/Data/TypeRegistry.cs`; wire both `LegacyProgramBinder` (binary read) and
  `StableProgramTypeBinder` (JSON) to it. Seed keys for every `[Serializable]` engine + `PropetyTool`
  + `ProgNo` + persisted enums. **This is the single place future tool moves touch.**
- **PJ3 · high · golden round-trip test = the acceptance gate.** Build a fixture corpus of real
  `.prog` files (saved by the current shipped build). Test: `binary→object→JSON→object` then assert
  the graph equals the binary-loaded graph — compare by re-serializing both to BinaryFormatter bytes,
  or deep field compare; **and assert tool counts per program match.** Fix fidelity gaps by adding
  `JsonConverter`s to `ProgramJson.Settings` (likely candidates: `System.Drawing.Color`/`Rectangle`,
  `PointF[]`, `RectRotate`, any engine holding an awkward struct). Iterate until the whole corpus is
  green. **Do not flip Save until this is green.**
- **PJ4 · high · flip Save to JSON.** Set `UseJsonPersistence` default **true**; `SaveProg`/`SaveProgNo`/
  program saves in `SaveData.cs` write the JSON envelope; add the one-time `.legacybak` on the
  binary→JSON switch. Old files keep loading via the binary path. Smoke: save a project, reload it,
  tool count + a spot-checked parameter match; confirm the file is now real JSON.
- **PJ5 · low · batch convert tool + telemetry.** Operator-triggered "Convert all"; log how many
  `.prog` remain binary vs JSON.
- **PJ6 · low (later) · retire binary WRITE.** Once the corpus/field is migrated, delete the binary
  write path. **Keep binary READ + LegacyProgramBinder indefinitely** (or behind a long deprecation
  window) so any archived `.prog` still opens.

## 7. Verification & risk (per slice)

1. Build to `build.log`; errors 0; warnings ≤ baseline (`docs/architecture/baseline_build.md`).
2. **Golden corpus (PJ3 onward) is the real gate:** every listed program loads with **matching tool
   count and field-level equality**; remember `LoadBase64` hides exceptions, so "no throw" is not
   enough.
3. `bash tools/check_propety_tools.sh` = 0.
4. `git diff --name-only` in-scope only (`BeeCore/Data/*`, csproj); append `CODEX_HISTORY.md`.
5. **Stop-and-ask** if a real program cannot round-trip after reasonable converters (a DTO may need
   an explicit `[JsonConstructor]`/converter, or a genuine binary-only quirk) — surface it rather
   than shipping lossy JSON.

### Known fidelity risks to watch
- **`dynamic` slots** (`Propety`/`Propety2`): reconstructed engine must be the exact concrete type
  via `$type`; a missing registry key silently falls back to `FullName` — PJ2 must seed all engines.
- **`ISerializable` + `<Propety2>k__BackingField`**: `PropetyTool` writes/reads odd backing-field
  names; JSON keeps them literally — fine as long as read/write use the same names (they do).
- **`[NonSerialized]` fields** (Mat/handles): must stay excluded; verify none accidentally serialize.
- **Structs from System.Drawing / OpenCvSharp**: most need a converter for clean JSON — the golden
  test surfaces each; fix incrementally.
- **Reference identity**: `PreserveReferencesHandling.Objects` mirrors binary's shared-object graph;
  if a program relies on two tools sharing one referenced object, confirm it round-trips.

## 8. Rollback

Each slice is atomic (`git reset --hard`). PJ1–PJ3 change nothing on disk (Save still binary), so
they are pure additions. PJ4 is the only behavior flip and is guarded by (a) the `UseJsonPersistence`
flag — set false to revert to binary writes instantly — and (b) the `.legacybak` copy of the last
binary version of each converted file.
```
