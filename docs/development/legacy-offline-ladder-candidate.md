# Offline LAD FC-call candidates

Manual-first implementation, 2026-10-02. `BuildFlgNetCallXml` and `ComposePlcLadFcBlockXml` generate bounded in-memory V21 candidate XML in the isolated .NET 8 host. These are not arbitrary ladder generators, schema validators, importers or ready PLC programs.

## Evidence and unchanged helpers

Read before implementation:

- [Official V21 behaviour changes](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/major-changes/major-changes-in-v14-sp1/changes-for-export-and-import/behaviour-changes?contentId=Ni5pupfJsmEBXkWXnOLCiA): UIds must be unique within a compile unit; duplicate parameters, invalid sections and some name/constant forms can cause import failure. Project resolution and compilation impose additional constraints. The chapter is not a full FlgNet schema.
- [Official V21 block interface](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/xml-structure-of-the-block-interface-section?contentId=TGNd5apTdFB9WBWcrgOXXg): Sections/Section/Member structure and required Name/Datatype; higher-version exported XML is not compatible with older-version import.

Only existing pure `FlgNetCallXmlBuilder.BuildFlgNet` and `PlcLadFcBlockXmlComposer.Compose` are invoked. No probe, file, worker, Siemens API, import, compile, project or network operation is reachable from these adapters. Helper source remains unchanged:

| File | SHA-256 |
|---|---|
| FlgNetCallXmlBuilder.cs | `c5eac905f6aa99e17a7056d65b52e6355d8446f8166be088c8f0406bd93d1785` |
| PlcLadFcBlockXmlComposer.cs | `d62085cb81a46e27c6a87491d4a97d080bba74c5625f960b40b6e4c7cd80e44e` |

The helper format is FlgNet/v5; complete documents retain Engineering V21 and Interface/v5. Both adapters require exact explicit `outputReleaseKey="21"` independently of the selected host key, including V17. There is no down-conversion, namespace stripping or marker rewriting. Host selection does not establish import compatibility.

## Bounded contracts

`BuildFlgNetCallXml` accepts `flgNetJson` and `outputReleaseKey`. Example JSON string contents:

```json
{"callName":"MyFC","parameters":[{"name":"InputA","section":"Input","dataType":"Int","sourceKind":"constant","value":"42"},{"name":"OutputB","section":"Output","dataType":"Bool","symbolPath":["DataDB","Ready"]}]}
```

- Exactly one callName/name; parameters is a required array, empty allowed. Exactly one name/parameterName per port. Names must be simple identifiers: initial Unicode BMP letter or underscore; subsequent Unicode BMP letters/digits or underscore. Quoted, dotted, indexed, whitespace-containing, supplementary-plane and path-like identifiers are unsupported. This narrow grammar is an adapter restriction, not a complete PLC naming specification.
- Section is exact `Input` or `Output`. InOut, Return, Static, Temp, EN/ENO user ports and all other directions are unsupported. Port names must be case-insensitively unique and cannot collide with EN or ENO.
- Exactly one dataType/datatype/type: exact spellings Bool, Byte, Word, DWord, LWord, SInt, USInt, Int, UInt, DInt, UDInt, LInt, ULInt, Real, LReal. Other datatypes, including strings, arrays, structs, UDTs and reference types, are unsupported. The allowlist does not establish actual source/destination type compatibility or literal lexical validity.
- Optional sourceKind/source/kind accepts exact lowercase constant/literal/literalconstant or global/globalvariable. Omitted or empty means global. Unknown and differently cased source values are rejected rather than silently treated as global.
- Constants require an Input port and exactly one value/constantValue string; no symbol fields. Text is serialized literally and escaped. Its PLC meaning is not checked.
- Globals require exactly one symbolPath array (1–32 simple identifier components) or dotted symbol string (1–32 components); no value fields. Empty segments, quotes, indexes, slash/backslash paths and URL forms are rejected; no trimming or dropped segments. Original path/plcTag aliases are deliberately unsupported.
- Caller-provided UIds, wire/port definitions, arbitrary parts, local/absolute/indirect accesses, FB instances and raw XML fields are rejected.

`ComposePlcLadFcBlockXml` accepts `ladFcBlockJson` plus explicit output release:

```json
{"blockName":"Caller","blockNumber":42,"inputs":[],"outputs":[],"networks":[{"callJson":{"callName":"MyFC","parameters":[]},"titleZhCn":"Call"}]}
```

