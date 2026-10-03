# LegacyHost bounded batch document export candidate

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

2026-10-02: source candidate only. Native acceptance **NOT RUN**. No Siemens runtime, native project, actual PLC, download, import, save, commit or push was performed. Exact V20/V21 typed SDK rebuild and Windows publication acceptance remain pending. This does not close historical API compatibility.

## Scope and explicit deviations

`ExportBlocksAsDocuments` admits exact releases `20` and `21`, one exact software path and one exact ordinary root/user-group `groupPath`. `recursive` is false by default. Group identity is resolved against a complete scan bounded to 1024 groups; selected block enumeration must complete within `maxItems` (default 128, maximum 256). Oversized and empty inventories are refused, never truncated or reported as successful empty exports. Duplicate paths are refused; ordinal path sorting makes the inventory deterministic.

Every member must be an unprotected LAD or DB block. Other languages/protection refuse the entire preview. Inconsistent members are reported in preview and refuse the entire apply. Software units, system groups, PLC types, regex selection and language/option overrides are outside this candidate. The old wrapper's `regexName` is replaced with exact `groupPath` and explicit recursion; `preservePath=true` is refused. These are deliberate contract deviations, not baseline parity.

`exportPath` is a **new output directory under an existing parent**, unlike the old overwrite-capable wrapper. Each block gets a fixed ASCII `block-<full-path-hash>` child directory, with its ordinary validated block name for `.s7dcl` and `.s7res`. This prevents cross-group name collisions without pretending to preserve PLC hierarchy. Preview creates no directories and invokes no export/offline callback. Both final and staging file paths are bounded for the Windows publication scope.

Apply requires `confirm=true`, the exact expected project identity, and `expectedPlanHash` from a fresh preview. The hash binds release, project, software, exact group, recursion, item bound, output root, per-block identity/language/consistency/output and native option/expected filename policy. It is an intent and inventory hash, not a snapshot of native block contents. Current source bytes or edits between preview and apply are not claimed to be detected.

## Publication and outcomes

The implementation reuses the single-document route's exact release, filename, destination, hash and reported-file validation helpers. Each native callback uses the reviewed `PlcBlock.ExportAsDocuments(DirectoryInfo,string)` overload and accepts only `DocumentResultState.Success`. It does not reuse the full engine's existing-pair deletion behavior.

V20 requires code and resource documents. V21 requires code and preserves an optional resource when reported. Each staged set must contain exactly the reported expected regular nonempty files, no links/extra entries, maximum 64 MiB per file. The whole staging tree and earlier sets are rechecked after the final callback. Files are neither transformed nor claimed to be syntactically validated.

One Windows-only, non-replacing directory move publishes the complete tree. Existing output directories/files and publication races refuse overwrite. Non-Windows production apply refuses before any offline/native callback; tests inject an ordinary test directory mover. Apply stops after the first block failure and never retries/replays native callbacks.

Per-item statuses are `planned`/`inconsistent` during preview, and `staged`, `failed`, `not-attempted` or `exported` during execution. `staged` explicitly means no confirmed publication. Only a successful whole-tree move changes all items to `exported`. A publication or final-validation failure can leave every item `staged` while the overall status is `failed`. Any execution-stage failure retains the staging path and poisons host and worker sessions. No rollback or cleanup is attempted. For an ambiguous move outcome, inspect **both staging and requested output locations** before a new explicit session; a recovery path is evidence, not a guarantee the directory still exists.

## Evidence and limits

The native signature/manual basis is the same as the reviewed [single-document candidate](legacy-document-export-candidate.md), including the documented V20 paired resource and V21 optional resource differences. No additional batch-native overload is invented. The candidate consists of bounded orchestration of the single-block native API.

Pure tests cover deterministic complete plans, version/language/protection/group refusal, hash bindings, no-write preview, inconsistent/empty/oversized refusal, no-overwrite, native/validation/publication failures, stop-first-failure, recovery evidence, no false all-success, late staged-file changes, extra tree entries, V21 code-only sets, strict response/request binding and session poisoning. Passing these tests does not certify Siemens bytes, Windows filesystem semantics, or native acceptance.
