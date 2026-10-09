# -*- coding: utf-8 -*-
"""Assert that the DEFAULT (lite) profile is a USABLE profile, and that full still differs.

lite is the default roster for every host. Orientation and the discovery/dispatch bridge
must be exposed directly; each golden-path tool must be registered and discoverable by
its exact name. V4 puts several read and authoring tools behind FindTools/CallTool, so
checking only the advertised lite names would reject a usable profile. This check proves
that route without dispatching native workflow operations.

It also guards the check itself. When lite became the default, "full" was still being requested
by *unsetting* the env var — so both probes returned the same ~48 tools and every assertion here
passed vacuously. full must now be requested explicitly AND come back strictly larger.

Usage:  python scripts/checks/Check-LiteProfile.py [path-to-TiaMcp.Engine.V21.exe]
Exit 0 = lite resolves its workflow tools, fits the host cap, and can reach everything else.
"""
import argparse
import json
import os
import pathlib
import subprocess
import sys
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from mcp_results import successful

ROOT = pathlib.Path(__file__).resolve().parents[2]
EXE = ROOT / "runtime" / "v21" / "TiaMcp.FoundationHost.exe"
HOST = None
PUBLIC_API = None
MAJOR = 21

# The documented golden path: orientation -> connect/open -> read -> author -> compile -> save.
# Every name here is referenced by the server instructions, README or GetToolUsage, so a
# profile that cannot resolve one is advertising a workflow it cannot perform.
REQUIRED = [
    # orientation / diagnostics
    "InitializeEnvironment", "GetEnvironmentDiagnostics", "GetToolUsage", "GetSessionState",
    # session + project
    "ConnectPortal", "DisconnectPortal", "OpenProject", "CreateProject", "AttachOpenProject",
    "CloseProject", "SaveProject", "GetProjectInfo", "GetProjectTree", "GetSoftwareTree",
    # read / understand
    "ListPlcBlocks", "GetPlcBlockHierarchy", "GetPlcBlockInfo", "DescribePlcBlockLogic",
    "GetPlcCrossReferences", "ListPlcTagTables",
    # author (golden path: SD documents preferred, SCL external source alternative)
    "BuildProjectScaffold", "BuildAndImportPlcArtifact", "WritePlcSclSourceFile",
    "ImportPlcBlockDocuments", "ExportPlcBlockDocuments",
    "ImportPlcBlocksDocuments", "ExportPlcBlocksDocuments",
    "GenerateBlocksFromExternalSource",
    # verify
    "CompilePlcSoftware", "CompilePlcDiagnostics",
    # the bridge out of lite — these expose the remaining registered tools
    "FindTools", "CallTool",
]

# VS Code refuses to enable more tools than this; lite exists to stay under it.
HOST_TOOL_CAP = 128


def tools_for_profile(profile):
    """profile=None means 'whatever a user gets with no flag and no env var'."""
    # Always pass --profile explicitly when asking for a specific roster. Relying on
    # "unset the env var" silently stopped meaning "full" the day lite became the default.
    env = dict(os.environ)
    env.pop("TIA_MCP_PROFILE", None)
    if EXE.name != 'TiaMcp.FoundationHost.exe':
        raise ValueError('Lite profile checks require FoundationHost and its engine worker')
    args = [str(EXE), '--release-key', str(MAJOR), '--logging', '0']
    if profile:
        args += ["--profile", profile]
    p = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace",
                         bufsize=1, env=env)
    seq = [0]

    def send(method, params=None, notify=False):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        if not notify:
            seq[0] += 1
            msg["id"] = seq[0]
        p.stdin.write(json.dumps(msg) + "\n")
        p.stdin.flush()
        if notify:
            return None
        while True:
            line = p.stdout.readline()
            if not line:
                raise SystemExit("engine closed stdout:\n" + p.stderr.read())
            line = line.strip()
            if not line:
                continue
            try:
                d = json.loads(line)
            except json.JSONDecodeError:
                continue
            if d.get("id") == seq[0]:
                return d

    try:
        send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                            "clientInfo": {"name": "lite-check", "version": "1"}})
        send("notifications/initialized", {}, notify=True)
        names = [t['name'] for t in send('tools/list', {})['result']['tools']]
        if profile == 'lite':
            for name in REQUIRED:
                if name in names: continue
                found = successful(send('tools/call', {'name': 'FindTools', 'arguments': {'query': name, 'limit': 1}}))
                if not any(line.startswith(name + '(') for line in found['data']['items']):
                    raise SystemExit('lite discovery cannot resolve required tool: ' + name)
        return names
    finally:
        try:
            p.stdin.close()
        except Exception:
            pass
        p.terminate()


def main():
    global EXE, HOST, PUBLIC_API, MAJOR
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('exe', nargs='?', type=pathlib.Path, default=EXE)
    parser.add_argument('--host-harness', type=pathlib.Path)
    parser.add_argument('--public-api', type=pathlib.Path)
    parser.add_argument('--major', type=int, choices=(20, 21), default=21)
    options = parser.parse_args()
    EXE, HOST, PUBLIC_API, MAJOR = options.exe.resolve(), options.host_harness, options.public_api, options.major
    if HOST and not PUBLIC_API: parser.error('--public-api is required with --host-harness')
    if HOST: HOST, PUBLIC_API = HOST.resolve(), PUBLIC_API.resolve()
    if not EXE.exists():
        print("[FAIL] engine not found:", EXE)
        return 1
    print("engine:", EXE)

    full = tools_for_profile("full")
    lite = tools_for_profile("lite")
    default = tools_for_profile(None)
    print("full profile   : %d tools" % len(full))
    print("lite profile   : %d tools" % len(lite))
    print("default profile: %d tools" % len(default))

    failures = []
    missing = [n for n in ('GetToolUsage', 'GetSessionState', 'FindTools', 'CallTool') if n not in lite]
    if missing:
        failures.append("lite is missing discovery/session tools: " + ", ".join(missing))

    unknown = [n for n in REQUIRED if n not in full]
    if unknown:
        failures.append("REQUIRED lists tools this engine does not expose at all: " + ", ".join(unknown))

    if len(lite) > HOST_TOOL_CAP:
        failures.append("lite exposes %d tools, over the %d host cap it exists to respect"
                        % (len(lite), HOST_TOOL_CAP))

    if not lite:
        failures.append("lite exposed no tools")

    # Sentinel: if these ever come back equal, the two probes are no longer probing two
    # different rosters and every assertion above is meaningless.
    if len(full) <= len(lite):
        failures.append("full (%d) is not larger than lite (%d) — the profile probes are not "
                        "distinguishing the two rosters, so this check proves nothing"
                        % (len(full), len(lite)))

    # lite must be what a user gets with no flags and no env var: every generated host
    # config now relies on that default rather than writing the flag out.
    if sorted(default) != sorted(lite):
        failures.append("the default profile (no flag, no env var) is not lite: %d tools vs %d"
                        % (len(default), len(lite)))

    for f in failures:
        print("[FAIL]", f)
    if failures:
        return 1
    print("[ ok ] default is lite; lite resolves all %d golden-path tools directly or through FindTools/CallTool, stays under the %d cap, "
          "and the other %d tools stay reachable via FindTools/CallTool"
          % (len(REQUIRED), HOST_TOOL_CAP, len(full) - len(lite)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
