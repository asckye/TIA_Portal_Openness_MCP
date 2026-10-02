# Current Capabilities

This is the authoritative capability and evidence matrix for `simaticml-decoder` 0.2.3. Source,
tracked tests, and CI configuration remain the final authority if this document drifts.

## Status vocabulary

| Status | Meaning |
| --- | --- |
| **Qualified** | Implemented and exercised in CI by committed representative native input with acceptable provenance and expected output or diagnostics. |
| **Implemented, not corpus-qualified** | Production logic and focused tests exist, but the representative native corpus gate is incomplete. |
| **Preserved only** | The artifact is discovered, inventoried, or retained with diagnostics; its logic is not translated. |
| **Unsupported or deferred** | No current translation claim is made. |

No input format currently meets the repository's full **Qualified** definition because the tracked
V21 evaluation corpus is non-redistributable and has not been redaction-reviewed. This does not
invalidate the automated implementation evidence; it limits the strength of public format-support
claims.

## Processing and format matrix

| Area | Current status | Evidence and boundary |
| --- | --- | --- |
| V21 FC/FB block XML intake | **Implemented, not corpus-qualified** | Parser, CLI, regression, and golden-output tests exist. Current native evaluation data lacks a redistributable provenance basis. |
| LAD `FlgNet` folding | **Implemented, not corpus-qualified** | Series logic, OR/AND junctions, fan-out, negation, coils, assignments, latches, timers, edges, comparisons, arithmetic, and calls have focused and regression coverage. The native corpus gate remains incomplete. |
| FBD/FBD_IEC `FlgNet` folding | **Implemented, not corpus-qualified** | FBD maps to the shared `FlgNet` path and relevant forms have unit coverage, but no committed fixture declares FBD or FBD_IEC. |
| F-system boxes `ACK_GL`, `ESTOP1`, `SFDOOR`, `FDBACK` | **Implemented, not corpus-qualified** | Catalog and folding tests cover known pins and negation behavior. No sanitized committed V21 safety-block fixture qualifies the claim. |
| Native SCL network reconstruction | **Implemented, not corpus-qualified** | Tokenized SCL networks are reconstructed through a separate path and covered by tests; output remains readability-first. |
| Recursive block-directory mode | **Implemented, not corpus-qualified** | Deterministic secure discovery, mirrored output, failure isolation, and CLI behavior are tested on Windows and POSIX paths. |
| V21 project indexing | **Implemented, not corpus-qualified** | Recognized blocks and UDTs, origins, deterministic manifests, block/UDT reference resolution, and diagnostics are covered by project tests and a real V21 local-evaluation corpus. |
| Non-V21 or versionless recognized XML | **Preserved only** | Project mode records identity and `UNSUPPORTED_TIA_VERSION` or `UNKNOWN_TIA_VERSION`; it does not force V21 translation. |
| PLC tag-table and other unrecognized XML | **Preserved only** | Project mode records the artifact with `UNSUPPORTED_ARTIFACT`. |
| SIMATIC SD `.s7dcl`/`.s7res` | **Preserved only** | Discovery reports unsupported and unpaired-resource states. There is no production SD dialect detector, parser, or normalized adapter. |
| GRAPH/SFC and STL semantics | **Unsupported or deferred** | Source remains visible where the XML model permits, but no qualified semantic translation or executable-looking output is promised. GRAPH requires a future state-machine IR. |
| Absolute, indirect, complex array, and unobserved `Operation` rendering | **Unsupported or deferred** | The parser may retain source shape; rendering must remain visible as unsupported until observed fixtures and policies exist. |
| TIA Portal import or recompilation | **Unsupported or deferred** | SCL, JSON sidecars, and project manifests are analysis artifacts, not TIA import files. |

## Project mode

`simaticml-decode --project ROOT` is a separate mode from block decoding. It performs serial,
bounded, deterministic discovery and writes one atomic `project-manifest.json`. It supports:

- V21 block and PLC-type adaptation;
- user, project-library, and unknown origin classification;
- repeatable validated `--library-root` overrides;
- unique-only block-call and UDT-member resolution;
- explicit duplicate, ambiguous, unresolved, unsupported, and malformed diagnostics; and
- stable root-relative paths, ordering, statuses, and output.

Cyclic calls remain visible as ordinary resolved edges; they do not produce a cycle-specific
diagnostic.

It does not provide concurrency, streaming, cancellation, checkpoints/resume, live TIA access, or
cross-format SIMATIC SD normalization. The normative behavior is in
[PROJECT_INPUT_CONTRACT.md](PROJECT_INPUT_CONTRACT.md).

## Output fidelity

- SCL is optimized for readable analysis, not byte fidelity or recompilation.
- JSON sidecars preserve structured interface, instruction, warning, cross-reference, and source
  trace data supported by the current model.
- Project manifests are deterministic inventories and reference graphs with per-artifact status.
- Unknown or unsupported constructs must remain visible; silence is not a fallback policy.
- No output is described as re-importable.

## Corpus provenance

The tracked compatibility corpus was exported from the public
`felipebojorquem/sorting-cell-s7-1200` repository as separate SimaticML and SIMATIC SD roots. That
upstream repository declares no license. The fixture manifest therefore records the corpus as:

- `redistributable: false`;
- `redaction_reviewed: false`; and
- `status: local-evaluation-only`.

Its declared cases are `preserved-only` or `unsupported`; it demonstrates parser and project-index
behavior but does not qualify public format support. The untracked `temp/Main_Safety_RTG1.xml` file
declares Engineering V16 and is not support evidence.

## Verification snapshot

The 2026-08-09 local audit on Windows with Python 3.14.5 produced:

| Check | Result |
| --- | --- |
| Ruff | Passed |
| Pytest | 228 passed, 3 skipped |
| Coverage | 91.46% total; configured floor 80% |
| Skips | Windows symlink-creation privilege unavailable; no fixture-related skips |
| Wheel | `simaticml_decoder-0.2.3-py3-none-any.whl` built successfully |
| Twine | Wheel metadata check passed |

This is automated/offline evidence. It is not live TIA Portal validation.

## Planned capability work

See the [directional roadmap](../ROADMAP.md), the
[detailed advanced translation roadmap](roadmap/advanced-translation.md), and the
[acceptance status](roadmap/advanced-translation-acceptance.md).
