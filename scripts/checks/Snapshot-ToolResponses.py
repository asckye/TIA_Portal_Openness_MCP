"""Capture and compare offline responses and pre-invocation dispatch refusals.

The executable/harness/PublicAPI options match Snapshot-ToolContracts.py. No TIA
attachment, worker, external process or network tool is invoked. check_usage owns
the existing in-memory example allowlist; additional calls below are deliberately
literal, never selected for execution by a tool description or a name prefix.
V20/V21 use full STDIO plus a separate lite STDIO bridge session. Foundation
14sp1..19 use worker-free STDIO and their host-side argument validators. See the
reviewed rejection rules below; no new target-bound tool body may be invoked.

Normalization rules (no automatic learning/blanket removal of volatile fields):
* Decode JSON in MCP text content; preserve the result/error envelope, non-JSON
  text, all keys and array order. Only the transport's fixed request ID/jsonrpc
  framing is omitted. Messages and error texts remain exact.
* A response envelope's meta.timestamp is a wall-clock DateTime.Now value. Mask
  only ISO timestamps there as <string:timestamp>, never example/source dates.
* Two consecutive V20 captures differed at data.timestamp in
  BuildClassicHmiScreenXml/BuildClassicHmiTagTableXml/BuildClassicHmiMinimalPackage,
  and data.screen.timestamp/data.tagTable.timestamp in the minimal package.
  These builders call DateTime.Now.ToString("O"); mask those five paths only.
* Other environmental fields are NOT masked unless encountered and documented
  here. In particular GUIDs/operation/export IDs, PIDs, paths, machine names,
  durations and binary hashes must first be observed at a specific response path.
  Current calls emit none requiring a mask. A new volatile field fails the two
  capture check instead of silently weakening the guard.

Capture runs twice in fresh processes and refuses to write if normalized results
differ. Output contains one entry per distinct (profile, tool, arguments), sorted by that
identity. GetToolUsage coverage is reported separately from actual tool calls;
retrieving an operation example is not execution of that operation.
TIA_MCP_MAX_RESPONSE_CHARS is fixed at 2000000 so complete JSON is captured, not
an export preview. This baseline does not cover automatic export pagination.
GetToolUsage, responses over 16384 UTF-8 bytes, and the exhaustive lite bridge
refusals store SHA-256, byte length and
top-level JSON types (both protocol envelope and decoded text content). Hashes
cover the ENTIRE normalized response, including messages/errors/array order.
The bridge's repetitive preflight catalog text would otherwise dominate the new
dispatch coverage; its rejection marker is asserted on the original response
before hashing. Existing small full-profile behavior responses remain readable.
Only full-content entries can report an inner differing path; digest entries
report the changed digest path and byte-size delta. No hash field is masked.
"""
import argparse
from collections import Counter
from contextlib import contextmanager
import importlib.util
import hashlib
import json
from pathlib import Path
import queue
import re
import shutil
import subprocess
import sys
import tempfile
import uuid

from tool_usage_checks import check_usage, unwrap_usage


