# Reviewed tool catalog inputs

`dotnet run --project build-tools/release -- generate-catalogs` owns the profile resource, engine rejection fixtures, version tool catalog, package counts, feature behavior table and ledger capability block. Run it and `generate-tool-usage` before building or capturing host responses. All generator `-Check` options compare raw bytes, including line endings and BOMs.

- `new-v4-tools.json`: reviewed additions with owner, operation and the ordered eight release keys. Historical names cannot occur here. Known owner/operation decisions are checked in C#, and host-only additions must match GenerationTool registrations and ToolMetadata classifications.
- `v3-v4-renames.json`: frozen historical names from the eight 3.x archives. Exactly one target per archived name; the only approved merge is the three guide entries into GetToolUsage. New additions live in the separate file.
- `parameter-shapes.json`: reviewed typed parameter families and exact overrides. The generator checks current method signatures, legacy parameter removal, declared types, envelope returns and Foundation adapter conversions.
- `lite-groups.json`: the reviewed workflow selection used to validate the original engine lite proposal. Shared Foundation contracts then determine the published lite roster.
- `behavior-examples.json`: release-specific examples for explicit BehaviorCandidate entry/family pairs. New candidate entries require reviewed examples. L5 acceptance comes only from exact release-scoped ledger decisions.
- `operation-review.json`: frozen operation exceptions, Foundation additions, withdrawn entries and source-name rebasing. The checker reads classification rules from ToolTaxonomy and explicit rows from ToolMetadata.
- `usage-topic-rules.json`: official topic and snippet mappings; project-defined and uncovered wrappers retain the no-direct-example rule.
- `phase6-groups.json`: frozen archive task memberships used by the remaining Python snapshot transition checks. Batch 4 will migrate this consumer; it never executes a generator.

Port batches update PortedFamilies' per-tool release table, add reviewed data rows where needed, regenerate catalogs and usage, build, then capture. The old-release registration rule remains: a tool is listed only when at least one action is available. Unparsed family rows and unknown overrides fail closed.

`audit-version-tools -PublicApiRoot <SDK-root> [-InputsRoot <bin-build>] [-Output <candidate-directory>]` verifies the two owned catalog files and writes API audit evidence and candidate lists. `audit-tool-usage [-InputsRoot <bin-build>]` is the only writer of `manifest/tool-usage-coverage.json`; `-Records <json>` also accepts the eight distinct, source-hash-checked STDIO receipts from the remaining Python capturers. Capture those receipts under `bin-build`, then run this audit. They cannot run in build-free preflight: they require the same SDK XML, compiled inventories and eight release usage reports produced by the build/capture stages.

`generate-release-migration` reads the frozen rename data and eight current contract baselines. Its output is frozen after the 4.0 release. `docs/development/phase6-review.md` is an archive; its marker blocks are not generated or checked.
