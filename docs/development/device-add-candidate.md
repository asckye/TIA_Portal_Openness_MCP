# Exact catalog device-add source candidate

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

Status: partial source candidate, **not original fallback compatibility**. Current exact Windows SDK rebuild and native acceptance are **NOT RUN**. No Siemens assembly or native project was loaded for these tests.

## Bounded contract

`AddDeviceWithFallback(preferredMlfb, preferredVersion, deviceName, family, dryRun=true, expectedPlanHash="", confirm=false, expectedProjectFile="")` retains the original four input names but deliberately removes automatic article/version guessing and insertion probes. It returns a distinct `PlcDeviceAddResult`, not the original `ResponseDeviceProbe`/Attempts envelope. Family-only calls, missing versions, approximate input, unknown models and HMI are refused.

Selection is exact installed-catalog ArticleNumber+Version, or an exact full TypeIdentifier with an empty version input. No space, case, version or identifier normalization is performed. The selected catalog identifier must equal `OrderNumber:` plus the exact selected article and version. The final native call uses the original selected identifier unchanged.

Positive admission is deliberately limited to two standard PLC models:

- S7-1200 CPU 1211C AC/DC/relay: `6ES7211-1BE40-0XB0` (catalog spaced representation `6ES7 211-1BE40-0XB0` also explicitly allowed). [Official Siemens product details](https://mall.industry.siemens.com/mall/en/riederpyown/Catalog/Product/6ES7211-1BE40-0XB0).
- S7-1500 CPU 1513-1 PN: `6ES7513-1AM03-0AB0` (catalog spaced representation `6ES7 513-1AM03-0AB0` also explicitly allowed). [Official Siemens product details](https://mall.industry.siemens.com/mall/dz/DZ/Catalog/Product/?mlfb=6ES7513-1AM03-0AB0).

These product pages establish model/family admission, not native insertion compatibility or firmware availability. A version must be explicitly requested and present in the installed catalog; no fixed version is inferred from a product page. Unknown types including Unified/HMI and safety variants fail before creation. The article spelling that matches the installed catalog must be supplied exactly.

Preview binds release, explicit attached process, absolute project file, project object identity, request, exact catalog selection, and the complete device/group identity graph. Root, grouped and ungrouped devices are inspected; all device/group parent identities are checked. Device moves, renames, replacement and empty group changes invalidate the preview. Device names collide case-insensitively and ambiguous existing duplicate names fail closed. Creation is at the project root only, with the same explicitly requested device/item name.

Apply requires the preview hash, `confirm=true`, and the exact expected project. Catalog and inventory are reread before one `CreateWithItem(identifier, name, name)` invocation. Post-verification requires the returned new device identity and exactly one added device with no other inventory changes. Any exception, partial creation, auto-rename, unknown parent or failed verification after entry yields `outcome-unknown` and poisons the shared host/worker mutation session. No retry, fallback, rollback, deletion or repair is attempted. Normal host requests terminate after unknown outcome; direct worker read-only operations retain existing protocol behavior, but further mutation is prohibited.

Bounds: catalog traversal 1,000 records, graph 4,096 device/group entries, group depth 32, device/group name 128 characters, stable identity registry 16,384 entries, native error diagnostic 2,048 characters. The synchronous native catalog Find has no server-side duration/materialization limit; these are client bounds. No automatic save, compilation, download, online access, configuration/security changes or runtime launch.

## Release and API evidence

V19–21 only, inheriting the [catalog evidence/gates](hardware-catalog-search-candidate.md). V14 SP1–17 catalog API absent; V18 metadata exists but catalog semantics remain gated. CreateWithItem existing in older releases does not remove that selection gate.

- V19 official system manual 11/2023, §§5.9.6–7, pp338–342: creating a device and enumerating root, user-group and ungrouped devices.
- [V20 creating a device](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-devices/creating-a-device), [enumerating devices](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-devices/enumerating-devices), 01/2025.
- [V21 creating a device](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-devices/creating-a-device), [enumerating devices](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-devices/enumerating-devices), 03/2026.

Static metadata across all eight releases: child turn `01a0fd9b-fd43-7595-979b-c5831bdeadfa`, evidence SHA-256 `248a05417687bd97e411ac0632b65882d0fe9ab3f8899db8c81b353cd8c896bf`: Project Devices/DeviceGroups/UngroupedDevicesGroup; Device Name/Parent; group Devices/Groups; DeviceComposition CreateWithItem(string,string,string)->Device. Group Name/Parent getters: child turn `01a0fda5-87e1-7688-a0f8-48ba7a0f06f5`, `group-name-parent.json` SHA-256 `0f592e241fca78fe581835236a9ace21d87ae8965ac6db16a734a0451b9fdafd`. This is static evidence only. Native duplicate handling, auto-renaming, rollback, proxy equality, device-item naming and all runtime behavior remain unverified. `object.Equals` is used rather than reference equality, but only native acceptance can verify proxy behavior.

## Validation

Dedicated `TiaMcpServer.DeviceAddTests` links the actual policy, adapter and host contract against pure typed fakes. It checks read-only preview, exact selection, unsupported releases/models, invalid inputs, collisions, complete graph ownership, graph changes, one call, uncertain partial outcomes and no replay. `LegacyHostTests/DeviceAddDispatchTests` checks actual MCP facade dispatch and shared host poisoning. These prove source/policy behavior, not SDK compatibility or native acceptance. Current counts are recorded by the final integration report, not a native capability claim.

Integration verification (2026-10-02 17:31 UTC): **124 dedicated checks passed**, independently rerun by reviewer; **3,582 LegacyHostTests passed, 0 failed, 4 explicit skips** before the next batch-document-import integration. `git diff --check` passed. These counts apply to this integration checkpoint only. Success verifies device identity/name/parent and the device/group graph delta; it does not read back inner DeviceItems or prove device-item naming/type, firmware compatibility, proxy equality or native acceptance.