def helper(filename):
    spec = importlib.util.spec_from_file_location(filename.replace('-', '_'),
                                                Path(__file__).with_name(filename + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


resources = helper('Test-ResourceDiscovery')
contracts = helper('Snapshot-ToolContracts')
RELEASES = contracts.RELEASES
FULL_RELEASES = ('20', '21')
RESPONSE_LIMIT = 16 * 1024

# Safety proof for every full-engine tool, including no-argument tools:
# ModelContextProtocol/Tools/McpServer.ArgDiagnostics.cs::WrapTools wraps both
# profiles in VersionPolicyTool (non-isolated server; isolation is never enabled).
# McpServer.VersionPolicy.cs::InvokeAsync rejects case-insensitive duplicate names
# BEFORE VersionCallProblem and inner.InvokeAsync, regardless of inputSchema.
# McpServer.Profile.cs::GetAllTools/GetLiteTools supply the advertised rosters.
# McpServer.ToolBridge.cs::CallTool(string,string) resolves the target then calls
# DuplicateArgumentProblem BEFORE parameter binding and InvokeToolMethod. A mere
# unknown name is NOT safe in this bridge: optional/no-argument targets ignore it.
# The two distinct JSON keys below trigger the same duplicate-name refusal on
# both paths. Do not replace this pair with a single unknown key or valid args.
# CallTool -> CallTool has an earlier self-recursion guard; record that exact
# refusal separately, never count it as duplicate-argument admission coverage.
REJECT_ARGUMENTS = {'SnapshotReject': True, 'snapshotReject': True}
DUPLICATE_MARKER = ('Duplicate argument names differing only by case are ambiguous: '
                    'snapshotReject. Nothing was executed.')
SELF_MARKER = "CallTool cannot invoke itself. Pass the target tool's own name."

# Foundation has no outer ArgDiagnosticTool and no CallTool/lite bridge.
# Reviewed host admission routines in TiaMcpServer.LegacyHost (all InvokeAsync):
# FoundationTools.cs::FoundationTool rejects unknown/case-mismatched keys before
# defaults, contract validation and worker.Call. These names use that class:
FOUNDATION_WORKER_TOOLS = set('''
GetState ListPortalProcessProjects DiagnosePortalConnectReadiness
AddDeviceWithFallback SearchHardwareCatalog GetPlcWatchTables GetTechnologyObjects
GetSoftwareInfo GetSoftwareTree GetBlockInfo GetTypeInfo Connect GetProject
AttachToOpenProject OpenProject CreateProject SaveProject CloseProject GetProjectTree
GetBlocks GetBlocksWithHierarchy Disconnect GetTypes GetPlcTagTables
DeletePlcExternalSource PlanPlcExternalSourceImport GetPlcExternalSources
ImportPlcExternalSource GenerateBlocksFromExternalSource ReadPlcTags
ReadPlcUserConstants ReadPlcSystemConstants ImportBlocksFromDirectory
ImportPlcProgramFromDirectory ExportBlocksAsDocuments ImportBlocksFromDocuments
ImportFromDocuments ExportAsDocuments ExportPlcWatchTable ExportTechnologyObject
ExportBlocks ExportTypes ExportBlock ExportType ExportPlcTagTable ImportBlock
ImportType ImportPlcTagTable CreatePlcTagTable CreatePlcTag CreatePlcUserConstant
CompileSoftware CompileAndDiagnosePlc
'''.split())

# The following named handlers reject unknown keys/counts before their builder,
# filesystem reader, planner or catalog delegate runs. Their catch blocks replace
# the original argument exception with the exact public marker listed here.
# Files: OfflineXmlTools.cs, OfflineCompositionTools.cs,
# OfflineBlockCompositionTools.cs, OfflineLadderTools.cs,
# OfflineSymbolManifestTools.cs, ImportOrderTool.cs, ToolUsageTool.cs,
# LegacyHostPassiveDiagnosticTools.cs. UsageHintTool only forwards InvokeAsync.
FOUNDATION_MARKERS = {
    'BuildPlcUdtXml': 'Invalid offline XML builder input.',
    'BuildPlcTagTableXml': 'Invalid offline XML builder input.',
    'BuildPlcGlobalDbXml': 'Invalid offline composition input.',
    'BuildStructuredTextXml': 'Invalid offline composition input.',
    'ComposePlcFcBlockXml': 'Invalid offline block composition input.',
    'ComposePlcFbBlockXml': 'Invalid offline block composition input.',
    'BuildFlgNetCallXml': 'Invalid offline ladder input.',
    'ComposePlcLadFcBlockXml': 'Invalid offline ladder input.',
    'BuildPlcSymbolManifestFromXmlPath': 'Expected inputRoot, an explicit files array and expectedOrigin.',
    'PlanArtifactImportOrder': 'artifactsJson string is required.',
    'GetToolUsage': 'Unknown argument.',
    'Bootstrap': 'Unsupported passive diagnostic argument.',
    'RunCapabilitySelfTest': 'Unsupported passive diagnostic argument.',
}

# Pure in-memory tools beyond check_usage's ten builders/planners. Exact inputs
# come from GetToolUsage, not another independently maintained example catalog.
PURE_EXAMPLES = (
    'BuildClassicHmiScreenXml', 'BuildClassicHmiTagTableXml',
    'BuildClassicHmiMinimalPackage', 'BuildUnifiedHmiLayoutDesignJson',
    'BuildUnifiedHmiThemeDesignJson', 'PlanHardwareNetworkConfiguration',
    'ComposePlcAliasAlarmLad', 'LintPlcSclSource',
)

# Every current [L1][Domain] group from ToolTaxonomy's description prefixes.
# All native-facing representatives stop at a null project before SDK access.
# Portal/Exports have no connection prerequisite: disconnected Disconnect and
# missing GetExport are their passive/negative representatives instead.
DOMAIN_CALLS = {
    'Diagnostics': 'ValidateAutomationContext',
    'Exports': 'GetExport',
    'HMI': 'CompileAndDiagnoseHmi',
    'Hardware': 'GetProjectTopology',
    'PLC-Online': 'GetOnlineState',
    'PLC-Software': 'GetSoftwareTree',
    'Portal': 'Disconnect',
    'Project': 'GetProjectTree',
    'VersionControl': 'GetVersionControlWorkspaces',
}

# ToolVersionPolicy.V21Only. Both direct admission and bridge admission are
# exercised on V20; never execute these target-bound examples on V21.
V21_ONLY = (
    'ReadCommunicationConnections', 'ManageCommunicationConnection',
    'ReadSafetyActivationTests', 'ManageSafetyActivationTest',
    'ManageSafetyActivationTestGroup', 'ManageSafetyFunction',
    'ManageSafetyFunctionCondition', 'ManagePlcBlockWriteProtection',
    'ManageDriveSafetyAcceptanceTest', 'ManageSivarcScreenLayout',
    'ManageClassicHmiGraphic',
)


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':'),
                      allow_nan=False)


