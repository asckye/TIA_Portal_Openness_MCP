# Kept-code offline checks

This project retains the original 4,854-check floor and adds 301 checks in Q2: all 292 observed version-catalog checks and the two HMI theories with four and five rows. `kept-code` runs once with a minimum of 5,155 passed and zero skips. Its namespaces, data and assertions remain; `offline` is retired.

The project links retained Logic, Shared, EngineHost, adapter and online-channel code. The frozen dispatcher under `tests/Fixtures/TiaMcp.Offline.Fixtures/Oracle` supplies the offline invocation fixture and the moved catalog tests' `EngineRouter`; it is not production engine code. The HMI theories are merged into the existing KeptCode `V4HmiEquivalenceTests`, preserving its existing tests and sample helpers.

Six test files remain at their original source paths: CodeDesignTests, McpLocalProcessTests, PlcEditingTests, SwallowedExceptionsTests, UnifiedScriptSyntaxCheckTests and V21EcosystemTests. Their paths are reviewed analyzer fingerprints. Harness screen traversal and renderer/argument fixtures also keep their canonical paths. The other engine-only tests and their fixtures are deleted without successor tests, as decided in ADR 0022 on 2026-11-02.

Q2 evidence and the per-file retirement inventory are in `bin-build/refactor/Q2.result.md`.
