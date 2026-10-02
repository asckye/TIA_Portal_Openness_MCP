#!/usr/bin/env python3
"""Keep the explicit prompt inventory complete, without loading engineering DLLs."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "tools/tiaportal-mcp/src/TiaMcpServer"
program = (SRC / "Program.cs").read_text(encoding="utf-8")
registration = (SRC / "ModelContextProtocol/McpPromptRegistration.cs").read_text(encoding="utf-8")
assert "WithPromptsFromAssembly(" not in program
assert program.count("ModelContextProtocol.McpPromptRegistration.Configure(mcp);") == 1
assert program.count("ModelContextProtocol.McpPromptRegistration.Configure(mcpHttp);") == 1
assert "MCP registration failed: ReflectionTypeLoadException" in program
assert "WithToolsFromAssembly failed:" not in program
loader_catch = program.split("catch (ReflectionTypeLoadException ex)", 1)[1].split("// Register the Portal service", 1)[0]
assert "throw;" in loader_catch and "ex.LoaderExceptions" in loader_catch
assert "catch" not in registration
assert "GetTypes(" not in registration
assert "builder.WithPrompts(new[] {" in registration

# Inventory all production source prompt containers. Fail on a new container
# until it is explicitly registered; never infer feature availability from this.
containers = []
prompt_count = 0
for path in SRC.rglob("*.cs"):
    if any(part.startswith(("obj", "bin")) for part in path.relative_to(SRC).parts):
        continue
    source = path.read_text(encoding="utf-8-sig")
    source = re.sub(r"(?m)^\s*//.*$", "", source)
    if re.search(r"\[McpServerPromptType(?:Attribute)?\]", source):
        found = re.findall(r"\[McpServerPromptType(?:Attribute)?\]\s*public\s+(?:static\s+)?class\s+(\w+)", source)
        assert len(found) == len(re.findall(r"\[McpServerPromptType(?:Attribute)?\]", source)), path
        containers.extend(found)
    prompt_count += len(re.findall(r"\[McpServerPrompt\(", source))
registered = re.findall(r"typeof\((\w+)\)", registration)
assert sorted(registered) == sorted(containers), (registered, containers)
assert len(registered) == len(set(registered))
assert prompt_count == 30, f"Review the prompt regression inventory after changing the current 30 prompts: {prompt_count}"
print("PASS: both transports use complete explicit prompt inventory; all 30 prompts retained; scan failure remains diagnostic and fatal.")
