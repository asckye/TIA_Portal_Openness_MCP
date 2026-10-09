# HTTP transport regression tests

`staging-sessions-only` checks the compiled HTTP session bookkeeping and staging
admission without a listener or native calls: open, DELETE-closed, idle-expired,
restarted-host, unknown-owner and in-flight sessions. It also checks that HTTP
request metadata carries the server's session identity instead of a caller's
peer token. The fixture uses short temporary roots for net48 paths.

This Windows/.NET Framework 4.8 harness loads the **compiled server EXE** via reflection.
It checks the single response reader, client ID restoration (including null), timeout
recovery, late replies, repeated IDs, queued deadlines, stream failure, disposal,
HTTP 504 recovery, SSE, HTTP 401 and transport cancellation while requests/uploads
are pending. It also verifies that the removed launcher environment and named-pipe
shutdown control are absent from the compiled runtime.
The test HTTP listener binds only to localhost with a temporary test key.

```powershell
dotnet build tests/Engine/TiaMcp.Engine.Harness/TiaMcp.Engine.Harness.csproj -c Release
$tests = 'tests/Engine/TiaMcp.Engine.Harness/bin/Release/net48/TiaMcp.Engine.Harness.exe'
& $tests 'runtime/v21/TiaMcp.Engine.V21.exe'
$fileVersion = (Get-Content 'manifest/release-build.json' -Raw | ConvertFrom-Json).fileVersion
& $tests 'runtime/v21/TiaMcp.Engine.V21.exe' hmi-only 21 $fileVersion
& $tests 'runtime/v21/TiaMcp.Engine.V21.exe' host-build-no-tia 21
& $tests 'runtime/v21/TiaMcp.Engine.V21.exe' packaged-start-no-tia 21
& $tests 'runtime/v21/TiaMcp.Engine.V21.exe' http-start-no-tia 21
```

Use the V20 executable and `hmi-only 20 $fileVersion`, `host-build-no-tia 20`, `packaged-start-no-tia 20`, and `http-start-no-tia 20` to check a V20 build. `host-build-no-tia` builds the real STDIO/HTTP service and protocol registrations, including the isolation parent, without opening a listener. `packaged-start-no-tia` stages the real EXE without `Siemens.Engineering*.dll`, then checks STDIO startup, the full roster, readiness diagnostics/refusal and safe local discovery with isolation off and on. `http-start-no-tia` performs the packaged-layout checks over localhost when the machine has no detected TIA installation and the platform allows a local listener; it reports a skip otherwise. These checks target the V20/V21 full engines; Foundation protocol checks are run by `dotnet run --project build-tools/release -- build-release`.
No TIA process is started and no engineering project is modified by these tests.
Actual project connection and nested HMI screen reads require separate TIA validation.

Tool and session lookups go through `EngineSurface.cs`. `Tool` uses the MCP attribute
name (or CLR name when unset), while `ToolMethod` finds CLR helpers and overloads.
Session members are searched on Portal first, then the explicit registered-service
list in that helper; extend that list as domain services move out of Portal.
Returned reflection members retain their declaring types for IL checks. Instance
tool invocation resolves the target through the loaded engine's `EngineServices`.
The harness has no compile-time engine reference; its compile-only MCP SDK reference
supplies the real attributes for test fixtures, with runtime dependencies loaded
beside the selected EXE.

`engineering-api-only <PublicAPI-directory>` also runs `EngineSurfaceChecks.cs`,
including instance/static invocation, name collisions, overloads, session/service
lookup and agreement with the woven engine's complete tool catalog.

`hmi-snapshot-only` exercises the actual EXE's bounded snapshot against a net48
transparent proxy that fails mid-read. It verifies partial evidence, failure
status, exact path and absence of later proxy/enumerator calls. The release
builder runs this mode for both V20 and V21. It does not connect to TIA.

`global-script-only <PublicAPI-directory>` checks the actual EXE's directory/file
argument conversion, write gates, exact indexed Scripts lookup and registration
of `SetUnifiedGlobalScript` with preview defaults. The V21 build additionally
checks the real Siemens DLL's native Import/Export signatures. The V20 build's
bridge checks do not establish native Unified script support. No native import
or live TIA connection is performed by this harness.

