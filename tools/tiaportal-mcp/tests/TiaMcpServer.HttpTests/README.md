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
& $tests 'runtime/v21/TiaMcpServer.exe' hmi-only 21 2.7.15.0
```

Use the V20 executable and `hmi-only 20 2.7.15.0` to check a V20 build (pass the engine file version recorded in `manifest/release-build.json`).
No TIA process is started and no engineering project is modified by these tests.
Actual project connection and nested HMI screen reads require separate TIA validation.

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
