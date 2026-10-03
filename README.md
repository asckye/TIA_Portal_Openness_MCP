# TIA Portal Openness MCP

[Chinese](README.zh-CN.md) · [Documentation](docs/README.md) · [Downloads](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/latest)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE) [![Release](https://img.shields.io/github/v/release/asckye/TIA_Portal_Openness_MCP)](https://github.com/asckye/TIA_Portal_Openness_MCP/releases) [![validate-bundle](https://github.com/asckye/TIA_Portal_Openness_MCP/actions/workflows/validate.yml/badge.svg)](https://github.com/asckye/TIA_Portal_Openness_MCP/actions/workflows/validate.yml)

Connect AI clients to Siemens TIA Portal V14 SP1, V15.1 and V16–V21 through version-specific Openness adapters. V14 SP1–V19 expose the PLC foundation catalog; V20/V21 retain the full engine catalog. [Version matrix and API gaps](docs/reference/version-tools.md). Includes an MCP server, a standalone WPF configurator, a JSON/YAML command-line workflow and reusable PLC / WinCC Unified templates.

![TIA Portal MCP architecture](docs/assets/architecture.svg)

The [v3.2.0 release](docs/releases/v3.2.0.md) includes all eight MCP runtimes and Studio. To rebuild the complete bundle, run `scripts/build/Build-MultiVersion.ps1 -PublicApiRoot <SDK-root> -Test`. Install Siemens TIA Portal, Openness and licenses separately. Full engines require .NET Framework 4.8; foundation hosts also require .NET 8 and ASP.NET Core 8; Studio requires .NET 10 Desktop Runtime. Native TIA acceptance for the newly enabled targets remains pending.

## Start here

Follow the [beginner guide (Chinese)](docs/getting-started/beginners.zh-CN.md) for installation, version selection, AI client setup, Studio operations and a first SCL import/compile exercise.

Download and extract the **TIA_MCP_Delivery** ZIP from [Releases](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/latest). Open **TiaMcpConfigurator.exe** at its root. Later releases update in place from the configurator's **Update → Update engine** menu (the configurator supports Chinese and English; stop the engine first; the configurator closes itself, `scripts\operations\Update-Engine.ps1` downloads and verifies the latest release in its own window, backs up to `.previous\`, replaces the files with robocopy and reopens the configurator; the TIA machine needs access to github.com; `-Rollback` goes back). The same script can be run by hand; the engine's `CheckForUpdate` tool only reports versions.

| Scenario | Configuration |
|---|---|
| TIA in a virtual machine | Keep the **VM ↔ host** mode. On the VM fill pane **A**: TIA version, installation root, IPv4, port and the shared key, then **Network permissions** and **Start service**. |
| AI on the host | Copy the configurator EXE to the host, enter the same address, port and key, pick clients in pane **B**, then **Test connection** and **Write client config**. TIA is not required on the host. |
| TIA and AI on one computer | Switch to **Same computer**. Pane A only needs version and path; clients launch the matching engine over stdio, so no address, port or key is used. |

The configurator and Studio both support Chinese and English, with Glass Layers light and dark themes.

Cards, CLIs first: Claude Code, Codex, Gemini CLI, Qwen (writes Qwen Code), Kimi (Kimi Code CLI), Yuanbao (CodeBuddy Code), DeepSeek / GLM / Grok (OpenCode, pick the provider there), then the Qwen Agent desktop app, then Cursor and VS Code / Copilot. Card names are English throughout. The model brands and Grok have no MCP client of their own, so their cards write the vendor CLI or OpenCode. For official Claude's **Code** page select **Claude Code**. Prerequisites and client-specific limits are in the [configuration guide](docs/getting-started/configuration.md).

Keep the configurator open while it runs the VM service. Restart configured clients and open a new session.

## Command line

From the package root, use the runtime matching your installed TIA version:

```powershell
.\runtime\v21\TiaMcpServer.exe doctor
.\runtime\v21\TiaMcpServer.exe schema
.\runtime\v21\TiaMcpServer.exe gen .\templates\project-blueprints\scaffold_spec_motor.json --dry-run
```

The last command validates the specification offline. Remove `--dry-run` when ready to create the project. V20 uses `runtime/v20/TiaMcpServer.exe`. See the [CLI guide](docs/getting-started/cli.md).

## Capabilities and limits

The V21 full-engine static inventory contains **488 tools** (V20 exposes 477) in 7 categories (session, project, plc, plc-online, hardware, hmi, runtime); default **lite** advertises **63**, with the remainder available through `FindTools` / `CallTool` (`ListToolCategories` shows the taxonomy, `FindTools(category=…)` browses one area). The running server's `tools/list` is authoritative. Use `--profile full` for full exposure.

Before an unfamiliar call, use `GetToolUsage(toolName)` for the selected version's MCP example/template, exact parameters, workflow and official source references.
All eight profiles include this offline reference tool; [coverage and provenance](docs/development/official-tool-usage.md) distinguish official patterns from project templates and native acceptance.

Version 3.1.0 offers opt-in [Openness worker isolation](docs/guides/openness-worker-isolation.md) with `--isolate-openness`. It keeps host diagnostics available after worker failure; real TIA acceptance and coordination between separate MCP services remain pending.

`master` is the only maintained branch. The configurator selects all eight release keys; each legacy runtime contains one exact-release worker. Studio calls Openness directly through its matching adapter. Historical branch heads remain documented in [version history](docs/reference/version-branches.md). Availability, compiled API checks and native acceptance are separate states.

Capabilities include project/session management, PLC blocks/types/tags, hardware/network engineering, WinCC Unified, file exchange, libraries, version control and read-only online monitoring. See the [tool matrix](docs/reference/tool-matrix.md) and [acceptance boundaries](docs/reference/capabilities.md). Tool availability does not imply full Siemens API coverage or real-project acceptance.

Delivery and engine versions are independent. [Delivery metadata](manifest/delivery.json) binds the engine and configurator records; [validation documentation](docs/development/validation.md) separates offline, API and real TIA checks.

## Repository map

| Directory | Contents |
|---|---|
| [docs](docs/README.md) | Setup, guides, references, troubleshooting, development and history |
| [runtime](runtime/README.md) | Eight version-specific MCP runtimes, Studio and dependencies |
| [tools](tools/README.md) | Engine source/tests, AI skill and WPF source |
| [scripts](scripts/README.md) | Build, checks, generators, diagnostics and operations |
| [templates](templates/README.md) | PLC/HMI assets and project specifications |
| [manifest](manifest/README.md) | Inventory, versions, build receipts and hashes |

Root launch/configuration CMD/BAT files were replaced by the GUI. Delivery ZIPs no longer duplicate runtimes into legacy `bin/Release` paths; use `runtime/v20` or `runtime/v21`.

Read [CONTRIBUTING](.github/CONTRIBUTING.md), [validation](docs/development/validation.md) and the [release workflow](docs/development/release-workflow.md). Changes target `master`. Questions: [SUPPORT](.github/SUPPORT.md); vulnerabilities: [SECURITY](.github/SECURITY.md) (private reporting, never a public issue). History: [CHANGELOG](CHANGELOG.md), per-version notes in [docs/releases](docs/releases/), [archived release notes](docs/archive/release-notes.md) for v2.7.2–v2.7.15.

Independently maintained by asckye. Provenance and third-party notices remain in [NOTICE](NOTICE.md) and [LICENSE](LICENSE).

### Integrated desktop (unreleased)

[TIA Openness Studio](tools/tia-openness-studio/README.md) adds an English/Chinese WPF desktop and offline demo. Its live workflows select one of eight Openness adapters through a local native bridge, independently of MCP; the original Studio source and history are preserved. Studio is included in the release ZIP; new native acceptance remains pending.
