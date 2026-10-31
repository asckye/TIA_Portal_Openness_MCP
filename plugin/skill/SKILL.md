---
name: tiaportal-mcp
description: Use this project's version-specific MCP tools to inspect and edit Siemens TIA Portal projects. Retrieve exact call examples, programming files and result interpretation through GetToolUsage for the connected release.
---

# Automation Engineering Workbench (TIA Portal MCP) usage

This skill explains how to find and use the project's maintained examples. Tool arguments and programming examples live in `reference/tool-examples` and are returned by `GetToolUsage`; this file does not duplicate their contracts.

## Install and configure the Claude Code plugin

Extract a complete delivery bundle to a fixed local directory, for example `D:\TIA-MCP`. In a Claude Code session, add that local marketplace and install the plugin:

```text
/plugin marketplace add D:\TIA-MCP
/plugin install tia-portal-openness-mcp@tia-portal-openness-mcp
```

Restart Claude Code and create a new session. Check the `tia-portal` server's `tools/list` and `GetToolUsage()` before choosing calls. Preserve the complete bundle; the plugin command is `${CLAUDE_PLUGIN_ROOT}/runtime/tools/TiaMcp.PluginLauncher.exe`. Updating files in the original directory does not refresh an installed cache copy; update the marketplace and installed plugin in Claude Code, then restart it. Check for duplicate manually configured servers.

Set these inherited environment variables before starting Claude Code:

- `TIA_MCP_BUNDLE_ROOT`: optional absolute root of the installed bundle used by the Workbench. It must contain `manifest/package-manifest.json` with the exact `packageType` `tia-portal-mcp-complete-offline-delivery-bundle`. The selected host uses that bundle, its data location and approval scope. A bad override fails with stderr and a nonzero exit code; it never falls back to the plugin copy. Open `TiaOpenness.exe` from the same bundle, choose the same release, and use matching data-location overrides in both processes.
- `TIA_MCP_RELEASE_KEY`: optional exact runnable key: `14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20` or `21`. Without it, the launcher shares the Workbench rule: newest installed TIA release, otherwise newest runnable bundled host/worker pair. Invalid keys and missing selected runtime files fail without switching releases.

Run this example from any PowerShell directory, replacing the bundle path with the Workbench's actual installation:

```powershell
$env:TIA_MCP_BUNDLE_ROOT = 'D:\TIA-MCP'
$env:TIA_MCP_RELEASE_KEY = '19'
claude
```

The launcher starts `runtime/v<key>/TiaMcp.FoundationHost.exe --release-key <key>`, inherits stdin/stdout/stderr unchanged, passes extra arguments unchanged and forwards the host exit code. Closing the launcher stops its host through a Windows lifetime job. Changes to environment variables require a full client restart. See the [plugin installation guide](../../docs/getting-started/plugin.md) for automatic selection and variable removal.

## Select the current release and tool

The root launcher is `TiaOpenness.exe`, which opens the Workbench. Every release uses `runtime/v<key>/TiaMcp.FoundationHost.exe --release-key <key>`, with one version-matched worker per MCP session. V20/V21 default to lite; `FindTools` and `CallTool` reach the remaining tools. Older releases also provide discovery and bridge tools. Consult the generated counts and release applicability in `docs/reference/version-tools.md`, and use runtime `tools/list`. The Engineering page shares an MCP session's tools, approvals and audit. The retired `--isolate-openness` and `--no-isolate-openness` switches must be removed from old client commands.

Read the connected server's `tools/list` and `GetToolUsage()` index. Select tools from that release, since identical names can have different arguments. The V4 result envelope is `{schemaVersion, ok, data, error, meta}`; inspect `meta.outcome`, `meta.execution` and `meta.completeness`, and follow `meta.paging` only for the same release/session/binding/query snapshot. On `OUTCOME_UNKNOWN`, inspect the target and reset the session when required; never replay a write automatically. Use the implemented typed `GetToolUsage` selectors: `toolName`, `operation`, `language`, `exampleId`, `exampleKind`, `query`, `documentId`, `offset` and `limit`. For an unfamiliar call, retrieve the tool's schema and example. On every release, `FindTools` can locate the tool and its schema before `CallTool` executes it.

