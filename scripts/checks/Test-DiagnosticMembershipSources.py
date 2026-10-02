#!/usr/bin/env python3
"""Source-only guard: run without loading Siemens or any Windows identity APIs."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "tools/tiaportal-mcp/src/TiaMcpServer"
source = (SRC / "ModelContextProtocol/Tools/McpServer.cs").read_text(encoding="utf-8")
bootstrap = source.split("public static async Task<ResponseBootstrap> Bootstrap()", 1)[1].split("#endregion", 1)[0]
selftest = source.split("public static async Task<ResponseCapabilitySelfTest> RunCapabilitySelfTest(", 1)[1].split('[McpServerTool(Name = "RunOnlineMonitoringSafetySelfTest")', 1)[0]
for name, body in (("Bootstrap", bootstrap), ("RunCapabilitySelfTest", selftest)):
    assert body.count("Siemens.Openness.IsUserInGroupNoFix()") == 1, name
    assert not re.search(r"(?:IsUserInGroup|AddUserToGroupAsync|EnsureOpennessUserGroup)\s*\(", body), name
assert "catch { env.OpennessGroupOk = false; }" in bootstrap
assert 'nextTool = "EnsureOpennessUserGroup";' in bootstrap  # recommendation only
assert 'catch (Exception ex)' in selftest and '"fail", ex.Message' in selftest
assert 'bool connectIfNeeded = false' in selftest
assert 'if (!isConnected && connectIfNeeded)' in selftest
assert 'isConnected = Portal.ConnectPortal();' in selftest
ensure = source.split("public static async Task<ResponseMessage> EnsureOpennessUserGroup()", 1)[1].split('[McpServerTool(Name = "Disconnect")', 1)[0]
assert "await Siemens.Openness.IsUserInGroup()" in ensure
openness = (SRC / "Siemens/Openness.cs").read_text(encoding="utf-8")
pure = openness.split("public static bool IsUserInGroupNoFix()", 1)[1].split("public static async", 1)[0]
assert re.fullmatch(r"\s*\{\s*return Api\.Global\.Openness\(\)\.IsUserInGroup\(\);\s*\}\s*", pure)
print("PASS: both diagnostics use NoFix; failure handling and opt-in connection preserved; explicit repair retained.")

startup = (SRC / "Program.cs").read_text(encoding="utf-8")
assert "var opennessUserOk = Openness.IsUserInGroupNoFix();" in startup
assert not re.search(r"Openness\.IsUserInGroup\s*\(", startup)
assert "if (opennessUserOk)" in startup
assert "User is not in the required group 'Siemens TIA Openness'. Exiting." in startup
assert "Environment.ExitCode = 2;" in startup
assert "tia.cmd doctor --fix" in startup
print("PASS: startup uses NoFix and retains fail-closed nonmember exit and explicit repair guidance.")
