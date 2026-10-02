# Project History — Specs, Plans, Decisions, and Reports

**This tree is development-process history, not current documentation.**

It preserves the design records, implementation plans, decisions, lessons, and reports produced
while building features. Read it to understand why a boundary exists or what was verified at a
specific time.

Do not read a dated plan as a description of current behavior:

- each document describes the state known on its date;
- later commits may have completed work without updating historical checkboxes or resumption notes;
- source, tests, [current architecture](../ARCHITECTURE.md), and
  [current capabilities](../CAPABILITIES.md) are authoritative for present behavior; and
- the [documentation index](../README.md) is the entry point for users and contributors.

Historical records are retained for auditability and are intentionally not rewritten to recreate
unrecorded TDD cycles or review decisions.

## Specs

| Date | Document |
| --- | --- |
| 2026-08-08 | [Documentation reorganization](specs/2026-08-08-documentation-reorganization-design.md) |

## Implementation plans

| Date | Document | Historical outcome |
| --- | --- | --- |
| 2026-07-13 | [Native Windows handle-anchored traversal](plans/2026-07-13-native-windows-handle-traversal.md) | Completed; plan contains completion notes |
| 2026-07-13 | [Project-scale SimaticML ingestion](plans/2026-07-13-project-scale-simaticml-ingestion.md) | Later commits delivered the immediate project-index scope; the dated resumption note is stale |
| 2026-07-13 | [FBD and advanced FlgNet qualification](plans/2026-07-13-fbd-and-advanced-flgnet-qualification.md) | Native FBD corpus qualification remains incomplete |
| 2026-07-13 | [V21 SIMATIC SD adapter](plans/2026-07-13-v21-simatic-sd-adapter.md) | Adapter implementation has not started |

## Decisions and module cards

| Document | Purpose |
| --- | --- |
| [Artifact traversal contract](memory/artifact-traversal-contract.md) | Immutable artifact and secure traversal boundary |
| [Input-boundary module card](memory/input-boundary-module-card.md) | Responsibilities and interactions around untrusted input |
| [Native handle traversal decision](memory/native-handle-traversal-decision.md) | Decision record for the Windows traversal design |

## Lessons

| Document | Purpose |
| --- | --- |
| [Closures over open handles need explicit close](memory/lessons/closures-over-open-handles-need-explicit-close.md) | Resource-lifetime lesson from handle-backed artifacts |
| [Platform-gated module imported unconditionally in tests](memory/lessons/platform-gated-module-imported-unconditionally-in-tests.md) | Cross-platform test-collection lesson |

## Reports

| Date | Document |
| --- | --- |
| 2026-07-13 | [Native Windows handle traversal](memory/reports/2026-07-13-native-windows-handle-traversal.md) |
