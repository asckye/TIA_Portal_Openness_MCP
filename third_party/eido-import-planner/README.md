# Eido import dependency planner

Adapted from [EidoAut/EidoTiaWorkbench](https://github.com/EidoAut/EidoTiaWorkbench),
commit `7a918b81925ed77bea96157d294610210b3de58c`, under the [MIT license](LICENSE).
Copyright (c) 2026 EIDO AUTOMATION, S.L.U. The upstream file and Git blob are pinned
in [UPSTREAM.json](UPSTREAM.json).

The integrated implementation is [ImportDependencyPlanner.cs](../../src/Shared/ImportDependencyPlanner.cs).
It retains dependency-first traversal, stable ordering and cycle detection. The local
DTO removes the upstream V21-specific artifact model and automatic domain heuristics;
callers supply dependencies explicitly. Missing dependencies and cycles return issues
and an empty order. Duplicate IDs are rejected. Priority orders independent items.

Both MCP profiles expose `PlanArtifactImportOrder`, using the same implementation.
The full-engine result is under `meta.plan`; the foundation result is the plan itself.
Studio can consume the shared planner when it gains a multi-artifact import workflow;
no MCP client was added to Studio.

Tests include the upstream HMI tag/template/screen scenario, PLC UDT/block dependencies,
Unicode identifiers, cycles, missing dependencies and real MCP transport calls. The
planner neither parses native dependencies nor imports anything into TIA.
