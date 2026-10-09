#!/usr/bin/env python3
"""Keep the explicit prompt inventory complete, without loading engineering DLLs."""
import re
import json
from pathlib import Path
from engine_sources import EngineSources, block_after

sources = EngineSources()
root = Path(__file__).resolve().parents[2]
program = (root / 'src/FoundationHost/EngineReleaseHost.cs').read_text(encoding='utf-8-sig')
pipeline = (root / 'src/EngineHost/EngineHostPipeline.cs').read_text(encoding='utf-8-sig')
registration = sources.type_text('McpPromptRegistration')
assert "WithPromptsFromAssembly(" not in sources.all_text()
assert program.count("pipeline.RegisterHandlers(mcp);") == 1
assert program.count("probe.RegisterHandlers(mcp);") == 1
assert "McpPromptRegistration.Configure(builder)" in pipeline
assert "WithToolsFromAssembly failed:" not in sources.all_text()
assert "catch" not in registration and "GetTypes(" not in registration
assert "builder.WithPrompts(new[] {" in registration
assert "WithStdioServerTransport()" in program and "WithHttpTransport(" in program
assert "WorkerShutdown.StopAll" in program
startup = sources.member('Main')
assert "Environment.ExitCode = 70;" in startup and "throw;" in startup

# Inventory all production source prompt containers. Fail on a new container
# until it is explicitly registered; never infer feature availability from this.
containers = []
prompt_count = 0
for path, source in sources.sources.items():
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
# P6-40 prompt contract: every invocation recipe shares the current typed V4
# rules, and every referenced tool name exists in a checked-in V4 release schema.
prompt_source = next(source for path, source in sources.sources.items() if path.name == "McpPrompts.cs")
for required in (
    "private static string WithV4Rules",
    "ListPortalProcessProjects",
    "ConnectPortal with that row's processId, then AttachOpenProject",
    "schemaVersion 4 envelope",
    "OUTCOME_UNKNOWN",
    "D1 native behavior remains current",
    "L5 is NOT RUN",
    "Do not save or close a project unless the user explicitly requested that action",
    "Workbench approval before dispatch",
    "data/logs/audit",
    "tia audit verify",
):
    assert required in prompt_source, f"Prompt contract missing: {required}"

for stale in (
    "EnsureOpennessUserGroup", "fbBlockJson", "designJson", "ConnectPortal — attach to a running",
    "meta.success / meta.operationSuccess", "SaveProject — save any pending changes first",
    "ConnectProject",
):
    assert stale not in prompt_source, f"Stale prompt instruction remains: {stale}"

prompt_tools = (
    "ListPortalProcessProjects", "ConnectPortal", "AttachOpenProject", "GetSessionState", "OpenProject",
    "GetProjectTree", "CloseProject", "SaveProject", "DisconnectPortal", "CreateProject",
    "SearchHardwareCatalog", "CreateHardwareDevice", "CreateHardwareCatalogDevice",
    "ConnectDeviceNodesToProfinetSubnet", "GetSoftwareTree", "ExportPlcBlocks", "ExportPlcTypes",
    "ExportPlcBlocksDocuments", "ImportPlcBlockDocuments", "ImportPlcBlocksDocuments",
    "BuildAndImportPlcArtifact", "CompilePlcDiagnostics", "EnsureUnifiedHmiConnection",
    "EnsureUnifiedHmiScreen", "EnsureUnifiedHmiTagTable", "EnsureUnifiedHmiTag",
    "EnsureUnifiedHmiScreenItem", "ApplyUnifiedHmiScreenDesign", "EnsureUnifiedHmiDynamization",
    "SetUnifiedHmiRuntimeState", "BuildUnifiedHmiLayoutDesign", "ApplyUnifiedHmiLayout",
    "BuildClassicHmiScreen", "WriteClassicHmiMinimalPackageFiles", "ValidateClassicHmiMinimalPackageFiles",
    "ImportHmiScreen", "ImportHmiTagTable", "PlanOnlineReadOnlyMonitoring",
    "ProbePlcMonitorOnlineCapabilities", "ListPlcWatchTables", "GetPlcWatchTableCurrentValuesReadOnly",
    "RunOfflineReleaseValidationSuite", "BuildReleaseDiagnosticReport", "BuildReleaseRunbook", "GetToolUsage",
)
release_tools = set()
contract_root = Path("manifest/contracts/v4/baseline")
for contract_path in contract_root.glob("*.json"):
    contract = json.loads(contract_path.read_text(encoding="utf-8"))
    release_tools.update(tool["name"] for tool in contract["tools"])
for name in prompt_tools:
    assert name in release_tools, f"Prompt references a tool missing from all V4 schemas: {name}"
print("PASS: both transports use complete explicit prompt inventory; all 30 prompts retained; scan failure remains diagnostic and fatal.")
print("PASS: all prompt workflows share typed V4, explicit process selection, D1/approval/audit rules, and V4-registered tool names.")
