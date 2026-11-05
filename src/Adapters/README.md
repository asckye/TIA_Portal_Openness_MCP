# Release adapters and PLC workers

Status: Current · Last checked: 2026-11-04, master `871d54da`

All eight releases (`14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20`, `21`) use FoundationHost and a PLC worker per MCP session. Each worker loads its matching adapter. The public profile is `plc-foundation`, with a lite catalog by default and a full roster available on every release. Native calls run on STA for V14 SP1–V19 and MTA for V20/V21; changing that thread model remains planned work.

Native implementations live in [Native](Native), API-independent DTOs and interfaces in [Adapters.Contracts](../Adapters.Contracts), and source selection in [Adapter.Sources.props](build/Adapter.Sources.props). Each version's `Release.props` and project select the exact SDK; [Adapter.Common.props](build/Adapter.Common.props) supplies shared build settings. The version feature symbols come from [TiaFeatures.props](../Shared/TiaFeatures.props).

## Build and validation

From a clean checkout, `prepare -BuildOnly` builds and packages all eight releases. Match `-Version` to `Version.props` and supply the local licensed SDK root and pinned decoder Python required by the [release manual](../../docs/manuals/release.md):

```powershell
dotnet run --project build-tools/release -- prepare -Version <version> -BuildOnly -PublicApiRoot <eight-version-sdk-root> -Python <absolute-python.exe>
```

An individual adapter can be built against its exact SDK, for example:

```powershell
dotnet build build-tools/native-call-weaver/NativeCallWeaver.csproj -c Debug
dotnet build src/Adapters/V20/Adapter.20.csproj -c Debug -p:SiemensEngineeringDirectory="C:/authorized-sdk/TIA_V20_PublicAPI/V20"
```

Adapters and PLC workers target .NET Framework 4.8. Siemens references are non-copy-local. Adapter compilation invokes the native-call weaver and verifies coverage; `prepare` also verifies the copied adapter in each worker output. Missing instrumentation or coverage failures stop the build.

The host merge gate is `merge-check`, with a manual `merge-check -All` before each release candidate. Full `prepare` builds, packages and checks the extracted ZIP, seals the candidate and uploads a private draft. It does not run registered suites; `publish-prepared` publishes after maintainer confirmation. There is no nightly. Follow [validation](../../docs/manuals/validation.md) for suite names and current count gates; Codex runs only affected Debug projects and test classes in its sandbox.

## Input regression checks

The retained SDK input and worker isolation checks can also be invoked directly:

```powershell
dotnet run src/Adapters/build/Test-AdapterInputs.cs -- --source-root src --public-api-root <eight-version-sdk-root> --evidence-directory <output>
dotnet run src/Adapters/build/Test-WorkerIsolation.cs -- --evidence-directory <output> --native-call-weaver <weaver.dll>
dotnet run --project tests/Adapters/TiaMcp.Adapters.Diagnostics.Tests/TiaMcp.Adapters.Diagnostics.Tests.csproj -c Debug -- <worker-bin-root> <new-journal-directory>
```

The diagnostic console needs the eight compiled worker/adapter inputs under `src/PlcWorker/bin`, even though its own runtime has no Siemens references. These are offline checks; they do not prove actual TIA import, export, compilation or cleanup behavior.

## Retained Studio path

The Workbench project UI uses the shared MCP session through its operator channel. The legacy Studio bridge and its version-specific assemblies remain. `StudioAdapter` and the Core adapter-backed path are selected by `TiaSharedAdapterPaths`; [PrepareBuild.cs](../../build-tools/release/PrepareBuild.cs) explicitly sets it to `false`. This switch is a retained Studio migration mechanism, not an alternate shipped engine profile.

See the [architecture](../../docs/standards/design.md) and [real-machine ledger](../../docs/reference/real-machine-ledger.md) for current ownership and pending native acceptance. Historical migration counts and comparisons do not establish acceptance of the current build.