- Exactly one blockName/name and positive Int32 blockNumber/number. Optional inputs/outputs arrays contain flat members with name, exactly one datatype/dataType and optional commentZhCn/comment/commentZh. The same identifier/datatype restrictions apply. Supplied names are case-insensitively unique across both sections; Ret_Val is reserved.
- The unchanged helper emits Input/Output, empty InOut/Temp/Constant and a fixed Return member Ret_Val/Void. It always emits LAD with SetENOAutomatically=false. No caller return type, additional section or interface flags are supported.
- Required networks array, 1–64 items, each containing exactly one callJson/call structured object under the call contract. Optional network titleZhCn/title and commentZhCn/comment.
- Optional block commentZhCn/blockCommentZhCn/comment and titleZhCn/blockTitleZhCn/title. All text strings preserve accepted content, including XML-looking text, Unicode supplementary pairs and CR/LF/tab. Nonempty whitespace-only member comments are rejected because the unchanged helper omits them. Empty and whitespace block/network comments remain accepted.
- No raw FlgNet fragments are accepted anywhere. Every network is exactly one generated FC call, with one enable powerrail wire and one wire per supplied parameter; no contacts, coils, SR, compare, math or general ladder.

These strict, bounded contracts are deliberately narrower than the permissive original tool wrappers and are not exact contract-compatible V17 implementations. Unknown fields, duplicate JSON keys, conflicting aliases, wrong scalar types, undecodable Unicode and invalid XML characters are rejected with fixed sanitized InvalidParams errors. Cancellation propagates; unexpected helper failures become fixed InternalError messages without input echoes.

## Bounds and validation scope

JSON <=262,144 UTF-16 characters, depth <=16; each string <=4,096 characters; <=1,000 total parameters across all networks; <=1,000 total interface members; <=64 networks; <=32 components per symbol. Output <=1,048,576 characters. Existing bounded-size constants and warning text are reused; private strict JSON/text guards follow the SCL candidate pattern without changing its shared files.

XML serialization uses NewLineHandling.Entitize, preserving text CR and avoiding platform-specific rewriting. Generated output is reparsed with DTD prohibited, null resolver and a size bound. Complete-document IDs are checked globally unique. Per network, generated Access/Call/Wire UIds are unique; every parameter has one corresponding wire; IdentCon resolves exactly once to an access and NameCon resolves to the call and named parameter with the generated Input/Output endpoint ordering. Reused UIds across different compile units follow the official documented scope. No caller XML is parsed.

These are structural checks of this helper's generated shape, not a full schema or program proof. Callee existence, symbol binding, actual interface arity, datatype/literal validity, writable destinations, CPU support, library block resolution and project-level semantics remain unknown. Installed Siemens XSD closure and isolated-project import/compile acceptance have NOT RUN.

Response retains Message/Meta/Ok/Data/Errors/Warnings/OutputPath/OutputFiles/Xml under the SDK naming policy. OutputPath/OutputFiles are null; schemaValidated/importValidated/programSemanticsValidated are false in Meta and Data; nativeAcceptance is NOT RUN. xmlParseOk only means well-formed generated XML. Existing unrelated fragment-XSD tests do not validate these outputs.

## Reproduction

```sh
dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.LegacyHostTests -c Release
python3 scripts/checks/Test-LegacyOfflineLadder.py --dotnet /path/to/dotnet --host tools/tiaportal-mcp/src/TiaMcpServer.LegacyHost/bin/Release/net8.0/TiaMcpServer.LegacyHost.dll --evidence-dir /new/owned/output-directory
```

Final implementation integrated suite: 2,883 checks passed, zero failed, three explicitly untested Windows/native cases. This includes concurrent unrelated work. Real stdio: 144 checks passed across all eight host release keys, no worker/native-session flag/PublicAPI. Coverage includes helper shape, deterministic output, complete document and return interface, UIds, aliases, case/duplicate/size/Unicode errors, literal/comment fidelity, XML injection and path-string handling, output release and sanitized error contracts. Additional boundary coverage accepts exactly 1,000 parameters, rejects combined-network parameter overflow and XML expansion beyond its output limit, and rejects escaped unpaired surrogates in the outer SDK argument.

Generated samples: call SHA-256 `bd1bf5d8ca826a847fd52abc84e2f73f75333d5f74d3f283849c11cfcbe3b8cf`; LAD block SHA-256 `6eddd1a4f9bc702a3f49a04b24a8b9bd8a7a0011f3dfa881b1c981b5e9d9563b`. These are owned test artifacts, not deployment/import evidence. Windows Build-Release and all native acceptance remain unrun.

Independent read-only review repeated a fresh host build, all 2,883 pure SDK checks (zero failures; three explicit skips), and all 144 eight-host stdio checks. The only build warnings were two pre-existing catalog nullability warnings. No remaining source blocker was reported; native/schema/program acceptance remains unverified.
