# HTTP transport regression tests

This Windows/.NET Framework 4.8 harness loads the **compiled server EXE** via reflection.
It checks the single response reader, client ID restoration (including null), timeout
recovery, late replies, repeated IDs, queued deadlines, stream failure, disposal,
HTTP 504 recovery, SSE, HTTP 401 and transport cancellation while requests/uploads
are pending. It also verifies that the removed launcher environment and named-pipe
shutdown control are absent from the compiled runtime.
The test HTTP listener binds only to localhost with a temporary test key.

```powershell
dotnet build tools/tiaportal-mcp/tests/TiaMcpServer.HttpTests/TiaMcpServer.HttpTests.csproj -c Release
$tests = 'tools/tiaportal-mcp/tests/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe'
& $tests 'runtime/v21/TiaMcpServer.exe'
$fileVersion = (Get-Content 'manifest/release-build.json' -Raw | ConvertFrom-Json).fileVersion
& $tests 'runtime/v21/TiaMcpServer.exe' hmi-only 21 $fileVersion
```

Use the V20 executable and `hmi-only 20 $fileVersion` to check a V20 build. These reflection checks target the V20/V21 full engines; foundation protocol checks are run by `Build-MultiVersion.ps1`.
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
of `UpdateUnifiedGlobalScript` with preview defaults. The V21 build additionally
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
no simulator is loaded. Batch and offline analysis also record the same action
matrix, including their different handling of `operationSuccess=false`.

E3's closed mask list is `Meta.timestamp` and `Meta.lastFailure.timestamp`
(wall clocks, replaced as **DateTime with the original Kind**),
`Meta.elapsedMs`/`Meta.lastFailure.elapsedMs` (Int64 stopwatch values), and
`Meta.operationId`/`Meta.lastFailure.operationId` (GUID strings). Only the
first line of `Meta.error`/`Meta.lastFailure.error` is compared, per D1. The
blocked HMI fixture supplies the retained fault timestamp/GUID. Arbitrary data,
messages and other paths remain unchanged. Mask checks include Local/UTC/
Unspecified kinds and data fields that must not be masked.

For portable Local DateTime golden bytes, the test temporarily sets only its
own net48 `TimeZoneInfo` cache to a fixed +08:00 zone and uses invariant culture;
both are restored in `finally`. Windows settings and engine serializer options
are untouched. `Golden/response-bytes-v20.txt` and `response-bytes-v21.txt` store
one case ID, TAB and **exact output text** per LF-terminated line, UTF-8 without
BOM. They are generated from this task's unchanged engine base, never manually
edited; `.gitattributes` prevents checkout newline conversion. Tests compare
every case and the complete file byte-for-byte, including BOM/newlines.

Baseline recording is explicit, separate from verification:

```powershell
& $tests $engine 'response-golden-record' $publicApi 'tools/tiaportal-mcp/tests/TiaMcpServer.HttpTests/Golden'
& $tests $engine 'response-golden-only' $publicApi 'tools/tiaportal-mcp/tests/TiaMcpServer.HttpTests/Golden'
```

Run verification twice for each release. Recording writes new data and must not
be used to accept a changed response during P2-01d. Command logs, engine/base
hashes and mode counts are kept in `bin-build/refactor/evidence/P3-02` locally.
The historical `baseline` mode is a negative control for the old HTTP race and
deliberately fails with “Baseline unexpectedly passed” on a fixed engine.
`protocol-host`, `isolated-worker-host`, `worker-fixture`, `lease-holder` and
`stdin-hex-fixture` are child-process fixtures used by the corresponding tests,
not standalone finite test suites.
