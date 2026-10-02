# Multi-version WIP checkpoint - 2026-10-02

This independent development checkpoint is not a release or native support claim.
Baseline: `fac2b6aab343257bb10af421ed30b1c65f82375f`.
Branch: `offline/publicapi-validation`. Master and historical version branches are not merged or changed.

## One-time commit exception

The maintainer explicitly authorized this WIP commit to omit the complete Build-Release gate on 2026-10-02. That gate includes native diagnostic JIT, remoting and engine/worker execution outside the current compile/metadata/pure-test scope. This exception applies only to this checkpoint; it does not amend CLAUDE.md, relax future release rules or authorize native execution. Original validation manifests are retained unchanged and are stale for these source changes. Release/validate-bundle checks requiring fresh manifests are not claimed to pass.

## Verified scope

Exact targets are V14 SP1, V15.1, V16, V17, V18, V19, V20 and V21; original V14/V15 are excluded. Eight real API adapter/worker builds and copied Adapter weave/verify checks passed. Main pure MCP/policy/PE/XML suite: 1851 passed, zero failed/skipped. Separate diagnostic/PE checks: 124; worker isolation checks: 36; forty deliberate coverage corruptions rejected. Host builds with two known catalog CS8625 warnings. Adapter builds additionally retain two CS0649 warnings for unwired diagnostic hooks.

The pinned 62-tool V17 PLC profile has 23 implementation/offline verified entries: seven scoped ordinary-PLC reads closed, sixteen manual-partial, thirty-nine unimplemented. Six extras remain; three existing tag/constant readers now have their own scoped closure. Public preview count remains 29. See legacy-plc-manual-checklist.json and legacy-plc-host-preview.md for per-tool limits and evidence.

Check-DeadToolReferences passed. Check-Repository scanned 234 Markdown files and reports four missing runtime entrypoints (V20/V21 engine executables); those build artifacts are intentionally absent here. This check is not reported as passed.

## Remaining and ownership

- Functional work: manual-first API reconciliation and remaining tools; hardware topology/R-H/passive coverage and LocalSession identity/lock gaps keep affected mutations blocked.
- Isolation work: strict negotiated v2 codec, independent binding snapshot/epoch, request correlation, journal hooks and process-loss observation. Coordinate LegacyHost WorkerClient/WorkerProtocol, PlcWorker Program, Adapter InvocationJournal and Foundation binding transitions before editing. WorkerOperations registrations are retained.
- A separate seven-file pure request-identity/context candidate is backed up with the handoff archive, but is NOT integrated or wired by this commit. Its 105 reported pure checks do not establish wire compatibility or native behavior.
- Production legacy admission, Publish/Pack and native acceptance remain blocked. No TIA/worker launch, project attachment, PLC operation, native smoke, installation, system configuration change or Siemens DLL distribution was performed.

The verified local/Library handoff archive preserves the cumulative patch and candidate separately. Future cloud work should handle source/protocol/pure tests; Windows is needed for builds against the user's actual SDK references. Obtain fresh authorization before any native acceptance. Do not use historical machine addresses, PIDs or projects as active targets.
