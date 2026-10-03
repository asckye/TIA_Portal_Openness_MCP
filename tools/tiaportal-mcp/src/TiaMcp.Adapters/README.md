# Explicit release adapters and PLC foundation workers

These eight peer projects compile the existing real PLC foundation subset into
separate typed libraries. V20 and V21 have exactly the same project structure as
the earlier releases. The foundation host publishes the matching subset for V14 SP1–V19,
while V20/V21 retain their full engines. Each worker references exactly one selected adapter.
Use `scripts/build/Build-MultiVersion.ps1` from the repository root to build, test and deploy
the eight-version distribution. Native TIA acceptance remains pending.

The native source allowlist is shared by source, not by an API-bound binary.
It is intentionally transitional: `PlcReadContracts.cs` still contains typed
implementation and the existing PLC feature constants remain internal to this
subset. No full eight-copy source fork is introduced. Contract extraction and
per-feature native implementation migration remain separate work.

## Build

From the repository root, with the matching real PublicAPI directory and the
real .NET Framework targeting pack installed:

```powershell
dotnet build tools/native-call-weaver/NativeCallWeaver.csproj -c Release
dotnet build tools/tiaportal-mcp/src/TiaMcp.Adapters/V20/Adapter.20.csproj -c Release -p:SiemensEngineeringDirectory="C:/authorized-sdk/TIA_V20_PublicAPI/V20"
```

`scripts/build/Build-PlcAdapterWorkers.ps1 -PublicApiRoot <eight-version-root>`
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

In a read-only source review, `-p:AdapterSourceRoot=<repo>/tools/tiaportal-mcp/src`
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
serializer still uses Newtonsoft.Json 13.0.4 after the net48 migration;
the original modern runtime and its System.Text.Json dependency are unchanged.
The exact existing PortalFailureClassifier source is linked without Siemens
references. This is a serializer variant to maintain in parallel until a later
common diagnostic abstraction removes the duplication, not eight runtime copies.

## Integration and remaining work

Each worker selects one exact adapter and preserves its resolver and STA session model.
The host and configurator now select the eight precise release keys. V14 SP1–V19 use
the foundation catalog; V20/V21 retain their full engines. `Build-MultiVersion.ps1`
assembles the runtime directories, tests real STDIO/HTTP against synthetic child workers,
and records hashes. The former blanket Publish/Pack refusal has been removed.

Do not load both the old PLC foundation assembly and a typed adapter in one process;
their implementation types intentionally overlap during this migration. Native call
instrumentation remains required. Complete native diagnostic correlation and actual
Siemens import/export/compile roundtrips still require separate validation.

## Input regression checks

`build/Test-AdapterInputs.ps1 -SourceRoot <repo>/tools/tiaportal-mcp/src
-PublicApiRoot <eight-version-sdk-root> -EvidenceDirectory <writable-output>`
checks eight real API selections and twelve invalid configurations. It invokes
only the metadata validation target, without restore, compilation or native
execution. Compile each adapter separately to verify its actual source bindings.
For isolated builds, set DOTNET_CLI_HOME and NuGet caches to writable task-local
directories; set DOTNET_GENERATE_ASPNET_CERTIFICATE=false and
DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1. Use `-p:UseSharedCompilation=false` if a shared
compiler process has a different sandbox identity.

After eight builds, `build/Test-WorkerIsolation.ps1 -EvidenceDirectory <output>
-NativeCallWeaverPath <weaver.dll>` checks project selection, copied PE coverage,
five deliberate coverage corruptions per release, invalid worker configurations,
missing instrumentation tooling.

`tools/tiaportal-mcp/tests/TiaMcp.Adapters.DiagnosticsTests/Diagnostics.Tests.csproj`
builds only the API-independent runtime against net8.0, keeping the assembly name
`Diagnostics.Tests` for `InternalsVisibleTo`. From the repository root, run:

```powershell
dotnet run --project tools/tiaportal-mcp/tests/TiaMcp.Adapters.DiagnosticsTests/Diagnostics.Tests.csproj -c Release -- <worker-bin-root> <new-writable-journal-directory>
```

The worker bin root is `tools/tiaportal-mcp/src/TiaMcpServer.PlcWorker/bin` after
building all eight workers; it contains `<release-key>/Release/<framework>/`
subdirectories. The journal directory must not already exist. These checks
exercise diagnostic behavior and inspect eight worker/adapter PE files without
loading them. The project contains no Siemens references, replacement SDK, or
native tests.
