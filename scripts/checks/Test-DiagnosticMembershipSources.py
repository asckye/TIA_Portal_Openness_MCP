#!/usr/bin/env python3
"""Source-only guard: run without loading Siemens or any Windows identity APIs."""
import re
from engine_sources import EngineSources

sources = EngineSources()
bootstrap = sources.member('Bootstrap', tool=False, owner='SessionTools')
bootstrap_group = sources.member('ReadBootstrapOpennessGroup', tool=False, owner='SessionTools')
bootstrap_portal = sources.member('ReadBootstrapPortal', tool=False, owner='SessionTools')
doctor = sources.member('Doctor', tool=False, owner='McpServer')
doctor_group = sources.member('ReadOpennessGroup', tool=False, owner='McpServer')
selftest = sources.member('RunCapabilitySelfTest', tool=False, owner='DiagnosticsTools')
assert "ReadBootstrapOpennessGroup()" in bootstrap
assert "else if (OpennessReadiness.Ready)" in bootstrap
assert "ReadBootstrapPortal()" in bootstrap and "OpennessReadiness.Ready ? ReadBootstrapPortal()" in bootstrap
assert "Siemens.Openness." not in bootstrap and "Siemens.Portal" not in bootstrap
assert bootstrap_group.count("Siemens.Openness.IsUserInGroupNoFix()") == 1
bootstrap_source = sources.type_text('SessionTools')
doctor_source = sources.type_text('McpServer')
assert re.search(r'\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static bool ReadBootstrapOpennessGroup', bootstrap_source)
assert re.search(r'\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static BootstrapPortal ReadBootstrapPortal', bootstrap_source)
assert 'EngineServices.Get<IEngineeringSession>()' in bootstrap_portal
assert 'if (!Runtime.OpennessReadiness.Ready)' in doctor
assert 'ReadPortalState()' in doctor and 'ReadOpennessGroup(fix: true)' in doctor and 'ReadOpennessGroup(fix: false)' in doctor
assert 'Siemens.Openness.' not in doctor and 'Siemens.Portal' not in doctor
assert 'Siemens.Openness.IsUserInGroup()' in doctor_group and 'Siemens.Openness.IsUserInGroupNoFix()' in doctor_group
assert re.search(r'\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static \(bool Connected, string\? ProjectName\) ReadPortalState', doctor_source)
assert 'EngineServices.Get<Siemens.Portal>()' in doctor_source
assert re.search(r'\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static Task<bool> ReadOpennessGroup', doctor_source)
assert selftest.count("Siemens.Openness.IsUserInGroupNoFix()") == 1
assert not re.search(r"(?:IsUserInGroup|AddUserToGroupAsync|EnsureOpennessUserGroup)\s*\(", selftest)
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
print("PASS: bootstrap keeps Siemens access behind ready-only no-inline helpers; self-test NoFix, failure handling and opt-in connection preserved; explicit repair retained.")

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