## Retrieve an example

| Need | GetToolUsage selector |
|---|---|
| Arguments, value origins and response interpretation | `toolName`, optionally `operation` |
| Available programming examples | `language` |
| Complete source file or ordered call sequence | `exampleId` from the index |
| Search the pinned official references | `query` |
| Read an official reference | `documentId`, with returned paging fields |

Example lookups:

```json
{"exampleId":"scl-add"}
```

```json
{"exampleId":"sequence/plc-scl-block-foundation"}
```

Select the sequence returned for the connected release; shared session tools use the Foundation sequence on all eight releases. Other languages, HMI event code, global modules and XML examples have their own identifiers and version requirements in the same index.

## Apply the example to the user's project

Resolve the intended project and software path with the selected release's connection/state/tree tools. Use `InitializeEnvironment` and the connected host's current session examples.

Replace example placeholders with values read from the actual project or supplied by the user. A file path refers to the computer running TIA/MCP; in VM mode this is the VM, not the AI client's host. Engineering object paths are a different kind of value from filesystem paths.

Follow the selected tool's V4 schema and typed arguments. Some tools only read or construct an artifact; some write tools require a preview followed by its returned confirmation/hash. Inspect imported/generated identities and compiler error counts, including nested messages. A transport success or generated file does not establish a successful project operation. D1 behavior remains `current` with L5 `NOT RUN`; treat `UNVERIFIED_BEHAVIOR` as a warning and do not infer safe-v4 semantics.

Keep project save, close, compilation and PLC download distinct. Never save or close implicitly. MCP `WRITE` and `ONLINE-WRITE` calls wait for Workbench approval by default. `SaveProject`, `SaveProjectCopy` (save-as) and `CloseProject` also require approval even though the catalog marks them `SESSION`; connect, attach, open and disconnect remain ungated. Denial, timeout or unavailable Workbench rejects before dispatch, and MCP clients cannot self-approve. Request-specific decisions and outcomes are recorded in the audit chain at `data/logs/audit`; verify it in the Workbench or with `tia audit verify`. The hash chain cannot prove the log's completeness if its tail or whole directory was removed. When a write result is unknown, inspect the current project before deciding whether it should be repeated.

## Programming and output versions

Use complete source examples when a complete file is required; fragments need the declarations/context described by their example. Foundation external-source import currently accepts ASCII. Retrieve the current `BuildPlcUdt` or `BuildPlcGlobalDb` example to choose `outputReleaseKey` for any of the eight releases; other shared builders still emit V21 candidate XML. An XML version marker alone does not convert a document.

Unified button events and global modules use different operations. Document import, drive parameters, compilation and other families are explained by their own entries in the same example library. Use result interpretation from the selected operation rather than treating every returned object as a scalar or every import as a compile pass.

## References

- [Beginner guide (Chinese)](../../docs/getting-started/beginners.zh-CN.md)
- [Version scope](../../docs/reference/version-tools.md)
- [Example library and evidence](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/official-tool-usage.md)
- [Current capabilities](../../docs/reference/capabilities.md)
- [Known limits](../../docs/troubleshooting/openness-limitations.md)

Official API snippets describe Siemens APIs; project-authored MCP examples describe these wrappers. Build, schema, offline and native acceptance evidence are identified separately. New native acceptance remains pending unless a matching recorded test says otherwise.

The bundle root can be selected with `--bundle-root` or `TIA_MCP_BUNDLE_ROOT`. Runtime logs and audit records are under `data/logs`; diagnostic call bundles are under `data/diagnostics`. The PLC Tools Python default is `%LOCALAPPDATA%\TiaMcp\ecosystem-python\Scripts\python.exe`, overridable with `TIA_MCP_PLC_TOOLS_PYTHON`.
