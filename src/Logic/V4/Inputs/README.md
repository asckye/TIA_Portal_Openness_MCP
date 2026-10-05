# V4 input validation adapters

These types are not wired to tools. Obtain a parameter schema from the **same**
`InputContract<T>.Schema` instance used to validate that action and release. Embed it
under `inputSchema.properties[parameter]`; the outer tool schema owns whether that
parameter is required. Call `Read(JsonElement, parameter, optional)` before native
work, or `Validate(typedValue, parameter)` for an already typed caller. Both return
`InputResult<T>` with V4 `Error` details and a normalized value. Do not deserialize
the DTO and skip the contract.

`InputPresence` distinguishes an omitted argument, explicit null, and a value
(including an empty collection). Optional does not imply nullable and does not
insert a default. `Scalar` and a nullable `NativeValuePolicy` explicitly admit null.
Defaults and requiredness must come from the release's tool schema at integration.
Strings are never parsed a second time: a string cannot stand in for an array or
object. Where a native scalar string is allowed, its contents remain literal text.

All serialization uses `V4Json`, including converters' supplied options. The one
new internal `V4Json.ParseInput` hook uses a bounded reader depth of 65 (the largest
family depth budget, 64, plus one). The adapter checks raw character length before
parsing, then checks parsed elements iteratively. Reader depth failures become
`LIMIT_EXCEEDED`; their `actual:65` reports a reached lower bound, not the full
depth of rejected input. Syntax errors remain value-free `INVALID_ARGUMENT` errors.
The hook does not configure another serializer. Constructors bound external
`JsonElement` trees before recursive V4 or schema validation; schema recursion is
bounded by its own checked document depth.
DTO fields are closed, camelCase and ordinal. Dynamic map keys retain exact SDK
spelling and insertion order. Duplicate JSON keys are rejected before materializing
a dictionary. Input errors do not echo JSON, values, keys or parser diagnostics.

| Family | Type / validator | Retained policies and source locations |
|---|---|---|
| P | `string[]` / `PathValidator` | Device 1–64, item 0–64 nonblank decoded segments; no delimiter splitting or case normalization. `Portal.SessionResolvers.cs:84–104`, `ProjectSecurityLogic.cs:55–64`. Dot-segment rejection is opt-in for the separate `EngineeringPath.cs:9–14` policy. |
| S | `string[]` / `NameListValidator` | Hardware 64, scopes 32, ordinal uniqueness; permission enum / exclusive None; harmonize flags. Runtime 1–500 trimmed unique names; simulation 0–500 trimmed names; drive 200 / 64 characters. Cultures 1–64 / 4096 JSON characters with culture normalization and case-insensitive uniqueness. `HardwareNetworkLogic.cs:51–67`, `LibraryDeepLogic.cs:99–108`, `RuntimeChannelsLogic.cs:76–105`, `PlcSimAdvancedLogic.cs:148–162`, `StartdriveLogic.cs:97–120`, `PlcBlockServicesLogic.cs:76–90`. |
| N | `int[]`, `ParameterRef[]` / `NumberListValidator` | Plain numbers use `Drive`; all legacy object selectors use `ParameterReferences`. Number 0–65535, optional arrayIndex -1–32767, lexical integers only, names + numbers <=200, duplicates and ordering preserved. `StartdriveLogic.cs:86–105`. |
| R | `PropertyStep[]` / `PropertyPathValidator` | 24 steps / 32768 JSON characters, no backlinks or method syntax, public readable non-indexer properties from the caller's admitted target graph. `EngineeringObjectAddress.cs:11–38`. `ValidateTraversal` preserves the 10000-object enumeration cap from `EngineeringGroupOperations.cs:17–23`. |
| M | `AttributeMap<Scalar>` / `AttributeMapValidator` | Mandatory action/release/target field table; exact spelling, writable fields, declared type/enum/range. Hardware 32768 characters / 50 entries / identifier keys; safety 16384 / 50. `ArgumentRules.cs:38–42,53–87`, `EngineeringScalarProperties.cs:16–48`, `SoftwareUnitDeepLogic.cs:33–35,69–74`. `CpuSettings` wraps the admitted map in `exactAttributes`. |
| M (screen item only) | `CompositeAttributeMap` / `CompositeAttributeMapValidator.ScreenItem` | Scalar or one map of scalar leaves, exact admitted keys and writable leaf rules at both levels, no renames/backlinks/collections. 65536 characters, depth 2 including the root map, 50 scalar leaves. `UnifiedScreenItemLogic.cs:36–40`, `UnifiedUiModelLogic.cs:117–138`. |
| L | `AttributeMap<string>` (access: `AttributeMap<int>`) / `TextMapValidator` | Text maps, comment key length 16 and active-culture admission, prompt identifiers / 64-character answers / supplied selection or checkbox metadata / refusal of password prompts. Access area enum, levels 0–4, safety 0–1. `PlcTagEditingLogic.cs:48–66`, `DownloadPromptPolicy.cs:43–57,79–100`, `OpcUaService.cs:298–305`. |
| V | `NativeValue` / `NativeValueValidator` | Explicit schema and budget per action; no permissive default. Drive scalar-or-bicoSource, all drive-function action fields/enums/data sets/entry counts. `StartdriveLogic.cs:131–158,208–280`. Simulation conversions reuse `PlcSimAdvancedLogic.cs:166–213` (invariant culture, checked widths, hex strings). Nonfinite numbers are refused by V4. |
| C | `ToolCall[]`, `ToolArguments` / `ToolCallValidator` | Caller supplies target schema and business validator, release catalog admission flags and budget. Read/preview 1–50, transaction 1–20, no nested orchestration, preview forces dryRun after input validation. `McpServer.Batch.cs:73–97`, `ToolTransactionRules.cs:12–28`. No invocation occurs. |
| W | `WriteValue[]` / `WriteValueValidator` | 1–500, trimmed ordinal unique names, channel-specific value policy, dryRun or confirmWrite required. `RuntimeChannelsLogic.cs:111–160`, `RuntimeChannelTools.cs:103–121,378–392`. Simulation may explicitly retain duplicate names (`uniqueNames:false`) from `PlcSimAdvancedLogic.cs:122–145`; per-tag primitive policy uses the type already read by the caller. |