def identity(call):
    return call['profile'], call['tool'], canonical(call['arguments'])


def normalize(call):
    result = json.loads(canonical(call))
    paths = [('meta', 'timestamp')]
    if call['tool'] in ('BuildClassicHmiScreenXml', 'BuildClassicHmiTagTableXml',
                        'BuildClassicHmiMinimalPackage'):
        paths.append(('data', 'timestamp'))
    if call['tool'] == 'BuildClassicHmiMinimalPackage':
        paths.extend([('data', 'screen', 'timestamp'), ('data', 'tagTable', 'timestamp')])
    # Only actual MCP result content: never recurse into arguments, usage
    # examples, XML strings, error text, source hashes or arbitrary nested JSON.
    for block in result['response'].get('result', {}).get('content', []):
        for path in paths:
            parent = block.get('text')
            for key in path[:-1]:
                parent = parent.get(key) if isinstance(parent, dict) else None
            stamp = parent.get(path[-1]) if isinstance(parent, dict) else None
            if isinstance(stamp, str) and re.fullmatch(
                    r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)', stamp):
                parent[path[-1]] = '<string:timestamp>'
    return result


def json_type(value):
    if value is None:
        return 'null'
    if isinstance(value, bool):
        return 'boolean'
    if isinstance(value, (int, float)):
        return 'number'
    if isinstance(value, str):
        return 'string'
    return 'object' if isinstance(value, dict) else 'array'


def shape(value):
    return {key: json_type(item) for key, item in value.items()} if isinstance(value, dict) else json_type(value)


