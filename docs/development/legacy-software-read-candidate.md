# Software information and tree: manual-first candidate

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

Reviewed 2026-10-02 before implementation. Scope: two previously pending V17-profile operations, `GetSoftwareInfo` and `GetSoftwareTree`. Native execution remains disabled; no Siemens DLL is available in this cloud workspace. Source/pure-contract tests are not typed API compilation or native acceptance. No completion count is changed by this document.

## Official evidence

- [V14 SP1 system manual, 05/2017](https://cache.industry.siemens.com/dl/files/163/109477163/att_923000/v1/TIAPortalOpennesszhCN_zh-CHS.pdf), sections 7.10.9–7.10.10, printed pp112–115: engineering attribute self-description/read, `SoftwareContainer` acquisition, `Software`/`PlcSoftware.Name`. Requires attached application and open project.
- [V17 system manual, 05/2021](https://cache.industry.siemens.com/dl/files/533/109798533/att_1069908/v1/TIAPortalOpennessenUS_en-US.pdf), sections 5.3.8 (pp131–132), 5.12.3.4–5 (pp568–570): software acquisition and recursive user-block groups/blocks. Requires attachment, open project and determined PLC. This is an engineering read; compilation, offline CPU state and write approval are not generic prerequisites.
- Existing `legacy-six-read-api-evidence.json` records exact XML documentation paths, SHA-256 and member presence per selected release for block/type roots, groups, compositions and names. Existing `legacy-object-info-api-evidence.json` records object attribute properties. These are inherited versioned evidence, not a new cloud SDK inspection. Only members already called by `PlcFoundationEngine.cs` / `PlcReadContracts.cs` are used. No new Siemens assembly/reference is introduced.

## Version and scope limits

Candidate keys: 14sp1, 15.1, 16, 17, 18, 19, 20, 21. Original V14 and V15 are excluded. Existing framework and exact assembly-identity checks remain unchanged (V21 Step7 split included). V14 SP1 and V17 chapters were directly read for this change; do not call this full per-release manual reconciliation. Matching Windows PublicAPI rebuild for all eight keys and native read acceptance are still required.

Ordinary PLC only. Information returns Name, Attributes and Description; it does not invent language/version/block-count fields. Tree includes root/user block and type groups, retaining empty groups; excludes system block groups, software units, tag tables, external sources and technology objects. Missing or ambiguous software paths fail through the existing exact/unique-alias policy. Native enumeration failure or unavailable required block/type root aborts the whole tree; no partial tree is a success. Snapshot traversal is capped at 10,000 nodes and one Mi character of names/paths/display; rendering is capped at two Mi characters. Optional display attributes may be unavailable and are reported in metadata. No write, compile, import, source generation, live PLC value read or file export occurs.

## Contract and validation

Implemented and registered in the current v1 preview host/worker allowlist on 2026-10-02, with parent-approved narrow seam ownership. The separate protocol-v2 candidate was not changed. Native calls remain disabled by default. The new response wrapper preserves the pinned V17 top-level `Message`, `Meta`, `Name`, `Attributes`, `Description` or `Tree` fields with SDK naming policy, and includes exact PLC/object addresses and explicit scope in Meta. Tree display is not a path parser. `Meta.softwarePath` selects the PLC; each `Meta.paths` entry has a full identity `path` plus `kind`, and a root-relative `objectPath` to pass as the named `selectorParameter` (`blockPath`, `typePath`, or `groupPath`). Same-name group/object siblings remain distinct by kind.


## Verification results (2026-10-02)

- Standalone `TiaMcpServer.SoftwareReadTests`: **39 passed**. Package-free .NET 8 tests cover formatting/empty groups, escaped paths, duplicate identity/depth guards, ambiguous/missing selection, wire naming, opaque attribute values, null/malformed payloads, honest status metadata, worker path boundary, and actual new source control flow using explicit native-shape test doubles. The fake types are not Siemens SDK evidence.
- Existing `TiaMcpServer.LegacyHostTests`: **978 passed, 0 failed, 1 skipped**. New real MCP SDK dispatch checks verify both operations, single dispatch, exact parameter contract, SDK naming policy, malformed response rejection and preservation of -32602/-32603 read errors. The skip is the existing Windows atomic publication filesystem case; no worker process or Siemens assembly was loaded.
- No matching Siemens SDK compilation, Windows full release build, eight-version typed regression, native attachment, project read, commit, push or deployment was performed.
- Current per-release candidate state is source-implemented/contract-tested with typed rebuild pending, not full migration closure. Do not include these two in the seven already-closed tools until remaining gates pass.

Reproduce package-free checks:

`DOTNET_CLI_HOME=/tmp/software-read-dotnet /workspace/shared/dotnet/dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.SoftwareReadTests -c Release`

Existing SDK packages were read from `/workspace/shared/dotnet-home/.nuget/packages` for host tests; no downloads or Siemens DLL access are required.

- Full preview-host .NET 8 build **passed, 0 errors, 2 existing nullable warnings** after project-scoped restore from official `https://api.nuget.org/v3/index.json`. Version pins were unchanged; HTTP/package caches were explicitly in writable cloud workspace paths. Initial offline restore lacked the pinned Hosting package; the authorized official restore resolved that blocker. This host build does not reference Siemens SDK assemblies and is not the Windows typed worker build.

Independent review identified null-root completeness, output budget, and directly reusable object-selector issues; all were addressed with regression tests. The real worker serializer/date/scalar matrix also passes.

Final independent source re-review found no remaining blocker in this bounded candidate. The wrapper rejects unknown kinds, wrong collection/selector mappings, missing or inconsistent relative selectors, noncanonical escaping and duplicate kind/address identities. Review does not supply missing Siemens SDK or native acceptance evidence.
