# Windows compile-only evidence for aaa9e0d (2026-10-03)

## Provenance and scope

- Published source commit: `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`.
- Completed Windows compile-only validation on 2026-10-03.
- The task reported matching HEAD/tree and a clean validation worktree. This is
  evidence for that exact commit, not a claim that a separate reconstruction or
  later documentation commit was independently rebuilt.
- Actual Csc invocations were verified. Validation used `DesignTimeBuild`
  compile-only builds. Static PE inspection did not execute the produced artifacts
  or load/execute Siemens DLLs.
- Detailed report and log index remain machine-local:
  `RESULT-aaa9e0d.txt`.
  This basename is provenance metadata, not a cloud artifact or downloadable attachment.
  No cloud upload of these detailed logs is claimed.

This report transcribes the completed task result. It is not a regenerated build
manifest, a new build on the documentation-editing machine, or release acceptance.

## Results

| Exact release | Restore | Adapter compile | Worker compile | Static PE | Warnings per Adapter/Worker build log |
|---|---|---|---|---|---|
| 14sp1 | PASS | PASS | PASS | PASS | 13 |
| 15.1 | PASS | PASS | PASS | PASS | 13 |
| 16 | PASS | PASS | PASS | PASS | 13 |
| 17 | PASS | PASS | PASS | PASS | 13 |
| 18 | PASS | PASS | PASS | PASS | 13 |
| 19 | PASS | PASS | PASS | PASS | 9 |
| 20 | PASS | PASS | PASS | PASS | 5 |
| 21 | PASS | PASS | PASS | PASS | 5 |

All builds reported zero errors. Warning counts are each build log's totals;
Worker logs include dependency warnings. They are not deduplicated totals and
must not be summed as distinct defects.

Both full production-engine source targets were recompiled in this run:
V20 passed with 23 warnings, V21 passed with 21 warnings, both with zero errors.
The cumulative WIP therefore cannot be described as leaving the full engines
unchanged. The static PE inspection found `AddDeviceWithFallback` and the added
document Import/Export members in all eight target assemblies. Member existence
does not establish route enablement or exact-release semantic compatibility.

The validation task reported unchanged D-drive Git state and unchanged hashes
for 904 files. No new local-computer action was performed to write this report.

## What remains open

- No weaving or native/runtime acceptance was run; historical weave evidence
  belongs to its earlier source increment, not this commit.
- No produced artifact or Siemens DLL was executed. Windows filesystem/read-lock,
  batch-publication and live-project behavior remain separately unverified.
- This was not `Build-Release.ps1`, release packaging, deployment, or CI/release
  gate completion. Existing generated manifests and their hashes are unchanged.
- Legacy production entries remain disabled. The 62-tool source ledger remains
  55 implemented, including 12 scoped source/offline/manual closures and 43 partial
  implementations, with seven pending/unimplemented. Exactly compatible migrations
  remain seven. No feature status, closure count or production gate is promoted.
- Final independent batch-document-import wiring review remains incomplete after
  an automated tool block. Compile success does not substitute for that review.

## Reading older candidate records

Earlier candidate records and ledger feature/release-state strings retain their
batch-local typed-build-pending labels as historical provenance. For this exact
commit, this report and the ledger's `compileEvidence` supersede only the pending
compilation claim. `typedApiCompilation` evidence fields identify the bounded
compile-only pass. Manual semantics, native behavior, Windows tests, unsupported
release routes and review gaps retain their original restrictions. Any later
source change needs its own evidence; this report supplies no automatic freshness.
