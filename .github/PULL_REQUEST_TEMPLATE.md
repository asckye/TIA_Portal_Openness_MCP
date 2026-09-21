<!-- One problem per PR. English or Chinese, both are read. 中英文皆可。 -->

## What changes

<!-- One or two sentences: what the change does and why. Link the issue if there is one: Closes #123 -->

## Kind of change

- [ ] Engine (`tools/tiaportal-mcp/src`) — requires a `Build-Release.ps1` rerun before the manifests are committed (see `docs/development/release-workflow.md`)
- [ ] Offline tests (`tools/tiaportal-mcp/tests`)
- [ ] Configurator (`tools/mcp-configurator`)
- [ ] Scripts / CI
- [ ] Docs / templates / skill only

## How it was verified

<!-- Offline checks and real TIA acceptance are different things; say which you did.
     "It builds" is not real-project acceptance — Openness accepts input that only fails at compile time. -->

- [ ] `dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj -c Release` passes
- [ ] `python scripts/checks/Check-Repository.py` and `python scripts/checks/Check-DeadToolReferences.py` pass
- [ ] Real TIA Portal (version: V20 / V21): what was imported / compiled / downloaded, and the actual error / warning counts
- [ ] Not tested on a real TIA install — say so here rather than leaving it blank

## Checklist

- [ ] Docs updated where the change invalidates them (`docs/`, `tools/tiaportal-mcp/skill/SKILL.md`)
- [ ] `CHANGELOG.md` entry under `[Unreleased]` for user-visible changes
- [ ] No TIA project files, logs, screenshots, machine-specific paths or customer data committed
- [ ] New tool parameters carry a `[Description]` (the schema hints, preflight and examples read it)
