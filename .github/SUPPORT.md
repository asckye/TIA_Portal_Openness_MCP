# Support

Support requests may be written in English or Chinese.

## Before asking

1. On the TIA computer, run `doctor` with the host of the release you use (every release has the same host; give its release key),
   or use the environment check of the Workbench:

   ```bat
   runtime\v21\TiaMcp.FoundationHost.exe doctor --release-key 21
   ```

2. Read the docs for your question:
   - Setup and clients: [docs/getting-started/configuration.md](../docs/getting-started/configuration.md)
   - Command line: [docs/getting-started/cli.md](../docs/getting-started/cli.md)
   - Guides (project generation, PLC, HMI, hardware, online): [docs/README.md](../docs/README.md)
   - Errors and Openness limits: [docs/troubleshooting/errors.md](../docs/troubleshooting/errors.md), [docs/troubleshooting/openness-limitations.md](../docs/troubleshooting/openness-limitations.md)
   - What is verified on a real machine and what is not: [docs/reference/capabilities.md](../docs/reference/capabilities.md), [docs/reference/real-machine-ledger.md](../docs/reference/real-machine-ledger.md)

3. Inside an AI session, retrieve `GetToolUsage` for the current release, tool and operation. Every release also offers `InitializeEnvironment`, `GetEnvironmentDiagnostics` and `FindTools`.

## Where to ask

- **Bug or unexpected behaviour** → open an issue with the [bug report form](https://github.com/asckye/TIA_Portal_Openness_MCP/issues/new?template=bug_report.yml); paste the `doctor` output and the full error text.
- **Result of a real-machine check on a pre-release** → [real-machine verification result](https://github.com/asckye/TIA_Portal_Openness_MCP/issues/new?template=real_machine_result.yml); the items are in the [beta tester checklist (Chinese)](../docs/guides/beta-tester-checklist.md).
- **Missing tool or workflow** → [feature request](https://github.com/asckye/TIA_Portal_Openness_MCP/issues/new?template=feature_request.yml).
- **Question** → open an issue with the `question` label; say which TIA version, client and product version (from the current host's state/environment output).
- **Security problem** → do not open a public issue; follow [SECURITY.md](SECURITY.md).

Please do not attach real customer projects or unredacted logs. Reduce a reproduction to the smallest block or spec that still fails.

## What this project is not

It drives TIA Portal through the official Siemens Openness API. It does not ship Siemens software or licences and cannot help with TIA Portal installation, licensing or Siemens support cases - those go to Siemens.