`response-golden-only <PublicAPI-directory> [golden-directory]` is the P2-01a
E1/E3 byte-level baseline. It loads the woven V20/V21 EXE, reflects `BridgeJson`
and `DisciplineJson`, and uses the loaded SDK's `McpJsonUtilities.DefaultOptions`
(the default for `McpServerToolCreateOptions.SerializerOptions`). The fourth path
calls plain `ToJsonString()` on nodes, wrapping scalar/POCO samples in `JsonValue`.
No options objects are copied or re-created. `ResponseXmlBuild` is instantiated
from the engine, with null/populated properties, Chinese and escaping-sensitive
text, typed Local/UTC dates, double/long/int values and nested arrays.

The E3 cases invoke the real private `RunHmiStepTool`, `RunOfflineAnalysisTool`,
`RunPlcSimTool`, `BatchResult`, `BuildOfflineXmlBuilderReport` and
`ToolBridgeStatus.Create`. Full results are serialized both with SDK defaults
(direct) and `BridgeJson` (the inner bridge payload). Portal is uninitialized,
as in the existing shape checks; its native handles remain null. HMI actions
cover success, operation false, ArgumentException, PortalException,
TargetInvocationException, missing project and the `_hmiReadFault` block. The
last two assert the action was never entered. PLCSIM uses synthetic bodies for
each refusal status and calls the empty-instance guard before API loading;
no simulator is loaded. Offline analysis records the same action matrix,
including its different handling of `operationSuccess=false`.

The V4 `BatchResult(string tool, JsonArray rows, bool write)` aggregates results
instead of executing a delegate. The batch cases pass the real HMI executor's
five in-memory outcomes through `ToolResult` and `BatchRow`, then aggregate them
as a read-only batch. They verify the envelope verdict, execution/completeness,
MCP `IsError`, text/structured-content agreement, retained target fields and
`rollbackPerformed=false`. An additional missing-verdict target preserves the
old check that returning text alone must not imply success. No synthetic target
is registered or invoked through a live session.

The P6-07d assertion mapping is:

| Previous batch evidence | V4 assertion |
| --- | --- |
| `Meta.success` | `ok`, `meta.outcome`, `meta.execution`, `meta.completeness`, `IsError` and error presence |
| `Message`, `Meta.rows`, `Meta.operationSuccess` | Retained target `data.items[0].result.message` and `.meta` fields |
| Argument, portal and invocation exception messages | Retained target `.meta.error` (the existing first-line mask), plus failed parent envelope |
| No explicit success verdict | `missing-verdict` remains unsuccessful; the successful HMI target now correctly produces a successful parent |
| `Meta.timestamp` | Parent `meta.timestamp`, plus the separately masked target timestamp |

The old batch helper's exception-catching implementation no longer exists.
Its three failure cases now check propagation of the actual executor's error
evidence through the aggregator; the HMI/offline exception golden cases remain
unchanged. This does not claim to execute the removed delegate-catching path.

E3's closed mask list is `Meta.timestamp` and `Meta.lastFailure.timestamp`
(wall clocks, replaced as **DateTime with the original Kind**),
`Meta.elapsedMs`/`Meta.lastFailure.elapsedMs` (Int64 stopwatch values), and
`Meta.operationId`/`Meta.lastFailure.operationId` (GUID strings). Only the
first line of `Meta.error`/`Meta.lastFailure.error` is compared, per D1. The
blocked HMI fixture supplies the retained fault timestamp/GUID. Arbitrary data,
messages and other paths remain unchanged. Mask checks include Local/UTC/
Unspecified kinds and data fields that must not be masked.
V4 batch envelopes additionally mask only the parent `meta.timestamp` (validated
as UTC RFC3339 text ending in `Z`) and generated `meta.requestId` (validated as a
GUID in `N` format). Target results are masked before aggregation using the
existing closed list; the V4 mask never traverses `data`.