def compact(call):
    call = normalize(call)
    response = call['response']
    raw = canonical(response).encode('utf-8')
    if call['tool'] == 'GetToolUsage' or len(raw) > RESPONSE_LIMIT or call['profile'] == 'lite':
        call.pop('response')
        call['responseDigest'] = {'sha256': hashlib.sha256(raw).hexdigest(), 'length': len(raw),
            'shape': shape(response), 'textShapes': [shape(block['text'])
                for block in response.get('result', {}).get('content', []) if 'text' in block]}
    return call


def snapshot_text(snapshot):
    # One sorted call per line keeps exhaustive dispatch/catalog coverage small
    # while retaining useful diffs. Metadata remains indented for readability.
    metadata = {key: value for key, value in snapshot.items() if key != 'calls'}
    return ('{\n  "calls": [\n' + ',\n'.join('    ' + canonical(call) for call in snapshot['calls'])
            + '\n  ],' + json.dumps(metadata, ensure_ascii=False, sort_keys=True, indent=2)[1:] + '\n')


def decode_reply(reply):
    # Keep protocol errors too (unknown/unavailable tools return JSON-RPC errors).
    result = {key: value for key, value in reply.items() if key not in ('id', 'jsonrpc')}
    for block in result.get('result', {}).get('content', []):
        if block.get('type') == 'text':
            try:
                block['text'] = json.loads(block['text'])
            except (ValueError, TypeError):
                pass
    return result


def body(response):
    result = response.get('result', {})
    content = result.get('content', [])
    resources.require(not result.get('isError') and len(content) == 1
                      and isinstance(content[0].get('text'), dict),
                      'Expected a JSON tool result: ' + canonical(response))
    decoded = content[0]['text']
    resources.require(not decoded.get('meta', {}).get('truncated'),
                      'Response was parked; add explicit GetExport paging before recording it')
    return decoded


def rejection(response, marker):
    result = response.get('result', {})
    error = response.get('error', {})
    text = '\n'.join(block['text'] for block in result.get('content', [])
                     if isinstance(block.get('text'), str))
    resources.require((result.get('isError') is True or error.get('code') == -32602)
                      and marker in (text + error.get('message', '')),
                      'Missing pre-invocation rejection marker: ' + canonical(response))


def initialize(rpc):
    reply = rpc('initialize', params={'protocolVersion': '2024-11-05',
        'capabilities': {}, 'clientInfo': {'name': 'response-snapshot', 'version': '2'}})
    resources.require('result' in reply, 'Initialize failed: ' + canonical(reply))
    rpc('notifications/initialized', notification=True)
    tools, cursor, seen = [], None, set()
    while True:
        reply = rpc('tools/list', params={} if cursor is None else {'cursor': cursor})
        tools.extend(reply['result']['tools'])
        cursor = reply['result'].get('nextCursor')
        if cursor is None:
            break
        resources.require(cursor not in seen, 'Repeated tools/list cursor')
        seen.add(cursor)
    contracts.tool_records(tools)
    return tools


def recorder(rpc, entries, profile):
    def call(name, arguments):
        key = profile, name, canonical(arguments)
        if key not in entries:
            response = decode_reply(rpc('tools/call', params={'name': name, 'arguments': arguments}))
            entries[key] = {'profile': profile, 'tool': name, 'arguments': arguments, 'response': response}
        return entries[key]['response']
    return call


@contextmanager
def scratch_directory(parent):
    # mkdir's ordinary inherited ACL is intentional: Python 3.12's Windows
    # mkdtemp(mode=0700) prevents the sandboxed host from writing its journal.
    parent.mkdir(parents=True, exist_ok=True)
    scratch = parent / ('tool-responses-' + uuid.uuid4().hex)
    scratch.mkdir()
    try:
        yield scratch.resolve()
    finally:
        # Only remove the directory created here, after verifying containment.
        resources.require(scratch.resolve().parent == parent.resolve(), 'Unsafe scratch path')
        shutil.rmtree(scratch)


