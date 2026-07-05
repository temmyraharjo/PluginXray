# PluginXray for XrmToolBox

A XrmToolBox tool that executes a Dataverse plugin assembly under a **synthesized execution
context with a real debugger attached — without registering the plugin** in the environment.
See [requirements.md](requirements.md) for the full spec.

> **Hydrate a context from a live record, hit a breakpoint, iterate — no registration, no real
> platform trigger required.**

## Status

**All build-order steps (1–7) complete** (requirements §8) and validated:

- ✅ Service bridge with three execution modes (full-real / read-real-write-mock / full-mock)
- ✅ Per-run shadow-copy child AppDomain — original `.dll` stays unlocked so you can rebuild in VS
- ✅ PDB copied alongside the shadow copy; **symbols loaded ✓/✗** indicator per run
- ✅ Trace output, every SDK request (real/mock annotated), exceptions, and `OutputParameters`
  streamed to a copy/exportable run log
- ✅ **Message + Stage form-shape engine** (§4.3): the resolved shape drives the Target editor,
  image panels, and `OutputParameters["id"]`; impossible combinations are disabled, not warned
- ✅ **Metadata-driven typed attribute/image editor** (§4.5): add-only, type-appropriate inputs
  (string/memo/bool/number/Money/DateTime/OptionSet/MultiSelect/Guid/lookup), polymorphic-lookup
  target chooser, record picker, multiple keyed pre/post images, and typed-envelope JSON
  import/export (plain values rejected for ambiguous columns)
- ✅ **Execution context editor** (§4.4): context Mode (sync/async), Depth, UserId / InitiatingUserId
  (defaulted from WhoAmI on connect), BusinessUnitId, OrganizationId, CorrelationId, and a typed
  SharedVariables key/value grid
- ✅ **Table picker + live-record hydration** (§4.2): searchable table picker drives the primary
  entity; "Hydrate from record…" pulls a real record and lets you choose which attributes to load
  into the Target (defaults to none-checked for Update, so the Target stays "changed attrs only")
- ✅ **Visual Studio auto-attach** (§4.9): enumerates running VS instances (ROT/EnvDTE) and attaches
  the selected one to the XrmToolBox host process; manual attach still works as a fallback
- ✅ Trigger UI: assembly + type picker, message/stage/mode, real-write confirmation, prod banner

All §8 build-order steps (1–7) are implemented. Remaining polish: metadata-based production
detection (currently a name/url heuristic).

## Layout

```
src/PluginDebugger.Runtime/   net48 class lib — all cross-AppDomain types (SDK refs only)
src/PluginDebugger/           net48 WinForms — the XrmToolBox plugin (UI)
samples/SamplePlugin/         net462 sample IPlugin used for validation
tests/SmokeTest/              net48 console harness that drives PluginRunner directly
```

The runtime is split out so it can be loaded into the child AppDomain without dragging WinForms
along. The cross-domain marshaling design (why SDK objects travel as XML strings, not by value)
is documented in the source headers of `ServiceBridge`, `ChildServices`, and `PluginExecutor`.

## Build & test

```powershell
dotnet build PluginDebugger.slnx

# validate the keystone (no live connection needed):
dotnet build samples/SamplePlugin/SamplePlugin.csproj
dotnet build tests/SmokeTest/SmokeTest.csproj
tests/SmokeTest/bin/Debug/SmokeTest.exe
```

## Deploy into XrmToolBox

Copy `src/PluginDebugger/bin/Debug/PluginDebugger.dll` and `PluginDebugger.Runtime.dll` into
your XrmToolBox `Plugins` folder, then restart XrmToolBox. The tool appears as
**“PluginXray.”**

## Debugging loop

1. Open the tool, connect to an environment.
2. Browse to your built plugin `.dll`, **Load types**, pick the plugin type.
3. Choose message / stage / mode, fill the Target, attach Visual Studio to the **XrmToolBox**
   process and set breakpoints in your plugin source.
4. **Trigger.** Breakpoints bind (watch the symbols indicator). Rebuild in VS and Trigger again —
   no XrmToolBox restart needed.
