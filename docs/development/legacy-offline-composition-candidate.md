# Offline GlobalDB and StructuredText candidates

Manual-first review, 2026-10-02. This migrates two existing pinned-profile names, `BuildPlcGlobalDbXml` and `BuildStructuredTextXml`, to the isolated .NET 8 host. It adds functional bounded in-memory construction, not placeholders. These remain candidates: no Siemens worker, PublicAPI assembly, TIA project, import, external XML file or network is used by either tool.

## Evidence and version boundary

- The [official V21 block-interface XML chapter](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/xml-structure-of-the-block-interface-section?contentId=TGNd5apTdFB9WBWcrgOXXg), read before implementation, defines Sections/Section/Member, required Name/Datatype, and ordered member children. It also rules out importing higher-version exported XML into an earlier product.
- The [official V21 structured SCL export/import chapter](https://docs.tia.siemens.cloud/r/es-es/v21/tia-portal-openness-api-para-la-automatizacion-de-flujos-de-trabajo-de-ingenieria/exportacion/importacion/importacion/exportacion-de-datos-de-un-dispositivo-plc/bloques/exportacion/importacion-de-tipos-estructurados-de-bloques-scl), read before implementation, describes symbol components and dot-token separation. It does not certify every generator input or program correctness.
- Existing GlobalDB helper emits Engineering V21 and Interface/v5; StructuredText emits StructuredText/v4. The additional exact `outputReleaseKey="21"` is required, independent of host release. No marker or namespace rewrite implements earlier versions.
- No target-installed XSD was available to this cloud implementation. Earlier evidence for a different UDT Sections sample does not validate this GlobalDB sample, complete document, StructuredText fragment, or future combinations.

### Narrow helper correction

The manual's block-interface Basic structure says its description sequence is required; its member descriptions list AttributeList, StartValue, then Comment. Existing `PlcGlobalDbXmlBuilder.BuildMember` emitted Comment before StartValue, creating a discrepancy when both were populated. The authorized fix only moves the existing StartValue conditional ahead of Comment. Values, escaping, namespaces, defaults and other code are unchanged. A regression asserts exact combined-child order. This aligns the manual narrative; it is not a claim that the prior order failed XSD validation. Native acceptance and complete-document validation remain unverified.

| Helper | SHA-256 |
|---|---|
| GlobalDB before correction | `13fc61534ef73be6472e061c973a405dac131072fe05960e68594b8931a75d79` |
| GlobalDB after correction | `24b9992001f9d22831e2c67cf883aa620a53d72c0be4d9bdb06758d7c50eec43` |
| StructuredText, unchanged | `2104d18d825a9c8ef81d03cc8058a178ad14073acc1f336f16df68c4914b5da3` |

Historical file/probe methods in these helpers are not exposed or invoked by the adapters.

## Contracts

`BuildPlcGlobalDbXml(globalDbJson, outputReleaseKey)` accepts a JSON string:

```json
{"dbName":"SampleDb","dbNumber":42,"staticMembers":[{"name":"Ready","datatype":"Bool","externalWritable":false,"startValue":"TRUE","commentZhCn":"Sample"}]}
```

Aliases: dbName/name, dbNumber/number, staticMembers/members, datatype/dataType, commentZhCn/comment/commentZh. Supply only one spelling per group. dbNumber is a positive Int32. Members are flat; externalWritable is an optional boolean, distinguished from omission. Present null is rejected. Member names are nonempty and case-insensitively unique through the helper. Datatype and StartValue strings are not PLC-semantically validated.

`BuildStructuredTextXml(structuredTextJson, outputReleaseKey, innerOnly=false)` accepts:

```json
{"firstUid":21,"operations":[{"op":"assignment","target":"#Count","value":"1"}]}
```

Default output retains the StructuredText/v4 namespace. `innerOnly=true` returns exact inner helper bytes; callers must supply that namespace context. Well-formedness is checked under that context without namespace stripping. This fragment is not a block document.

Supported operation fields:

- Selector: exactly one op/kind/type; selector values are trimmed and case-insensitive.
- if/ifheader and elsif/elseif/elsifheader: condition/conditionVariable/variable; optional indent.
- else and endif/end_if: optional indent.
- assignment/assign: target and exactly one source/fromSymbol or literalValue/value; optional indent. Conflicting source and literal are rejected.
- token: text, optional indent. Always escaped; never raw XML.
- blank: count defaults to 1, range 1..4096. newline/new_line: no additional fields.
- global/local/symbol: name, optional indent. global accepts unquoted dot-separated components; local a single identifier; symbol local names/paths with optional # or one entirely quoted global path. Mixed quotes and quoted local names are rejected because the helper cannot represent them faithfully.
- literal: value/literalValue, optional indent.
- line: items, optional indent. Each item contains exactly one sym/token/lit/raw. raw is an escaped token without automatic blanks, not XML. Spacing/termination use the existing JSON adapter convention.

firstUid defaults to 21 and is bounded to 1..1,000,000,000; output bounds provide ample integer-rollover headroom. indent is 0..4096. No control-flow, type, declaration or SCL semantic correctness is claimed. Unmatched control-flow operations and arbitrary tokens can still be generated.

## Guardrails and response

Exact property names, unknown/duplicate rejection, alias-collision rejection and strict scalar types. JSON maximum 262,144 characters and depth 16; arrays 1..1,000 items; strings at most 4,096 characters; output at most 1,048,576 characters. XML characters are checked; strings are escaped by helpers. To avoid silent XML parser normalization, StructuredText attribute contexts (tokens and all names/symbols) reject CR/LF/tab, and element-content contexts reject CR. This includes StructuredText literals plus GlobalDB dbName/comment/startValue. Supported LF/tab in element text are preserved exactly. GlobalDB attribute whitespace remains supported because its XDocument writer entitizes it. Nonempty whitespace-only optional GlobalDB comments/start values are rejected rather than silently dropped; absent or empty strings omit these optional elements. StructuredText expansion is bounded during construction, including periodically within line items. Generated XML parsing prohibits DTDs, has a null resolver and a character limit. No XML input path exists and no user-file reads were tested.

ResponseXmlBuild retains Message, Meta, Ok, Data, Errors, Warnings, OutputPath, OutputFiles and Xml under the SDK naming policy. File outputs are null. Construction sets xmlParseOk=true, but schemaValidated=false, importValidated=false and programSemanticsValidated=false. Validation scope distinguishes GlobalDB document well-formedness from StructuredText fragment well-formedness. Fixed public errors omit input values and exception chains. Cancellation propagates.

`BuildPlcSymbolManifestFromXmlPath` remains pending. Its direct file loads and broad directory enumeration first need approved path scope, bounded streaming, explicit limits, prohibited DTDs and null resolution. This batch does not expose it.

## Verification

`OfflineCompositionTests` uses real SDK tool classes and in-memory fixtures: helper equivalence, ordering, optional booleans, namespaces, innerOnly, UIds, aliases, operation families, escaping, expansion limits, malformed/unknown/duplicate/conflicting inputs, version rejection, sanitized errors and cancellation. It joins the existing console suite; `dotnet test` does not run it.

```sh
dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.LegacyHostTests/TiaMcpServer.LegacyHostTests.csproj -c Release
python3 scripts/checks/Test-LegacyOfflineComposition.py --dotnet /path/to/dotnet --host tools/tiaportal-mcp/src/TiaMcpServer.LegacyHost/bin/Release/net8.0/TiaMcpServer.LegacyHost.dll --evidence-dir /new/owned/test-output
```

2026-10-02: the integrated LegacyHost console suite passed 1,655 checks with 0 failures and 1 Windows-only publication skip; the general offline console suite passed 3,034 checks with 0 failures or skips. These aggregate counts include other concurrent host work, not just these two builders. Separately, 128 real stdio MCP invocation checks passed across all eight host keys. No worker, native-session flag, PublicAPI or user XML path was supplied. The new evidence directory contains owned generated samples; these are not deployed artifacts. Complete Windows Build-Release, target XSD validation and native import acceptance remain separate gates.

| Owned generated sample | SHA-256 |
|---|---|
| GlobalDB complete document | `3fa08415240eb25cfbf81da9225ca84f1fca401f52b5be7f89893bea9672aaf2` |
| Extracted GlobalDB Sections, namespace preserved | `73efcbecc7214728c14abe1f2db312108112e719b9e8ee5db8353fb37bcca2b7` |
| StructuredText fragment | `28c46e75c6d7fa165520bc0d8d4d38c0dbc35f323c91a73c22b6b368b3d02bc2` |


## Supplemental desktop fragment evidence

The parent relayed a separately authorized desktop check of the concrete GlobalDB Sections sample against the user-supplied V21 Interface/v5 schema closure. Both the original AttributeList/Comment/StartValue ordering and the reordered AttributeList/StartValue/Comment ordering passed with zero schema errors and warnings. The supplied XSD uses a repeating choice for StartValue and Comment, so both orders are accepted. The code change follows the manual narrative; this check did not expose an XSD ordering defect.

Desktop-reported original result JSON SHA-256: `882ae2950199f71b7268aa959a2ae762679d62871adfb0261e731803a1f98613`; reordered result JSON SHA-256: `0c88c79cdaeebb5312b5b1683c6f8477ccbf49138a533562536c3c6c53935340`. The helper source hashes matched those listed above. These hashes and results are relayed desktop evidence, not independent cloud reads. Namespace-preserving fragment validation does not establish full SimaticML document validity, import acceptance, all input combinations, or StructuredText validity. Runtime schemaValidated/importValidated flags remain false.

Independent review identified XML whitespace normalization in the original string-emitting StructuredText helper and GlobalDB text serialization. Adapter guards now fail closed for the unrepresentable controls; the helper remains unchanged apart from the separately documented GlobalDB ordering correction. Regression tests compare parsed values for accepted attribute/text inputs and verify sanitized rejection for unsupported whitespace across all affected operation families. The independent reviewer reran the final Release stdio host: nine mutation reproductions returned InvalidParams, accepted StructuredText literal and GlobalDB comment LF/tab round-tripped exactly, and no remaining concrete blocker was found. The dedicated stdio script independently passed 128 checks, including normalization guards and accepted-value equality across all eight host keys.
