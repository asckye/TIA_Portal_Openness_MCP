# HMI construction inputs (P6-05)

These types are offline, unwired V4 inputs. Use `HmiInputs.Contract<T>().Read(json,
parameter)` for text or parsed elements and `.Validate(dto, parameter)` for typed
callers. The shared `InputContract<T>` parses through `V4Json.ParseInput`, applies
`InputBudget`, and returns `InputResult<T>` with the shared presence and V4 error
details. `V4Json.Serialize` and the family converters retain the same serializer
options. Constructors validate too, and collections are copied. An omitted
optional field remains omitted. Explicit DTO-field null, unknown fields,
duplicate fields at any depth, wrong field/discriminator case and double encoding
are rejected. Shared `Scalar` explicitly admits scalar null only inside native
property dictionaries. Shared `AttributeMap<T>` copies dictionaries and supplies
their converters; keys and insertion order are preserved. Text maps additionally
reject null entries, as required by the HMI builders.

| Type | Current builder/parser and retained rules |
|---|---|
| `UnifiedThemeSpec` | [HmiUnifiedThemeLayoutBuilder.cs](../../ModelContextProtocol/Builders/HmiUnifiedThemeLayoutBuilder.cs), lines 13–40 and 146–155: `Page`, `Background`, `Surface`, `Text`, `Border` use `0x` plus eight hex digits; Pascal palette keys take precedence over their legacy camel aliases. Other string palette entries are copied verbatim. |
| `ClassicScreenSpec` | [ClassicHmiScreenXmlBuilder.cs](../../ModelContextProtocol/Builders/ClassicHmiScreenXmlBuilder.cs), lines 43–55, 165–256, 362–402: minimum 320×240, default 640×480, positive item sizes, nonnegative positions, screen containment, case-insensitive unique nonblank names; Text/Button/IOField/Rectangle/Lamp; per-control properties; Classic RGB pass-through and hex conversion. Bounds use Int64 to prevent overflow bypass. |
| `UnifiedScreenSpec` | [HmiTemplateDesignJsonBuilder.cs](../../ModelContextProtocol/Builders/HmiTemplateDesignJsonBuilder.cs), lines 28–109, and [UnifiedHmiService.cs](../../../Engine/Siemens/Services/UnifiedHmiService.cs), lines 1127–1268, 1840–1866, 2539–2588: separate Text/Button/IOField/Rectangle union, geometry, scalar native properties, font/content/padding, text omission versus clearing; IOField's seven-property allowlist and Rectangle exclusions. Text parsing is [UnifiedMultilingualText.cs](../../../Engine/Siemens/Hmi/UnifiedMultilingualText.cs), lines 15–23. |
| `ClassicTagTableSpec` | [ClassicHmiTagTableXmlBuilder.cs](../../ModelContextProtocol/Builders/ClassicHmiTagTableXmlBuilder.cs), lines 43–51, 127–159, 170–207: nonblank table/tag names, at least one tag, case-insensitive unique tags, paired connection/controllerTag, original type-dependent length defaults. `length` stays a string, matching the current builder. |
| `ClassicPackageSpec` | [ClassicHmiMinimalPackageBuilder.cs](../../ModelContextProtocol/Builders/ClassicHmiMinimalPackageBuilder.cs), lines 19–59 and 260–323: required screen/tag table, original reference/readiness checking. Undeclared tag references remain builder findings (`ok=false`), not invented parse errors. |
| `UnifiedLayoutSpec` | [HmiUnifiedThemeLayoutBuilder.cs](../../ModelContextProtocol/Builders/HmiUnifiedThemeLayoutBuilder.cs), lines 43–109 and 157–170: required named item objects, grid rounding, defaults, minimum-one clamping of columns/cells/spans, negative row/column meaning automatic placement. Retains left/top/gap and supported optional control fields. Layout controls use the same Unified field restrictions. |
| `DeviceAmlSpec` | [HardwareAmlLogic.cs](../../Siemens/HardwareAmlLogic.cs), lines 91–155: at least one named/typed device, required deviceItems array (empty allowed), named items, four roles, typeIdentifier unless built-in or interface/port, Int32 positions, IP-or-digits addresses, automatic subnet insertion, 5000 total device items, item depth 0–8. Optional node/subnet/attribute fields are retained. |

