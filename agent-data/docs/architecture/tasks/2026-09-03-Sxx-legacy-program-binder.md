# S-FOUNDATION — LegacyProgramBinder (run FIRST, before S022 / S023 / S035)

> Register under `.structure-map.md` with the next free `S` id. **Sequence: this slice must land
> BEFORE any serialized-type move** (companion `beeglobal-decoupling-plan.md` S022 enum-down / S023
> ParaBit/ParaValue; `solution-structure-review.md` S035 Unit engines → BeeCore.Tools). It changes
> no type locations — it only makes the load path tolerant of future moves.
> Risk: **medium** (touches the one deserialization path all programs go through).
>
> **Relationship to the JSON migration** (`docs/architecture/persistence-json-migration-plan.md`):
> this binder keeps the **legacy binary READ** path working for existing `.prog` files after types
> move. The JSON plan makes **new/re-saved** files immune to moves entirely (stable type keys).
> They coexist: binder = backlog of old binary files; JSON = everything saved from now on. The two
> binders share one `TypeRegistry` (JSON plan PJ2).

## Why this must be first

Programs persist as `List<List<PropetyTool>>` via `BinaryFormatter` → base64
(`BeeCore/Data/Access.cs`). BinaryFormatter stores each object/field record's **assembly + full type
name**. When a `[Serializable]` type (`PropetyTool`, the `BeeCore/Unit/*` engines, `ProgNo`,
`AutoTriggerProfile`, `CameraCalibrationConfig`, `ImageSourceProfile`, the `Enums.cs` enum types)
later moves to a new assembly, an old file names the **old** assembly and load fails.

Worse: `LoadBase64` (Access.cs:81-93) **swallows every exception** and returns
`defaultFactory()` — i.e. an **empty program**, silently. Without this binder a type move would not
crash; it would make saved tools *vanish*. So: install the binder first, then moves become safe.

## What it does

A single `SerializationBinder` on the **read** formatter that resolves a stored type name to the
type wherever it now lives, by:
1. an explicit **rename table** (`OldFullName → Type`) for any type whose *name/namespace* ever
   changes (empty today — we only move assemblies, not rename); then
2. a **FullName scan** across candidate assemblies (BeeCore, BeeCore.Tools, BeeGlobal,
   BeeIOContracts, BeeEcat, …): the stored `typeName` (namespace+name) is unchanged, only the
   assembly moved, so a match by `Type.FullName` finds it regardless of assembly.
3. Non-`Bee*` types (BCL, System.Drawing, OpenCvSharp) → return `null` = default binding untouched.

## Slice steps

1. **Add** `BeeCore/Data/LegacyProgramBinder.cs` (code below).
2. **Wire it into the read path only** — in `CreateFormatter(bool forRead)` (Access.cs ~125),
   inside the `if (forRead)` block, after the existing `SurrogateSelector` setup, add:
   ```csharp
   formatter.Binder = LegacyProgramBinder.Instance;
   ```
   Do **not** set a binder on the write path (`forRead == false`) — new files must be written with
   current names.
3. **Optional but recommended (same commit):** make silent load failures visible during migration.
   In `LoadBase64` (Access.cs:89) change the bare `catch` to log before falling back, so a real
   deserialization break is not mistaken for "no file":
   ```csharp
   catch (Exception ex)
   {
       BeeGlobal.Global.LogError(nameof(Access), "LoadBase64 failed: " + path, ex);
       return defaultFactory != null ? defaultFactory() : default(T);
   }
   ```
   Keep the missing-file branch (Access.cs:85-86) returning the default **without** logging.
4. Build; register the S-id; append `CODEX_HISTORY.md`.

## File: `BeeCore/Data/LegacyProgramBinder.cs`

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

namespace BeeCore
{
    /// <summary>
    /// Read-side SerializationBinder that keeps OLD saved programs (BinaryFormatter streams that
    /// embed assembly-qualified type names) loadable after Bee* [Serializable] types are relocated
    /// to different assemblies. Type NAME/namespace must stay unchanged; only the assembly moves.
    /// For a genuine rename, add an entry to <see cref="Renamed"/>.
    /// </summary>
    public sealed class LegacyProgramBinder : SerializationBinder
    {
        public static readonly LegacyProgramBinder Instance = new LegacyProgramBinder();

