# Documentation Reorganization Design

**Status:** Approved design, 2026-08-08

## Objective

Reorganize and expand the `simaticml-decoder` documentation so a reader can distinguish current
behavior, evidence-qualified capability claims, future direction, and historical development
records without reconstructing the project from dated plans and Git history.

The organization follows the successful documentation pattern used by `tia-portal-mcp`: a concise
root README, a trustworthy documentation hub, current architecture and capability authorities,
separate directional and detailed roadmaps, and an explicitly historical `docs/superpowers/`
tree.

## Scope

This is a documentation-only change on the current branch. It may create, move, and edit Markdown
files and update their internal links. It does not change Python code, fixtures, tests, packaging,
Git branches, or the untracked `temp/Main_Safety_RTG1.xml` file.

Existing dated implementation plans, decisions, reports, and lessons remain audit records. Their
checkboxes and historical execution instructions will not be rewritten to imply that an original
TDD or review step was recorded when it was not.

## Documentation authority

The reorganized documentation will use this authority order:

1. Source code, tracked tests, and CI configuration are authoritative implementation evidence.
2. `README.md`, `docs/ARCHITECTURE.md`, `docs/CAPABILITIES.md`, and the detailed public contracts
   describe current behavior.
3. `ROADMAP.md` and `docs/roadmap/` describe approved direction and remaining work.
4. `docs/superpowers/` preserves dated process history and is not a current behavior reference.

Every current capability claim must identify its evidence boundary. Offline parsing tests do not
prove TIA importability, runtime behavior inside TIA Portal, or compatibility with unrepresented
exports.

## Target structure

```text
README.md
ROADMAP.md
docs/
  README.md
  ARCHITECTURE.md
  CAPABILITIES.md
  PROJECT_INPUT_CONTRACT.md
  SIMATICML_READING_GUIDE.md
  roadmap/
    advanced-translation.md
    advanced-translation-acceptance.md
  superpowers/
    README.md
    specs/
    plans/
    memory/
```

### Root README

`README.md` remains the public entry point. It will retain the project purpose, quick start, usage,
and input-safety summary while reducing roadmap prose to links. Its scope section will use the
support vocabulary below instead of presenting manually observed or synthetic-test behavior as
fully qualified support.

### Documentation hub

`docs/README.md` will list every public document by audience and purpose. A public document under
`docs/` is not complete until it is listed in this hub. The sections will be:

- using the decoder;
- understanding the design;
- formats and contracts;
- project direction; and
- project history.

### Current architecture

`docs/ARCHITECTURE.md` will describe the current implementation rather than the original scaffold:

- single-file and recursive directory decoding;
- parse, stable XML model, fold, semantic IR, and emit stages;
- SCL reconstruction as a separate path;
- explicit project-mode discovery, V21 adaptation, identity resolution, and manifest emission;
- handle-anchored or descriptor-relative filesystem traversal;
- input limits, failure isolation, deterministic ordering, and analysis-only output; and
- automated-test versus fixture-qualification evidence.

It will link to source modules for implementation detail and to the input contract for normative
project-mode behavior.

### Current capabilities

`docs/CAPABILITIES.md` will be the authoritative support matrix. It will distinguish:

- **Qualified:** implemented and exercised by committed representative evidence in CI.
- **Implemented, not corpus-qualified:** production logic and focused tests exist, but the native
  representative corpus gate is incomplete.
- **Preserved only:** input is inventoried or retained with diagnostics but is not translated.
- **Unsupported or deferred:** no current translation claim.

The document will cover block kinds, programming languages, instruction families, project mode,
SIMATIC SD, GRAPH/SFC/STL, output fidelity, and re-importability. It will state the provenance and
licensing limits of the current V21 evaluation corpus and will not use the untracked V16 safety
file as support evidence.

### Roadmaps and acceptance status

Create concise `ROADMAP.md` for directional priorities. Move the detailed advanced roadmap and
acceptance criteria into `docs/roadmap/` and update their links.

The detailed roadmap will report phase status without erasing history:

- evidence and compatibility contract: partial;
- native FBD qualification: not complete;
- advanced `FlgNet`: partially implemented, not fully qualified;
- immediate serial project-ingestion subset: delivered;
- scale follow-ons: planned;
- SIMATIC SD adapter: not started;
- GRAPH representation: deferred.

The acceptance document will record current AC-001 through AC-014 status as `met`, `partial`,
`not met`, or `deferred`, with a short evidence note. Passing unit tests alone will not satisfy a
criterion that explicitly requires a committed native fixture or cross-format adapter.

### Historical documentation

`docs/superpowers/README.md` will explain that dated specs, plans, decisions, reports, and lessons
are historical process material. It will index the existing records and identify current
documentation as the authority for behavior.

`docs/IMPLEMENTATION_PLAN.md` and `docs/PROJECT_CAPABILITY_ASSESSMENT.md` will keep their existing
bodies but receive prominent historical-snapshot notices linking to the current architecture,
capabilities, and roadmap. Existing detailed plan checkboxes remain untouched.

## Link and compatibility policy

All repository-relative Markdown links affected by moved roadmap files will be updated. Root and
docs indexes will use relative links suitable for both GitHub and local rendering. No duplicate
current authority will remain at the old advanced-roadmap paths.

Because the project has no published documentation-URL compatibility contract, the two advanced
documents may move into `docs/roadmap/`; repository references to their old paths must be removed.
Git history remains the recovery path for the old locations.

## Validation

The documentation change is complete only when:

1. every public document is listed in `docs/README.md`;
2. every link to a repository-local Markdown file resolves;
3. current docs do not describe `parse.py`, `fold.py`, `emit.py`, or project mode as unimplemented;
4. historical statements remain visibly labeled as historical rather than silently corrected;
5. the support matrix does not claim native FBD or SIMATIC SD qualification without the required
   committed evidence;
6. `git diff --check` passes;
7. Ruff passes; and
8. the full pytest suite passes using a repository-local pytest base directory on this host.

## Non-goals

- No Python, fixture, CI, packaging, or generated-artifact changes.
- No attempt to qualify the untracked V16 safety export.
- No new SIMATIC SD, FBD, GRAPH, or project-scale implementation.
- No retroactive reconstruction of unrecorded red/green test cycles or reviewer decisions.
- No commit, push, release, or pull request unless separately requested.
