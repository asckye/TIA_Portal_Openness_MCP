# Support

English or Chinese, both are read. 中英文皆可。

## Before asking

1. Run the built-in doctor on the machine with TIA Portal - it names the exact environment fix for most problems:

   ```bat
   runtime\v21\TiaMcpServer.exe doctor
   ```

2. Read the docs for your question:
   - Setup and clients: [docs/getting-started/configuration.md](../docs/getting-started/configuration.md)
   - Command line: [docs/getting-started/cli.md](../docs/getting-started/cli.md)
   - Guides (project generation, PLC, HMI, hardware, online): [docs/README.md](../docs/README.md)
   - Errors and Openness limits: [docs/troubleshooting/errors.md](../docs/troubleshooting/errors.md), [docs/troubleshooting/openness-limitations.md](../docs/troubleshooting/openness-limitations.md)
   - What is verified on a real machine and what is not: [docs/reference/capabilities.md](../docs/reference/capabilities.md), [docs/reference/real-machine-ledger.md](../docs/reference/real-machine-ledger.md)

3. Inside an AI session, `Bootstrap`, `Doctor`, `FindTools`, `PreflightToolCall` and `GetRecipe` answer most "which tool / which arguments / which order" questions without guessing.

## Where to ask

- **Bug or unexpected behaviour** → open an issue with the [bug report template](https://github.com/asckye/TIA_Portal_Openness_MCP/issues/new?template=bug_report.md); paste the `doctor` output and the full error text.
- **Missing tool or workflow** → [feature request](https://github.com/asckye/TIA_Portal_Openness_MCP/issues/new?template=feature_request.md).
- **Question** → open an issue with the `question` label; say which TIA version, client and engine version (`Bootstrap` → `serverVersion`).
- **Security problem** → do not open a public issue; follow [SECURITY.md](SECURITY.md).

Please do not attach real customer projects or unredacted logs. Reduce a reproduction to the smallest block or spec that still fails.

## What this project is not

It drives TIA Portal through the official Siemens Openness API. It does not ship Siemens software or licences and cannot help with TIA Portal installation, licensing or Siemens support cases - those go to Siemens.
