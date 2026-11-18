# LAD rung candidate goldens

These are synthetic V21 candidate documents from `LadRungBuilderTests`, not native TIA exports. Each contains one minimal network and the FC envelope from `PlcLadFcBlockXmlComposer`. Import and compile acceptance remain **NOT RUN**; see the [real-machine ledger](../../../../../docs/reference/real-machine-ledger.md).

Update only for an intended output change by setting `TIA_MCP_UPDATE_GOLDEN=1` and running `LadRungBuilderTests` in Debug. Unset it and rerun the same tests to verify the stored bytes. Git preserves the XML bytes, including the existing Windows writer's line endings. Do not edit a golden by hand.