def capture_release(args, release, exe, public_api):
    with scratch_directory(args.temp_root) as scratch:
        env = {'TEMP': str(scratch), 'TMP': str(scratch), 'TIA_MCP_MAX_RESPONSE_CHARS': '2000000',
               'TIA_MCP_DIAGNOSTICS_DIRECTORY': str(scratch / 'diagnostics')}
        with resources.server(exe, public_api, int(release), 'stdio', 'full',
                              args.harness.resolve(), public_api, env_overrides=env
                              ) as (rpc, _, logs):
            tools = initialize(rpc)
            entries = {}
            call = recorder(rpc, entries, 'full')

            def decoded(name, arguments):
                return body(call(name, arguments))

            state = decoded('GetState', {})
            resources.require(state['isConnected'] is False, 'Capture requires a disconnected host')
            resources.require(state['meta']['journalHealth']['failedWrites'] == 0,
                              'Journal is not writable; use --temp-root inside the writable worktree')
            decoded('ReadPortalInfo', {'includeProcesses': False, 'includeSessions': False,
                                       'includeProducts': False})
            decoded('ListToolCategories', {})
            decoded('FindTools', {'query': 'ManageMotionAxis', 'limit': 1})
            decoded('FindTools', {'query': 'no-such-snapshot-tool', 'limit': 3})
            decoded('PreflightToolCall', {'name': 'GetDevices', 'argumentsJson': '{}'})
            decoded('PreflightToolCall', {'name': 'GetBlocks', 'argumentsJson': '{}'})

            # The existing audit performs schema/operation checks and executes its
            # literal in-memory allowlist, using the same public examples as users.
            usage = check_usage(decoded, tools, release, exhaustive=True, verify_documents=False)

            def example(name):
                return unwrap_usage(decoded('GetToolUsage', {'toolName': name}))['example']['request']['params']['arguments']

            for name in PURE_EXAMPLES:
                arguments = example(name)
                resources.require(not arguments.get('filePath'), 'Pure example acquired a file input')
                decoded(name, arguments)

            domains = {}
            for tool in tools:
                match = re.match(r'^\s*\[L1\]\[(?:Category:)?([^\]]+)\]', tool['description'])
                if match:
                    domains.setdefault(match[1].strip(), set()).add(tool['name'])
            resources.require(set(domains) == set(DOMAIN_CALLS),
                              'L1 taxonomy changed; review offline domain representatives')
            for domain, name in sorted(DOMAIN_CALLS.items()):
                resources.require(name in domains[domain], name + ' moved out of its L1 domain')
                call(name, example(name))

            # Unknown argument, wrong JSON type, and missing required argument.
            # Safe targets ensure even a diagnostic regression cannot attach TIA.
            call('GetState', {'unknownParameter': True})
            call('FindTools', {'query': 'PLC', 'limit': {'wrong': 'type'}})
            call('BuildPlcUdtXml', {})
            if release == '20':
                for name in V21_ONLY:
                    call(name, {})
                    decoded('PreflightToolCall', {'name': name, 'argumentsJson': '{}'})
                    call('CallTool', {'name': name, 'argumentsJson': '{}'})

            registered = {tool['name'] for tool in tools}
            behavior_calls = sorted({entry['tool'] for entry in entries.values()} & registered)
            selected_operations = sorted({(entry['tool'], str(entry['arguments'][key]))
                for entry in entries.values() for key in ('action', 'operation')
                if key in entry['arguments'] and entry['tool'] != 'GetToolUsage'})
            for name in sorted(registered):
                rejection(call(name, REJECT_ARGUMENTS), DUPLICATE_MARKER)
            snapshot = {'formatVersion': 2, 'release': release, 'profiles': ['full', 'lite'], 'transport': 'stdio',
                'maxResponseChars': 2000000,
                'coverage': {'registeredTools': len(tools), 'calledTools': sorted(registered),
                    'behaviorCallTools': behavior_calls, 'directRejectedTools': sorted(registered),
                    'directSkipped': {},
                    'calledOperations': [list(pair) for pair in selected_operations],
                    'usageTools': usage['checkedToolCount'], 'usageOperations': usage['operationExampleCount'],
                    'offlineExamples': sorted(usage['offlineCallExamplesExecuted'] + list(PURE_EXAMPLES)),
                    'l1Domains': DOMAIN_CALLS}}
        resources.require(not any('Invocation journal unavailable' in line for line in logs),
                          'Invocation journal failed during capture')
        with resources.server(exe, public_api, int(release), 'stdio', 'lite',
                              args.harness.resolve(), public_api, env_overrides=env) as (rpc, _, logs):
            lite = initialize(rpc)
            resources.require('CallTool' in {t['name'] for t in lite}, 'Lite bridge is not advertised')
            bridge = recorder(rpc, entries, 'lite')
            for name in sorted(registered):
                reply = body(bridge('CallTool', {'name': name, 'argumentsJson': canonical(REJECT_ARGUMENTS)}))
                marker = SELF_MARKER if name == 'CallTool' else DUPLICATE_MARKER
                resources.require(reply['meta'].get('bridgeSuccess') is False and reply['message'] == marker,
                                  name + ': missing bridge admission marker: ' + canonical(reply))
            snapshot['coverage'].update(bridgeRejectedTools=sorted(registered - {'CallTool'}),
                bridgeSelfGuardTools=['CallTool'], bridgeSkipped={},
                liteAdvertisedTools=sorted(t['name'] for t in lite))
        resources.require(not any('Invocation journal unavailable' in line for line in logs),
                          'Invocation journal failed during bridge capture')
        snapshot['calls'] = [compact(entries[key]) for key in sorted(entries)]
        return snapshot


