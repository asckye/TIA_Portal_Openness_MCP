# Bounded host preview, not a production transport

This standalone net8.0 project is intentionally unreferenced by both existing
hosts/workers. The sibling identity core and strict JSON codec are unchanged.
No process, pipe, filesystem IPC, Siemens call or native execution occurs.

`ExplicitProtocolSelection.RequireV2` requires an explicit version 2 selection
and matching endpoint versions before selecting the preview. These are local
configuration inputs, not trusted remote capabilities. `JsonSession.AcceptHello`
still validates exact release, both binary hashes, launch session and fresh
unbound state. Existing production v1 remains selected through its existing
launcher; there is no trial decoding, retry, negotiation probe or v2-to-v1 fallback.
The supported production endpoint pair still does not speak this protocol.

`BoundedMemoryExchange` is scripted in-memory IPC. Its finite response snapshot
is limited to 4 MiB before copying, 32 frames and 1 MiB per frame. Each frame has a
signed 32-bit little-endian byte length followed by UTF-8 JSON. Zero, negative,
oversized, incomplete lengths/payloads fail closed. Caller mutation cannot change
the response snapshot. Cancellation is checked before dispatch accounting, before
each frame and at completion. Both dispatch and enumeration are single-use.
JsonSession adds strict decoding, identity/progress validation and exactly one
final reply; any attempted exchange failure poisons the session without replay.
This finite memory implementation makes no wall-clock deadline guarantee and is
not a wrapper for an unbounded or blocking stream. Its framing is provisional;
it is neither legacy newline framing nor modern MCP JSON-RPC framing.

## Next coordinated integration

1. Keep v1 endpoint selection the default. Add an explicitly selected launch mode
   only when both endpoints have a compatible v2 codec. Never downgrade a selected
   v2 session after handshake, identity, timeout or result validation failure.
2. Replace the synchronous `IV2Exchange` seam with an asynchronous, per-request
   transport contract before using real pipes. Enforce byte bounds before allocation,
   progress count/total bounds, deadline and cancellation on every read/write;
   distinguish final-reply completion from persistent-pipe EOF. A timeout after any
   possible write is unknown outcome, never an automatic retry.
3. Independently verify worker executable and adapter-module hashes at launch.
   Generate a fresh session token; pass it only through the coordinated launch mode.
   V14 SP1 through V20 adapters cannot take a net8 JSON dependency: a reviewed
   compatible adapter or explicitly versioned sidecar is still required.
4. Foundation must expose independently observed PID/start time, exact project
   identity and monotonic binding epoch at serialized operation boundaries. Agree
   a versioned length-delimited project digest/canonicalization; do not derive it
   from the caller's expected path or from asynchronous diagnostic callbacks.
5. Connect worker admission/finish and RequestLogContext on the engineering thread,
   retain existing native operation implementations and pure tool-specific response
   validation. Review lifecycle changes and known-no-mutation/read-only outcomes.
6. Connect the host adapter to `IFoundationWorker` only after both ends and the
   async contract pass adversarial fake transport tests. Native/runtime and Windows
   validation remain separate gates. Modern V20/V21 integration is a later change.

The migration workstream may add tools through current v1 contracts independently.
Nothing here changes its tool registration, native support status or acceptance.

## Verification

Run the three net8 test executables after restoring with an offline NuGet config
and `-p:TargetFrameworks=net8.0`. Keep certificate generation disabled in this cloud
workspace. Core net461 compilation needs the real reference assemblies; it is a
separate compile-only gate and JSON/HostPreview remain net8.0-only. No release,
Build-Release, Windows-runtime or native pass is implied by these focused tests.
