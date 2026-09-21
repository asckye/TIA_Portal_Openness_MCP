---
name: Bug report
about: Report a problem with the MCP server or a tool
title: "[bug] "
labels: bug
---

**Environment**
- TIA Portal version: V20 / V21
- Executable used: `runtime/v21` (V21) / `runtime/v20` (V20)
- MCP client: Claude Code / Claude Desktop / Codex / Cursor / VS Code / Gemini CLI / Windsurf / Cline / other
- Engine version (`Bootstrap` → `serverVersion`, or the startup log line `TiaMcpServer 2.7.58.0`):
- Output of `runtime\v21\TiaMcpServer.exe doctor` (paste it - it names the environment fix for most problems):

**What happened**
A clear description of the bug, including the tool name and the full error text
(the server unwraps the real Openness exception — paste it verbatim).

**To reproduce**
1. Tool called + arguments (redact secrets)
2. ...

**Expected behavior**

**Logs**
Run with `--logging 1` and paste the relevant stderr lines.
