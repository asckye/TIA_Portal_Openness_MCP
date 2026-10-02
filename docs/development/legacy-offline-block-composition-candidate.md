# Offline SCL FC/FB block composition candidates

Manual-first review, 2026-10-02. `ComposePlcFcBlockXml` and `ComposePlcFbBlockXml` now compose bounded in-memory candidate documents in the isolated .NET 8 host. They never call a worker, Siemens API, file/probe helper, import, compilation or instance DB creation. `BuildFlgNetCallXml` and `ComposePlcLadFcBlockXml` now have separately reviewed [bounded V21 LAD FC-call candidates](legacy-offline-ladder-candidate.md); schema/import/program validation remains false.

## Evidence and limits

The [official V21 interface chapter](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/xml-structure-of-the-block-interface-section?contentId=TGNd5apTdFB9WBWcrgOXXg) was read before implementation: it describes Sections/Section/Member, mandatory member Name/Datatype and comments. It explicitly excludes higher-version XML import into older products. The [official V21 structured SCL chapter](https://docs.tia.siemens.cloud/r/es-es/v21/tia-portal-openness-api-para-la-automatizacion-de-flujos-de-trabajo-de-ingenieria/exportacion/importacion/importacion/exportacion-de-datos-de-un-dispositivo-plc/bloques/exportacion/importacion-de-tipos-estructurados-de-bloques-scl) describes symbol components, dot tokens and special quote/hash representation. These chapters do not certify arbitrary generated programs or the whole-document schema.

Unchanged existing composers supply Engineering V21, Interface/v5 and StructuredText/v4. Both tools require explicit `outputReleaseKey="21"` independently of selected host key. An older host may generate that explicitly requested candidate; this never claims it can import the result. No conversion, guessed schema, namespace stripping or marker rewriting occurs.

| Unchanged helper | SHA-256 |
|---|---|
| PlcFcBlockXmlComposer.cs | `dbb60ba47fde1d53a814a55d08ca94d448ba8327484fa2bf6922f14a38b0aadc` |
| PlcFbBlockXmlComposer.cs | `59e1e0387c1b9bdd974280d33dcc8b5a346b4bcc2e271f38b763502762c8e060` |

Only their pure `Compose` methods are invoked. The adapter serializes the returned XDocument using `NewLineHandling.Entitize`, preserving element CR and attribute CR/LF/tab instead of allowing platform-dependent newline replacement. This changes serialization policy, not XML structure. StructuredText still uses the existing bounded operation guard and unchanged helper.

## Contracts

Both calls accept their JSON string parameter (`fcBlockJson` or `fbBlockJson`) plus `outputReleaseKey`:

```json
{"blockName":"SampleFC","blockNumber":42,"inputs":[{"name":"Ready","datatype":"Bool"}],"outputs":[],"structuredText":{"operations":[{"op":"assignment","target":"#Ready","value":"TRUE"}]}}
```

- Name aliases: blockName/name; number aliases: blockNumber/number. Positive Int32 number. Exactly one alias per group.
- FC requires inputs and outputs arrays; empty is valid. Its unchanged helper emits empty InOut/Temp/Constant and the fixed Return member Ret_Val/Void. A case-insensitive Ret_Val input/output collision is rejected explicitly because the helper adds it after its own duplicate check.
- FB permits omitted or empty inputs/outputs; inouts/inOuts/inOut, statics/staticMembers/static and temps/tempMembers/temp arrays. All interface names are case-insensitively unique across supplied sections.
- Each flat member requires name and datatype/dataType; optional commentZhCn/comment/commentZh. Nonempty whitespace-only member comments are rejected because the existing helper silently omits them. Other text is preserved, not trimmed. PLC identifier and datatype semantic validity are not established by these checks.
- Block comment aliases: commentZhCn/blockCommentZhCn/comment; titleZhCn/blockTitleZhCn/title. Network comment: networkCommentZhCn/networkComment; title: networkTitleZhCn/networkTitle. All are optional strings, including empty or whitespace-only block/network text; supplied null is rejected.
- structuredText must be an object using the [existing bounded operation contract](legacy-offline-composition-candidate.md#contracts), including its operation-specific aliases and fidelity restrictions. In particular, StructuredText attributes reject CR/LF/tab and literal content rejects CR; accepted LF/tab literal content survives composition.
- Raw structuredTextInnerXml, structuredTextXml, sclInnerXml and paths are deliberately unsupported in this candidate. XML-looking text supplied to normal text fields is escaped as text, never interpreted as caller XML. This is narrower than the original tool and is not an exact contract-compatible migration.

No declaration resolution, symbol binding, control-flow analysis, type checking, call-port arity or complete-program validation is performed. Users must validate actual program meaning separately. SCL structure is a single generated compile unit; no instance DB is produced for FB.

## Bounds and validation claims

JSON at most 262,144 characters, nesting depth 16; at most 1,000 supplied interface members in total across sections; each string at most 4,096 characters; output at most 1,048,576 characters. StructuredText expansion has its existing incremental limit. Unknown/duplicate keys, conflicting aliases, invalid scalar types and invalid XML characters and undecodable Unicode strings (including escaped unpaired surrogates) are rejected. Generated complete XML is reparsed with DTD prohibited, null resolver and bounded document length, then checked for its expected root/kind/version/namespaces. No external XML is parsed.

Response envelope retains Message/Meta/Ok/Data/Errors/Warnings/OutputPath/OutputFiles/Xml under the SDK naming policy. File outputs remain null. xmlParseOk describes well-formedness only. schemaValidated, importValidated and programSemanticsValidated remain false; nativeAcceptance is NOT RUN. Fixed errors omit submitted values and exception chains. Cancellation propagates.

Previous fragment XSD checks for other builders do not establish validity of these compositions. Full exact-release schema closure, representative interface and StructuredText validation, whole-document validation where supported, and separately authorized isolated-project import/compile acceptance remain open. This is not a ready PLC program or a production acceptance claim.

## Reproducible checks

```sh
dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.LegacyHostTests -c Release
python3 scripts/checks/Test-LegacyOfflineBlockComposition.py --dotnet /path/to/dotnet --host tools/tiaportal-mcp/src/TiaMcpServer.LegacyHost/bin/Release/net8.0/TiaMcpServer.LegacyHost.dll --evidence-dir /new/owned/output-directory
```

Initial integration: dedicated new tests passed 261 checks. The integrated console suite passed 1,942 checks with zero failures and one Windows-only filesystem skip; this includes concurrent work, not only this family. The new real stdio script passed 112 checks across all eight host keys with explicit V21 output and no worker/native-session flag/PublicAPI. Tests cover unchanged helper structure, complete document markers, deterministic outputs, alias/duplicate/type/size failures, reserved FC return name, raw XML rejection, injection escaping, parsed-value fidelity, sanitized errors, cancellation and honest validation flags. Complete Windows Build-Release and native acceptance have not run in this cloud environment.

Owned generated stdio samples: FC SHA-256 `3721857c4e538287b9fd0a87488a914deeccbd554636f2708543a25b0d7678c2`; FB SHA-256 `7e5b430434864dd44aa1f1b7134835d13aa933e010f6bb768134d9e8810beea7`. These are test artifacts, not deployment or import evidence.

Independent read-only review rechecked the official chapters, passed 141 additional protocol/fidelity/Unicode assertions and repeated all 112 eight-host stdio checks after the Unicode fix; generated sample hashes were unchanged. No remaining finding was reported for this family. A later full-suite reviewer rerun was blocked by concurrent unrelated batch-export source closure work, so the last successful integrated result for this family remains 1,942/0/1, not a claim about subsequently edited sources.
