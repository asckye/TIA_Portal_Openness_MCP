# Integrated TIA Openness Studio

The desktop UI, English/Chinese localization, themes, inspection rules and offline mock from
`asckye/tia-openness-studio` now live here. The active desktop uses this repository's existing
MCP service for native work. It does not download a compiler, load Siemens DLLs, launch TIA,
or maintain a second native engine.

Upstream: commit `87099c576fbc06e6b6ac523ddbf763fe0aa2ce02` (v2.4.0), MIT, copyright 2026 asckye.
See [LICENSE](LICENSE) and [file provenance](upstream.json). The complete original tree and
Git history are preserved under `upstream/`; these archives are reference data, not build inputs.

## Run

Requires Windows and .NET 10 SDK/runtime for this source build:

```powershell
dotnet build tools/tia-openness-studio/src/TiaOpenness.Gui/TiaOpenness.Gui.csproj -c Release
& tools/tia-openness-studio/src/TiaOpenness.Gui/bin/Release/net10.0-windows/TiaOpenness.exe --mock --lang zh
```

For the native route, first run and bind the repository's V20 or V21 HTTP MCP engine to the
intended project through your existing MCP client. Then use its actual loopback endpoint:

```powershell
& tools/tia-openness-studio/src/TiaOpenness.Gui/bin/Release/net10.0-windows/TiaOpenness.exe --mcp-url http://127.0.0.1:8080/mcp
```

Press **Connect** to adopt that service's existing project. No TIA process is started or attached
by Studio. An optional `--bound-project C:\Projects\Line.ap21` checks a specific project;
repeat `--software PLC_1` to choose a subset of exact software paths. With no `--software`,
Studio discovers PLC names using the integrated engine's `GetDevices(includePlcSoftware:true)`.
Set `TIA_STUDIO_API_KEY` in the launching environment if the HTTP engine requires a bearer token.
Keys are not stored in UI settings or logged. Remote HTTP endpoints are outside this build's scope.

## Integrated workflows

| Workflow | Route and behavior |
|---|---|
| Doctor | Existing `Doctor(fix:false)`; diagnosis does not change user-group membership. |
| Project / PLC browser | Cached exact engine binding, PLC inventory and structured user-block hierarchy. |
| XML export | Selected/all user blocks through `ExportBlock`; each run has its own output folder. |
| Text export | `ExportAsDocuments`; actual SIMATIC SD files, not a claim of lossless source conversion. |
| XML import | Explicit overwrite through `ImportBlock`, followed by the engine's verified readback. |
| Compile | `CompileAndDiagnosePlc`; structured state, counts and diagnostics. |
| Inspection | Upstream naming, author, consistency and protection rules on returned metadata. |
| V21 workspace | List/create, compare, map preview/apply, synchronize preview/apply, local Git diff. |
| MCP tools panel | Existing discovery, preflight and exact tool dispatch, including tools outside the desktop forms. |
| Offline mock | Original synthetic PLC/HMI fixtures, file exports, import/compile/inspection and VCI simulation. |

The integrated browser currently lists **root PLC user blocks**. Software/safety units, system
blocks, UDTs and HMI artifacts remain accessible through the MCP tools panel, not this grid.
The desktop's VCI Map action targets the whole project. Workspace-to-project synchronization
retains the engine's existing edition restrictions. Studio does not bypass them.

Native XML import cannot promise atomic no-overwrite behavior; choosing No in the upstream
overwrite dialog is rejected before import. Source/document imports are available through the
MCP tools panel. Opening another project, closing the project or disconnecting TIA remains with
the MCP session owner. Headless selection is relevant only to the mock UI in this integrated build.

The original dead-code rule runs only when actual reference data is supplied. The desktop does
not classify all blocks as unused when that data is absent. The legacy `RequireBlockComment`
option checks **HeaderAuthor**, as documented by the upstream rule, not the block's comment.

## Validation

```powershell
dotnet test tools/tia-openness-studio/tests/TiaOpenness.Core.Tests/TiaOpenness.Core.Tests.csproj -c Release
dotnet test tools/tia-openness-studio/tests/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj -c Release
```

Core tests include real typed-client workflows against an in-memory HTTP handler and the mock
backend. GUI tests render and inspect WPF controls without launching TIA. These are functional
offline checks; a native scratch-project run remains necessary before release acceptance.

Upstream's standalone `mcp` mode, dynamic compiler/native adapter and release scripts are
preserved in the archive but not part of the active build. Use this repository's MCP engine and
release process. This integration does not enable V14 SP1–V19 production routes or alter release gates.
