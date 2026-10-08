# Explicit release adapters and PLC foundation workers

These eight peer projects compile the PLC foundation subset and Studio native
extension surfaces into separate typed libraries. V20 and V21 have exactly the same project structure as
the earlier releases. The foundation host publishes the matching subset for V14 SP1–V19,
while V20/V21 retain their full engines. Each worker references exactly one selected adapter.
Use `dotnet run --project build-tools/release -- build-multi-version` from the repository root to build, test and deploy
the eight-version distribution. Native TIA acceptance remains pending.

The native source allowlist is shared by source, not by an API-bound binary.
It is intentionally transitional: `PlcReadContracts.cs` still contains typed
implementation. Version symbols come from `src/Shared/TiaFeatures.props`.
No full eight-copy source fork is introduced; plain DTOs and interfaces live in
`TiaMcp.Adapters.Contracts`.

## Studio extension surfaces (step G, part 1)

`StudioAdapter` is an opt-in `IOpennessAdapter` implementation for the bridge's
existing behavior. It owns one `Native/Studio/OpennessSession`; all its facets share
that session and its project/index/VCI caches. The caller installs the exact SDK
resolver before construction and creates/uses/disposes the adapter on the bridge's
STA. Every operation and VCI access checks the owning thread and rejects reentry,
including calls from synchronous progress callbacks. Nothing switches apartments.
Construction and disconnected state reads do not attach to or launch TIA.

| Surface | Studio operations |
|---|---|
| `IStudioSession` | Attach-or-launch, project lifecycle, PLC/HMI block listing and export, PLC import, tags and compile |
| `IHardware` | Device listing only |
| `IHmiExport` | Item listing and XML export through the existing device dispatch |
| `IVersionControl` | Workspace listing/creation, mapping, status and synchronization |

The separate session profile preserves Studio's device-name and block-path lookup,
results, overwrite behavior, exception text and callback order; these differ from
Foundation's `IPortalSession`, `IPlcProgram` and `IPlcData`. Those three facets are
null on `StudioAdapter`. Conversely, the Foundation `OpennessAdapter` still has
null Studio/hardware/HMI/VCI facets and unchanged capabilities. PLC block export
with `ExportFormat.Source` keeps the original `GenerateSource` block/type overloads
and `GenerateOptions.None`; `AdapterCapabilities.GenerateSource` advertises it.
The host still owns error mapping, cancellation and timeouts.

`StudioSession`, `Hardware`, `HmiExport` and `GenerateSource` are available in all
eight builds. `VersionControl` is compiled for 16–21, `VersionControlInitial` for
16–17, and `VersionControlModern` for 20–21. Versions 18–19 use the existing legacy
VCI variant. The VCI facet is null until the open project supplies its service;
the capability flag describes compiled support, not a native service probe.

The five moved source files retain their native method bodies. Conditional imports
select the new `Contracts.Studio` values inside adapters and the unchanged original
Studio DTOs inside `StudioOpenness.V*`. The legacy `Studio.Common.props` explicitly
links the moved files and retains `OpennessSessionFactory`/`StudioRelease` locally.
Core, bridge, GUI and packaging still load the legacy, unwoven assemblies. No product
path instantiates `StudioAdapter` yet. Inspection rules and Git workspace diff stay
in Core; the legacy `Inspect` method is compiled only for that existing host. The
new profile supplies `FindPlcDeviceId` followed by `ListBlocks(deviceId, false)`
for inspection: these reuse the same `TryGetPlc` then `ListBlocks` sequence, return
the canonical device name, and let the host retain empty inspection for non-PLCs.

The 23 Studio DTO/enum types preserve their original member order, defaults and
enum values in a distinct namespace. They have no Siemens, JSON or Studio host
dependency. 68 frozen JSON samples come from the unchanged Studio DTOs with the
bridge's existing string-enum/null settings. Foundation DTOs and wire fixtures are
unchanged.

## Build

From the repository root, with the matching real PublicAPI directory and the
real .NET Framework targeting pack installed:

