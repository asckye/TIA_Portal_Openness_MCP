# LegacyHost single document export candidate

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

2026-10-02: source candidate only. No Siemens runtime calls, native projects, download, or native acceptance were performed. Exact V20/V21 SDK rebuild is pending. This does not close historical API compatibility.

## Bounded contract

`ExportAsDocuments` selects one exact escaped ordinary root/user-group block path and exact software path. It only admits release keys `20` and `21`, LAD/DB languages, and a consistent unprotected block. The complete selection scan is capped at 1024 groups and 10000 blocks, with no acceptance of truncated uniqueness. Software units, system groups, PLC types, batches, imports, preserving directory hierarchy and other languages are outside this candidate. V21 supports additional languages officially; this route intentionally does not claim their implementation.

Unlike the full wrapper, `exportPath` must be a **new directory** under an existing directory. `preservePath=true` is rejected. This is an explicit contract deviation, not full-wrapper parity. Preview creates nothing and calls no native export or offline transition. Apply requires confirmation, exact project identity and the fresh preview hash. The hash binds release, project, software, block, output, language, consistency, option policy and expected filenames; it is an intent hash, not a source-content snapshot.

The native call reuses the full engine's two-argument `PlcBlock.ExportAsDocuments(DirectoryInfo, string)` overload and `DocumentResultState.Success` criterion, but never reuses its deletion/overwrite behavior. No export options or project-language overrides are invented. Resource text languages follow the native project's configured export semantics.

V20 expects `.s7dcl` plus `.s7res`. V21 requires `.s7dcl` and preserves `.s7res` when the native result supplies it; the official V21 documentation makes the comment resource optional. Exactly the reported expected files must exist in staging, without extra entries, links, empty files or files over 64 MiB. Both name length and output path are bounded. Files are not rewritten or interpreted as validated source syntax.

Windows-only production publication uses a sibling staging directory and a non-replacing directory move, publishing the document set together. If export, validation or publication fails, staging is retained, status is failed, and both host and worker poison the session. There is no retry, native replay, deletion or compensating rollback. Existing targets and publication races refuse overwrite. Non-Windows production publication is refused before native callbacks; tests explicitly inject a non-native mover to exercise pure filesystem policy.

## Evidence and limits

- [V20 export API](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/exporting-program-block-as-document) documents the overload, state and exported document list. The prose has singular/plural naming inconsistencies; the code example and existing full engine use `ExportAsDocuments`.
- [V21 export API](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/exporting-program-block-as-document) documents the same overload and exported documents.
- [V20 format scope](https://docs.tia.siemens.cloud/r/en-us/v20/creating-and-managing-blocks/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500): LAD, DB and PLC types, paired code/resource, mixed-language and know-how protection restrictions. This candidate handles blocks only.
- [V21 format scope](https://docs.tia.siemens.cloud/r/en-us/v21/creating-and-managing-blocks/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500-s7-1200-g2/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500-s7-1200-g2): optional multilingual resource, additional languages, attribute limitations and a LAD format change around V20 Update 4. No cross-version roundtrip or full backup claim is made. Unsupported target families/protection/native feature restrictions fail through the native boundary rather than triggering a fallback.

Tests cover zero-write preview, exact release/language refusal, stale hashes, inconsistent plans, reserved/path names, no-overwrite targets, valid pair and V21 code-only publication, missing/extra/escaped/duplicate/empty documents, native/publish failure, target races, retained recovery evidence, strict response identities, explicit registration and poison behavior. Pure passes do not certify Siemens-generated bytes or Windows filesystem publication.

## Static signature check

The parent's authorized read-only metadata inspection of exact V20 and V21 SDK bytes confirmed `PlcBlock.ExportAsDocuments(DirectoryInfo,string)` returns `Siemens.Engineering.SW.DocumentExportResult`, whose read-only `State` and `ExportedDocuments` getters match this source. Enum values are Success=0, PartialSuccess=1, Failure=2; only Success is accepted. This is static member evidence, not a typed rebuild or native execution. Inspection evidence SHA-256: `1ccebbc896351369b2c31c1f38abef6081f3b2de56724137c52fffb56e80f721`.

If publication has an ambiguous outcome, inspect both the reported staging location and the requested output directory before a new explicit session. The route does not claim rollback or that a reported recovery directory necessarily still exists after an unknown move result.
