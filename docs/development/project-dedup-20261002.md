# Project deduplication review — 2026-10-02

> Historical implementation snapshot. Current version routing, direct Studio integration,
> tool counts and validation are maintained in [the release matrix](../reference/version-tools.md).
> Previous build/runtime restrictions and MCP-backed desktop descriptions below are superseded where that matrix says so.


Baseline: `701afc5187c70873c87637b4ed6521b563240c28`.

## Implemented

- Native-call diagnostics now share one tracking/enumeration implementation. Runtime-specific
  partial classes retain Newtonsoft or System.Text.Json journal serialization.
- Legacy and modern candidate JSON protocols source-link the same session and strict codec.
  Type aliases preserve both public APIs; parser and encoder branches retain their original libraries.
  This does not connect the candidate protocols to production or enable historical versions.
- Studio Git and MCP ecosystem tools share process I/O. Configurator, isolated worker and router
  also share argument quoting. Existing router formatting, MCP result fields, timeouts and limits remain.
- Read-only Windows registry access, .NET release detection and path version parsing share one implementation.
  Studio still discovers API installations; Configurator still offers installation roots; the engine still
  resolves exact assemblies. Their selection order and report models are intentionally separate.
- Patch and scaffold share PLC element/source import, compile reporting, and Unified HMI setup.
  Create/open decisions, hardware creation, dry-run defaults and LAD overwrite policy remain at entrypoints.
- SCL/LAD FC document envelopes and FC/FB interface sections use the existing XML helper.
- HMI package fixtures, identical PLC fixture XML and library-section report formatting are shared.

Functional checks also exposed two defects: Studio accepted exit code 1 for every Git command, and
its child bridge request stream used the console code page. Only `diff --no-index` now accepts exit 1;
the bridge explicitly sends UTF-8. The shared process writer also sends UTF-8 on .NET Framework.

## Audit scope and retained separation

The scan covered 887 owned source/build files at baseline and 893 after the implementation.
It excluded vendor code, official reference material, pinned upstream archives and design inputs.
It compared whitespace-normalized full files and overlapping 20-line C# windows of at least
700 characters, excluding comments and test directories. Pairs present in 12 or more files were
excluded as common scaffolding. Nine of the ten reported implementation pairs were consolidated.
Window counts overlap and are not line counts; this is not a proof of zero semantic duplication.

| Retained area | Reason |
|---|---|
| Eight identical adapter project shells | Local Release.props chooses different framework, SDK identity and output; native sources already shared |
| AsyncPreview / HostPreview project shells | Implicit compile items select different protocol layers |
| HMI screen / HMI tag / PLC tag bulk import | Similar input preparation, different failure behavior: stop after failed screen write, collect HMI tag failures, collect only expected PLC exceptions; preserve native batch policies |
| Studio native session / MCP Portal operations | Different session ownership, result contracts, overwrite rules and export lifecycles; their common support code is shared, but broad operation merging would change behavior |
| Native inspection / offline XML quality analysis | Different input evidence and coverage; equal rule names do not establish interchangeable implementations |
| Doctor report models | Desktop and MCP expose different reports; common machine facts are shared underneath |
| Upstream and third-party archives | Provenance and license evidence, not duplicate active build inputs |

## Verification

- Both engine identities: 3,048 offline checks each, including actual Git init/stage/commit/history,
  Unicode paths and input, output truncation, child timeout, and shared scaffold workflows.
- Studio: 37 client/bridge tests and 604 WPF tests, including in-process and child-process
  Unicode workspace creation, mapping, sync and Git diff. V20/V21 native targets compile.
- JSON protocol suites are now included in offline CI: 5,462 differential legacy checks and 343 modern checks; net461/net48 compile.
- Eight adapter/worker pairs compile against their exact official references. 124 portable
  diagnostics and PE isolation checks pass; no worker or Siemens module is executed by that check.
- A .NET Framework 4.8 process fixture verifies the exact UTF-8 bytes sent to real Git by SHA-1.
- Full Build-Release passed in both the verification clone and destination checkout. Strict binary
  and source bundle validation passed; Configurator passed 144 tests. The destination repository
  check covered 284 Markdown files with no broken links; all 486 tool descriptions resolve.
  V20/V21 each passed 4,484 local calls in both direct and isolated modes (17,936 in total).

Development was performed and tested in a workspace clone before synchronization to the main
checkout. Native TIA start/attach, project import/save and PLC operations were not run. Native
acceptance remains pending; compilation and mock workflows are not native project acceptance.