Source paths above are under `src/Logic/Siemens`, `src/Logic/Runtime`,
`src/Engine/Siemens`, `src/Engine/Siemens/Portal`, `src/Engine/Siemens/Services`, or
`src/Engine/ModelContextProtocol/Tools` as appropriate. Tests give full paths beside
the executable legacy samples.

`InputBudget` bounds raw text length **before parsing**, and also measures characters
on compact V4 JSON **before** business trimming for every entry point. Whitespace
cannot bypass the raw cap, nor can compact escaping bypass the normalized cap.
It also enforces depth, per-string length and per-container item counts. The existing
Foundation budget can be declared as `new InputBudget(262144, 16, 4096, 1000)` by
the builder-family tasks; this task does not create their types. Output/XML budgets
remain the builders' responsibility. Standard schema keywords express shapes,
required fields, enums, ranges, lengths, cardinalities, unions and uniqueness.
Compact JSON character counts, traversal counts, culture/SDK admission, confirmation
and normalized-name uniqueness remain business validation, not schema annotations.

`InputSchema` accepts an explicit, closed vocabulary (see `InputSchema.cs`). It
rejects unsupported keywords, including refs and formats, rather than claiming to
have validated them. No remote schema fetching occurs. Callers must provide an
inlined supported schema or add reviewed support before wiring a schema that uses
additional keywords. `ToolTarget.BusinessValidation` is also required when the
target has constraints beyond its schema; target budgets are checked independently
of the batch's budget. All policies are scoped to the caller's action and release;
these adapters do not imply a capability exists on every CPU/release.

Before using R with a native collection, the integration must construct the
admission graph from the supported SDK surface and call `ValidateTraversal` while
consuming items. The current legacy path policy does **not** support index
selectors, so `PropertyPathPolicy.AllowIndex` defaults to false. An action can
declare an index range only if that action supports it. No user-provided CLR type,
property getter or native enumeration is executed by this layer.

The follow-up decisions keep plain N lists as `int32[]` and add
`ParameterRef{number,arrayIndex?}` for every legacy object selector. Omitted
`arrayIndex` means the legacy -1 sentinel; explicit -1 and non-default indices retain
their wire representation. Callers supply the validated name count to enforce the
combined 200-parameter limit.

Default M maps remain scalar-only, even if a caller supplies a collection schema.
Only screen-item writes opt into `CompositeAttributeMap`, for example
`Font:{Size:14}`. The host supplies exact admitted public readable non-indexer
parts and scalar leaf rules; a get-only Font object can have writable Size. The
same scalar/writability/type/range checks build both levels of the emitted schema,
including uint SDK attributes. Parts never contain another map or array, even if
an older generic HMI traversal could descend further. A multilingual map can be
declared with the exact admitted culture keys and string rules. No native getters
or reflection are invoked by validation. CPU's explicit `exactAttributes` shape
also remains closed to its supplied admission table.

`V4InputEquivalenceTests` runs the original parsers for P/S/N/R/M/L/V/C/W, compares
accept/reject and normalized JSON, and pins one golden byte sequence per family.
`V4InputValidationTests` adds culture and primitive conversion equivalence, drive
action equivalence, schema, budgets, casing, duplicates, null/missing, nested
orchestration, reflection admission and credential non-echo checks.
`V4InputFollowupTests` adds 28 indexed-selector equivalence samples (including six
combined-count boundaries), 22 composite-property equivalence samples, golden bytes
for both added shapes, leaf budgets, closed DTOs, raw/normalized character limits
and 20000-level text and external-element safety checks. The generated phase-6
review document is left for the reviewer to regenerate and record these decisions.
