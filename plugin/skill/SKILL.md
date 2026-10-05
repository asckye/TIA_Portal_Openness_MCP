---
name: tiaportal-mcp
description: Use this project's version-specific MCP tools to inspect and edit Siemens TIA Portal projects. Retrieve exact call examples, programming files and result interpretation through GetToolUsage for the connected release.
---

# TIA Portal MCP usage

This skill explains how to find and use the project's maintained examples. Tool arguments and programming examples live in `reference/tool-examples` and are returned by `GetToolUsage`; this file does not duplicate their contracts.

## Select the current release and tool

The configurator supports V14 SP1, V15.1 and V16–V21. Foundation hosts (V14 SP1–V19) expose their implemented PLC subset. V20/V21 use the full engine, with 63 directly visible tools in the default lite profile and `FindTools`/`CallTool` discovery for the rest. Foundation hosts have no full-engine discovery bridge or project-generation CLI. Studio is a separate direct Openness application.

Read the connected server's `tools/list` and `GetToolUsage()` index. Select tools from that release, since identical names can have different arguments and result envelopes. Use `GetToolUsage(toolName="<actual tool>", operation="<actual action>")` for an unfamiliar call. On full engines, `FindTools` can locate the tool and its schema before `CallTool` executes it.

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

The foundation sequence applies to V14 SP1–V19. Full V20/V21 engines use `sequence/plc-scl-block`. Other languages, HMI event code, global modules and XML examples have their own identifiers and version requirements in the same index.

## Apply the example to the user's project

Resolve the intended project and software path with the selected release's connection/state/tree tools. Full engines offer `Bootstrap`; use the foundation host's own schema and examples instead of assuming the same startup sequence.

Replace example placeholders with values read from the actual project or supplied by the user. A file path refers to the computer running TIA/MCP; in VM mode this is the VM, not the AI client's host. Engineering object paths are a different kind of value from filesystem paths.

Follow the selected tool's actual execution fields. Some tools only read or construct an artifact; some writes require a preview followed by its returned confirmation/hash. `planned` is a preview. Inspect the returned imported/generated identities and compiler error counts, including nested messages. A transport success or a generated file does not establish a successful project operation.

Keep project save, compilation and PLC download distinct. Perform the stages requested for the task and report which ran. When a write result is unknown, inspect the current project before deciding whether it should be repeated.

## Programming and output versions

Use complete source examples when a complete file is required; fragments need the declarations/context described by their example. Foundation external-source import currently accepts ASCII. Retrieve the current `BuildPlcUdtXml` or `BuildPlcGlobalDbXml` example to choose `outputReleaseKey` for any of the eight releases; other shared builders still emit V21 candidate XML. An XML version marker alone does not convert a document.

Unified button events and global modules use different operations. Document import, drive parameters, compilation and other families are explained by their own entries in the same example library. Use result interpretation from the selected operation rather than treating every returned object as a scalar or every import as a compile pass.

## References

- [Beginner guide (Chinese)](../../docs/getting-started/beginners.zh-CN.md)
- [Version scope](../../docs/reference/version-tools.md)
- [Example library and evidence](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/official-tool-usage.md)
- [Current capabilities](../../docs/reference/capabilities.md)
- [Known limits](../../docs/troubleshooting/openness-limitations.md)

Official API snippets describe Siemens APIs; project-authored MCP examples describe these wrappers. Build, schema, offline and native acceptance evidence are identified separately. New native acceptance remains pending unless a matching recorded test says otherwise.
