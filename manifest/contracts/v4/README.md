# V4 contract snapshots

These files freeze the contracts exposed by the built products. The release key is exactly one of
`14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20`, or `21`; each snapshot directory contains exactly
one `<releaseKey>.json` for each key. Foundation snapshots cover 14sp1–19. Full-engine snapshots
cover 20–21 and include the lite roster.

## Contract baseline format 1

`baseline/<releaseKey>.json` has these fields:

- All releases: `formatVersion` (integer `1`), `release`, `profile` (`plc-foundation` or
  `full-engine`), `tools`, and `behaviorCapabilities`.
- V20/V21 only: `liteTools`, a sorted list of names in the advertised lite profile.
- Each `tools` item has exactly `name`, `inputSchema`, `outputSchema`, and `descriptionSha256`.
  Schemas are the built product's complete MCP schemas; an absent MCP output schema is recorded
  explicitly as `null`. `descriptionSha256` is SHA-256 of the exact UTF-8 description text.
- Each `behaviorCapabilities` item has exactly `family`, `state`, `l5`, and `entries`. The table
  is copied from the built product and its entries are sorted.

Tool records are sorted by name. JSON object keys are sorted, UTF-8 is used without a BOM, output
uses two-space indentation and ends with LF. Schemas and descriptions are not inferred from a
proposal or normalized beyond the MCP product output.

## Response snapshot format 3

`responses/<releaseKey>.json` has exactly `formatVersion` (integer `3`), `rawMaskRules`, `release`,
`profiles`, `transport`, `coverage`, and `calls`. V20/V21 also have `maxResponseChars` (integer
`2000000`). `transport` is `stdio`; `profiles` is `['full', 'lite']` for V20/V21 and
`['plc-foundation']` for Foundation.

`coverage` is an exact inventory of tested and intentionally skipped calls. Full-engine coverage
fields are `registeredTools`, `calledTools`, `behaviorCallTools`, `directRejectedTools`,
`directSkipped`, `calledOperations`, `usageTools`, `usageOperations`, `offlineExamples`, `l1Domains`,
`bridgeRejectedTools`, `bridgeSelfGuardTools`, `bridgeSkipped`, and `liteAdvertisedTools`.
Foundation fields are `registeredTools`, `calledTools`, `directRejectedTools`, `directSkipped`,
`passiveTools`, `passiveSkipped`, `bridgeRejectedTools`, and `bridgeSkipped`.

Each call has `profile`, `tool`, `arguments`, `rawTextBlocks`, and exactly one of `response` or
`responseDigest`. A digest has `length`, `sha256`, `shape`, and `textShapes`. Each raw text block
has `contentIndex` and `sha256`. Calls are sorted by profile, tool name, and canonical arguments;
coverage name lists are sorted. Responses above 16 KiB, `GetToolUsage`, and full lite-bridge
rejection responses use digests; other responses retain their complete protocol result or error.

Canonical JSON sorts object keys and preserves array order. The fixed JSON-RPC `id` and `jsonrpc`
framing fields are omitted. The only volatile response fields masked are:

- `meta.timestamp` in response envelopes (wall clock).
- `data.timestamp` for `BuildClassicHmiScreen`, `BuildClassicHmiTagTable`, and
  `BuildClassicHmiMinimalPackage` (builder wall clock).
- `data.screen.timestamp` and `data.tagTable.timestamp` for
  `BuildClassicHmiMinimalPackage` (embedded builder wall clock).
- `meta.requestId` in V4 infrastructure envelopes, including batch-child envelopes (correlation
  GUID).

For raw MCP text evidence, the same listed timestamp values are replaced lexically while retaining
the original JSON text's whitespace, key order, quoting, and other bytes. Its UTF-8 SHA-256 is
recorded before JSON decoding/canonicalization. No other GUID, path, PID, machine name, duration,
identifier, message, error, or hash is masked. An unrecognized volatile value must fail the
two-capture comparison and be reviewed here before a narrowly scoped rule is added.

`Snapshot-ToolContracts.py verify` and `Snapshot-ToolResponses.py verify` reject unknown or missing
format fields, noncanonical release coverage, and disagreement with the generated catalog and
source-registered tool inventory; the source-contracts CI job runs both on every push, so an added,
removed or renamed tool or a changed lite roster fails CI until the baselines follow. Schema,
description and response changes are visible only in built products, which hosted Ubuntu CI cannot
build without the Siemens PublicAPI assemblies, so they are a reviewer gate before every merge:
capture all eight releases twice from one build, check byte identity, compare with these files and
review every difference before the capture output replaces them.
