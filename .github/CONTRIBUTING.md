# Contributing

This project is independently maintained by asckye. Original copyrights and
provenance are retained in NOTICE.md and LICENSE. The bar for a change is simple: **does it help someone drive a real
TIA Portal project without clicking through the UI?**

Issues, bug reports and PRs are all welcome, in **English or Chinese** — both are
read. 中英文皆可，随便哪种都行。

---

## Before you open an issue

Most problems on Windows + Openness are environment problems, and the bundle can
tell you which one:

```bat
runtime\v21\TiaMcpServer.exe doctor          :: TIA V21
runtime\v20\TiaMcpServer.exe doctor      :: TIA V20
```

It checks the TIA installation, the exe/version match, the local
`Siemens TIA Openness` group and the host registration, and prints the exact fix
for each. Paste its output into the issue — that alone usually settles it.

Please include:

- TIA Portal version (V20 or V21) and Windows version
- Which exe / branch you are on (`runtime\v21\TiaMcpServer.exe version`)
- The MCP client (Cursor, VS Code, Claude Desktop, own HTTP client) or the CLI
  command you ran
- What you expected, what happened, and the full error text

**Do not attach real customer projects.** If a project is needed to reproduce,
strip it down to the smallest block that still fails.

Issue templates live in [`.github/ISSUE_TEMPLATE/`](ISSUE_TEMPLATE).

---

## Which branch does my change go to?

All changes target this repository's `master`, which supports TIA Portal V20 / V21.
The two versions share source and are built with their matching PublicAPI assemblies.
Check both runtimes when a shared change can affect either version.

`master` is the only branch of this repository: no release, older-version or bot
branches (the maintainer bumps the workflow action versions by hand, so there is no
Dependabot configuration), and no synchronization with the original repository.
Preserve LICENSE, NOTICE.md and dependency notices.

---

## Pull requests

1. Keep it focused. One problem per PR; a 40-line PR gets merged, a 4000-line one
   waits for a weekend that may not come.
2. Say **how you verified it**. For anything touching the Openness layer, separate offline/API checks from real-project acceptance. For the latter: import the block,
   run `CompileSoftware`, and report the actual error/warning counts. "It builds" is not
   real-project acceptance — Openness accepts plenty of input that only explodes at compile
   time.
3. If you could not test on real hardware or a real TIA install, say so plainly in
   the PR. An honest "untested on real TIA V20/V21" is far more useful than silence.
4. Match the surrounding style. This is a mixed C# / PowerShell / docs repo; each
   part already has a convention.
5. Update the docs you invalidate — `docs/` and
   `tools/tiaportal-mcp/skill/SKILL.md` (the tool spec) are part of the product,
   not an afterthought.
6. Add a `CHANGELOG.md` entry under `[Unreleased]` for user-visible changes ([Keep a Changelog](https://keepachangelog.com/en/1.1.0/) layout: newest first, ISO dates); the maintainer moves it under the version heading at release time.

### Please do not commit

TIA project files (`.ap16`…`.ap21`), `bin/` or `obj/`, logs, screenshots,
backups, machine-specific absolute paths, scratch/verification projects, or any
customer data.

---

## Development

- **Offline suite** (no TIA needed, .NET 8 SDK): `dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj -c Release` - it is a console program, `dotnet test` runs nothing. Repository checks: `python scripts/checks/Check-Repository.py`, `python scripts/checks/Check-DeadToolReferences.py`. Details: [docs/development/validation.md](../docs/development/validation.md).
- **Engine build** needs the Siemens Openness PublicAPI of V20 and V21 on the machine (licensed, never committed): `scripts/build/Build-Release.ps1` builds both engines, runs the offline suite and the API shape checks, regenerates `manifest/*` and writes the binaries to `runtime/v20`, `runtime/v21` and `TiaMcpConfigurator.exe`. Those binaries are **not tracked in Git** (since 2.8.1; a fresh clone has none until the script has run) - only their hashes are, in `manifest/release-build.json` and `manifest/configurator-build.json`, and the maintainer's `Release.ps1` uploads the delivery ZIP straight to the GitHub release. Any change under `tools/tiaportal-mcp/src` or `tests` invalidates the committed manifests until that script has run - the `validate-bundle` workflow refuses a mismatch. Layout: [docs/development/repository-layout.md](../docs/development/repository-layout.md).
- **Tool conventions**: every tool description starts with `[L?][Domain][OPERATION]` (registered domains in `ModelContextProtocol/ToolTaxonomy.cs`); every parameter carries a `[Description]` - the schema hints, `PreflightToolCall` and the examples are generated from it, and the build refuses when the count of undocumented parameters goes up; write tools default to `dryRun=true`; a name mentioned in a description must be a real tool (`Check-DeadToolReferences.py`).
- **Commit messages**: one imperative English sentence saying what changed and why (long is fine); no AI attribution trailers (`Co-Authored-By`, "Generated with ...") anywhere - commits, PRs or releases. Releases are cut by the maintainer with `scripts/build/Release.ps1` ([docs/development/release-workflow.md](../docs/development/release-workflow.md)); version tags are `vX.Y.Z` and point at `master` HEAD.
- **Real-machine facts** learned while testing go into [docs/development/handoff.md](../docs/development/handoff.md) section 6 and the [real-machine ledger](../docs/reference/real-machine-ledger.md), not into commit messages only.

---

## Encoding traps (this bites everyone once)

This repo is edited on Chinese Windows, and text encoding is the most common cause
of a "mysteriously broken" file:

- `.s7dcl` and Openness XML must be saved as **UTF-8 *with* BOM**.
- `.scl` must be **UTF-8 *without* BOM**.
- `.ps1` scripts that contain Chinese text must be **UTF-8 with BOM**, otherwise
  Windows PowerShell 5.1 silently swallows a line ending and eats the next line
  into a comment.
- Do not "fix" mojibake by rewriting the text — check the encoding first.

---

## Scope

This project drives TIA Portal through the official **Siemens Openness** API. It
does not ship, unlock, or work around any Siemens licensing, and it does not
bundle Siemens installation media. Contributions must stay on that side of the
line.

## Licence

By contributing you agree that your contribution is licensed under the
[MIT Licence](../LICENSE), the same as the rest of the project.