The Classic properties DTOs expose only fields used by each control. Classic
aliases (`TextField`/`HmiText`, `Tag`/`HmiTag`, `PlcTag`, action aliases and Pascal
field names) must be converted by the migration caller; the V4 boundary does not
accept them. Unified's legacy `Lamp`/`HmiRectangle` aliases normalize to
`Rectangle`. The current arbitrary CLR-type fallback is deliberately absent from
the closed union. Classic actions and Unified text properties advertise only
their supported canonical values. Native dictionaries retain exact SDK spelling;
they do not acquire a camel-case property alias.

`ToBuilderInput()` returns a fresh current-builder input object, without invoking
any builder or native operation. Most shapes already match the builders' camel
aliases. Unified screen `name/width/height` map to `Name/Width/Height`; the explicit
`properties` dictionary is flattened into the old screen property object. Empty
and omitted text remain different. The eventual tool integration must retain the
existing output-version and native capability checks; these DTOs do not establish
native import or property-write support.

`HmiInputs.Contract<T>().Schema` is checked by the shared `InputSchema` and emits
draft-2020-12 JSON Schema with
closed object definitions, required fields, per-control `oneOf`, canonical
discriminators, scalar dictionaries and expressible constraints. The existing
`HmiSchemas.For<T>()` and `dto.GetSchema()` facades export the same checked schema.
A later task can
start its tool schema with `HmiSchemas.InputSchema<ClassicScreenSpec>("design")`,
then add that tool's other arguments and required entries. This helper puts
`$defs` at the inputSchema root so references resolve correctly. When combining
multiple H schemas, merge definitions at that root; do not nest a standalone
schema under `properties` without relocating its definitions/references.

Cross-object uniqueness, screen containment, AML aggregate item count/depth,
legacy Classic color conversion and platform IP/culture validation still run in
code. They are not replaced by schema validation. Schema comments describe the
cross-field/runtime checks. Standard JSON Schema integer semantics also cannot
express System.Text.Json's rejection of fractional lexical spellings for Int32.

Budget provenance matters: H entries in [phase6-review.md](../../../../docs/development/phase6-review.md)
appendix B1 have **no character or string-length limit**. The 262144-character,
4096-string and depth-16 budgets are Foundation PLC construction limits, not H
limits. The shared input budget retains JSON depth 64 and reports excess depth as
`LIMIT_EXCEEDED`, including reader overflow. If a later contract introduces a
character cap, `InputBudget` checks both raw text before parsing and compact V4 JSON
before business normalization. This task retains AML's existing 5000/depth-8 limits
and tests both sides of those boundaries, including the total across devices.
It also tests text longer than 262144 characters to guard against importing an
unrelated Foundation restriction. No H string/character cap has been invented.

Equivalence evidence is in
[V4HmiEquivalenceTests.cs](../../../../tests/Engine/TiaMcpServer.Tests/V4HmiEquivalenceTests.cs)
with source-file/line citations per sample group. Deterministic Classic XML and
theme/layout design output are compared as UTF-8 bytes. Package XML, readiness
and other fields are compared after removing only its three report timestamps.
AML cannot have identical raw bytes across two builds: its builder calls
`Guid.NewGuid` (line 268) and `DateTime.Now` (line 287). Tests replace GUIDs
bijectively, verify every link resolves, validate and replace only the writer
timestamp/project GUID, then compare serialized AML bytes. They do not erase
link structure or other payload fields. Unified has no standalone offline Apply
builder: tests compare the existing template builder's design through the adapter
and invoke the actual pure text parser. Native Apply/property writes are not run.

The unification removes the local scalar/converter and map-copy helper, and uses
`InputGuard.Closed` for required/optional fields, duplicates and exact case. The
family converter still handles the Classic/Unified control discriminators,
constructor field order and omission. `ClassicText` retains its string-or-language
map union. Color precedence, control allowlists, containment, culture/address
rules and AML aggregate limits remain family rules.

Two shared hooks were necessary to retain the original schema and constructor
contracts. `InputSchema` now accepts root-local `$defs`/`$ref`, `$comment` and
`if`/`then`/`else`: recursive AML schemas cannot be inlined without changing the
existing schema output. Remote/missing references and non-consuming reference
cycles are rejected. `InputContract` has an optional `validateInput` callback after
budget/schema checks and before DTO construction. AML uses it to report shared
limit details before constructors raise their existing `ArgumentException`.
Both paths use the same family traversal and `InputGuard.Limit` checks.
No project files, tool/catalog registration, Siemens calls or manifest hashes
are changed. The original HMI contract/equivalence tests are unchanged;
[V4HmiInputUnificationTests.cs](../../../../tests/Engine/TiaMcpServer.Tests/V4HmiInputUnificationTests.cs)
also exercises their samples through text, element and typed contract entry points.
