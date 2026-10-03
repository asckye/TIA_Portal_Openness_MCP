# Scoped standard-provider PLC offline precondition

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

## Result and limits

This change removes the unconditional hardware-taxonomy block from the **selected-target** offline guard used by block/type XML exchange. It does not declare complete XML workflow support, native acceptance, R/H support, or all-device inventory coverage. The separate project-wide compile review gate remains unconditionally blocked. All existing schema, format, ownership, session, mutation and publication guards remain applicable.

The positive case is one selected PLC software object, one matching owning DeviceItem within its selected Device, a non-null standard OnlineProvider on that same owner, and an exactly `Offline` state read immediately before the operation. Object names or service absence are not positive ownership or standard-provider evidence.

## Evidence and scope

- [Siemens V21, major changes in V15.1](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/major-changes/major-changes-in-tia-portal-openness-v15.1?contentId=aTiKwCFH22IZNRJ9ycDo3A), R/H systems section, reviewed 2026-10-02: R/H access is device-level; the standard online/download providers are unavailable on individual R/H CPU DeviceItems, and PLC2 lacks SoftwareContainer. This supports a bounded **positive standard-provider** path. It does not support treating an absent RHOnlineProvider as proof of ordinary hardware.
- `legacy-offline-state-api-evidence.json` records exact release SDK XML hashes and state member presence for 14sp1, 15.1, 16, 17, 18, 19, 20 and 21. Metadata presence is not native execution evidence.
- [Exact V14 SP1 Chinese manual, 05/2017](https://cache.industry.siemens.com/dl/files/163/109477163/att_923000/v1/TIAPortalOpennesszhCN_zh-CHS.pdf), pp255–257, is the CPU OnlineProvider/state evidence; pp422–425 block export, pp428–430 import and pp435–437 UDT exchange provide the target-offline requirement. The earlier V14 10/2016 manual is not substituted for this SP1 source.
- The existing exact-release manual review in `legacy-plc-manual-checklist.md`, offlineStateReview, records V14 SP1 05/2017 status pp255–257 and block/type XML pp422–437; V15.1 status pp257–258; V16 pp425–426; V17 pp523–524. Exact Offline is a conservative adapter restriction relative to the XML requirement that the target not be online. Compile's all-device precondition remains separate.
- V15.1 manual `manuals/v151.txt`, section 7.13.7 (printed p172), documents System.Object.Equals for engineering-object identity, rather than reference or name equality. The ownership comparison uses that mechanism.

## Actual gate

1. Require a reviewed exact release and the Device and DeviceItem captured with the selected software.
2. Where the release exposes RHOnlineProvider, reject positive R/H service evidence. This is exclusion only; a null R/H provider never admits a target.
3. Traverse only the selected Device's DeviceItems, recursively. Read SoftwareContainer.Software and find objects equal to the selected software. Require exactly one match equal to the originally selected DeviceItem.
4. Require a non-null standard OnlineProvider on that verified owner. Read State and require exact ordinal `Offline`.
5. Invoke the existing operation only after the guard succeeds. Service, state, enumeration or equality exceptions propagate and prevent the operation; no exception is converted to Offline.

Missing/mismatched/ambiguous owners, null graph branches, repeated/cyclic nodes and excessive depth fail closed. Other software in the selected device does not add an all-device state requirement. Unknown ownership-read failures are conservative rejections. No project-wide scan is introduced for XML, no generic passive/rack/HMI classification is inferred, and no connection-changing call is made.

R/H XML operations remain explicitly unsupported. No two-sided R/H state observation is used to bypass that exclusion. The older project traversal is still unreachable behind the all-device review gate and does not establish positive inventory coverage.

Engineering Offline does not imply physical CPU STOP. State checking and native execution are not atomic against outside Portal/UI changes. This change does not eliminate that existing race.

## Validation

- Initial scoped .NET host suite: **1681 passed, 0 failed, 1 skipped**, 2026-10-02. Independent reviewer later rebuilt the integrated current suite: **1906 passed, 0 failed, 1 skipped** (includes concurrent additions). Windows-only publication filesystem cases were skipped; no Siemens assembly or worker process loaded.
- Added fake-graph cases: unique nested owning CPU admits; unrelated online PLC does not prevent target-only admission; absent/mismatched/multiple owners reject; repeated/cyclic/deep/incomplete graph rejects; absent standard provider rejects even without R/H evidence; positive R/H rejects; null/online/transitional/wrong-case/empty/numeric state rejects; software/provider/state exceptions reject; distinct equal software/owner wrappers preserve identity; throwing equality fails closed; duplicate equal wrappers reject; unsupported release rejects; successful target observation does not unlock project compilation.
- These tests run the same API-independent traversal and policy used by the typed adapter. They do **not** emulate or validate Siemens SDK service discovery.
- Real exact-release SDK compilation of these changed native adapter sources: **pending separate Windows verification**. TIA execution/native acceptance: **NOT RUN**.

The general hardwareCoverageDesign in the earlier checklist was deliberately broader than this target guard. Its passive/unknown hardware inventory requirements remain relevant to project compilation, not prerequisites for this positively evidenced selected standard-provider case. Earlier statements that every XML target is blocked are superseded only within the scope above.
