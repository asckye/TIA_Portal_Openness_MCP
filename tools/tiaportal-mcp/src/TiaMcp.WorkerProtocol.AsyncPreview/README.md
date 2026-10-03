# Async v2 request boundary (unwired preview)

This additive .NET 8 library is the adapter-facing successor to the scripted
`HostPreview` experiment. It leaves `JsonSession`, `IV2Exchange`, the production
Host and Worker wires, launchers, and native dispatch unchanged. No production
IPC, process isolation, Siemens execution, or legacy compatibility is established
by its tests. It does not auto-detect v1, downgrade a failed v2 exchange, or replay.

## Contract and ownership

`AsyncJsonSession.CallAsync` takes an `IAsyncV2Exchange`, a finite total timeout,
a cancellation token, and a synchronous side-effect-free result validator.
The existing `RequestGuard` owns request IDs, dispatch state, binding transitions,
and terminal fault state; the existing `StrictCodec` owns JSON encoding/decoding.
The preflight checks mirror the reviewed synchronous session, and authoritative
completion still happens in `RequestGuard.Complete` after validation.

- The caller creates one exchange for one request. Before dispatch the caller
  retains it. Canceled/expired/invalidly encoded unsent calls clear any pending
  guard state without poisoning the session. A new request is not a replay of
  that earlier identity.
- Invoking `DispatchAsync` is the first possible write. It is marked **before**
  the call, so partial-write exceptions, cancellation and ambiguous failures are
  terminal even when the adapter cannot report whether bytes left the process.
- After dispatch, every failed await, invalid/trailing/missing frame, result
  validation error, cleanup failure, deadline or cancellation poisons the session
  with unknown outcome. No automatic replay or v1 fallback is available.
- Frames must belong only to this exchange. Enumeration must finish at an
  explicit per-request end boundary; a persistent worker pipe's process EOF is
  not an acceptable end condition. Progress is identity-checked and monotonic.
- One total monotonic budget covers dispatch, every `MoveNextAsync`, the final
  end-boundary wait, both disposals, validation and the check before completion.
  A `TimeProvider` timer cancels pending waits; elapsed-time checks reject clock
  regressions and deadline expiry at synchronous checkpoints. Progress does not
  reset the budget. Only one call or hello acceptance may execute per session.
- Enumerator and exchange disposal finish before result validation/commit. On
  failure, `Abort` runs first and cleanup is invoked at most once per resource.
  Waiting for cleanup never extends the request budget. Late task faults are
  observed, and a late result cannot advance the guard or run the validator.
- Public exchange errors expose fixed codes, never adapter exception messages,
  frame bodies, or payloads.

`Abort` must promptly terminate/unblock pending IO, be idempotent/nonthrowing,
and allow safe disposal while abandoned async operations unwind. All adapter
methods' synchronous portions and the validator must return promptly. Bounded
awaits cannot forcibly interrupt synchronous code or guarantee resource release
by a broken adapter that ignores `Abort`. Tests deliberately demonstrate this
limit; a timeout is not proof that a native operation stopped.

## Live framing bounds

`FrameLimits` caps a request at 32 frames, 1 MiB per frame, and 4 MiB aggregate
(including four-byte frame headers), with optional stricter per-call limits.
The session checks each frame as it arrives, even when an adapter misbehaves.
The adapter must additionally enforce bounds **before allocating payloads**.

`BoundedAsyncFrames.ReadAsync` is a reusable building block over an asynchronous
byte source already scoped to a request. It handles fragmented reads and checks
signed little-endian lengths/count/aggregate bounds before payload allocation.
Buffers are owned per emitted frame. An EOF probe may consume one byte beyond
an accepted frame-count/aggregate boundary to detect prohibited extra content;
no additional payload is allocated or emitted. Aggregate limits describe
accepted framed bytes, with at most this one-byte boundary probe.

The length-prefix framing is still a preview, not legacy newline framing or
modern JSON-RPC. A real adapter must provide an unambiguous exchange boundary,
keep logs off the protocol channel, and enforce the same ownership/abort rules.

## Verification

The standalone executable tests use fake exchanges, fake asynchronous byte
sources, a deterministic clock, and one real system-timer timeout. They cover
successful and negative outcomes, identities, live count/byte limits, malformed
and truncated frames, before/after-dispatch cancellation, dispatch/read/end/
disposal stalls, enumerator faults, negative/regressing clocks, expiry after a
reply or validation, cumulative budgets, cleanup failure, late task failure,
no replay, and the existing synchronous seam remaining unchanged.

From the repository root (adjust SDK path as needed):

```sh
export DOTNET_CLI_HOME="$PWD/.dotnet-home"
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
dotnet build \
  tools/tiaportal-mcp/tests/TiaMcp.WorkerProtocol.AsyncPreview.Tests \
  -c Release -p:TargetFrameworks=net8.0 -p:NuGetAudit=false \
  -m:1 --disable-build-servers
dotnet \
  tools/tiaportal-mcp/tests/TiaMcp.WorkerProtocol.AsyncPreview.Tests/bin/Release/net8.0/TiaMcp.WorkerProtocol.AsyncPreview.Tests.dll
```

## Next production gate (not another preview layer)

Implement a concrete owned process/pipe adapter against this seam, with a paired
worker endpoint and explicitly selected matching v2 launch mode. Decide and test
the per-request frame/end boundary on persistent connections, partial writes,
process death, bounded stderr draining, hard abort/disposal, and removal of stale
responses before any production wiring. Verify worker/engine binary identity at
launch and derive observed binding identity from actual native state. Then run
real out-of-process fault-injection tests and version-specific Windows/Siemens
validation before enabling the adapter. The .NET 8 async host seam also needs an
explicit integration choice for existing legacy-framework endpoints; this code
does not claim it can simply be dropped into those processes. Production rollout
and any change to the existing v1 launcher/wire remain a separate reviewed gate.
