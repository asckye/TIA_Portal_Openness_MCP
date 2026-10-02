# Binding snapshot candidate: observation and lifecycle contract

This is a read-only implementation seam, not production v2 activation. Existing
`Attach(int)` remains PID-only. No existing worker dispatch or lifecycle method
calls these hooks. No TIA process was attached, started, closed, disposed or
controlled to implement or test this change. Native SDK compilation and Windows
runtime acceptance remain pending for every exact supported API version.

## Implemented boundary

- `BindingSnapshotContract.cs`: immutable observation DTOs, fakeable process and
  project observation interfaces, independent observation policy, project tuple
  digest and fail-closed epoch tracker. It has no Siemens dependency.
- `BindingSnapshotProcessSource.cs`: short-lived OS queries through
  `Process.GetProcessById`, `StartTime.ToUniversalTime()` and `HasExited`. It only
  releases its own OS query handle. It never disposes a native TIA object.
- `BindingSnapshotAdapter.cs`: a partial `PlcFoundationEngine` implementation that
  reads existing `portal`, `project`, `lifecycle` fields on the owning STA. Native
  reads are restricted to the existing typed `project.Name` and
  `project.Path.FullName` API properties already used by Foundation. No
  `GetCurrentProcess`, new SDK members, reflection, attach or dispatch is added.

The source reads cached binding fields, an independent OS PID/start identity,
project name and path twice, another independent OS PID/start identity, and the
cached fields again. Any exception, missing process/permission, malformed or
changed project identity, contradictory fields or changed OS identity yields
`Unknown`. OS identity is never inferred from Siemens `AcquisitionTime`.

Successful project-property reads and a stable OS identity are point-in-time
observations. They do not assert a persistent live connection, attachment
permission, process ownership or runtime readiness. There is no `isConnected`
or `LiveConnectivity` promise. There is no automatic retry or reattachment.

## PID-only remains weak

`ReadBindingObservation()` can inspect an already attached legacy engine, but
without an anchor captured *before that attachment* its result remains
`WeakPidOnly`, including when fresh OS start-time samples match. It cannot
retroactively prove which process the original attach selected.

Both weak and unknown observations deny v2 admission. Calling
`ObserveBindingSnapshot()` on such an engine permanently faults that candidate
snapshot session. A later healthy read cannot reset its epoch or repair it.
A new engine/session and explicit lifecycle integration are required.

## Required future lifecycle calls (not wired here)

Use one engine, one snapshot tracker and one owning engineering thread per
worker session. Do not run concurrent requests. Do not take identities from
request arguments, process listings or an unrelated engine.

1. Before a fresh hello, call `ObserveBindingSnapshot()`. Only known detached,
   unbound epoch zero is suitable for the existing endpoint's hello.
2. Immediately before an actual explicit attachment attempt, call
   `BeginBindingSnapshotTransition(BindingLifecycleChange.Attach, selectedPid)`.
   This observes a fresh OS anchor before attachment. Call the existing
   `Attach(selectedPid)` exactly once, then
   `CompleteBindingSnapshotTransition()`. Completion independently brackets
   project/field reads with OS observations and must match the pre-attach anchor
   and the actual engine's recorded PID. This is the only path that establishes
   an attachment anchor. A mismatch or missing permission denies admission.
3. Bracket every actual bind, open or create with `Begin...(Bind)` / the existing
   operation / `Complete...()`. Bracket unbind or successful close with
   `Begin...(Unbind)` / operation / `Complete...()`. Do not bracket dry-run calls.
   Bind/unbind completion must preserve the anchored process identity.
4. If a bracketed operation throws or its result is uncertain, call
   `FailBindingSnapshotTransition()` while the engine is usable and terminate
   the v2 candidate session. Do not complete an operation that threw, recover
   automatically, replay it or emit a successful response.
5. Each attempt consumes an epoch *before* the operation. Reads never increment
   epochs. Every bind/unbind/rebind, including same-path/name ABA, must be
   bracketed. A read cannot detect a complete unbind/rebind cycle that happened
   between samples; lifecycle hooks, not observed identity equality, provide
   that guarantee. Until all lifecycle entry points are covered, activation is
   prohibited.
6. The pure tracker also models a future `Detach` transition. The current engine
   only has terminal `Dispose`; its disposed instance cannot be observed or
   completed. End/fault the session before terminal disposal. Do not wrap
   `Dispose` as a recoverable detach and do not publish a detached snapshot from
   it. No detach or disposal execution is included in this patch.

A transition's observation must complete successfully before another operation
or snapshot. Interrupted, nested or out-of-order transitions fail closed.
Lazy capture callbacks refuse known-invalid, pending or reentrant observations
before accessing native properties; a stale anchor is rejected before project
reads. Nested same-STA observation faults cannot be overwritten by an outer
callback. Diagnostic observation also refuses capture while pending/faulted, and
an unknown diagnostic observation permanently faults the snapshot session.
Wrong-thread calls are rejected before touching tracker state.

## Future endpoint conversion

The Foundation DTO does not add a dependency on WorkerProtocol. The integration
owner must build a narrow conversion in the matching worker, after exact SDK
compilation and lifecycle-hook review:

```csharp
var observed = engine.ObserveBindingSnapshot();
if (!observed.AllowsV2Admission)
    throw new IdentityViolation("BindingObservationUnknownOrWeak");
var b = observed.Binding;
return b.State == BindingObservationState.Unbound
    ? BindingSnapshot.Unbound(observed.Epoch)
    : BindingSnapshot.Bound(observed.Epoch,
        new ProjectIdentity(b.ProjectSha256!, b.ProcessId!.Value,
                            b.ProcessStartUtcTicks!.Value));
```

Use this independently observed callback before and after every admitted
read-only operation. Never replace an exception/unknown with
`BindingSnapshot.Unbound(...)`. Do not synthesize an epoch from a request.
The present endpoint exposes read-only operations and requires a fresh unbound
hello; it does **not** implement attach/bind transitions. This observation hook
alone therefore cannot enable a bound production endpoint. Lifecycle transport
integration and its tests remain a separate gate.

The digest is SHA-256 of four UTF-8 fields, each prefixed by a signed Int32
little-endian byte length: `tia-binding-v1`, `project` or `local-session`, canonical
absolute Windows file path uppercased invariantly, exact project name. It hashes
identity metadata, not file contents. Canonicalization reuses the reviewed
`MutationIdentityPolicy.AbsoluteFile`, with its invalid/ambiguous path rejection.
Changing this tuple or its normalization requires explicit versioning.

## Verification

Run the Siemens-free checks:

```sh
DOTNET_CLI_HOME=/workspace/shared/dotnet-home /workspace/shared/dotnet/dotnet run \
  --project tools/tiaportal-mcp/tests/BindingSnapshot.Tests/BindingSnapshot.Tests.csproj
```

The final standalone suite passes 69 checks. Fake-source coverage includes weak existing PID binding, strong anchored binding,
OS access denial, process disappearance/reuse, wrong PID, project read failures,
rename/path/cached-field changes, local-session kind separation, digest stability,
fresh epochs, same-project ABA, interrupted/unannounced transitions, unexpected
results and sticky failure. The suite checks that no Siemens assembly is loaded.
It does not substitute fake SDK types or claim native compile/runtime coverage.