```powershell
dotnet build build-tools/native-call-weaver/NativeCallWeaver.csproj -c Release
dotnet build src/Adapters/V20/Adapter.20.csproj -c Release -p:SiemensEngineeringDirectory="C:/authorized-sdk/TIA_V20_PublicAPI/V20"
```

`dotnet run --project build-tools/release -- build-plc-workers -PublicApiRoot <eight-version-root>`
builds each release-matched worker with exactly its matching Adapter project. It
verifies coverage again on the copied Adapter DLL, checks that no old Foundation
or Siemens DLL was copied, and records hashes. It never launches a worker or TIA.
`-SourceRoot` can select a read-only snapshot; outputs stay in the candidate tree.

The release is fixed by the project. An optional `TiaReleaseKey` must match it.
All eight adapters and workers target net48. Missing 4.8 reference assemblies
are a prerequisite failure, never a reason to substitute a TFM.
`UseReferenceAssemblyPackage=true` is the default and selects Microsoft's
`Microsoft.NETFramework.ReferenceAssemblies.net48` 1.0.3 package. Use
`-p:UseReferenceAssemblyPackage=false` with an installed 4.8 targeting pack.
An existing authorized package cache can be selected with `RestoreSources`.
V21 checks Base, Step7 and Safety; other targets check the monolithic core.
Every selected assembly must have the exact expected full strong-name identity.
Siemens references are non-copy-local. No Siemens package, DLL or stub ships here.

In a read-only source review, `-p:AdapterSourceRoot=<repo>/src`
reads the original allowlisted source while all outputs remain next to these new
projects. Normally the source root is derived from the integrated layout.

Each project owns `obj/<key>/` and `bin/<key>/<configuration>/<framework>/`.
There are no project references into the original tree and no source glob.
The projects opt out of inherited Directory.Build props/targets so the existing
EXE instrumentation target does not silently execute on these libraries.
Their own target now invokes the existing weaver on the intermediate Adapter DLL
after CoreCompile and verifies it before output copying. Missing weaver or failed
coverage stops the build. `NativeCallWeaverPath` may select an existing verified
tool build; the tool is an additional compile input so changes invalidate output.

`Diagnostics/` is one API-independent runtime source shared by all eight builds.
It preserves the existing runtime's call bracketing, weak object lineage,
enumeration adapters, exception redaction and best-effort flushed journal. Its
adapter serializer uses the existing JSON-library-free implementation;
the full-engine runtime retains its existing System.Text.Json dependency.
The exact existing PortalFailureClassifier source is linked without Siemens
references. This is a serializer variant to maintain in parallel until a later
common diagnostic abstraction removes the duplication, not eight runtime copies.

## Integration and remaining work

Each worker selects one exact adapter and preserves its resolver and STA session model.
The host and configurator now select the eight precise release keys. V14 SP1–V19 use
the foundation catalog; V20/V21 retain their full engines. `build-multi-version`
assembles the runtime directories, tests real STDIO/HTTP against synthetic child workers,
and records hashes. The former blanket Publish/Pack refusal has been removed.

Do not load both the old PLC foundation assembly and a typed adapter in one process;
their implementation types intentionally overlap during this migration. Native call
instrumentation remains required. Complete native diagnostic correlation and actual
Siemens import/export/compile roundtrips still require separate validation.

## Input regression checks

`dotnet run src/Adapters/build/Test-AdapterInputs.cs -- --source-root src
--public-api-root <eight-version-sdk-root> --evidence-directory <writable-output>`
checks eight real API selections and twelve invalid configurations. It invokes
only the metadata validation target, without restore, compilation or native
execution. Compile each adapter separately to verify its actual source bindings.
For isolated builds, set DOTNET_CLI_HOME and NuGet caches to writable task-local
directories; set DOTNET_GENERATE_ASPNET_CERTIFICATE=false and
DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1. Use `-p:UseSharedCompilation=false` if a shared
compiler process has a different sandbox identity.

