# Optional managed v2 worker endpoint (unwired)

`WorkerEndpoint` joins the reviewed `JsonLegacy.StrictCodec` and shared
`WorkerRequestGuard` to explicitly registered, synchronous read-only callbacks.
It targets net461 and net8.0 without Siemens references or SDK substitutes.
It does not register itself with the production worker or host. The separate
`TiaMcp.EndpointFixture` is the only process executed by its integration tests.

## Dispatch contract

The owner invokes `CreateHello` once, followed by serialized `Handle` calls on its
engineering thread. It supplies independently observed binding snapshots, never
copies binding identities from incoming requests, and closes the transport on any
endpoint exception. A successful Handle emits bounded progress then one reply;
the owner writes its exchange terminator only after Handle returns successfully.
The endpoint has no stream/process/network server and no raw payload logger.

Hello requires observed unbound epoch zero. Every request is strictly decoded,
checks canonical numeric or `worker_<requestId>` outer identity, and checks
allowlisted operation, exact release/binary hashes/session, strictly increasing
request ID, unique correlation nonce, before epoch/project/process identity and
unchanged expected-after identity before entering a callback. The replay set is
bounded to 65,536 requests per endpoint; exceeding it fails closed and requires a
new explicitly owned session. No automatic session restart or replay exists.

Callbacks receive immutable arguments and return an independently computed JSON
result. Progress is valid only synchronously on that invocation's thread, at most
256 frames, with generated monotonic sequence. Late, concurrent, invalid or failed
progress emission poisons the endpoint even if the callback catches the exception.
Callbacks are declared read-only, not proven so by this seam: native side effects
cannot be detected from a binding snapshot alone. Bind/unbind/write registration
is deliberately unavailable. Consequently project-required callbacks remain
inaccessible in the fresh unbound session until a separately reviewed native
binding lifecycle is designed; tests do not fabricate a successful attach.

The endpoint observes binding again after both successful and throwing callbacks.
Only a stable observation permits success or sanitized `ReadFailed` with
`{"code":"ReadOperationFailed"}`. Observation loss/change, invalid result,
reentrancy/concurrency, malformed request, replay or emission failure poison the
endpoint. No reply claiming success is emitted for changed post-operation binding.
The pipe owner closes without a terminator; the host treats any dispatched result
as unknown and forbids replay. The endpoint cannot preempt a blocking callback;
the host's monotonic request deadline aborts and reaps its owned fixture process.

## Remaining production hooks (not implemented or validated)

1. Verify selected executable and engine binary hashes, exact release and fresh
   session at real worker launch; the fixture's constant hashes are test inputs.
2. Derive project digest, TIA PID/start time and monotonic binding epoch from actual
   native worker state on its serialized engineering thread before and after each
   operation. Define observed bind/unbind transitions before enabling project calls.
3. Audit and register concrete read-only operations, validate their argument/result
   contracts, and guarantee no accidental write or implicit attach in callbacks.
4. Wire bounded v2 framing/owner handshake into the optional worker path and host
   selector without changing default v1. Reserve stdout solely for protocol; ensure
   teardown and native journal context use validated identities and no raw data.
5. Verify Windows pipe/process lifetime, STA/native thread affinity, net461 runtime
   execution and independently authorized native SDK acceptance per release.

Linux net8 fixture execution and net461 compile-only results establish none of the
native/runtime release gates. No all-eight-release support claim is made.

## Reproduction

From `tools/tiaportal-mcp`, with an installed .NET 8 SDK and official cached packages:

```sh
dotnet build src/TiaMcp.WorkerProtocol.Endpoint -c Release -m:1 \
  -p:UseSharedCompilation=false -p:UseReferenceAssemblyPackage=true
dotnet build tests/TiaMcp.WorkerProtocol.Endpoint.Tests -c Release -m:1 \
  -p:UseSharedCompilation=false -p:UseReferenceAssemblyPackage=true
dotnet tests/TiaMcp.WorkerProtocol.Endpoint.Tests/bin/Release/net8.0/TiaMcp.WorkerProtocol.Endpoint.Tests.dll \
  "$PWD/tests/TiaMcp.EndpointFixture/bin/Release/net8.0/TiaMcp.EndpointFixture.dll"
```

Keep both TFMs in lock-file generation. Original codec/core and owned-pipe suites
are preserved and must be rerun independently. Full Build-Release/native gates
remain outside this pure, unpublished change.

## Verified in this change (2026-10-02)

- Endpoint net461 and net8.0 compile: zero warnings/errors; net461 is compile-only.
- New suite: 302 real-process/pipe assertions plus 81 direct endpoint assertions,
  383 total. Both outer-ID styles return independently filtered/sorted typed sample
  data through modern host JSON, real owned pipes, and legacy worker validation.
- Covers before/after binding and observer failure, exact identities, replay,
  unknown operations, stable ReadFailed/recovery, progress, deadline/cancellation,
  incomplete replies, ownership, isolation, thread/reentry errors and no-replay.
- Independent review harness: 23 additional assertions, including full request
  capacity and request 65,537 rejection. This is separate review evidence, not part
  of the checked-in 383-count suite.
- Preserved regression suites rerun: core 105, JsonV2 343, JsonLegacy 5,462,
  AsyncPreview 203 and original actual-pipe transport 102, all passing. The reviewed
  343-check source corpus hash is unchanged.