def capture_foundation(args, release, exe):
    with scratch_directory(args.temp_root) as scratch:
        # Shared server launcher accepts release keys verbatim. LegacyHost
        # HostOptions.Parse accepts --tia-major-version/--tia-portal-location;
        # no harness, --catalog, worker executable or native-session flag is used.
        env = {'TEMP': str(scratch), 'TMP': str(scratch)}
        if args.dotnet_root:
            env.update(DOTNET_ROOT=str(args.dotnet_root.resolve()),
                       DOTNET_ROOT_X64=str(args.dotnet_root.resolve()))
        with resources.server(exe, scratch, release, 'stdio', 'full', env_overrides=env) as (rpc, _, _):
            tools = initialize(rpc)
            entries, rejected, skipped = {}, [], {}
            call = recorder(rpc, entries, 'plc-foundation')
            for tool in sorted(tools, key=lambda t: t['name']):
                name = tool['name']
                marker = ('Unknown/case-mismatched argument: SnapshotReject'
                          if name in FOUNDATION_WORKER_TOOLS else FOUNDATION_MARKERS.get(name))
                props = tool['inputSchema'].get('properties', {})
                if marker is None or any(key.lower() == 'snapshotreject' for key in props):
                    skipped[name] = 'No reviewed host argument rejection before the worker/builder; not invoked.'
                    continue
                rejection(call(name, REJECT_ARGUMENTS), marker)
                rejected.append(name)
            # Only these two passive tools call the pure registration inspector.
            # Foundation GetState is NOT passive in the host: FoundationTool maps
            # it to worker.Call("ReadState"); WorkerClient.Call starts a worker
            # when process is null. Do not try a valid GetState call here.
            passive = ['Bootstrap', 'RunCapabilitySelfTest']
            for name in passive:
                reply = body(call(name, {}))
                resources.require(reply['sideEffects']['workerInvoked'] is False
                                  and reply['sideEffects']['tiaLaunchedOrAttached'] is False
                                  and reply['checks']['passed'] is True,
                                  name + ': passive contract changed')
            names = sorted(t['name'] for t in tools)
            resources.require('CallTool' not in names, 'Foundation added a bridge; review its rejection path first')
            return {'formatVersion': 2, 'release': release, 'profiles': ['plc-foundation'], 'transport': 'stdio',
                'coverage': {'registeredTools': len(tools), 'calledTools': sorted(set(rejected) | set(passive)),
                    'directRejectedTools': rejected, 'directSkipped': skipped, 'passiveTools': passive,
                    'passiveSkipped': {'GetState': 'Requires worker ReadState; no worker is started by this capture.'},
                    'bridgeRejectedTools': [],
                    'bridgeSkipped': {name: 'Foundation does not advertise CallTool or a lite bridge.' for name in names}},
                'calls': [compact(entries[key]) for key in sorted(entries)]}