After eight builds, `dotnet run src/Adapters/build/Test-WorkerIsolation.cs --
--evidence-directory <output> --native-call-weaver <weaver.dll>` checks project selection, copied PE coverage,
five deliberate coverage corruptions per release, invalid worker configurations,
missing instrumentation tooling.

`tests/Adapters/TiaMcp.Adapters.Diagnostics.Tests/TiaMcp.Adapters.Diagnostics.Tests.csproj`
builds only the API-independent runtime against net10.0, keeping the assembly name
`TiaMcp.Adapters.Diagnostics.Tests` for `InternalsVisibleTo`. From the repository root, run:

```powershell
dotnet run --project tests/Adapters/TiaMcp.Adapters.Diagnostics.Tests/TiaMcp.Adapters.Diagnostics.Tests.csproj -c Release -- <worker-bin-root> <new-writable-journal-directory>
```

The worker bin root is `src/PlcWorker/bin` after
building all eight workers; it contains `<release-key>/Release/<framework>/`
subdirectories. The journal directory must not already exist. These checks
exercise diagnostic behavior and inspect eight worker/adapter PE files without
loading them. The project contains no Siemens references, replacement SDK, or
native tests.

The console itself needs no Siemens SDK, but its required eight worker/adapter
PE inputs must first be compiled against the eight exact licensed PublicAPI
SDKs. Hosted CI has neither those SDKs nor checked-in worker binaries, so it
cannot run this complete diagnostic check. The Python-only source-contracts job
instead runs `python scripts/checks/Check-AdapterBoundary.py --self-test` and
`python scripts/checks/Check-AdapterBoundary.py` to reject adapter references to
Logic or JSON libraries, including source use and transitive project references.
Local acceptance still requires the full eight-release diagnostic console.

## Studio migration verification

Run `foundation`, `foundation-api` (with `TIA_MCP_TEST_PUBLIC_API_ROOT`) and
`adapter-contracts` through `scripts/checks/Test-DotnetSuites.py`. The new checks
raise their minimums by 146, 146 and 97 respectively, to 6,740, 7,806 and 329.
The Foundation minimum excludes the 16 optional SDK XSD checks. Source closure
checks cover all eight evaluated allowlists, Studio VCI defines, missing native
modules, and legacy source links. Contract tests cover shapes, golden JSON and STA
ownership/reentry without loading Siemens.

For the initial migration, the same weaver's `inventory` on each original Studio
DLL and `verify` on each original/new adapter produced the following exact multisets.
Each cell is **original adapter + original Studio = new adapter**. The full site
comparison includes opcode/category/member and normalizes only the new DTO/delegate
namespaces; the Siemens comparison needs no normalization. Every original adapter
site retains its caller, offset, opcode, member and category.

| Release | All woven sites | Siemens member references |
|---|---|---|
| 14sp1 | 1174 + 279 = 1453 | 393 + 106 = 499 |
| 15.1 | 1201 + 279 = 1480 | 411 + 106 = 517 |
| 16 | 1206 + 374 = 1580 | 416 + 162 = 578 |
| 17 | 1233 + 374 = 1607 | 437 + 162 = 599 |
| 18 | 1233 + 373 = 1606 | 437 + 161 = 598 |
| 19 | 1289 + 373 = 1662 | 473 + 161 = 634 |
| 20 | 1381 + 371 = 1752 | 522 + 157 = 679 |
| 21 | 1378 + 371 = 1749 | 519 + 157 = 676 |

`dotnet run --project build-tools/release -- build-studio` preserves the legacy assembly identities and deployment layout.
Comparing all legacy method bodies (instructions, operands, locals and exception
handlers) gives zero differences: 207 methods for each of 14sp1/15.1 and 252 for
each of 16–21. Moving source files changes build/debug identity and binary hashes;
it does not imply byte-identical DLLs. Core and GUI retain 118 and 1,407 passing
xunit tests. These are offline checks; native TIA acceptance remains pending.
