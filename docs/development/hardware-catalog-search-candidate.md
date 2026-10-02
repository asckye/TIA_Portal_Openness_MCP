# Bounded hardware catalog search candidate

Status: source/pure-test candidate. Exact current Windows SDK rebuild and native acceptance are NOT RUN. No ledger coverage increment is implied by this document.

## Release evidence and gate

- V14 SP1, V15.1, V16, V17: static metadata proves `TiaPortal.HardwareCatalog`, `HardwareCatalog` and `CatalogEntry` absent. Fail closed before session access.
- V18: static metadata proves the members below exist. Supplied German manual lists read access as a new feature but detailed method semantics were not located. Execution remains gated; this is a semantics gap, not an API-absence claim.
- V19: enabled source candidate. Official system manual 11/2023, pp344–346, section “Accessing the TIA Portal hardware catalog,” documents the query and all seven fields.
- V20: enabled source candidate. [Official page, 01/2025](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-devices/accessing-the-tia-portal-hardware-catalog).
- V21: enabled source candidate. [Official page, 03/2026](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-devices/accessing-the-tia-portal-hardware-catalog).

Static metadata result: task `01a0fca8-2831-76f4-a8a9-0bd5a1cc1027`, turn `01a0fd77-55b9-7632-8847-1c339c26151b`; `catalog-signatures/evidence.json` SHA-256 `1998e9efa39752e161ba8da726cf8a27d626571cb25df37562fa7aa0b67dc703`. This is metadata evidence, not SDK execution or native acceptance. The report confirms V18–21:

- `TiaPortal.HardwareCatalog` has public read-only type `Siemens.Engineering.HW.HardwareCatalog.HardwareCatalog`.
- Its `Find(System.String)` returns `IList<Siemens.Engineering.HW.HardwareCatalog.CatalogEntry>`.
- `CatalogEntry.ArticleNumber`, `CatalogPath`, `Description`, `TypeIdentifier`, `TypeIdentifierNormalized`, `TypeName`, `Version` have public read-only `System.String` getters.
- V18–20 core: matching major `.0.0.0`, public key token `d29ec89bac048f84`. V21 uses `Siemens.Engineering.Base,21.0.0.0`, token `29bfe5fdf4ba5d3b`.

## Contract and safety boundary

`SearchHardwareCatalog(keyword, limit=50)` preserves the original Keyword/Count/Items/Message/Meta envelope and candidate fields. Insertable and Score remain unknown; no insertion probe or score is invented. It uses one documented literal query and native enumeration order. It does not retain the full engine's undocumented reflection fallback/filter expansion or exception swallowing.

The manuals require connection and an open project. The adapter checks an already explicit project binding and attached session. It never opens, selects or binds a project automatically. This is stricter than the old full engine's attachment-only preflight exemption.

Client bounds: keyword ≤256 characters; nonempty substantive text, no controls or wildcard-only query; limit 1–100; at most 1,000 enumerated entries including duplicates; each returned field ≤16,384 characters; aggregate candidate text ≤262,144 characters. Exact identifiers are never normalized, rewritten or truncated. Duplicates compare exact original TypeIdentifier using ordinal equality. Truncation is explicit with a reason and completeness flag. Reaching the traversal budget is conservatively incomplete even if the next MoveNext might have exhausted the sequence.

The synchronous native Find API exposes no result limit or cancellation argument. These bounds constrain client traversal and response construction, not native query materialization or duration. Enumeration/getter failures fail the operation rather than returning an empty-success or partial-success envelope. Enumeration is disposed even on early exit. Host validation rejects unexpected fields and conflicting bounds, identities or completeness evidence.

No AddDevice, CreateWithItem, CanPlugNew, repair/install, process launch, online access, PLC access, project mutation or SDK execution is part of this work.

Dedicated pure policy/host-contract/typed-adapter-fake suite: 56 checks. The fake adapter proves source call ordering and mapping only, not SDK compatibility. Shared host dispatch and source inventory must also pass after aggregate integration.

Aggregate baseline before the subsequent delete-route work: LegacyHostTests **3,237 passed, 0 failed, 4 skipped**, completed 2026-10-02 16:50:42 UTC by the document-route worker. This includes real MCP catalog dispatch checks and adapter-source inventory checks. Catalog dedicated suite independently reviewed/rerun: **56 passed**. Later edits require a fresh aggregate; these counts do not certify a changed source tree.
