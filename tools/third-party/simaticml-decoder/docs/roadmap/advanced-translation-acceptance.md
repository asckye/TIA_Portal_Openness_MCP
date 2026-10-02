# Acceptance Criteria: Advanced Translation

**Spec:** [advanced-translation.md](advanced-translation.md)
**Date:** 2026-07-13
**Status:** Approved criteria; implementation status reconciled 2026-08-09.

---

## Scope

These criteria cover the approved immediate scope: TIA Portal V21 project exports, FBD qualification, project indexing, and SIMATIC SD. GRAPH and re-importability are explicitly deferred; they are not implementation gates for this phase.

## Criteria

| ID | Description | Test Type | Preconditions | Expected Result |
| --- | --- | --- | --- | --- |
| AC-001 | Discover a V21 SimaticML project export containing UDTs, user blocks, project-library blocks, and nested paths. | API | Committed SimaticML fixture root and its manifest are available. | The CLI/project ingestion command produces one deterministic manifest whose artifact order, qualified identities, source paths, and artifact kinds match the fixture manifest exactly. |
| AC-002 | Preserve project-library blocks as first-class artifacts. | Logic | A fixture contains a user block calling a block supplied by the project library. | The project index contains both blocks and records a resolved call edge from the user block to the library block. |
| AC-003 | Diagnose unresolved or ambiguous references without discarding unrelated artifacts. | API | A fixture contains one deliberately unresolved or ambiguous call and at least one independent valid block. | The result contains a structured diagnostic with source location and reference identity; the independent valid block remains present with a successful artifact status. |
| AC-004 | Process the separate SimaticML and SIMATIC SD exports of the same small project. | API | Committed fixture roots and a cross-format mapping manifest are available. | Each root is accepted as an independent input; normalized UDT, block, and library identities match the mapping manifest without requiring a combined export. |
| AC-005 | Qualify native FBD and FBD_IEC `FlgNet` exports. | Logic | Focused FBD and FBD_IEC fixtures cover signal flow, EN/ENO, calls, fan-out, and multiple outputs. | Each fixture produces the committed semantic-IR and JSON golden output; no fixture is labelled supported unless it executes in CI without skip. |
| AC-006 | Emit readability-first FBD output. | API | A qualified FBD fixture and its golden SCL/JSON artifacts are available. | The command writes readable SCL plus traceable JSON that matches the approved golden artifacts and marks the output as non-re-importable. |
| AC-007 | Preserve unknown `FlgNet` constructs visibly. | Logic | A fixture contains an intentionally unsupported part or shape. | The output contains an `Unhandled` or equivalent structured diagnostic with source identity; no executable-looking substitute is emitted for that construct. |
| AC-008 | Identify and parse the supported V21 SIMATIC SD dialect deterministically. | Logic | One code-only V21 `.s7dcl` fixture and one resource-backed `.s7dcl`/`.s7res` fixture are available. | The adapter accepts the code-only artifact, associates the optional resource only by the observed identity rule, reports the detected dialect/version, and emits normalized artifacts matching the committed JSON golden files. |
| AC-009 | Preserve SIMATIC SD comments, source locations, and unknown fields. | Logic | A paired SD fixture contains multilingual comments and at least one unknown or unsupported field. | JSON output retains the original source location, comment association, and unknown-field payload without interpreting it as executable code. |
| AC-010 | Enforce documented input and traversal budgets. | API | Fixtures exceed each configured size, depth, file-count, XML/`FlgNet`/call-reference complexity, or SIMATIC SD parser-complexity limit independently. | Each invocation exits with a structured limit diagnostic naming the exceeded budget; it does not process files outside the configured project root. |
| AC-011 | Keep multi-artifact project results deterministic and recoverable. | API | A mixed valid/invalid fixture project is processed twice using the same configuration. | Both runs produce identical manifest ordering and statuses; each artifact is `complete`, `partial`, `preserved`, or `failed`, with a structured diagnostic whenever it is not complete. |
| AC-012 | Make the committed fixture corpus runnable from a fresh clone. | Logic | CI uses only tracked fixture files and project metadata. | The integration/E2E suite runs all FBD, project-index, and SIMATIC SD cases without fixture-related skips. |
| AC-013 | Meet the repository quality gate for the immediate scope. | Logic | Full test suite and coverage configuration are installed. | Ruff succeeds, all tests pass, and total coverage is at least 80% with non-skipping integration coverage for the supported formats. |
| AC-014 | Keep GRAPH and re-importability out of the immediate implementation claim. | Logic | The support matrix and CLI output metadata are generated for the immediate release. | GRAPH is labelled deferred with its future JSON state-machine direction; no artifact is labelled re-importable and no TIA import file is generated. |

