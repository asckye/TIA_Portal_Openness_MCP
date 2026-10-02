# Offline UDT and tag-table builders: bounded candidate

Manual-first review, 2026-10-02. Selected tools from the pinned 62-tool profile: BuildPlcUdtXml and BuildPlcTagTableXml. No additional tool names, Siemens API calls, worker operations, project writes or imports are introduced.

## Evidence and decision

- The supplied ledger preserves the pinned 70758f2b7359021fefb674c099c1c8eddbba44e1 V17-profile wrapper parameters and ResponseXmlBuild properties. That commit object/wrapper is not present in this cloud checkout; source equivalence to that commit has not been independently re-established here.
- Existing source helpers PlcUdtXmlBuilder and PlcTagTableXmlBuilder deliberately emit Engineering version V21. UDT uses Interface/v5. Existing current-tool descriptions also explicitly say V21.
- V17 system manual (05/2021), section 6.4.1.1, printed p942, states that higher-version XML is not import-compatible with a lower product version. Locally read from the official manual downloaded earlier: https://cache.industry.siemens.com/dl/files/533/109798533/att_1069908/v1/TIAPortalOpennessenUS_en-US.pdf
- The [official V21 XML interface chapter](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/xml-structure-of-the-block-interface-section?contentId=TGNd5apTdFB9WBWcrgOXXg), read before implementation, confirms the version restriction and Section/Member ordering, required Name/Datatype and optional comments.
- The [official V21 tag-table import chapter](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/tag-tables/importing-plc-tag-table), read before implementation, documents an XML-file import into a chosen tag-table composition. It does not certify the hand-generated document.
- No official target-installed XSD or independent native fixture was available here. Well-formed XML is not schema validity or import acceptance.

## Safe contract

The host exposes real, pure in-memory XML generation through the existing two helper implementations. A new required outputReleaseKey must equal the exact string 21; 14sp1, 15.1 and 16–20 are rejected, not relabeled. Host selection does not choose an output format: any isolated host can produce the explicitly requested V21 candidate XML without loading a worker.

The original JSON argument and ResponseXmlBuild envelope fields remain. Input parsing is deliberately stricter than the historical adapter: exact property names and documented aliases, no unknown/duplicate properties, no conflicting aliases, no scalar-to-string coercion, bounded JSON/array/string/XML sizes. Raw XML supplied inside strings is escaped as data. Unsupported features must be rejected rather than silently discarded.

Successful results mean XML construction and parsing succeeded only. Both top-level warnings and metadata declare schemaValidated=false and importValidated=false. XML is never written to a file or sent to TIA. Only flat members and root tag tables are covered; no nested types, constants, online addresses/values, automatic imports, software-unit scope or cross-version conversion is implied.

Status remains partial for both tools. Native acceptance NOT RUN, target XSD verification NOT RUN, exact pinned implementation comparison pending. This does not increase seven completed contract migrations.

## Verification

- Full .NET 8 preview-host build: passed, zero errors and two existing TiaVersionCatalog nullable warnings.
- LegacyHostTests: 1096 passed, zero failed, one existing Windows-only atomic-publication skip. Includes 118 new checks through the real MCP SDK and direct helpers: response surface, exact output gate, duplicate/unknown fields, aliases, type coercion rejection, member/string/JSON/XML bounds, cancellation, XML escaping/entity-shaped text, determinism and honest validation metadata.
- Actual stdio MCP subprocess calls: 48 passed across eight exact host release keys (14sp1, 15.1, 16–21). Both builders generate explicit V21 candidate XML and reject V17 output requests. No --native-session, worker executable or PublicAPI supplied.
- Existing pure helpers are linked into the host unchanged; their RunProbe/file-analysis methods are not reachable from these two registered tools. The adapters call only BuildXml in memory.
- No Siemens SDK compile, matching Windows eight-release worker build, native TIA connection, import, XSD validation, commit or push occurred. Build-Release requires Windows and supplied licensed PublicAPI assemblies unavailable here.

Reproduction:

    DOTNET_CLI_HOME=/workspace/shared/dotnet-home NUGET_PACKAGES=/workspace/shared/dotnet-home/.nuget/packages /workspace/shared/dotnet/dotnet build tools/tiaportal-mcp/src/TiaMcpServer.LegacyHost -c Release --no-restore
    DOTNET_CLI_HOME=/workspace/shared/dotnet-home NUGET_PACKAGES=/workspace/shared/dotnet-home/.nuget/packages /workspace/shared/dotnet/dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.LegacyHostTests -c Release --no-restore
    python scripts/checks/Test-LegacyOfflineBuilders.py --dotnet /workspace/shared/dotnet/dotnet --host tools/tiaportal-mcp/src/TiaMcpServer.LegacyHost/bin/Release/net8.0/TiaMcpServer.LegacyHost.dll