For portable Local DateTime golden bytes, the test temporarily sets only its
own net48 `TimeZoneInfo` cache to a fixed +08:00 zone and uses invariant culture;
both are restored in `finally`. Windows settings and engine serializer options
are untouched. `Golden/response-bytes-v20.txt` and `response-bytes-v21.txt` store
one case ID, TAB and **exact output text** per LF-terminated line, UTF-8 without
BOM. They are generated by the record mode, never manually edited;
`.gitattributes` prevents checkout newline conversion. Tests compare
every case and the complete file byte-for-byte, including BOM/newlines.

Baseline recording is explicit, separate from verification:

```powershell
& $tests $engine 'response-golden-record' $publicApi 'tests/Engine/TiaMcp.Engine.Harness/Golden'
& $tests $engine 'response-golden-only' $publicApi 'tests/Engine/TiaMcp.Engine.Harness/Golden'
```

Run verification twice for each release. Recording writes new data and must not
be used to accept a changed response during P2-01d. P6-07d re-records only the
migrated batch cases in both `Golden/response-bytes-v20.txt` and
`Golden/response-bytes-v21.txt`: ten existing batch records change to V4
envelopes and two missing-verdict records are added. All 104 other records in
each file remain byte-identical; no case is removed. Command logs, engine/base
hashes and mode counts are kept in `bin-build/refactor/evidence/P3-02` locally.
The historical `baseline` mode is a negative control for the old HTTP race and
deliberately fails with “Baseline unexpectedly passed” on a fixed engine.
`protocol-host`, `isolated-worker-host`, `worker-fixture`, `lease-holder` and
`stdin-hex-fixture` are child-process fixtures used by the corresponding tests,
not standalone finite test suites.

P6-07d covers the registered V4 PLC routes for preview/confirmation,
singleton ownership, and typed input validation. Native service-member checks keep
their CLR names. F19 expectations moved to Foundation and adapter tests in P8-02;
the engine harness retains the Siemens SDK address signature checks. The
coverage includes `GetPlcBlockInfo`, `ListPlcBlocks`, `GetPlcBlockHierarchy`,
`ExportPlcBlock`, `ImportPlcBlock`, `ImportPlcBlocksFromDirectory`,
`CompilePlcDiagnostics`, `RepairAndReimportPlcBlock`, `ExportPlcBlocks`,
`DescribePlcBlockLogic`, `SetPlcProgram`, `GetPlcBlockFingerprints`,
`GetPlcBlockEditCapabilities`, `MovePlcBlockToGroup`, and `GetDeviceAttributes`.


P8-02 moves the five F19 hardware-addressing tools to Foundation. Both
`engineering-api-only` and `hardware-contracts-only` enforce G4: the native
`AddressesService` and Portal address methods are absent, and the exported engine
worker catalog contains none of the five tools or their descriptors. Shared,
Siemens-free CLR declarations remain in the engine for nested bridge compatibility;
the exported worker catalog defines registration ownership. The remaining P6-12
engine hardware group has 21 tools.

`HardwareAddressingAdmissionTests` in the Foundation suite covers the five typed
signatures, schema defaults, invalid scalar-map JSON, omitted maps, path refusals
(including `LIMIT_EXCEEDED`), and invalid address properties before native lookup,
for all eight releases. `HardwareAddressingParityTests` covers succeeded, partial,
unknown and failed envelopes, partial-read paging and sanitized errors. Adapter
`HardwareAddressingTests` retain typed worker DTO, scalar and native-value checks.
Run these through the registered suite gates:

```powershell
dotnet run --project build-tools/release -- test-suites -Suite foundation -Suite foundation-api -Suite adapter-contracts
```

The release harness modes `engineering-api-only`, `hardware-contracts-only`,
`descriptor-catalog-only`, `concurrency-only`, `response-golden-only`,
`offline-contracts-only`, `software-lookup-only`, `worker-supervisor-only`,
`process-leases-only`, `child-stdin-only`, `router-only`, `native-export-only`,
`hmi-snapshot-only`, `global-script-only`, `graphic-selection-only`,
`runtime-settings-only`, `example-library-only`, `native-diagnostics-only`,
`test-ecosystem-assembly` and `hmi-only` can be verified without a network listener
or TIA connection. `concurrency-only` uses in-process regression fixtures.
The default HTTP mode opens a localhost listener; omit it when repository
instructions forbid connecting to network services.
