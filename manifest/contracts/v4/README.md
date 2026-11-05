# V4 contract snapshots

These files freeze the contracts exposed by the built products. The release key is exactly one of
`14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20`, or `21`; each snapshot directory contains exactly
one `<releaseKey>.json` for each key. All eight releases use the `plc-foundation` profile and include both the full tool
roster and the advertised lite roster.

## Contract baseline format 2

`baseline/<releaseKey>.json` has these fields:

- All releases: `formatVersion` (integer `2`), `release`, `profile` (`plc-foundation`), `tools`, `behaviorCapabilities`, and `schemas`.
- All releases: `liteTools`, a sorted list of names in the advertised lite profile.
- Each `tools` item has exactly `name`, `inputSchema`, `outputSchemaSha256`, `annotations`, and
  `descriptionSha256`. `inputSchema` is the built product's complete MCP input schema;
  `outputSchemaSha256` references its complete output schema in `schemas`, or is explicitly `null`
  when the product exposes no output schema. `annotations` contains the four explicit boolean hints
  `readOnlyHint`, `destructiveHint`, `idempotentHint`, and `openWorldHint`.
  `descriptionSha256` is SHA-256 of the exact UTF-8 description text.
- `schemas` maps lowercase SHA-256 hex keys to schema objects. Each hash is computed over
  `json.dumps(schema, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode('utf-8')`.
  Each referenced schema is stored once per file; dangling references, hash mismatches and
  unreferenced entries are rejected. Each release file remains self-contained.
- Each `behaviorCapabilities` item has exactly `family`, `state`, `l5`, and `entries`. The table
  is copied from the built product and its entries are sorted.

Tool records are sorted by name. JSON object keys are sorted, UTF-8 is used without a BOM, output
uses two-space indentation and ends with LF. Schemas and descriptions are not inferred from a
proposal or normalized beyond the MCP product output.
`load_snapshots` resolves references back to `outputSchema` in memory. Capture writes format 2;
capture-to-baseline comparisons remain byte-exact, and a changed output schema remains breaking.

## Verification

`Snapshot-ToolContracts.py verify` rejects unknown or missing format fields,
noncanonical release coverage and disagreement with the generated catalog and
source-registered tool inventory. The source-contracts CI job runs this check.
Capture all eight releases from built products, compare with the baseline and
review every difference before replacing it.

Per-tool response snapshots are retired. The Studio reader retains frozen real
MCP envelopes in `tests/Studio/V4RealEnvelopes.json`; these are reader fixtures,
not generated release evidence. V3 contract and response archives remain in
`manifest/history/contracts-v3` with their original provenance and hashes.
