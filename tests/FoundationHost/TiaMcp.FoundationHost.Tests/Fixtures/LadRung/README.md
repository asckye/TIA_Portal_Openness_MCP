# LAD rung candidate goldens

These are synthetic V14 SP1–V21 candidate documents from `LadRungBuilderTests` and `LadRungReleaseTests`, not native TIA exports. Each contains one minimal network and the FC envelope from `PlcLadFcBlockXmlComposer`. Import and compile acceptance remain **NOT RUN**; see the [real-machine ledger](../../../../../docs/reference/real-machine-ledger.md).

Update only for an intended output change by setting `TIA_MCP_UPDATE_GOLDEN=1` and running `LadRungBuilderTests` and `LadRungReleaseTests` in Debug. Set `TIA_MCP_TEST_PUBLIC_API_ROOT` to the local SDK root for the per-release FlgNet and interface XSD checks. Unset it and rerun the same tests to verify the stored bytes. Git preserves the XML bytes, including the existing Windows writer's line endings. Do not edit a golden by hand.

The V14 SP1 `indexed-local.xml` exercises the same local output with an unindexed global component because this release forbids array indices. The separate rejection tests pin its `/index` error pointer. Each release also has a `legacy-call.xml`; all pre-existing V21 fixtures retain their bytes. Public registration and the output selector remain V21-only under A3-2(b), pending native acceptance per release.