def capture(args):
    root = args.repo_root.resolve()
    api_root = (args.public_api_root or root).resolve()
    executables = {}
    for override in args.exe:
        release, separator, path = override.partition('=')
        if not separator or release not in RELEASES or not path or release in executables:
            raise ValueError('--exe must be a unique RELEASE=PATH for a supported release')
        executables[release] = Path(path).resolve()
    snapshots = {}
    for release in args.releases:
        exe = executables.get(release, root / 'runtime' / ('v' + release) / 'TiaMcpServer.exe')
        if release in FULL_RELEASES:
            api = api_root / ('TIA_V' + release + '_PublicAPI') / ('V' + release)
            if release == '21':
                api /= 'net48'
            first = capture_release(args, release, exe, api)
            second = capture_release(args, release, exe, api)
        else:
            first = capture_foundation(args, release, exe)
            second = capture_foundation(args, release, exe)
        difference = first_difference(first, second)
        if difference:
            raise ValueError(f'V{release}: consecutive captures differ at {difference}; baseline not written')
        snapshots[release] = first
        c = first['coverage']
        print(f'V{release}: {len(first["calls"])} calls, {len(c["directRejectedTools"])}/{c["registeredTools"]} '
              f'direct refusals, {len(c["bridgeRejectedTools"])} bridge argument refusals, '
              f'{len(c.get("bridgeSelfGuardTools", []))} bridge self-guard, '
              f'{len(snapshot_text(first).encode("utf-8"))} bytes; two captures identical', flush=True)
    args.output.mkdir(parents=True, exist_ok=True)
    for release, snapshot in snapshots.items():
        (args.output / (release + '.json')).write_text(snapshot_text(snapshot), encoding='utf-8', newline='\n')
    return 0


def first_difference(old, new, path='$'):
    if type(old) is not type(new):
        return path + ' (type changed)'
    if isinstance(old, dict):
        for key in sorted(old.keys() | new.keys()):
            location = path + '/' + key.replace('~', '~0').replace('/', '~1')
            if key not in old or key not in new:
                return location + (' (added)' if key not in old else ' (removed)')
            difference = first_difference(old[key], new[key], location)
            if difference:
                return difference
    elif isinstance(old, list):
        for index, (a, b) in enumerate(zip(old, new)):
            difference = first_difference(a, b, path + '/' + str(index))
            if difference:
                return difference
        if len(old) != len(new):
            return path + '/' + str(min(len(old), len(new))) + ' (array length changed)'
    elif old != new:
        return path
    return None


def load_snapshots(directory):
    snapshots = {}
    for path in sorted(directory.glob('*.json')):
        snapshot = json.loads(path.read_text(encoding='utf-8'))
        release = snapshot['release']
        calls = snapshot['calls']
        if path.stem != release or release in snapshots or not calls:
            raise ValueError('Invalid/empty response snapshot: ' + str(path))
        if len({identity(call) for call in calls}) != len(calls):
            raise ValueError('Duplicate call identity: ' + str(path))
        snapshots[release] = snapshot
    if not snapshots:
        raise ValueError('No response snapshots in ' + str(directory))
    return snapshots


