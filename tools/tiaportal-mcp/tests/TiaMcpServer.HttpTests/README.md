# HTTP transport regression tests

This Windows/.NET Framework 4.8 harness loads the **compiled server EXE** via reflection.
It checks the single response reader, client ID restoration (including null), timeout
recovery, late replies, repeated IDs, queued deadlines, stream failure, disposal,
HTTP 504 recovery, SSE, HTTP 401 and transport cancellation while requests/uploads
are pending. It also verifies that the removed launcher environment and named-pipe
shutdown control are absent from the compiled runtime.
The test HTTP listener binds only to localhost with a temporary test key.

```powershell
dotnet build tools/tiaportal-mcp/tests/TiaMcpServer.HttpTests/HttpTests.csproj -c Release
$tests = 'tools/tiaportal-mcp/tests/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe'
& $tests 'runtime/v21/TiaMcpServer.exe'
& $tests 'runtime/v21/TiaMcpServer.exe' hmi-only 21 2.7.2.4
```

Use the V20 executable and `hmi-only 20 2.7.2.4` to check a V20 build.
No TIA process is started and no engineering project is modified by these tests.
Actual project connection and nested HMI screen reads require separate TIA validation.
