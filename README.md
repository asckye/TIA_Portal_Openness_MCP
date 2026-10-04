# TIA Portal Openness MCP

[Chinese](README.zh-CN.md) · [Beginner guide (Chinese)](docs/getting-started/beginners.zh-CN.md) · [Documentation](docs/README.md) · [Downloads](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/latest)

One TIA Portal Workbench window contains **MCP & clients** and **Engineering** pages. Configure clients, manage the MCP service and work directly with TIA projects without opening a second desktop application. Chinese and English are supported.

This desktop merge is in the current source. The published v3.2.0 ZIP retains its original separate interfaces; build the current source to use the unified desktop until the next release.

![Architecture](docs/assets/architecture.svg)

## Get started

Download the complete **TIA_MCP_Delivery** ZIP from Releases and extract the entire archive to a short, permanent directory. GitHub's source archives contain no runtime binaries.

- **AI client:** open `TiaMcpConfigurator.exe`, choose **MCP & clients**, select the installed TIA release and choose Same computer or VM ↔ host. Select the actual MCP client, write its configuration, restart it and start a new conversation. Follow the [configuration guide](docs/getting-started/configuration.md).
- **Engineering:** open the same `TiaMcpConfigurator.exe`, switch to **Engineering**, select the TIA release before connecting, select the intended PLC and use browse, export/import, compile and save. Studio calls Openness directly and does not require an MCP client.
- **Studio demonstration:** run `TiaMcpConfigurator.exe --mock --lang en` from the extracted package. Results are synthetic.

The [beginner guide](docs/getting-started/beginners.zh-CN.md) explains installation, VM paths, PLC selection and a first SCL import/compile exercise.

## Supported releases

| TIA release | Registered tools | MCP profile |
|---|---:|---|
| V14 SP1 | 57 | PLC foundation |
| V15.1 | 58 | PLC foundation |
| V16 / V17 / V18 | 60 each | PLC foundation |
| V19 | 62 | PLC foundation |
| V20 | 477 | Full engine |
| V21 | 488 | Full engine |

V20/V21 expose 63 tools in the default lite profile; use `FindTools` and `CallTool` for the rest, or configure `--profile full`. Foundation hosts expose their own complete subset and have different contracts. Read [version scope](docs/reference/version-tools.md) and the connected server's `tools/list`. Original V14/V15 are not aliases for V14 SP1/V15.1.

Before an unfamiliar operation, `GetToolUsage(toolName, operation)` supplies the current release's arguments, examples and result interpretation. Use `language` to list programming examples and `exampleId` to retrieve complete files or call sequences. Official API patterns and project-authored wrappers are identified separately.

## Installation and validation

Install the matching TIA Portal, Openness and licenses separately. The .NET 10 runtime for the unified desktop and the foundation hosts ships in `runtime/dotnet`. The Openness bridge, workers and full engines require .NET Framework 4.8, which is part of Windows 10 1903 and later. See [runtime paths](runtime/README.md).

All eight runtimes and Studio are built locally against the supplied SDKs. Functional, protocol, API metadata and XML interface checks are recorded separately from real-project acceptance. New native TIA/project acceptance remains **NOT RUN**; a tool catalog or successful build does not establish every engineering operation. Current limits are in [capabilities](docs/reference/capabilities.md).

## Development

`master` is the maintained branch. Runtime binaries are release assets, not tracked Git files. With the documented SDK layout and companion Python environment:

```powershell
pwsh -NoProfile -File scripts/build/Build-MultiVersion.ps1 -PublicApiRoot <SDK-root> -Python <python.exe> -Test
```

[Build and validation](docs/development/validation.md) · [Release workflow](docs/development/release-workflow.md) · [Current handoff](docs/development/handoff.md) · [Changelog](CHANGELOG.md) · [License notices](docs/licenses/THIRD-PARTY-NOTICES.md)