def response_length(call):
    return call['responseDigest']['length'] if 'responseDigest' in call else len(canonical(call['response']).encode('utf-8'))


def compare(args):
    baseline, current = load_snapshots(args.baseline), load_snapshots(args.current)
    total = Counter()
    releases = set(args.releases) if args.releases else baseline.keys() | current.keys()
    missing = releases - (baseline.keys() | current.keys())
    if missing:
        raise ValueError('Selected releases have no snapshots: ' + ', '.join(sorted(missing)))
    for release in sorted(releases):
        old, new = baseline.get(release, {}), current.get(release, {})
        a = {identity(call): call for call in old.get('calls', [])}
        b = {identity(call): call for call in new.get('calls', [])}
        changes = []
        for key in sorted(a.keys() | b.keys()):
            label = key[0] + ' ' + key[1] + '(' + key[2] + ')'
            if key not in b:
                changes.append(('removed', label))
            elif key not in a:
                changes.append(('added', label))
            else:
                difference = first_difference(a[key], b[key])
                if difference:
                    before, after = response_length(a[key]), response_length(b[key])
                    if ('responseDigest' in a[key] and 'responseDigest' in b[key]
                            and a[key]['responseDigest']['sha256'] != b[key]['responseDigest']['sha256']):
                        difference = '$/responseDigest/sha256'
                    changes.append(('changed', label + ' ' + difference
                                    + f'; response bytes {before} -> {after} (delta {after - before:+d})'))
        metadata = first_difference({k: v for k, v in old.items() if k != 'calls'},
                                    {k: v for k, v in new.items() if k != 'calls'})
        counts = Counter(kind for kind, _ in changes)
        counts['metadata'] = int(metadata is not None)
        total.update(counts)
        print(f'V{release}: changed={counts["changed"]} added={counts["added"]} removed={counts["removed"]} metadata={counts["metadata"]}')
        for kind, detail in changes:
            print(f'  {kind}: {detail}')
        if metadata:
            print('  metadata: ' + metadata)
    print(f'TOTAL: changed={total["changed"]} added={total["added"]} removed={total["removed"]} metadata={total["metadata"]}')
    # Additions also fail: migration acceptance requires exactly zero differences.
    return int(any(total.values()))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    capture_parser = commands.add_parser('capture')
    capture_parser.add_argument('--repo-root', required=True, type=Path)
    capture_parser.add_argument('--harness', required=True, type=Path)
    capture_parser.add_argument('--public-api-root', type=Path)
    capture_parser.add_argument('--dotnet-root', type=Path,
                                help='Private .NET/ASP.NET Core 8 root; sets DOTNET_ROOT and DOTNET_ROOT_X64 for Foundation only')
    capture_parser.add_argument('--exe', action='append', default=[], metavar='RELEASE=PATH')
    capture_parser.add_argument('--releases', nargs='+', choices=RELEASES, default=RELEASES)
    capture_parser.add_argument('--output', required=True, type=Path)
    capture_parser.add_argument('--temp-root', type=Path, default=Path(tempfile.gettempdir()),
                                help='Parent for disposable host journals (use a writable worktree directory in a sandbox)')
    capture_parser.set_defaults(run=capture)
    compare_parser = commands.add_parser('compare')
    compare_parser.add_argument('--baseline', required=True, type=Path)
    compare_parser.add_argument('--current', required=True, type=Path)
    compare_parser.add_argument('--releases', nargs='+', choices=RELEASES,
                                help='Compare only these releases (default: compare all releases strictly)')
    compare_parser.set_defaults(run=compare)
    args = parser.parse_args()
    try:
        return args.run(args)
    except (OSError, ValueError, KeyError, AssertionError, queue.Empty, subprocess.SubprocessError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