## Coverage check

- Project input, UDTs, user blocks, library blocks, deterministic traversal, and cross-reference resolution: AC-001 through AC-004.
- FBD qualification and safe fallback behavior: AC-005 through AC-007.
- SIMATIC SD parsing and preservation: AC-008 and AC-009.
- Scale, failure isolation, deterministic results, and CI corpus availability: AC-010 through AC-013.
- Approved deferrals and output-boundary constraints: AC-014.

## Current status

Status meanings:

- **Met:** the current implementation and automated evidence satisfy the criterion as written.
- **Partial:** meaningful implementation or evidence exists, but at least one explicit precondition
  or expected result is incomplete.
- **Not met:** the required adapter, native fixture, or end-to-end behavior does not exist.
- **Deferred:** intentionally outside the immediate implementation claim.

| ID | Status | Current evidence and remaining gap |
| --- | --- | --- |
| AC-001 | **Met** | Project mode discovers nested V21 blocks and UDTs and produces a deterministic manifest checked against tracked golden data. |
| AC-002 | **Met** | Project-library artifacts remain first-class and unique user-to-library call edges are resolved. |
| AC-003 | **Met** | Ambiguous and unresolved references produce structured diagnostics without removing independent valid artifacts. |
| AC-004 | **Not met** | Both export roots are tracked, but SIMATIC SD content is not normalized through an adapter and cross-format identities are not produced by the application. |
| AC-005 | **Not met** | No committed fixture declares FBD or FBD_IEC, so native parity cannot be qualified in CI. |
| AC-006 | **Partial** | The shared `FlgNet` path can emit readability-first output for implemented FBD forms, but qualified native fixtures and approved FBD goldens are absent. |
| AC-007 | **Met** | Unknown instructions and unsupported forms remain visible as `Unhandled` values and warnings; native FBD qualification is still governed separately by AC-005. |
| AC-008 | **Not met** | There is no V21 SIMATIC SD dialect detector, content-identity resource pairing, parser, or normalized golden output. |
| AC-009 | **Not met** | SIMATIC SD comments, spans, and unknown fields are not parsed into a structured representation. |
| AC-010 | **Partial** | File, tree, XML, `FlgNet`, and reference-edge budgets are enforced; SIMATIC SD parser-complexity, duration, and peak-memory gates remain. |
| AC-011 | **Met** | Project results use deterministic ordering and explicit complete/partial/preserved/failed statuses with diagnostics. |
| AC-012 | **Partial** | Project-index corpus tests run from tracked files without fixture-related skips; required native FBD and SIMATIC SD adapter cases remain incomplete. |
| AC-013 | **Partial** | Ruff, 228 tests, and 91.46% coverage passed on 2026-08-09 against an 80% floor; supported-format qualification remains blocked by AC-005, AC-008, AC-009, and AC-012. |
| AC-014 | **Met** | GRAPH is deferred, outputs are labelled analysis-only/non-re-importable, and no TIA import artifact is generated. |

The automated snapshot above is offline evidence. It does not constitute live TIA Portal
validation or replace the native-format provenance gates in the individual criteria.
