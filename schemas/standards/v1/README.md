# Generation format 1

These offline JSON Schema 2020-12 files are shipped with the bundle and embedded in
`TiaMcp.Logic`. Their HTTPS `$id` values identify schemas; validation never fetches
them. All `$ref` targets resolve to these bundled files.

| Schema | Document |
|---|---|
| [package](package.schema.json) | Standard package manifest, SemVer, targets, dependencies, languages and file inventory |
| [naming](naming.schema.json) | Naming templates and style constraints |
| [structure](structure.schema.json) | Groups, organization blocks and number ranges |
| [hardware](hardware.schema.json) | Hardware roles, release variants, slots and allocation ranges |
| [library](library.schema.json) | Logical types, interfaces, SCL, global library and master copy implementations |
| [modes](modes.schema.json) | Modes, states, commands, transitions, availability matrix and OPC UA/HMI mappings |
| [alarms](alarms.schema.json) | Alarm classes, device templates, localized texts and numbering |
| [hmi](hmi.schema.json) | Profiles, theme data, screen/widget templates and tag groups |
| [checks](checks.schema.json) | Style checks, XPath, severity, references and optional Test Suite mappings |
| [rule](rule.schema.json) | Device types, parameter schemas, signals and declarative emit entries |
| [machine](machine.schema.json) | MachineDescription, stations, topology, devices, IO and target |
| [plan](plan.schema.json) | Generation plan, steps, argument digests, availability, conflicts and artifacts |
| [check-result](check-result.schema.json) | Findings, repair hints and unavailable checks |
| [common](common.schema.json) | Shared definitions; its root validates one file digest record |

The C# validator implements the keywords used here: `$ref`, `type`, `const`, `enum`,
`properties`, `patternProperties`, `propertyNames`, `required`, `additionalProperties`,
`items`, `uniqueItems`, `minItems`, `maxItems`, `minProperties`, `maxProperties`,
`minLength`, `maxLength`, `minimum`, `maximum`, `pattern`, `allOf`, `anyOf`, `oneOf`,
`if`, `then`, `else`. It is scoped to these schemas, rather than a public general
purpose schema service. Object members are closed except explicit data maps such
as tool arguments, machine parameters and HMI theme properties.

Device parameter schemas use the restricted recursive definition in `common`:
type, properties, required, additionalProperties (boolean), items, enum, numeric,
string and array bounds, pattern, default and description. They cannot load remote
references or executable extensions. Regular expressions use the common .NET and
ECMAScript syntax subset, culture invariant matching and a 100 ms timeout in C#.

Package parts and machine descriptions are JSON only. SCL, optional global library
files and documentation may be inventoried resources; loading never executes or
imports them. Rule paths are exact file names, with no glob expansion. `extends`
and `dependsOn` contain `{ "package": "id", "version": "^1.2" }` references.
Implementation `releases` accepts `*`, a numeric comparison/caret range, or an
array of exact release keys (including `14sp1`). Range comparison maps `14sp1` to
14.1 and `15.1` to 15.1. Package version ranges support `*`, caret and conjunctions
of numeric comparisons; dependency resolution belongs to package management.

Rule conditions express existence (a field path or `{ "exists": "path" }`),
equality (`{ "equals": { "path": "path", "value": 1 } }`) or membership
(`{ "in": { "path": "path", "values": [1, 2] } }`). `forEach` selects signals,
children, devices, stations or topology. Placeholders contain field paths or
`naming.<rule>` calls, `tag`, `alloc.io`, `alloc.ip`, `upper`, `lower`, `text`
(one argument), `pad` (two arguments), or `seq` (zero or one argument).
Naming calls accept one to eight arguments; nesting is limited to eight calls.
Arguments can be field paths, nested calls, unescaped quoted strings or unsigned
32 bit integers. There are no operators, script engines, custom functions or
network access. Actual expansion is the responsibility of P8-31b.

The loader requires root `package.json` and inventories every other file in
`files`. A JSON file's `sha256` is the digest of its canonical JSON bytes; other
resources use their exact bytes. The manifest cannot inventory itself. Each part
references a distinct inventoried JSON file. Empty directories are irrelevant.
Default limits are 1,024 files, 2,048 entries including directories, 16 MiB per
file, 64 MiB in total and 32 MiB for a compressed archive. Limits apply to actual
decompressed bytes. Streams must be seekable; zip files are read in memory without
extraction. Paths use forward slashes, NFC, at most 240 UTF-16 code units and 16
segments. Traversal, absolute paths, backslashes, Windows reserved names, trailing
dots/spaces, case insensitive duplicates, file/directory collisions and links or
reparse points are rejected. Directory trees must remain unchanged during loading.
JSON requires strict UTF-8 without BOM, unique property names, valid Unicode,
no comments/trailing commas and nesting at most 64. Decimal exponents are bounded
to ±1,000,000. Typed count, slot and number fields are bounded to signed 32 bit
integers; arbitrary machine parameters and tool arguments preserve exact JSON numbers.

Canonical JSON orders object keys by ordinal UTF-16, preserves array order and
string contents, emits UTF-8 without BOM, and uses no whitespace. Quotes and
backslashes are escaped; control characters use lowercase `\u00xx`. Other Unicode
is emitted directly. Numbers use exact decimal coefficients, remove leading and
trailing zeroes, normalize negative zero, and emit plain integers up to 21 digits;
other numbers use a lowercase `e` with no plus sign or redundant zeroes. This is a
versioned project format, not an RFC 8785 implementation. JSON layout line endings
have no effect; line endings inside strings and non-JSON resources remain content.

Hashes are lowercase `sha256:<hex>`. Machine hashes cover the entire canonical
input. Plan hashes cover the entire input except the root `planHash` field;
`planId`, target identity, steps, arguments, digests and artifacts are retained.
Argument digests cover canonical `arguments`. A package hash covers canonical
`{ "format": "tiamcp.package-content/1", "files": [...] }`, with entries sorted
by ordinal path and each entry containing `path` and the canonical/raw content
digest. This includes `package.json`, so manifest changes affect the hash. Archive
order, compression and timestamps do not. `WriteCanonicalZip` permits a lossless
load → canonical zip → load round trip. Models are mutable editor DTOs; a loaded
package exposes immutable JSON and copies of resource bytes.

Schema errors provide an instance JSON Pointer, rule keyword and message. Loader
errors use a relative file path; part errors use `file.json#/pointer`. Semantic
checks add duplicate ids, references, required languages, release coverage,
state transitions, machine station/parent references, dependency order and
argument digests. `ValidateMachine` additionally checks this package's local
device types, parameters, signal roles and target release. Inherited definitions
must be resolved before full effective-package validation. Neither validation nor
loading generates names, dispatches tools, verifies native behavior, or establishes
that a stored `planHash` is trustworthy: the Apply boundary must recompute it.
Steps use `create` or `update` for object changes and `execute` for actions such
as backup, compilation and saving.
