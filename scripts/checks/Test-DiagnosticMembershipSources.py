#!/usr/bin/env python3
"""Source-only guard: run without loading Siemens or any Windows identity APIs."""
import re
from engine_sources import EngineSources, ROOT, block_after, lexer

sources = EngineSources()
bootstrap = sources.member('Bootstrap', tool=False, owner='SessionTools')
bootstrap_group = sources.member('ReadBootstrapOpennessGroup', tool=False, owner='SessionTools')
bootstrap_portal = sources.member('ReadBootstrapPortal', tool=False, owner='SessionTools')
doctor = sources.member('Doctor', tool=False, owner='McpServer')
doctor_group = sources.member('ReadOpennessGroup', tool=False, owner='McpServer')
selftest = sources.member('RunCapabilitySelfTest', tool=False, owner='DiagnosticsTools')
selftest_logic = sources.member('Run', tool=False, owner='CapabilitySelfTestLogic')
assert "ReadBootstrapOpennessGroup()" in bootstrap
assert "else if (OpennessReadiness.Ready)" in bootstrap
assert "ReadBootstrapPortal()" in bootstrap and "OpennessReadiness.Ready ? ReadBootstrapPortal()" in bootstrap
assert "Siemens.Openness." not in bootstrap and "Siemens.Portal" not in bootstrap
assert bootstrap_group.count("Siemens.Openness.IsUserInGroupNoFix()") == 1
bootstrap_source = sources.type_text('SessionTools')
doctor_source = sources.sources[ROOT / 'src/Shared/Host/McpServer.Doctor.cs']
assert re.search(r'\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static bool ReadBootstrapOpennessGroup', bootstrap_source)
assert re.search(r'\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static BootstrapPortal ReadBootstrapPortal', bootstrap_source)
assert 'EngineServices.Get<IEngineeringSession>()' in bootstrap_portal
assert 'if (!Runtime.OpennessReadiness.Ready)' in doctor
assert 'ReadPortalState()' in doctor and 'ReadOpennessGroup(fix: true)' in doctor and 'ReadOpennessGroup(fix: false)' in doctor
assert 'Siemens.Openness.' not in doctor and 'Siemens.Portal' not in doctor
tokens, _ = lexer.Lexer(doctor).scan()
pairs = lexer.matching_pairs(tokens)
cursor = doctor.index('if (!Runtime.OpennessReadiness.Ready)')
branches = []
for marker in ('if (!Runtime.OpennessReadiness.Ready)', 'else if (isolatedParent)', 'else if (fix)', 'else'):
    assert doctor.startswith(marker, cursor)
    opening = next(i for i, token in enumerate(tokens) if token.start >= cursor + len(marker) and token.value == '{')
    closing = pairs[opening]
    branches.append(doctor[tokens[opening].start:tokens[closing].end])
    cursor = tokens[closing].end
    while doctor[cursor].isspace():
        cursor += 1
unready, isolated, repair, no_fix = branches
assert 'Runtime.OpennessReadiness.GroupOk == true' in unready
assert 'ReadOpennessGroup(' not in unready and 'ReadPortalState(' not in unready
assert 'ReadOpennessGroup(' not in isolated
assert 'await ReadOpennessGroup(fix: true)' in repair and 'await ReadOpennessGroup(fix: false)' in no_fix
assert doctor.count('ReadOpennessGroup(') == 2
assert 'ReadPortalState()' in block_after(doctor, 'if (Runtime.OpennessReadiness.Ready)')
assert doctor.count('ReadPortalState()') == 1
assert 'HostToolServices.Observe(fix ? "group.fix" : "group", new JsonObject())' in doctor_group
assert 'Siemens.Openness.' not in doctor_group
assert re.search(r'\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static \(bool Connected, string\? ProjectName\) ReadPortalState', doctor_source)
doctor_portal = block_after(doctor_source, 'private static (bool Connected, string? ProjectName) ReadPortalState()')
assert 'HostToolServices.Observe("session.GetState", new JsonObject())' in doctor_portal
assert 'EngineServices.' not in doctor_source and 'Siemens.Portal' not in doctor_source
assert re.search(r'\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static Task<bool> ReadOpennessGroup', doctor_source)
# The host forwards managed observations. Reflection in the worker keeps the
# Openness wrapper's Siemens dependencies out of the no-install JIT path.
observations = sources.member('Read', tool=False, owner='HostObservations')
group = block_after(observations, 'if (operation == "group" || operation == "group.fix")')
assert 'Assembly.GetType("TiaMcpServer.Siemens.Openness")' in group
assert 'GetMethod(operation == "group" ? "IsUserInGroupNoFix" : "IsUserInGroup")' in group
assert '.Invoke(null, null)' in group and 'task.GetAwaiter().GetResult()' in group
assert 'Siemens.Openness.' not in observations and 'global::Siemens.' not in observations
assert selftest.count("Siemens.Openness.IsUserInGroupNoFix") == 1
assert 'CapabilitySelfTestLogic.Run(Siemens.Openness.IsUserInGroupNoFix,' in selftest
assert '_session.GetState, _session.ConnectPortal, _session.ValidateAutomationContext, _session.GetProjectTree' in selftest
assert selftest_logic.count('opennessOk = group();') == 1
assert not re.search(r"(?:IsUserInGroup|AddUserToGroupAsync|EnsureOpennessUserGroup)\s*\(", selftest)
assert not re.search(r"(?:IsUserInGroup|AddUserToGroupAsync|EnsureOpennessUserGroup)\s*\(", selftest_logic)
assert re.search(r'catch\s*(?:/\*\s*swallow\(env-probe\):\s*[^*]+\*/\s*)?'
                 r'\{\s*env\.OpennessGroupOk = false;\s*\}', bootstrap)
assert 'nextTool = "EnsureOpennessUserGroup";' in bootstrap  # recommendation only
assert 'catch (Exception ex)' in selftest_logic and '"fail", ex.Message' in selftest_logic
assert 'bool connectIfNeeded = false' in selftest
assert 'if (!isConnected && connectIfNeeded)' in selftest_logic
assert 'isConnected = connect();' in selftest_logic
ensure = sources.member('EnsureOpennessUserGroup', tool=False)
assert "await Siemens.Openness.IsUserInGroup()" in ensure
pure = sources.member('IsUserInGroupNoFix', body_only=True)
assert re.fullmatch(r"\s*\{\s*return Api\.Global\.Openness\(\)\.IsUserInGroup\(\);\s*\}\s*", pure)
print("PASS: bootstrap and host Doctor keep membership/session access behind readiness and no-inline helpers; worker observations preserve NoFix/repair routing; self-test failure handling and opt-in connection preserved.")

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
