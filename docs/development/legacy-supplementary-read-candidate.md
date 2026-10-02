# Watch-table and technology-object read candidates

2026-10-02, unpublished source work. Real implementations of two exact-62 operations; native acceptance and full legacy contract closure remain pending. No user project or TIA process was accessed by this cloud work.

## Manual-first and exact API evidence

[V17 official system manual, 05/2021](https://cache.industry.siemens.com/dl/files/533/109798533/att_1069908/v1/TIAPortalOpennessenUS_en-US.pdf):

- §5.12.4.4, printed pp601–602: `PlcSoftware.TechnologicalObjectGroup`, `TechnologicalInstanceDBGroup.TechnologicalObjects`. Attachment, open project, and selected PLC are prerequisites.
- §6.4.1.24, printed pp1029–1030: watch composition and published read-only Name. The example spells the root property `PlcWatchAndForceTableGroup`; actual supplied SDK metadata resolves the correct property as `WatchAndForceTableGroup`.
- §7.4.4.6, printed p1277: `OfSystemLibElement`/`OfSystemLibVersion` relocated from general to specific attributes, and version changed to `System.Version`. XML meanings are also described in §6.4.2. The later exact PE/XML supplement below confirms both direct public property getters in all eight target releases; dynamic attribute discovery is unnecessary for these two properties.
- §5.12.4.8, printed p605: the composition enumerates top-level TOs. Sub-level objects such as output cams/cam tracks/measuring inputs require different APIs and are excluded here.

[Exact SDK metadata summary](legacy-supplementary-read-api-evidence.json) records parent-relayed read-only desktop findings and eight SDK SHA-256 values. Original report SHA-256 is recorded separately; this summary is not represented as original report bytes. No fake SDK assembly was generated or used.

- Watch root property absent in V14 SP1, present V15.1–V21; exact group type, WatchTables, Groups and Name were verified in metadata.
- Technology root/composition and Name exist in all eight releases; Groups absent V14 SP1–V18 and present V19–V21.
- Later static PE/XML evidence from desktop task `01a0fca8-2831-76f4-a8a9-0bd5a1cc1027`, turn `01a0fda6-f042-7398-85cb-af37ef2d7fc3`, report SHA-256 `218064feefe8607eca95ec414ecde65b282a946a3ef1790e637831801b47abaa`, confirms `TechnologicalInstanceDB.OfSystemLibElement` (`System.String`, getter) and `OfSystemLibVersion` (`System.Version`, getter/setter) in all eight SDKs. Each version adapter compiles the same direct getter calls. This reader never invokes the version setter and no longer relies on `GetAttributeInfos`/`GetAttribute` for either value. Getter exceptions or null values are reported unavailable without invented metadata or native exception text. Static evidence does not verify runtime success.

## Implemented behavior and bounds

- `GetPlcWatchTables` → `ReadWatchTableNames`: V15.1–V21 recursive root/user-group paths, canonical escaping and stable ordinal order. V14 SP1 returns a precise missing-public-API denial before native access. Force tables, entries and live values excluded.
- `GetTechnologyObjects` → `ReadTechnologyObjects`: top-level TO engineering metadata; root-only V14 SP1–V18, recursive root/user groups V19–V21. Response retains `Ok`, `SoftwarePath`, `Count`, `Items`, `Message`. Missing optional attributes are omitted and reported by exact object identity in `Meta.unavailableAttributes`.
- Both use existing project binding, owning STA, and exact software selection. No write or all-project-offline prerequisite is introduced for these read-only calls.
- Canonically escaped paths and strict failure/response behavior differ from permissive legacy behavior, so full contract compatibility is not claimed.
- Duplicate identities, cycles, depth over 128, per-field sizes over 4096, over 10,000 groups/items, and aggregate text budgets fail. Empty group paths consume budget too. Failed native enumeration cannot return partial success.
- Native exceptions are sanitized at a shared production snapshot boundary; no raw SDK/project details or inner exceptions reach worker/MCP errors. Optional getter failures/nulls are represented as unavailable. Returned metadata validation remains outside the getter-error catch, so oversized/invalid values fail the whole snapshot.
- Explicit per-release adapter symbols and source lists select exact known member shapes. The previously omitted software information/tree implementation and policy were also added to adapter shared sources.

## Verification

Real pinned MCP SDK dispatch/schema/response tests plus production pure policies with explicitly fake worker/delegates; no fake Siemens classes. Tests include sentinel-secret errors through policy → real worker codec → actual MCP invocation, canonical paths, optional metadata, empty-group size, depth 129, duplicate siblings, excessive fields, and failed enumeration.

Historical candidate re-review (before the direct-getter correction): **1,177 passed, 0 failed, 1 expected Windows-filesystem skip**; preview host Release build **0 errors, 2 existing nullable warnings**. Those results do not establish verification of later edits.

Direct-getter correction check (2026-10-02): current LegacyHostTests **3,588 passed, 0 failed, 4 expected skips**, with no worker process or Siemens assembly loaded. Six focused pure-delegate checks cover exact version text, one call per getter, null fields, independent getter failures, exception-text suppression, and fail-closed metadata bounds. `Test-SupplementaryReadSources.py` passed for 49 operations at test time (50 on the subsequent source-only rerun as parallel candidates were added) and all eight release-symbol gates, and now guards direct getter wiring, absence of dynamic probing, and absence of metadata setter assignment. These are managed policy/source checks, not typed SDK compilation or native verification.

Real per-version SDK compilation and native acceptance remain pending. No commits or pushes were made.
