# TIA Portal Openness MCP

[简体中文](README.zh-CN.md) · [Documentation](docs/README.md) · [Downloads](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/latest)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE) [![Release](https://img.shields.io/github/v/release/asckye/TIA_Portal_Openness_MCP)](https://github.com/asckye/TIA_Portal_Openness_MCP/releases) [![validate-bundle](https://github.com/asckye/TIA_Portal_Openness_MCP/actions/workflows/validate.yml/badge.svg)](https://github.com/asckye/TIA_Portal_Openness_MCP/actions/workflows/validate.yml)

Connect AI clients to Siemens TIA Portal V20/V21 through the official Openness API. Includes an MCP server, a standalone WPF configurator, a JSON/YAML command-line workflow and reusable PLC / WinCC Unified templates.

![TIA Portal MCP architecture](docs/assets/architecture.svg)

The complete release ZIP includes both runtimes and dependencies. Install Siemens TIA Portal, Openness and licenses separately. The Windows server requires .NET Framework 4.8 and membership in the `Siemens TIA Openness` group.

## Start here

Download and extract the **TIA_MCP_Delivery** ZIP from [Releases](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/latest). Open **TiaMcpConfigurator.exe** at its root.

| Scenario | Configuration |
|---|---|
| TIA in a virtual machine | Keep the **虚拟机 ↔ 宿主机** (VM ↔ host) mode. On the VM fill pane **A**: TIA version, installation root, IPv4, port and the shared key, then **网络权限** (network permissions) and **启动服务** (start). |
| AI on the host | Copy the configurator EXE to the host, enter the same address, port and key, pick clients in pane **B**, then **测试连接** (test) and **写入客户端配置** (write client config). TIA is not required on the host. |
| TIA and AI on one computer | Switch to **同一台电脑** (same computer). Pane A only needs version and path; clients launch the matching engine over stdio, so no address, port or key is used. |

The configurator UI is Chinese-only; the labels above are its exact button names.

Cards, CLIs first: Claude Code, Codex, Gemini CLI, 通义千问 (Qwen Code), Kimi (Kimi Code CLI), 腾讯元宝 (CodeBuddy Code), DeepSeek / 智谱清言 / Grok (OpenCode, pick the provider there), then Cursor and VS Code / Copilot. The Chinese model brands and Grok have no MCP client of their own, so their cards write the vendor CLI or OpenCode. For official Claude's **Code** page select **Claude Code**. Prerequisites and client-specific limits are in the [configuration guide](docs/getting-started/configuration.md).

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

The static inventory contains **380 tools** in 7 categories (session, project, plc, plc-online, hardware, hmi, runtime); default **lite** advertises **56**, with the remainder available through `FindTools` / `CallTool` (`ListToolCategories` shows the taxonomy, `FindTools(category=…)` browses one area). The running server's `tools/list` is authoritative. Use `--profile full` for full exposure.

Capabilities include project/session management, PLC blocks/types/tags, hardware/network engineering, WinCC Unified, file exchange, libraries, version control and read-only online monitoring. See the [tool matrix](docs/reference/tool-matrix.md) and [acceptance boundaries](docs/reference/capabilities.md). Tool availability does not imply full Siemens API coverage or real-project acceptance.

Delivery and engine versions are independent. [Delivery metadata](manifest/delivery.json) binds the engine and configurator records; [validation documentation](docs/development/validation.md) separates offline, API and real TIA checks.

## Repository map

| Directory | Contents |
|---|---|
| [docs](docs/README.md) | Setup, guides, references, troubleshooting, development and history |
| [runtime](runtime/README.md) | Canonical V20/V21 executables and dependencies |
| [tools](tools/README.md) | Engine source/tests, AI skill and WPF source |
| [scripts](scripts/README.md) | Build, checks, generators, diagnostics and operations |
| [templates](templates/README.md) | PLC/HMI assets and project specifications |
| [manifest](manifest/README.md) | Inventory, versions, build receipts and hashes |

Root launch/configuration CMD/BAT files were replaced by the GUI. Delivery ZIPs no longer duplicate runtimes into legacy `bin/Release` paths; use `runtime/v20` or `runtime/v21`.

Read [CONTRIBUTING](.github/CONTRIBUTING.md), [validation](docs/development/validation.md) and the [release workflow](docs/development/release-workflow.md). Changes target `master`. See [CHANGELOG](CHANGELOG.md) and [archived release notes](docs/archive/release-notes.md).

Independently maintained by asckye. Provenance and third-party notices remain in [NOTICE](NOTICE.md) and [LICENSE](LICENSE).
