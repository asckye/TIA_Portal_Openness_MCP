# Focused independent review: parallel migration candidates

Date: 2026-10-02. Read-only source review, except this dedicated report. No Siemens assembly, TIA process, project, native operation, commit or push was used by this reviewer.

## Result

No remaining actionable source blocker in the declared candidate scopes after the fixes below. This is not release approval, exact-SDK build evidence or native acceptance.

- Document export: exact V20/V21, ordinary LAD/DB single block, new output directory only. Reviewed intent hash, bounded complete block selection, staged-set validation, V20 required/V21 optional resource, non-replacing whole-directory publication, retained failure evidence and host/worker poison/no replay.
- External source: distinct plan-only tool, all eight exact release keys. Apply is refused before I/O, including with matching confirmation/hash. Reviewed one-file allowlist, canonical Windows path restrictions, held stream and unchanged-byte rehash, ASCII/4 MiB budget, two complete 4,096-entry root snapshots, collision checks and identity-bound hash. No native CreateFromFile/generation/delete/compile/save/download call exists in this candidate.
- Hardware catalog: V19–21 only; V18 has static API evidence but unresolved semantics and stays gated. Reviewed one typed Find call, exact identifiers, unknown insertion compatibility, bounded client enumeration/field text, explicit truncation and closed response contract. Native Find materialization/duration is not bounded by this wrapper.

## Findings resolved during review

1. Document selection filtered before Take(2), allowing unbounded candidate-group/block traversal. Owner replaced it with complete bounded selection: 1,024 groups and 10,000 blocks; overflow, missing and ambiguous identities fail closed. Empty groups count toward the bound. Five regression checks added.
2. Catalog response validation admitted arbitrary extra root/meta/row fields while forwarding a deep clone, bypassing its field-text budget. Owner added closed allowed-field validation plus exact fixed metadata checks and regression tests for extra/oversized fields.
3. Document tool description incorrectly implied a mandatory V21 resource pair. It now explicitly distinguishes required V20 and optional V21 .s7res.

## Verification

Independently executed on cloud Linux, using the prescribed dotnet executable and environment variables on every run:

- ExternalSourcePlanTests: 84 passed, 0 failed.
- HardwareCatalogTests: 56 passed, 0 failed.
- git diff --check: clean at review.

Aggregate LegacyHost verification is coordinated with the integration owner; final result is recorded separately below when available. Pure tests use fake/native-free adapters or injected file callbacks and do not establish Windows filesystem or Siemens behavior.

## Evidence and scope limitations

Reviewed repository candidate documents and the external-source audit at the task's audit-evidence directory. Static signature provenance supplied by the completed read-only SDK audit:

- Document exports: SHA-256 1ccebbc896351369b2c31c1f38abef6081f3b2de56724137c52fffb56e80f721.
- External-source API: SHA-256 58ca48e3f85e70280d66118479416dde7d3830425e189d54f5c8612a6ccdc6bc; XML scope d77773b6817fc0797f825ef1745e9d323e4cd332e7f77cc5a789727bd655aa8b.
- Hardware catalog: SHA-256 1998e9efa39752e161ba8da726cf8a27d626571cb25df37562fa7aa0b67dc703.

These hashes are reported provenance; this reviewer did not access or independently hash the desktop SDK files. Signatures are not native behavior acceptance. Shared ReadSelection still materializes device/PLC candidates with depth limits only; candidate-specific inventory bounds must not be described as end-to-end bounded native traversal. The document hash binds reviewed intent, not a block-content snapshot. External-source snapshots/locks do not prove native no-overwrite, normalization or source lifetime, so mutation stays blocked. Existing Build-Release/exact-SDK and native acceptance gates remain open; no migration completion or release claim is added by this review.

## Coordinated aggregate checkpoint

The document/integration owner completed the pre-delete-integration LegacyHost aggregate at 2026-10-02 16:50:42 UTC: **3,237 passed, 0 failed, 4 skipped**, log `/tmp/document-fixed-aggregate.log`. This reviewer read the final log result; it was not an independently rerun aggregate. It includes the reviewed document export, external-source plan and hardware catalog changes. A separate delete candidate began shared integration afterward; the checkpoint does not certify that later source state. Native acceptance remains NOT RUN.
