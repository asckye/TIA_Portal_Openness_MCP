# Root external-source import planning candidate

Date: 2026-10-02. Exact releases: 14sp1, 15.1, 16, 17, 18, 19, 20, 21.

## Exposed scope

The foundation host exposes **PlanPlcExternalSourceImport**, a separate planning tool. This does **not** migrate or implement the existing full-engine ImportPlcExternalSource mutation tool. V20/V21 full-engine routing is unchanged. It creates no sources and is not a completion claim for import, generation, deletion or a manual migration row.

Inputs: exact `softwarePath`, empty-string `groupPath` (root only), canonical absolute Windows drive `filePath`, identical case-sensitive `allowedFilePath`, `dryRun=true`, optional `expectedPlanHash`, `confirm=false`, `expectedProjectFile`. The allowlist has exactly one explicit file. The candidate requires an already bound ordinary project, explicit attached process, exact software/root identity and a scoped offline PLC check. Local sessions, aliases and software units are excluded. Offline-only is a wrapper policy, not a documented prerequisite of the CreateFromFile topic.

`dryRun=false` always fails, including with confirmation and matching project/hash, before native lookup or filesystem access. The host rejects it before worker dispatch and the policy independently rejects it. There is no CreateFromFile call, mutation callback, alternate name, native fallback, delete, generation, compile, save or download in this slice.

## Validation and result

- One existing regular local file, 1..4,194,304 bytes; AWL/SCL/DB/UDT only. No directories, UNC/device namespace, glob, traversal, alternate data stream, reserved Windows name, trailing-dot/space or noncanonical path. Reparse-point ancestry is refused.
- Requested name is the filename including its extension. This deterministic wrapper choice is **not** a native name-normalization guarantee.
- Bytes remain unchanged. Conservative ASCII printable bytes plus TAB/CR/LF are accepted; BOM, NUL, DEL, high bytes and other controls are refused. This is wrapper validation, not evidence of native syntax/encoding support.
- The existing FileShare.Read pattern holds an input stream against Windows write/delete while validation, inventory/identity recheck and rehash occur. Production file validation refuses non-Windows hosts. Linux tests inject memory streams and do not claim Windows filesystem/lock acceptance.
- Complete bounded root enumeration (maximum 4,096) must succeed; truncation, enumeration errors and ambiguous identities fail. Exact, case-fold and stem/extension collisions fail. A second complete snapshot and target identity check must agree; bytes are rehashed using the held stream.
- SHA256 binds exact release, project, process ID, software/root, ordinary-project session kind, file/allowlist, source name, bytes, wrapper policy and sorted complete source-name inventory. A nonempty expectedPlanHash requires confirm=true, expectedProjectFile and an unchanged plan. This is reviewed preview validation only.
- Result says `Status=planned`, `MutationStatus=notAttempted`, `Attempted=false`, `Executed=false`, `CreatedCount=0`, `ApplyBlocked=true`. Generation, compilation, save and download are `notRun`. It exposes file hash/size, target identity, naming policy and collision snapshot limit. Host response checks reject changed scope and fabricated success/counts.

## Evidence and remaining gates

The official external-source mutation audit of 2026-10-02 reports static signature verification in all eight SDKs: `PlcExternalSourceComposition.CreateFromFile(string name, string path) -> PlcExternalSource`, Find, Delete, Name, Parent and parameterless GenerateBlocksFromSource. Signature evidence SHA256 `58ca48e3f85e70280d66118479416dde7d3830425e189d54f5c8612a6ccdc6bc`; XML-scope evidence SHA256 `d77773b6817fc0797f825ef1745e9d323e4cd332e7f77cc5a789727bd655aa8b`. These are provenance supplied by the read-only desktop audit, not a native result from this cloud task.

[Siemens V20 adding an external file](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/adding-an-external-file) and [V21 topic](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/adding-an-external-file) describe root creation, supported extensions and no source subgroup access. The complete eight-release audit remains distinct from the repository's older navigation-only evidence.

Duplicate-name behavior, case/extension normalization and atomic no-overwrite remain UNKNOWN. Two matching inventories, ordinary filesystem locks and offline checks are not native race protection. Path ancestry checks also do not establish a native source-file lifetime/consumption guarantee. Thus all releases stay apply-blocked. Exact SDK compilation and disposable-project native acceptance remain NOT RUN, including Windows source locks, returned identity/Parent, encoding/parser behavior, no block changes, collisions/concurrency and failure/disconnect outcomes. The native source has no documented Path/FilePath/SourcePath property; no native stored-byte/path readback is claimed.

## Pure-cloud checks

Dedicated test project: `tools/tiaportal-mcp/tests/TiaMcpServer.ExternalSourcePlanTests`. It covers exact releases, path/allowlist, encoding/size, collision and full-inventory guards, identity/hash changes, immutable byte rehash, disposal and apply refusal before I/O. Host integration tests cover closed result shape, identity binding, zero-success claims and confirmed apply refusal before dispatch.

No Siemens runtime, TIA process, project, PLC or native API was executed. A cloud pass does not replace Build-Release or native acceptance; publication remains outside this task.
