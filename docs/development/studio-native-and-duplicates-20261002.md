# Native Studio and duplicate-code review — 2026-10-02

The maintainer explicitly selected a Studio implementation that does not use MCP and calls
Openness directly. The review started at `9a45819800b30f70c40ad0e839f247f311a745af`.

## Implemented

- Removed Studio's HTTP MCP connection, discovery/preflight/tool bridge and generic tools window.
- Restored the pinned upstream native session behind a local .NET Framework 4.8 x64 bridge.
  Its sequential STA dispatcher uses ordinary typed JSON-RPC methods over child-process pipes;
  it implements no MCP initialization, tools/list or tools/call interface.
- Contracts and Core are actual shared projects. Client, mock and native bridge use the same DTOs,
  inspection and dispatch code. V20/V21 native targets link one native source set; they do not copy it.
- Studio consumes the existing release catalogue by source link. Only V20/V21 are runnable.
  Neither a dynamic compiler nor a second MCP server is restored from the archive.
- Native import no longer deletes a pre-existing same-name source. Mapping no longer retries a
  failed export at another path. Batches stop at the first failed write; exports retain existing files.
- Git diff now includes staged-only changes; unused-block inspection skips incomplete reference evidence.
- Removed duplicate serializer settings and unused compiler/retry helpers. VCI capability messages
  now follow the project service; source export/import labels match the direct SCL/DB/UDT route.

## Whole-repository review

The baseline inventory contains 1,878 tracked files and 860 owned source/build files. Third-party
vendor trees, official reference material, build outputs and history archives were excluded from
copy detection. Normalized full-file hashes and matching 20-line source windows identified
candidates; entrypoints, project references and behavior were then reviewed. This detects exact
copies and selected repeated logic, not a proof that no semantic duplication exists.

| Finding | Evidence / ownership | Disposition |
|---|---|---|
| Studio MCP protocol layer | Former McpConnection, BridgeClient tool mapping, McpToolsWindow | Removed; desktop now owns its native session |
| Standalone upstream MCP server/compiler | Only in pinned `upstream/` archives | Not active build inputs; retain history and MIT provenance |
| Contracts and operation dispatch | Studio Client, Bridge and mock | Shared Contracts/Core projects, one dispatcher implementation |
| Eight identical Adapter project shells | `TiaMcp.Adapters/V*/Adapter.*.csproj` import different local Release.props | Keep: exact release identity and separate output are required; implementations already link shared source |
| Two identical preview project files | WorkerProtocol.AsyncPreview / HostPreview | Keep: default compile items select different source code; these are separate candidate layers |
| Repeated native-call diagnostics | `TiaMcp.Adapters/Diagnostics/NativeCallDiagnostics.cs` and modern `ModelContextProtocol/NativeCallDiagnostics.cs`: 24 matching windows | Genuine maintenance duplication; net461/Newtonsoft and modern System.Text.Json currently differ. Extract common event/state logic before replacing serializers; not deleted in this change |
| Transitional PLC foundation / adapters / full engines | Foundation source allowlist plus version-specific worker projects | Intentional migration overlap. Do not load Foundation and an Adapter together; legacy production routes remain disabled |
| Studio native business operations and MCP Portal operations | OpennessSession / OpennessVersionControl versus Siemens/Portal partials | Semantic overlap remains after the explicit independent-native requirement. A shared transport-independent native API layer is the next substantial consolidation; this change does not claim it is complete |
| Installation / Doctor logic | Studio Environment versus EngineRouter / MCP diagnostics / Configurator | Release policy is shared now; installation probes and report DTOs still overlap. A common discovery library would reduce maintenance drift |
| Git features | Studio GitWorkspaceDiff, MCP ManagePlcGitRepository, TiaGitAddIn.Core and vci-watch | Different roles: visual diff, repository operations, XML analysis and automation. A common process runner is possible; whole-feature deletion would remove behavior |
| Inspection / quality checks | Studio InspectionEngine versus AuditEngineeringExports and export validators | Some naming/author policy overlap, different inputs and coverage. Keep native metadata and offline file analysis distinct until a shared rule contract exists |
| Repeated HMI validation setup | ClassicHmiOfflineValidationSuite / ClassicHmiTemporaryImportPreflightSuite: one matched window | Test setup overlap only; cases test different import behavior |

Matching windows overlap, so their counts must not be added as a line-count estimate. The repeated
project shells do not mean eight copied native implementations. The native Studio/MCP business
overlap is real and remains explicitly recorded rather than presented as fully deduplicated.

## Verification and remaining acceptance

Use [Build-Studio.ps1](../../scripts/build/Build-Studio.ps1) to build and test the desktop and both
official SDK targets. Evidence is written to `bin-build/studio-native`. The normal bridge is tested
with mock project data; direct native adapters are compiled against the official PublicAPI.
This work does not start TIA, attach to a live process, open engineering projects, change group
membership, or operate a PLC. Native project acceptance remains pending. Existing historical
validation records are not promoted to acceptance of this new direct native route.

Local checks on 2026-10-02: 36 client/bridge functional tests and 505 WPF tests passed with no
skips. Both native adapter targets compiled with zero warnings and errors. Build-Release passed:
3,035 offline cases per V20/V21 identity, 10 SDK version-policy cases, 137 configurator cases,
2,840/3,126 official API-shape checks and 31 ecosystem assembly checks per runtime. The strict
binary and source-only bundle checks, 280-document link check and dead-tool-reference check passed.
Build outputs contain no redistributed Siemens DLLs. These counts do not establish live native
project behavior; the bridge's normal workflow cases use explicit mock data.
