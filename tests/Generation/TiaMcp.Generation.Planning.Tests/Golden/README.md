# Planner golden files

The Basic and plain fixtures use the real `GenerationPlanner`. Ordinary test runs
assert exact UTF-8 bytes in these files and check repeatability across all eight
releases and four cultures.

Regenerate from the repository root after running `generate-catalogs` and
`generate-tool-usage`:

```powershell
$env:TIA_MCP_UPDATE_GOLDEN = '1'
dotnet run --project build-tools/release -- test-suites -Suite generation-planning
Remove-Item Env:TIA_MCP_UPDATE_GOLDEN
git diff -- tests/Generation/TiaMcp.Generation.Planning.Tests/Golden
dotnet run --project build-tools/release -- test-suites -Suite generation-planning
```

Only the exact value `1` enables writing. `CallerFilePath` locates this source
Golden folder; the tests never update the copied build output. The same fixtures
also regenerate both `FC_U01_Calls.scl` files. Review every changed plan and source
file before accepting the new bytes. No TIA session or native acceptance runs.
