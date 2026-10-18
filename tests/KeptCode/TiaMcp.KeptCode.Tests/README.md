# Kept-code offline checks

This project owns 4,854 test identities moved from each offline engine variant. The registered `kept-code` and `kept-code-v20` suites retain the original namespaces, method names, data row displays, assertions and V20 compilation parameter. The engine residue remains registered as `offline` and `offline-v20`.

The project links retained Logic, Shared, EngineHost, adapter and online-channel code. The frozen dispatcher under `tests/Fixtures/TiaMcp.Offline.Fixtures/Oracle` supplies the old offline invocation fixture; engine-only subjects continue to be tested against their original sources in the residue.

Six test files remain at their original source paths and are linked only into this suite: CodeDesignTests, McpLocalProcessTests, PlcEditingTests, SwallowedExceptionsTests, UnifiedScriptSyntaxCheckTests and V21EcosystemTests. Their paths are part of reviewed analyzer fingerprints. This preserves that gate without changing the baseline or the test bodies. Renderer data and existing golden files remain at their canonical fixture paths.

See `bin-build/refactor/P8-04k1/test-mapping.csv` and `bin-build/refactor/P8-04k1.result.md` for the method-level D5 proposals, source derivation and test-identity comparison. No rewrite or retirement is implemented here.
