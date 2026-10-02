# Request identity and cached diagnostic context candidate

This addition is not wired into either host or worker. It is pure managed code
targeting the real net461 and net8.0 frameworks, with no Siemens, JSON, process,
network or filesystem dependency. Tests use only in-memory fake transport. No
existing tool registration, native behavior, protocol decoder or project is edited.

## Existing boundaries inspected

- Modern `TiaMcpServer/Isolation/WorkerConnection.cs` reads a Hello, then MCP
  JSON-RPC frames. It generates string IDs `worker_N`, allows one outstanding
  request and forwards progress. `OpennessWorkerSupervisor.cs` checks protocol 1,
  engine major, EXE hash and PID, initializes MCP, and compares tool catalogs.
  It adds `tiaMcpWorkerCorrelation` and `tiaMcpWorkerGeneration` to request metadata.
- `ModelContextProtocol/Tools/McpServer.SerializedCalls.cs` consumes correlation
  only for a child, treats it as optional, and generates a replacement correlation
  if missing/malformed. The inspected child path does not validate the transmitted
  worker generation. Thus missing identity cannot become optional in a v2 path.
- Modern `Portal.Binding.cs` verifies PID/start time/path/name independently of
  journaling. `Portal.cs` exposes an immutable cached `ProjectBindingIdentity`
  through the journal callback. Its GUID binding generation is different from the
  supervisor's numeric worker generation; neither should silently stand for the other.
- Legacy `LegacyHost/WorkerClient.cs` sends numeric `id`, `operation`, `arguments`,
  with no Hello. `WorkerProtocol.cs` rejects unknown/duplicate top-level fields,
  validates IDs and operation outcomes, and poisons a session after unknown results.
  Adding a metadata field to today's response would break that strict decoder.
  Legacy stderr is currently retained in memory; it is not a safe diagnostic feed.

## Candidate semantics

`EngineIdentity` requires protocol 2, exact release key, both worker EXE and engine
module SHA-256, and a fresh launcher-generated session token. The host compares
Hello against locally selected binaries and its launch token. Hello self-claims
are not authentication or proof that a native project was checked.

`BindingSnapshot` is explicit: new Hello is unbound at epoch 0. Absence is never
interpreted as unbound. Bound snapshots require a positive epoch, TIA PID/start
time and a project identity digest. The digest must cover a versioned,
length-delimited tuple of project kind, canonical full path and exact project
name. Both codecs must specify identical canonicalization before integration;
unknown local-session kind/identity must stay blocked. This candidate validates
the token shape and equality, not the truth of its native source.

The host guard assigns numeric internal IDs and fresh correlation IDs. Its
registered operation policy controls whether binding is required and whether an
explicit bind/unbind transition is allowed. Bind/unbind advance the binding epoch;
ordinary operations must leave it unchanged. A successful response must match
every request identity field and the expected independently observed binding.
Known pre-operation rejection and registered read-only failure may keep the
session usable only with intact identity and unchanged binding. Writes cannot
claim `read-failed`. Timeout, cancellation/pipe failure after attempted dispatch,
missing/mismatched reply identity, unknown result or binding loss poison the
session. There is no reset/replay method; recovery requires a new worker session
and explicit binding. Cancellation before dispatch consumes no transport attempt.

The worker guard validates identity and monotonic IDs before native dispatch,
rejects duplicate/stale requests, and checks independently observed completion.
Its caller must serialize it on the existing engineering thread and advance
state before invoking the operation. It must not echo caller-supplied binding as
an observation. Observe actual binding at the operation boundary, then open the
log context. SnapshotFields reads immutable cached data only; never make Siemens
calls from a log callback. Missing native observation aborts the session.

The diagnostic field allowlist contains correlation, internal ID, registered
operation, exact release/binary/session identity, epoch and hashed project identity.
It excludes arguments, output content, raw paths/names and exception messages.
Scope disposal invalidates captured asynchronous contexts as well as clearing the
current slot. Existing child JSON result validation must still run before accepting
completion; these identity guards do not replace tool-specific result validation.

## Negotiation and coordinated integration scope

Do not make today's v1 fields optional or fall back to v1 on an identity error.
Use a separately negotiated v2 codec/entry mode. Keep the current modern full
V20/V21 service path unchanged until its independent v2 transport tests pass.
The future codec must reject duplicate/unknown/missing identity fields, preserve
the existing outer-ID type and map it to the internal request ID, validate progress
identity, and enforce current frame limits before decoding. No codec is included
here, and typed fake frames do not establish JSON wire compatibility.

The following ownership handoff is required before touching existing files:

1. LegacyHost `WorkerClient.cs` and `WorkerProtocol.cs`: launch identity/hash
   validation, strict v2 codec, host guard, timeout/cancel poisoning and sanitized
   diagnostics. Preserve current known-no-mutation classifications and tool DTO checks.
2. PlcWorker `Program.cs`: negotiated Hello, guard before dispatch, request log
   scope and checked reply identity. Preserve all current registrations and read-only
   classifications, including `ReadExternalSourceNames`. `WorkerOperations.cs`
   does not need to be rewritten for this protocol candidate.
3. Foundation owner: supply an independently observed binding snapshot and
   monotonic binding epoch at operation boundaries, including TIA PID/start time,
   project kind/path/name. Prefer a new partial snapshot accessor plus coordinated
   lifecycle transitions. Do not invent identity from caller `expectedProjectFile`.
4. Adapter `Diagnostics/InvocationJournal.cs`: bind its request correlation and
   cached snapshot callbacks to RequestLogContext; clear them in finally. Project
   references/source inclusion require the build owner to choose assembly or source
   sharing while keeping the pure protocol free of Siemens references.
5. Modern path later: `IsolatedWorkerHost.cs`, `OpennessWorkerSupervisor.cs`,
   `WorkerConnection.cs`, `McpServer.SerializedCalls.cs` and the binding snapshot
   owner. Keep worker generation, binding generation and request ID distinct.

None of these existing files is changed by this patch. Publication/native gates
from the adapter workstream remain in place. Official per-tool semantics and
native acceptance remain owned by the functional migration workstream.

## Validation

Build the protocol project for both frameworks and the net8.0 test executable.
On machines using the existing Microsoft reference package, pass
`-p:UseReferenceAssemblyPackage=true`. Select a local NuGet config when offline.
Run only `TiaMcp.WorkerProtocol.Tests.dll`; it launches no process and loads no
Worker/Adapter/Siemens assembly. The core net461 output is compile-only in this task.
