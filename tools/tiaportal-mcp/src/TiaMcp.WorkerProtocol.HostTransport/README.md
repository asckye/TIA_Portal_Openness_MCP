# Owned process / pipe preview

This isolated .NET 8 project implements `IAsyncV2Exchange` over redirected OS stdin/stdout pipes. It reuses `AsyncJsonSession`, `StrictCodec`, `RequestGuard`, and `BoundedAsyncFrames`; it is not registered with the production host or existing worker launcher.

The dedicated `tests/TiaMcp.TransportFixture` executable is a test-only JSON peer. It is **not an SDK fake or native worker** and has no native SDK reference, assembly lookup, or native attachment. Only that fixture is executed in these tests. Linux results do not claim Windows pipe semantics or native integration have been validated.

## Ownership and wire contract

- The caller supplies a trusted executable and argument list. There is no shell, process discovery, attach-by-PID, retry, restart, or process-tree kill.
- Each start generates a fresh 32-byte ownership nonce in `TIA_TRANSPORT_OWNER_TOKEN`. The child first writes those 32 bytes and its little-endian 32-bit PID. The owner checks both against its freshly started Process and confirms its captured start time. This is process-instance correlation, not authentication of untrusted executable code. The nonce is removed from retained start info after startup and is not logged.
- The original Process object is retained from `Start` through teardown. No process handle is reconstructed by PID. Abort checks that object’s exit state and captured start time before killing only that process. A constructor failure still tears down only the exact Process just launched.
- After identity, the child emits exactly one length-prefixed v2 hello followed by a zero-length exchange terminator. A frame has a four-byte little-endian signed length and UTF-8 JSON payload. A positive frame length must satisfy reviewed limits before payload allocation. Zero is an explicit exchange boundary; negative sizes are invalid; physical EOF is premature even after a reply.
- Host requests are single length-prefixed frames. Each child response is bounded progress/reply frames followed by zero. The adapter does not invent a reply boundary on seeing a reply, so trailing frames remain rejectable by AsyncJsonSession.
- One exchange can be reserved at once. Reservation transfers to AsyncJsonSession on dispatch. For pre-dispatch validation/cancellation failures the caller must dispose its exchange to release that reservation. Disposing an unused reservation does not kill its process. Once dispatch may have occurred, any abort or incomplete exchange disposal terminates its owner; session state forbids replay.
- Startup has a cancellation/deadline budget; synchronous OS process creation itself cannot be forcibly interrupted. Request timeouts are the AsyncPreview shared monotonic budget. Read and chunked write operations link the request token to transport lifetime. Abort closes pipes and kills its own process to break backpressure. Cleanup waits are capped (up to two seconds each for process exit and stderr drain); callers should dispose the transport after use. Disposal reports `OwnedProcessExitUnconfirmed` if exit cannot be established; `ShutdownConfirmed` is true only after observing exit. A failed cleanup is retained as a failure, never silently reported as successful.
- Stderr is continuously drained with a fixed 1 KiB buffer and never retained. Stdout must contain only the wire protocol. Diagnostics and exceptions expose no stderr or nonce.

## Verification

From `tools/tiaportal-mcp` using the installed .NET 8 SDK (no network or installation needed for the net8-only graph):

```sh
DOTNET_CLI_HOME="$PWD/.dotnet-home" DOTNET_GENERATE_ASPNET_CERTIFICATE=false \
  dotnet build tests/TiaMcp.WorkerProtocol.HostTransport.Tests \
  -c Release -p:TargetFrameworks=net8.0 -m:1
dotnet \
  tests/TiaMcp.WorkerProtocol.HostTransport.Tests/bin/Release/net8.0/TiaMcp.WorkerProtocol.HostTransport.Tests.dll \
  "$PWD/tests/TiaMcp.TransportFixture/bin/Release/net8.0/TiaMcp.TransportFixture.dll"
```

Actual-process tests cover fresh owner identity, repeated exchange boundaries, large stderr, mismatched token/PID/release/hash, invalid/oversized/truncated framing, frame count bounds, startup/request deadlines, read and backpressured write cancellation, missing boundary, premature exit, broken-pipe dispatch, unknown outcomes, trailing frames, no replay, pre-dispatch cancellation, concurrent owner disposal, idempotent cleanup, and an independent second owned peer remaining alive and usable.

Remaining gates: Windows process/pipe behavior; production lifecycle/wiring and selected executable/hash verification; native SDK smoke tests; process-tree ownership policy if future native workers spawn descendants. None are implied by passing this pure fixture suite.