        // Only intervene for our own types; let the default binder handle BCL / third-party.
        private static readonly string[] OwnedPrefixes =
        {
            "BeeCore", "BeeGlobal", "BeeIOContracts", "BeeEcat", "BeeLadder"
        };

        // Explicit remap for RENAMED / re-namespaced types. Key = old Type.FullName.
        // Empty today (moves keep the name). Example for a future rename:
        //   { "BeeGlobal.OldToolName", typeof(BeeCore.Tools.NewToolName) }
        private static readonly Dictionary<string, Type> Renamed =
            new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                // { "BeeGlobal.SomeOldType", typeof(SomeNewType) },
            };

        private readonly ConcurrentDictionary<string, Type> _cache =
            new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);

        public override Type BindToType(string assemblyName, string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            // 1) explicit renames win
            Type mapped;
            if (Renamed.TryGetValue(typeName, out mapped))
                return mapped;

            // 2) only our types; everything else -> default binding (return null)
            if (!IsOwned(typeName))
                return null;

            // 3) resolve by FullName across loaded assemblies, ignoring the stored assembly name
            return _cache.GetOrAdd(typeName, ResolveByFullName);
        }

        private static bool IsOwned(string typeName)
        {
            for (int i = 0; i < OwnedPrefixes.Length; i++)
                if (typeName.StartsWith(OwnedPrefixes[i], StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static Type ResolveByFullName(string typeName)
        {
            // Fast path: already loadable in the current context (same assembly, or a
            // [TypeForwardedTo] hop the runtime follows on its own).
            Type direct = Type.GetType(typeName, throwOnError: false);
            if (direct != null)
                return direct;

            // Scan every loaded assembly for a type whose FullName matches (assembly moved).
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type t = SafeGetType(assemblies[i], typeName);
                if (t != null)
                    return t;
            }

            // Not found: return null so BinaryFormatter surfaces a clear SerializationException
            // (which LoadBase64 now logs) instead of us guessing wrong.
            return null;
        }

        private static Type SafeGetType(Assembly asm, string fullName)
        {
            try { return asm.GetType(fullName, throwOnError: false); }
            catch (ReflectionTypeLoadException) { return null; }
            catch (TypeLoadException) { return null; }
            catch (FileNotFoundException) { return null; }
        }
    }
}
```

## Remap table — how to use it going forward

| Situation | What to do |
| --- | --- |
| Move a `[Serializable]` type to another assembly, **same name+namespace** | Nothing to add — the FullName scan finds it. Still add `[assembly: TypeForwardedTo]` in the origin assembly as a belt-and-braces fallback. |
| Rename a type or change its namespace | Add `{ "Old.Full.Name", typeof(New.Full.Name) }` to `Renamed`. (The plans forbid renames during moves, so this stays rare.) |
| A moved type is in an assembly not in `OwnedPrefixes` | Add the new assembly's root name to `OwnedPrefixes`. |

The candidate assemblies do not need to be listed explicitly — the scan walks every loaded
assembly. `OwnedPrefixes` is only a guard so BCL/third-party types skip the scan.

## Verification

1. Build to `build.log`; `grep -cE ": error " build.log` = 0; warnings ≤ baseline.
2. **Golden-file smoke (the point of this slice):** keep a program file saved by the **current
   shipped build** as a fixture. Load it → assert the tool list is **non-empty and the same count**
   as before (not just "no exception" — remember `LoadBase64` hides exceptions). This test is the
   regression guard every later serialized-type slice (S022/S023/S035) re-runs.
3. `bash tools/check_propety_tools.sh` = 0.
4. `git diff --name-only` = only `BeeCore/Data/Access.cs` + `BeeCore/Data/LegacyProgramBinder.cs`
   (+ csproj Compile item). Append `CODEX_HISTORY.md`.

## Caveats for the implementer

- **Generics.** The root is `List<List<PropetyTool>>`. With a binder set, BinaryFormatter calls
  `BindToType` for the type records it reads; the `PropetyTool` element name is unchanged so the
  scan resolves it. Verify empirically with the golden file — if a generic container itself is a
  `Bee*` generic that fails to bind, return `null` for the container typeName to defer to default.
- **Do not** attach the binder to the write formatter — only `forRead`.
- **Order:** land + prove this slice (golden-file smoke green) before starting S022. After it,
  S022/S023/S035 add `[TypeForwardedTo]` in the origin assembly and re-run the same golden smoke.
```
