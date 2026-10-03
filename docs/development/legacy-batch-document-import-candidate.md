# Bounded document batch import candidate

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

Source-only, 2026-10-02. Native acceptance, Windows locking and the current exact SDK rebuild are NOT RUN by this change. This implements only an explicit ordered batch of the [single global-DB document candidate](legacy-single-document-import-candidate.md), not general SIMATIC SD support or pinned-wrapper compatibility.

## Admission and ownership

- Exact V20/V21 only. One exact existing ordinary PLC software, root/user group and local Windows directory; no root fallback, group creation, software units or target guessing.
- Required ordered `fileNamesWithoutExtension` contains 1–16 distinct case-folded basenames. No regex, wildcard, directory auto-selection, recursion or dependency guessing. Each selected basename must equal its parsed declaration identity.
- The same complete tiny global-DB grammar and single-item policy are reused. Only primitive Bool/integer scalar declarations are admitted; no FC/FB/LAD/FBD/SCL/OB/TYPE, instance DB, expressions, arrays, unknown pragma, manual block number, MLC reference or extra declaration. This is lexical admission, not Siemens syntax/content validity.
- All selected `.s7dcl` and `.s7res` handles are held with read sharing only for the entire batch. V20 requires the resource; V21 allows absence. Each file remains 1–4 MiB; at most 32 handles and 128 MiB of exact source bytes. No bytes, BOMs, newlines or resources are rewritten.
- All declarations, case-folded collisions and complete bounded ordinary-group inventories are checked before the first call. Every set must share exact project/process/target identities. The ordered manifest hash binds each full single-item preview hash, including input bytes, target and original inventory.

## Execution and evidence

Apply requires explicit project confirmation and the exact whole-manifest preview hash. Each native boundary rechecks all retained handles, selected file inventories, project/process/target/offline state and expected ordinary inventory. Separate immutable per-item native closures reuse the same single-item typed `ImportFromDocuments(..., ImportDocumentOptions.None)` implementation. No Override, culture activation, number restoration, compile, save or download occurs.

After success, exactly one new ordinary GlobalDB inventory identity must appear for the requested name/group. No original group sentinel or object may disappear/change. The next set uses only that verified inventory evolution. Each item retains its original preview, actual single-item outcome and observed post-import inventory; the host validates the inventory chain and final success delta independently.

Every native null/non-Success/exception/readback uncertainty stops the batch. Earlier succeeded items remain succeeded; the current failed item retains available native diagnostics; later items remain not-attempted. A failed preflight before any native entry throws without mutation. A later preflight failure retains earlier outcomes and marks the overall batch unknown. Handle-cleanup failure after any attempted native entry preserves per-item evidence but marks the batch unknown. Unknown outcomes poison both the entire worker and host, including subsequent reads. No automatic retry, replay, rollback or deletion is attempted.

`ExistsVerified` remains distinct from content verification. Native None is documented as “Throw if exists”; that does not establish normalization, race timing or atomic rollback. Installed update level remains unknown and is not inferred from the major SDK version.

## Verification

Fake-callback policy tests cover complete locking, manifest order/hash binding, case collisions, success, native exception/null/non-Success, unexpected post-import deltas, final preflight failure before entry, later preflight stopping, cleanup failures, host inventory-forgery refusal and whole-host poison. The aggregate includes existing single-document lexical admission tests. All native/runtime/project/PLC actions remain NOT RUN. Repository Build-Release remains a separate prerequisite before any authorized commit or release.

Final implementer checks: aggregate **3,655 passed, 0 failed, 4 explicit skips**; LegacyHost build **0 errors, 2 existing nullable warnings**; `git diff --check` passed. Independent partial review reproduced **42 dedicated checks** and confirmed fixes for its two findings. Its final wiring-review turn was blocked by an automated review classification, so independent sign-off is incomplete. This limitation is preserved rather than treating implementer checks as independent completion.
