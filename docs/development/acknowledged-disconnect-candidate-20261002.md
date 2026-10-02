# Explicit acknowledged Disconnect candidate

2026-10-02. Source and pure offline tests only. Native acceptance: **NOT RUN** for every release. Current typed candidate rebuild remains pending. This is a partial migration, not upstream response parity or production enablement.

## Contract

`Disconnect` has no arguments or bound-project prerequisite. An actual worker response is accepted only after request-id decoding and strict acknowledgement validation: terminal/disconnected stage, non-owning attachment strategy, exact attached PID (or no PID for an idle worker), `WorkerAcknowledged=true`, `Detached` matching PID presence, no saved/closed project, and launch mode never. An idle host with no worker returns a distinct `WorkerAcknowledged=false`, `Detached=false` acknowledgement locally; this is terminal host bookkeeping, not an assertion of native work. It runs without native enablement and never creates a worker.

Success ends the session. Repeated Disconnect returns the same validated result without another native call. Subsequent explicit Attach is rejected and needs a new explicitly created host/worker session; no automatic reconnect or replacement worker. Host requests, including idle disconnect, pass through the existing serialized gate.

Only provenance from an explicit `TiaPortalProcess.Attach()` permits native disposal. Owned or unknown Portal provenance is rejected before disposal. This distinction matters: the manuals permit TiaPortal.Dispose to terminate an application-created no-UI instance when that application is its sole client. This implementation creates no Portal instance and does not call TiaPortalProcess.Dispose, Save, Project.Close, process Kill, or any launch API for Disconnect.

The engine sets disconnect-attempted before native disposal. A thrown native call leaves the attempt uncertain and blocks retries; normal worker cleanup also skips a second disposal attempt. The host's existing fail-stop outcome state retains EOF, cancellation after sending, lost/malformed acknowledgement and native failure as unknown, never retrying or clearing poisoning. A prior poisoned session cannot use Disconnect as a reset. Confirmed rejection before operation may remain a known no-mutation failure.

Project binding, project ownership and project modification state do not trigger a project operation during detach. Clearing local references happens only after a successful native result. Native effects on borrowed or dirty projects still require isolated acceptance; pure tests are not evidence of those native effects.

## Verification and limits

Final offline host run: **3040 passed, 0 failed, 4 skipped**. The two existing nullable warnings in TiaVersionCatalog remain.

Offline tests exercise attached, bound and explicitly unbound lifecycle state, source assertions excluding project/dirty inspection or Save/Close calls, unknown/owned Portal rejection, idle no-worker operation with native enabled and disabled, repeated disconnect, subsequent explicit attach rejection, disposal callback exception with no retry, strict acknowledgement shape/identity, EOF/cancellation/malformed/failure poisoning, serialization behind an in-flight gate, and public dispatch without project arguments. The transport failure checks exercise outcome policy directly, not an end-to-end fault-injected worker transport. The final-disposal no-retry check is a source assertion; native dirty-project behavior is not tested. No Siemens assembly or worker process is loaded. Four existing Windows/native groups remain skipped.

Manual basis: `ordinary-project-lifecycle-review-20261002.md` and its exact-release disposal chapters for 14 SP1, 15.1, 16, 17, 18, 19, 20 and 21. That earlier audit's statement that Disconnect was unimplemented describes its pre-candidate source snapshot and is superseded only by this partial implementation record. Its native and other lifecycle limitations remain unchanged.

Remaining acceptance: exact-release typed compilation of this candidate, isolated Windows worker/native acknowledgement tests, actual borrowed/dirty/unbound project preservation, transport/process failure timing, and full upstream ResponseDisconnect parity. All native rows stay NOT RUN.
