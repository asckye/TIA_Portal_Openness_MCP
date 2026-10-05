# SimaticML Decoder

![PyPI - Downloads](https://img.shields.io/pypi/dm/simaticml-decoder)

Translate exported **SimaticML LAD/FBD** blocks (Siemens TIA Portal **V21**) into
readability-first **SCL** plus a **JSON metadata sidecar**.

## Why

A SimaticML export stores ladder logic as a `FlgNet` — a flat netlist of parts
and wires, not line-shaped rungs. Reading that graph by hand (or by eye, from the
editor) is token-heavy and error-prone: it is easy to connect the wrong variable
in a large block, and a single contact's negation is one diagonal slash. This tool
decodes the netlist deterministically, so what you analyse is folded logic with
explicit negation and an exact write/read cross-reference — not a graph you have to
trace yourself.

## How it works

Three cleanly separated, independently testable phases:

| Phase | Module | Input → Output |
| ------- | -------- | ---------------- |
| 1. Parse | `parse.py` → `model.py` | XML → faithful syntactic mirror |
| 2. Fold | `fold.py` → `ir.py` | model → boolean tree + assignments |
| 3. Emit | `emit.py` | IR → SCL text + JSON sidecar |

Supporting modules: `instructions.py` (the part catalog — data, not logic),
`operand.py` (Access → display string), `scl_reconstruct.py` (SCL networks, which
are reconstructed from their tokenised AST rather than folded).

Two commitments run through the design: every IR node keeps the source `UId` it
came from (so any rendered claim is traceable to a net), and anything the folder
cannot interpret is surfaced **loudly** rather than dropped — a silent omission in
authoritative-looking SCL is the worst possible failure.

## Scope

The production pipeline implements V21 FC/FB block parsing, LAD and shared-`FlgNet` FBD forms,
readability-first SCL/JSON emission, recursive secure discovery, and explicit project indexing.
Focused tests also cover `A`/`O` junctions, per-pin negation, common logic/math/timer/edge forms,
user calls, and the F-system boxes ACK_GL/ESTOP1/SFDOOR/FDBACK.

Implementation is not the same as native-format qualification. The current corpus does not
satisfy the project's redistributable, redaction-reviewed evidence gate, and no committed XML
fixture declares FBD or FBD_IEC. These areas are therefore **implemented, not corpus-qualified**.

SIMATIC SD is preserved or diagnosed but not decoded. GRAPH/SFC and STL semantic translation,
complex unobserved access forms, recompilable output, and live TIA Openness integration are
unsupported or deferred. See [Current capabilities](docs/CAPABILITIES.md) for the authoritative
matrix and evidence vocabulary.

## Fixture provenance and compatibility

Support claims require a sanitized, redistributable fixture, golden output or diagnostic, and a
non-skipping CI regression. Until those conditions are met, a format is not **qualified**.

In block and directory mode, exported SimaticML FC/FB XML is the working decode input. Project mode
also adapts recognized V21 FC/FB/DB/OB and UDT XML into a project index; unsupported XML remains
visible as a preserved artifact. SIMATIC SD `.s7dcl`/`.s7res` is not decoded. Output is
diagnostic/readability-first and not recompilable.

### Untrusted-input policy

Treat every export as untrusted. The CLI accepts regular UTF-8 SimaticML `.xml`
files only; it rejects direct and discovered symlinks, `.s7dcl`, `.s7res`, and
other file types. A resource-only `.s7res` receives the explicit
`SD_RESOURCE_WITHOUT_DCL` diagnostic when its declaration is absent from the
same export root—files are never paired across separate exports.

Directory scans are handle-anchored per platform. On Windows, the root
directory is opened once via native NT handles; child files are enumerated and
opened relative to their parent directory's own handle, never by re-resolving
the original path string. Every reparse point (junction, symlink, mount point)
is rejected during enumeration. On POSIX platforms, file descriptors and
`O_NOFOLLOW` are used to ensure traversal never follows symlinks. Platforms
without descriptor-relative filesystem support (`os.supports_dir_fd` and
`os.supports_follow_symlinks` unavailable) reject directory input outright
rather than falling back to path-based traversal. SIMATIC SD files are included
in discovery solely to report diagnostics; they are not decoded. A directory
that changes while being scanned aborts before any discovered file is decoded.

The current XML boundary rejects files over 10 MiB, input trees over 10,000 XML
files or 32 levels, DTD/entity declarations, XML documents over 100,000 elements
or 256 nesting levels, elements with over 100 attributes or 1 MiB of text, and
documents with over 1,000 `FlgNet` networks. Inputs are rejected, never
truncated. The future SIMATIC SD adapter must apply equivalent 10 MiB, 100,000
line/node, and 256 nesting-depth limits before parsing.

Malformed input is isolated per file. Default diagnostics use stable codes,
basenames only, and single-line bounded detail; they do not expose absolute
paths, raw parser output, or control characters. Folding/emission failures are
also isolated so another file in the same batch can continue.

See [`SECURITY.md`](SECURITY.md) for the full security model and vulnerability reporting.

## Install

```bash
pip install simaticml-decoder
```

For local development:

```bash
pip install -e ".[dev]"
```

Runtime dependencies: none (standard-library `xml.etree.ElementTree`). The `dev`
extra pulls lint, test, coverage, build, and package-validation tools.

## Development

After installing the `dev` extra, run the exact same two commands CI
(`.github/workflows/ci.yml`) runs before committing:

```bash
ruff check .
pytest -q --cov=simaticml_decoder --cov-report=term-missing --cov-fail-under=80
```

Both must pass locally. The 80% coverage floor is also recorded in
`pyproject.toml` (`[tool.coverage.report] fail_under = 80`), so it is enforced
even for a bare `pytest --cov=...` invocation, not only in CI. CI runs on
Python 3.11-3.14 on Ubuntu and Python 3.11 on Windows (the Windows job is a
platform-specific smoke test for the native, handle-anchored directory walk;
see "Untrusted-input policy" above), matching the versions declared in
`pyproject.toml`'s classifiers.

Please see [`CONTRIBUTING.md`](CONTRIBUTING.md) for development setup, the fixture policy, and the pull request workflow.

## Usage

```bash
# Single block -> SCL + JSON, written beside the input (or into -o OUTDIR):
simaticml-decode BLOCK.xml --format both     # --format {scl,json,both}

# Bulk: point at a directory to decode every .xml beneath it. The output tree
# mirrors the input's folder structure (blocks/motion/Axis.xml -> OUTDIR/motion/Axis.scl).
simaticml-decode blocks/ -o decoded/
simaticml-decode blocks/ --no-recursive      # top level only, skip subdirectories
```

In bulk mode each file is independent: a malformed block is reported on stderr and
skipped rather than aborting the run, and the process exits non-zero only when at
least one file failed. Pointing at a missing path or an empty directory is a quiet
no-op (exit 0). `-q` silences the per-file progress summary.

## Project mode

```bash
# Index a whole exported V21 project tree instead of decoding individual blocks:
simaticml-decode --project MyProject/
simaticml-decode --project MyProject/ -o out/ --library-root "PLC_1/Libraries"

# Every discovery/parse budget is independently overridable:
simaticml-decode --project MyProject/ --max-files 20000 --max-file-bytes 33554432
```

`--project ROOT` is a separate, explicit mode from the single-file/directory `PATH`
mode above -- exactly one of the two must be given. It discovers every `.xml`
artifact under `ROOT` (the same handle-anchored, symlink-rejecting walk described
below), classifies each as a block or UDT, resolves block-call and UDT-member
references across the whole project, and writes one analysis-only
`project-manifest.json` -- never `.scl`/`.json` sidecars. `--library-root` (repeatable)
overrides the default `Types/`-vs-`PLC_1/` origin convention for a given
project-relative subtree. The process exits `0` on success, `1` if any artifact
failed to be recorded at all, and `2` on a CLI usage error (e.g. passing both `PATH`
and `--project`).

Project mode is exercised against the same temporary, non-redistributable compatibility
corpus described above (see "Fixture provenance and compatibility") -- it demonstrates
that project-scale discovery/indexing runs against a real V21 export tree, not that
project-mode feature coverage itself is validated against a licensed corpus.

See [`docs/PROJECT_INPUT_CONTRACT.md`](docs/PROJECT_INPUT_CONTRACT.md) for the full
input contract: the V21-only compatibility profile, `--library-root` semantics, every
default budget, the diagnostic-code vocabulary, exit-code behavior, determinism, and
the explicit non-re-importability of the output.

## Documentation

- [Documentation index](docs/README.md) — current guides, contracts, architecture, and history
- [Current capabilities](docs/CAPABILITIES.md) — evidence-qualified support matrix and limitations
- [Architecture](docs/ARCHITECTURE.md) — block decode, project index, and input-safety design
- [Roadmap](ROADMAP.md) — directional priorities
- [Advanced translation roadmap](docs/roadmap/advanced-translation.md) — detailed phase boundaries

## Layout

```
src/simaticml_decoder/   parse · model · fold · ir · instructions · operand
                         scl_reconstruct · emit · cli
tests/                   authored separately; run by CI
.github/workflows/ci.yml ruff + pytest on push / PR
```

## Future

Priorities are maintained in [ROADMAP.md](ROADMAP.md). The next evidence gate is a sanitized,
redistributable V21 corpus, followed by native FBD/FBD_IEC qualification. The V21-specific SIMATIC
SD adapter and a separate future GRAPH front end remain planned work.

---
*Created with Claude AI*
