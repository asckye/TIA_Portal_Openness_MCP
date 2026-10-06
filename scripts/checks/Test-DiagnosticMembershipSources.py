#!/usr/bin/env python3
"""Source-only guard: run without loading Siemens or any Windows identity APIs."""
import re
from engine_sources import EngineSources

sources = EngineSources()
bootstrap = sources.member('Bootstrap', tool=False)
selftest = sources.member('RunCapabilitySelfTest', tool=False, owner='DiagnosticsTools')
for name, body in (("Bootstrap", bootstrap), ("RunCapabilitySelfTest", selftest)):
    assert body.count("Siemens.Openness.IsUserInGroupNoFix()") == 1, name
    assert not re.search(r"(?:IsUserInGroup|AddUserToGroupAsync|EnsureOpennessUserGroup)\s*\(", body), name
assert re.search(r'catch\s*(?:/\*\s*swallow\(env-probe\):\s*[^*]+\*/\s*)?'
                 r'\{\s*env\.OpennessGroupOk = false;\s*\}', bootstrap)
assert 'nextTool = "EnsureOpennessUserGroup";' in bootstrap  # recommendation only
assert 'catch (Exception ex)' in selftest and '"fail", ex.Message' in selftest
assert 'bool connectIfNeeded = false' in selftest
assert 'if (!isConnected && connectIfNeeded)' in selftest
assert 'isConnected = _session.ConnectPortal();' in selftest
ensure = sources.member('EnsureOpennessUserGroup', tool=False)
assert "await Siemens.Openness.IsUserInGroup()" in ensure
pure = sources.member('IsUserInGroupNoFix', body_only=True)
assert re.fullmatch(r"\s*\{\s*return Api\.Global\.Openness\(\)\.IsUserInGroup\(\);\s*\}\s*", pure)
print("PASS: both diagnostics use NoFix; failure handling and opt-in connection preserved; explicit repair retained.")

startup = sources.member('Main')
assert "OpennessReadiness.Ready" in startup
assert "OpennessReadiness.MarkUnavailable" in startup
assert not re.search(r"Openness\.IsUserInGroup\s*\(", startup)
assert "if (mcpHostInvocation)" in startup
assert 'Starting MCP host in environment-not-ready mode.' in startup
assert "await RunHttpHost(options)" in startup and "await RunStdioHost(options)" in startup
assert "User is not in the required group 'Siemens TIA Openness'. Exiting." in startup
assert "Environment.ExitCode = 2;" in startup
assert "OpennessReadiness.Guidance(false)" in startup
print("PASS: startup uses readiness state, retains explicit non-MCP exit, and keeps MCP hosts available while the environment is not ready.")