Closure requires licensed Siemens XSDs from the exact intended target installation, checked together with all their includes/imports: SimaticML Document/object definitions for SW.Types.PlcStruct and SW.Tags.PlcTagTable/SW.Tags.PlcTag, plus the exact Interface/v5 schema used by this UDT generator. Do not infer their filenames or availability from a version marker. Validate full documents, reconcile emitted attributes/options/comments with that release, and run separately authorized isolated-project import acceptance. Earlier-version generation requires its own evidence and implementation, never a marker rewrite.

Aggregate repository checks: Check-DeadToolReferences passed. Check-Repository found only five missing Windows distribution-binary entrypoints (runtime/v20, runtime/v21 and configurator), expected in this source-only cloud checkout. Full Build-Release and bundle validation remain blocked by those absent licensed/build-time Windows prerequisites; no manifest hashes were edited.

Independent-review correction: public invalid-input/internal-error messages are fixed and bounded; input-derived native helper messages and inner exception chains are not exposed. Five MCP regressions and sixteen real stdio checks prove secret-like property/member/tag names are not echoed in errors.

Independent post-fix verification rebuilt the host and reproduced 1096 passing SDK/helper checks (one Windows-only skip). A separate real stdio script passed 40 invocation checks plus discovery across all eight host keys, verifying member/tag ordering, explicit V21 marker, false schema/import flags, rejection of V17 output, and sanitized public errors. No remaining code blockers were identified; candidate-schema/import limitations remain.

## Supplemental desktop fragment-schema evidence (2026-10-02)

This later evidence supplements the initial cloud-only checks above. It does not validate either complete generated document, establish import acceptance, or change runtime flags or migration closure. The parent relayed results from the separately authorized read-only desktop schema investigation; no XSD files were transferred to this cloud workspace.

The desktop investigation inventoried 625 user-supplied XSD files, of which 622 dependency closures compiled. No full SimaticML Document, UDT-object or tag-table-object root schema was found in that supplied set. For the V21 UDT interface fragment, `SW.InterfaceSections_v5.xsd` has no target namespace. A chameleon `xs:include` wrapper bound to `http://www.siemens.com/automation/Openness/SW/Interface/v5` validated one original generated UDT Sections sample without stripping its namespace, rewriting the XML or changing the supplied XSD. Negative checks rejected missing required attributes, unknown children, a wrong namespace and DTD-bearing input. MSXML schema-include documentation supports the wrapper mechanism; no Siemens validator-usage evidence was found for this wrapper.

Reported SHA-256 identities:

| Evidence | SHA-256 |
|---|---|
| V21 SW.InterfaceSections_v5.xsd | `6e3dfc80d9709b0a0497224b2bb269fe34322659cd2c98e92bb68a99d71901e8` |
| SW.Common_v3 dependency | `183d17bcdab57ec465c2ff744fafb94a89caf217ad642562e68b740d18966f53` |
| Validated original UDT Sections sample | `47ca565813ee2c6d89c4251ed9054ff08e024484a45c2027f1a65a7aa01a155b` |
| Desktop result JSON | `4ba5d57c71f728686c0540c27ef8a80d770af51a2464bff3a12bba12bd7235f8` |
| PlcUdtXmlBuilder.cs | `38b4769861d7eb0b582e6aad37695513728c919fb3c80402ece1d3bb512c67cf` |
| PlcTagTableXmlBuilder.cs | `9009597a0e74afec76b7189ec8c7a69e9fefef1a784fbff49ca80bd7a1ffcbba` |

The parent verified that both helper-source hashes match the desktop checkout HEAD; this worker independently rehashed the cloud copies and obtained the same two values. Schema/sample/result hashes above are desktop-reported provenance, not independent cloud reads of those artifacts.

This closes only the schema check for that concrete Sections fragment example under the stated wrapper. It does not prove validity of all future member/datatype/comment combinations, the outer UDT document, any tag-table document, or any earlier-version output. Complete target-document XSD validation and separately authorized import acceptance remain outstanding. `schemaValidated=false` and `importValidated=false` remain correct for tool results. No Siemens SDK execution or native TIA access occurred for this supplemental check. No runtime source, ledger counts, version coverage or completion flags were changed by this documentation update.
