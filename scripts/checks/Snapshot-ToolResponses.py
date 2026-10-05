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
* V4 infrastructure envelopes introduce meta.requestId correlation GUIDs. Mask
  only the envelope and batch-child envelope paths, never example data.
* Other environmental fields are NOT masked unless encountered and documented
  here. In particular GUIDs/operation/export IDs, PIDs, paths, machine names,
  durations and binary hashes must first be observed at a specific response path.
  A new volatile field fails the two
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

Format 3 additionally hashes every original MCP text block's UTF-8 bytes BEFORE
decode_reply or canonicalization. The transport JSON string has already been
read by the RPC client; its inner text is not decoded/re-serialized for this hash.
RAW_MASK_RULES is the complete reviewed allowlist, including reasons. A lexical
JSON walk locates literal paths; regex substitutes only the timestamp's contents,
preserving quotes, whitespace, key order, escapes and all surrounding text. No
elapsed-time, PID, arbitrary GUID or temp-path mask is used by the capture set.
Unknown paths/encodings remain visible and must fail the consecutive-capture gate.
"""
import argparse
from collections import Counter
from contextlib import contextmanager
import importlib.util
import io
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
import unittest
from contextlib import redirect_stdout

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
# McpServer.ToolBridge.cs::CallTool(string,ToolArguments) calls BindV4Call, which
# rejects case-insensitive duplicate properties BEFORE schema validation, binding
# and InvokeToolMethod. It returns a V4 INVALID_ARGUMENT/not-started envelope.
# The two distinct JSON keys below trigger the same duplicate-name refusal on
# both paths. Do not replace this pair with a single unknown key or valid args.
# CallTool -> CallTool has an earlier self-recursion guard; record that exact
# refusal separately, never count it as duplicate-argument admission coverage.
REJECT_ARGUMENTS = {'SnapshotReject': True, 'snapshotReject': True}
DUPLICATE_MARKER = ('Duplicate argument names differing only by case are ambiguous: '
                    'snapshotReject. Nothing was executed.')
SELF_MARKER = "CallTool cannot invoke itself. Pass the target tool's own name."


def v4_tools(release=None):
    """Registered names whose runtime record carries envelopeVersion 4 (the P6-07b marker)."""
    import xml.etree.ElementTree as ET
    resource = Path(__file__).resolve().parents[2] / 'src/Logic/ModelContextProtocol/ToolProfiles.resx'
    releases = json.loads(ET.parse(resource).find(".//data[@name='Catalog']/value").text)['releases']
    return {row['currentName'] for key, rows in releases.items() if release in (None, key)
            for row in rows if row.get('envelopeVersion') == 4}


V4_TOOLS = v4_tools()

# Foundation has no outer ArgDiagnosticTool and no CallTool/lite bridge.
# FoundationV4Tool.InvokeAsync applies typed contracts and the closed input schema
# before invoking any existing worker/builder. Both rejection keys are unknown;
# every reviewed entry must return INVALID_ARGUMENT/not-started in a V4 envelope.
FOUNDATION_WORKER_TOOLS = set('''
GetSessionState ListPortalProcessProjects GetPortalConnectionReadiness
CreateHardwareDevice SearchHardwareCatalog ListPlcWatchTables ListTechnologyObjects
GetSoftwareInfo GetSoftwareTree GetPlcBlockInfo GetPlcTypeInfo ConnectPortal GetProjectInfo
AttachOpenProject OpenProject CreateProject SaveProject CloseProject GetProjectTree
ListPlcBlocks GetPlcBlockHierarchy DisconnectPortal ListPlcTypes ListPlcTagTables
DeletePlcExternalSource PlanPlcExternalSourceImport ListPlcExternalSources
ImportPlcExternalSource GenerateBlocksFromExternalSource ListPlcTags
ListPlcUserConstants ListPlcSystemConstants ImportPlcBlocksFromDirectory
ImportPlcProgramFromDirectory ExportBlocksAsDocuments ImportBlocksFromDocuments
ImportFromDocuments ExportAsDocuments ExportPlcWatchTable ExportTechnologyObject
ExportPlcBlocks ExportPlcTypes ExportPlcBlock ExportPlcType ExportPlcTagTable ImportPlcBlock
ImportPlcType ImportPlcTagTable CreatePlcTagTable CreatePlcTag CreatePlcUserConstant
CompilePlcSoftware CompilePlcDiagnostics
'''.split())

# Host-only handlers share the same V4 refusal at that boundary.
FOUNDATION_MARKERS = {
    'BuildPlcUdt': 'INVALID_ARGUMENT',
    'BuildPlcTagTable': 'INVALID_ARGUMENT',
    'BuildPlcGlobalDb': 'INVALID_ARGUMENT',
    'BuildStructuredText': 'INVALID_ARGUMENT',
    'BuildPlcFcBlock': 'INVALID_ARGUMENT',
    'BuildPlcFbBlock': 'INVALID_ARGUMENT',
    'BuildFlgNetCall': 'INVALID_ARGUMENT',
    'BuildPlcLadFcBlock': 'INVALID_ARGUMENT',
    'BuildPlcSymbolManifestFromPath': 'INVALID_ARGUMENT',
    'PlanArtifactImportOrder': 'INVALID_ARGUMENT',
    'GetToolUsage': 'INVALID_ARGUMENT',
    'InitializeEnvironment': 'INVALID_ARGUMENT',
    'RunCapabilitySelfTest': 'INVALID_ARGUMENT',
}

# Pure in-memory tools beyond check_usage's ten builders/planners. Exact inputs
# come from GetToolUsage, not another independently maintained example catalog.
PURE_EXAMPLES = (
    'BuildClassicHmiScreen', 'BuildClassicHmiTagTable',
    'BuildClassicHmiMinimalPackage', 'BuildUnifiedHmiLayoutDesign',
    'BuildUnifiedHmiThemeDesign', 'PlanHardwareNetworkConfiguration',
    'BuildPlcAliasAlarmLad', 'AnalyzePlcSclSource',
)

# Reviewed passive resource calls. These literal inputs never start a worker,
# execute guide content, or contact a network service. Keep the bin layout for
# CheckForUpdate: its four-parent delivery probe must return null (asserted below).
PASSIVE_RESOURCE_CALLS = (
    ('GetOpennessGuidance', {},
     'Lists bundled document IDs and line counts; no absolute paths in output.'),
    ('GetOpennessGuidance', {'query': 'hardware'},
     'Searches bundled Markdown as data; returns only IDs and line counts.'),
    ('GetOpennessGuidance', {'document': 'blocks/SKILL.md', 'limit': 20},
     'Reads a fixed page of a bundled document; never executes its instructions.'),
    ('GetV21EcosystemCatalog', {},
     'Reads the dated local JSON survey; no network lookup or native calls.'),
    ('CheckForUpdate', {'repository': 'x'},
     'Invalid owner/name returns before HTTP; bin layout has no installRoot.'),
)

# Every current [L1][Domain] group from ToolTaxonomy's description prefixes.
# All native-facing representatives stop at a null project before SDK access.
# Portal/Exports have no connection prerequisite: disconnected Disconnect and
# missing export content are their passive/negative representatives instead.
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
    'ListSafetyActivationTests', 'ManageSafetyActivationTest',
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


RAW_MASK_RULES = [
    {'tool': 'V4 infrastructure only', 'kind': 'requestId', 'path': ['meta', 'requestId'],
     'reason': 'V4 invocation journal correlation ID (32 lowercase hex). Also masks actual batch result envelopes, never examples.'},
    {'tool': '*', 'path': ['meta', 'timestamp'],
     'reason': 'Response envelope wall clock (DateTime.Now).'},
    *[{'tool': tool, 'path': ['data', 'timestamp'],
       'reason': 'Classic HMI builder DateTime.Now.ToString("O").'}
      for tool in ('BuildClassicHmiScreen', 'BuildClassicHmiTagTable',
                   'BuildClassicHmiMinimalPackage')],
    *[{'tool': 'BuildClassicHmiMinimalPackage', 'path': ['data', part, 'timestamp'],
       'reason': 'Embedded Classic HMI builder DateTime.Now.ToString("O").'}
      for part in ('screen', 'tagTable')],
]
RAW_TOKEN = re.compile(r'"(?:[^"\\\x00-\x1f]|\\(?:["\\/bfnrt]|u[0-9a-fA-F]{4}))*"'
                       r'|[{}\[\]:,]|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null')
RAW_TIMESTAMP = re.compile(r'(?<=")\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)(?=")')


def mask_raw_text(text, tool):
    # No JSON decoding: keep source spans and literal property tokens. Escaped
    # property names deliberately do not match the reviewed literal paths.
    paths = {tuple('"' + key + '"' for key in rule['path']) for rule in RAW_MASK_RULES
             if rule['tool'] in ('*', tool)}
    tokens = list(RAW_TOKEN.finditer(text))
    end = 0
    for token in tokens:
        if re.search(r'[^ \t\r\n]', text[end:token.start()]):
            return text
        end = token.end()
    if re.search(r'[^ \t\r\n]', text[end:]) or not tokens:
        return text
    replacements = []
    index = 0

    def take(expected=None):
        nonlocal index
        if index == len(tokens):
            raise ValueError('Incomplete JSON')
        token = tokens[index]
        if expected is not None and token.group() != expected:
            raise ValueError('Unexpected JSON token')
        index += 1
        return token

    def value(path):
        token = take()
        raw = token.group()
        if raw == '{':
            if index < len(tokens) and tokens[index].group() != '}':
                while True:
                    key = take().group()
                    if not key.startswith('"'):
                        raise ValueError('Expected property')
                    take(':')
                    value(path + (key,))
                    if index == len(tokens) or tokens[index].group() != ',':
                        break
                    take(',')
            take('}')
        elif raw == '[':
            item = 0
            if index < len(tokens) and tokens[index].group() != ']':
                while True:
                    value(path + (item,))
                    item += 1
                    if index == len(tokens) or tokens[index].group() != ',':
                        break
                    take(',')
            take(']')
        elif raw in ('}', ']', ':', ','):
            raise ValueError('Expected value')
        elif (tool in V4_TOOLS and raw.startswith('"')
              and (path in (('"meta"', '"requestId"'), ('"meta"', '"timestamp"'))
                   or len(path) == 6 and path[:2] == ('"data"', '"items"') and isinstance(path[2], int)
                   and path[3:5] == ('"result"', '"meta"') and path[5] in ('"requestId"', '"timestamp"'))):
            masked = re.sub(r'(?<=")[0-9a-f]{32}(?=")', '<string:requestId>', raw) if path[-1] == '"requestId"' else RAW_TIMESTAMP.sub('<string:timestamp>', raw)
            if masked != raw: replacements.append((token.start(), token.end(), masked))
        elif path in paths and raw.startswith('"'):
            masked, count = RAW_TIMESTAMP.subn('<string:timestamp>', raw)
            if count:
                replacements.append((token.start(), token.end(), masked))

    try:
        value(())
        if index != len(tokens):
            return text
    except (ValueError, RecursionError):
        return text
    for start, end, masked in reversed(replacements):
        text = text[:start] + masked + text[end:]
    return text


def raw_text_blocks(reply, tool):
    return [{'contentIndex': index,
             'sha256': hashlib.sha256(mask_raw_text(block['text'], tool).encode('utf-8')).hexdigest()}
            for index, block in enumerate(reply.get('result', {}).get('content', []))
            if block.get('type') == 'text']


def normalize(call):
    result = json.loads(canonical(call))
    paths = [('meta', 'timestamp')]
    if call['tool'] in ('BuildClassicHmiScreen', 'BuildClassicHmiTagTable',
                        'BuildClassicHmiMinimalPackage'):
        paths.append(('data', 'timestamp'))
    if call['tool'] == 'BuildClassicHmiMinimalPackage':
        paths.extend([('data', 'screen', 'timestamp'), ('data', 'tagTable', 'timestamp')])
    # Only actual MCP result content: never recurse into arguments, usage
    # examples, XML strings, error text, source hashes or arbitrary nested JSON.
    response = result['response'].get('result', {})
    blocks = list(response.get('content', []))
    if call['tool'] in ('BuildClassicHmiScreen', 'BuildClassicHmiTagTable', 'BuildClassicHmiMinimalPackage'):
        # P6-09 exposes the same builder data in structuredContent and text.
        # Apply only the existing Classic HMI timestamp paths to that copy.
        blocks.append({'text': response.get('structuredContent')})
    for block in blocks:
        for path in paths:
            parent = block.get('text')
            for key in path[:-1]:
                parent = parent.get(key) if isinstance(parent, dict) else None
            stamp = parent.get(path[-1]) if isinstance(parent, dict) else None
            if isinstance(stamp, str) and re.fullmatch(
                    r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)', stamp):
                parent[path[-1]] = '<string:timestamp>'
    def v4_envelope(envelope):
        if not isinstance(envelope, dict) or envelope.get('schemaVersion') != 4: return
        meta = envelope['meta']
        meta['timestamp'] = '<string:timestamp>'
        if re.fullmatch('[0-9a-f]{32}', meta['requestId']): meta['requestId'] = '<string:requestId>'
        for item in (envelope.get('data') or {}).get('items', []):
            if isinstance(item, dict): v4_envelope(item.get('result'))
    if call['tool'] in V4_TOOLS:
        protocol = result['response'].get('result', {})
        v4_envelope(protocol.get('structuredContent'))
        for block in protocol.get('content', []): v4_envelope(block.get('text'))
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
    resources.require(len(content) == 1 and isinstance(content[0].get('text'), dict)
                      and (not result.get('isError') or content[0]['text'].get('schemaVersion') == 4),
                      'Expected a JSON tool result: ' + canonical(response))
    decoded = content[0]['text']
    resources.require(not decoded.get('meta', {}).get('truncated'),
                      'Response was parked; add explicit GetExport paging before recording it')
    return decoded


def v4_rejection(response, name):
    reply = body(response)
    resources.require(reply.get('schemaVersion') == 4 and reply['ok'] is False
                      and reply['error']['code'] == 'INVALID_ARGUMENT'
                      and reply['meta']['execution'] == 'not-started',
                      name + ': missing V4 admission marker: ' + canonical(reply))


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
            reply = rpc('tools/call', params={'name': name, 'arguments': arguments})
            raw_blocks = raw_text_blocks(reply, name)
            response = decode_reply(reply)
            entries[key] = {'profile': profile, 'tool': name, 'arguments': arguments,
                            'response': response, 'rawTextBlocks': raw_blocks}
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
            decoded('PreviewToolCall', {'name': 'GetDevices', 'arguments': {}})
            decoded('PreviewToolCall', {'name': 'GetBlocks', 'arguments': {}})

            for name, arguments, reason in PASSIVE_RESOURCE_CALLS:
                result = decoded(name, arguments)
                meta = result['meta']
                if name == 'CheckForUpdate':
                    resources.require(meta['installRoot'] is None and meta['updaterScript'] is None
                                      and meta['success'] is False and 'releaseApiUrl' not in meta
                                      and result['message'].startswith("repository must be 'owner/name'"),
                                      'Expected bin-layout update refusal before HTTP: ' + reason)
                else:
                    resources.require(result['ok'] is True and result['data']['total'] > 0,
                                      'Expected successful offline resource read: ' + reason)

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
            domain_calls = dict(DOMAIN_CALLS)
            if 'GetExportContent' in domains['Exports']:
                domain_calls['Exports'] = 'GetExportContent'
            for domain, name in sorted(domain_calls.items()):
                resources.require(name in domains[domain], name + ' moved out of its L1 domain')
                call(name, example(name))

            # Unknown argument, wrong JSON type, and missing required argument.
            # Safe targets ensure even a diagnostic regression cannot attach TIA.
            call('GetState', {'unknownParameter': True})
            call('FindTools', {'query': 'PLC', 'limit': {'wrong': 'type'}})
            call('BuildPlcUdt', {})
            if release == '20':
                for name in V21_ONLY:
                    call(name, {})
                    decoded('PreviewToolCall', {'name': name, 'arguments': {}})
                    call('CallTool', {'name': name, 'arguments': {}})

            registered = {tool['name'] for tool in tools}
            behavior_calls = sorted({entry['tool'] for entry in entries.values()} & registered)
            selected_operations = sorted({(entry['tool'], str(entry['arguments'][key]))
                for entry in entries.values() for key in ('action', 'operation')
                if key in entry['arguments'] and entry['tool'] != 'GetToolUsage'})
            v4 = v4_tools(release)
            for name in sorted(registered):
                reply = call(name, REJECT_ARGUMENTS)
                v4_rejection(reply, name) if name in v4 else rejection(reply, DUPLICATE_MARKER)
            snapshot = {'formatVersion': 3, 'rawMaskRules': RAW_MASK_RULES,
                'release': release, 'profiles': ['full', 'lite'], 'transport': 'stdio',
                'maxResponseChars': 2000000,
                'coverage': {'registeredTools': len(tools), 'calledTools': sorted(registered),
                    'behaviorCallTools': behavior_calls, 'directRejectedTools': sorted(registered),
                    'directSkipped': {},
                    'calledOperations': [list(pair) for pair in selected_operations],
                    'usageTools': usage['checkedToolCount'], 'usageOperations': usage['operationExampleCount'],
                    'offlineExamples': sorted(usage['offlineCallExamplesExecuted'] + list(PURE_EXAMPLES)),
                    'l1Domains': domain_calls}}
        resources.require(not any('Invocation journal unavailable' in line for line in logs),
                          'Invocation journal failed during capture')
        with resources.server(exe, public_api, int(release), 'stdio', 'lite',
                              args.harness.resolve(), public_api, env_overrides=env) as (rpc, _, logs):
            lite = initialize(rpc)
            resources.require('CallTool' in {t['name'] for t in lite}, 'Lite bridge is not advertised')
            bridge = recorder(rpc, entries, 'lite')
            for name in sorted(registered):
                v4_rejection(bridge('CallTool', {'name': name, 'arguments': REJECT_ARGUMENTS}), name)
            snapshot['coverage'].update(bridgeRejectedTools=sorted(registered - {'CallTool'}),
                bridgeSelfGuardTools=['CallTool'], bridgeSkipped={},
                liteAdvertisedTools=sorted(t['name'] for t in lite))
        resources.require(not any('Invocation journal unavailable' in line for line in logs),
                          'Invocation journal failed during bridge capture')
        snapshot['calls'] = [compact(entries[key]) for key in sorted(entries)]
        return snapshot


@contextmanager
def foundation_v4_capture():
    # Foundation has no ToolProfiles rows. Enable the existing V4 correlation
    # masks only during this profile's capture; full-engine capture is unchanged.
    previous = V4_TOOLS.copy()
    V4_TOOLS.update(FOUNDATION_WORKER_TOOLS | set(FOUNDATION_MARKERS))
    try:
        yield
    finally:
        V4_TOOLS.clear()
        V4_TOOLS.update(previous)


def capture_foundation(args, release, exe):
    with foundation_v4_capture(), scratch_directory(args.temp_root) as scratch:
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
                marker = ('INVALID_ARGUMENT'
                          if name in FOUNDATION_WORKER_TOOLS else FOUNDATION_MARKERS.get(name))
                props = tool['inputSchema'].get('properties', {})
                if marker is None or any(key.lower() == 'snapshotreject' for key in props):
                    skipped[name] = 'No reviewed host argument rejection before the worker/builder; not invoked.'
                    continue
                v4_rejection(call(name, REJECT_ARGUMENTS), name)
                rejected.append(name)
            # Only these two passive tools call the pure registration inspector.
            # Foundation GetSessionState is NOT passive in the host: FoundationTool maps
            # it to worker.Call("ReadState"); WorkerClient.Call starts a worker
            # when process is null. Do not try a valid GetSessionState call here.
            passive = ['InitializeEnvironment', 'RunCapabilitySelfTest']
            for name in passive:
                reply = body(call(name, {}))['data']
                resources.require(reply['sideEffects']['workerInvoked'] is False
                                  and reply['sideEffects']['tiaLaunchedOrAttached'] is False
                                  and reply['checks']['passed'] is True,
                                  name + ': passive contract changed')
            names = sorted(t['name'] for t in tools)
            resources.require('CallTool' not in names, 'Foundation added a bridge; review its rejection path first')
            return {'formatVersion': 3, 'rawMaskRules': RAW_MASK_RULES,
                'release': release, 'profiles': ['plc-foundation'], 'transport': 'stdio',
                'coverage': {'registeredTools': len(tools), 'calledTools': sorted(set(rejected) | set(passive)),
                    'directRejectedTools': rejected, 'directSkipped': skipped, 'passiveTools': passive,
                    'passiveSkipped': {'GetSessionState': 'Requires worker ReadState; no worker is started by this capture.'},
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
        if snapshot.get('formatVersion') not in (2, 3):
            raise ValueError('Unsupported response snapshot format: ' + str(path))
        if snapshot['formatVersion'] == 3:
            for call in calls:
                blocks = call.get('rawTextBlocks')
                if not isinstance(blocks, list) or any(
                        set(block) != {'contentIndex', 'sha256'}
                        or type(block['contentIndex']) is not int or block['contentIndex'] < 0
                        or not re.fullmatch('[0-9a-f]{64}', block['sha256']) for block in blocks):
                    raise ValueError('Invalid/missing raw text hashes: ' + str(path))
                indices = [block['contentIndex'] for block in blocks]
                if indices != sorted(set(indices)):
                    raise ValueError('Duplicate/unordered raw text blocks: ' + str(path))
        snapshots[release] = snapshot
    if not snapshots:
        raise ValueError('No response snapshots in ' + str(directory))
    return snapshots


def response_length(call):
    return call['responseDigest']['length'] if 'responseDigest' in call else len(canonical(call['response']).encode('utf-8'))


def compare(args):
    if getattr(args, 'migration', None):
        return compare_migration(args)
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
                difference = first_difference({k: v for k, v in a[key].items() if k != 'rawTextBlocks'},
                                              {k: v for k, v in b[key].items() if k != 'rawTextBlocks'})
                if difference:
                    before, after = response_length(a[key]), response_length(b[key])
                    if ('responseDigest' in a[key] and 'responseDigest' in b[key]
                            and a[key]['responseDigest']['sha256'] != b[key]['responseDigest']['sha256']):
                        difference = '$/responseDigest/sha256'
                    changes.append(('changed', label + ' ' + difference
                                    + f'; response bytes {before} -> {after} (delta {after - before:+d})'))
                if not args.normalized_only:
                    raw_difference = first_difference(a[key].get('rawTextBlocks'), b[key].get('rawTextBlocks'),
                                                      '$/rawTextBlocks')
                    if raw_difference:
                        changes.append(('rawChanged', label + ' ' + raw_difference
                                        + '; raw text SHA-256 differs (or evidence missing)'))
        ignored = {'calls'} | ({'formatVersion', 'rawMaskRules'} if args.normalized_only else set())
        metadata = first_difference({k: v for k, v in old.items() if k not in ignored},
                                    {k: v for k, v in new.items() if k not in ignored})
        counts = Counter(kind for kind, _ in changes)
        counts['metadata'] = int(metadata is not None)
        total.update(counts)
        print(f'V{release}: changed={counts["changed"]} added={counts["added"]} removed={counts["removed"]} metadata={counts["metadata"]} rawChanged={counts["rawChanged"]}')
        for kind, detail in changes:
            print(f'  {kind}: {detail}')
        if metadata:
            print('  metadata: ' + metadata)
    print(f'TOTAL: changed={total["changed"]} added={total["added"]} removed={total["removed"]} metadata={total["metadata"]} rawChanged={total["rawChanged"]}')
    if args.normalized_only:
        print('Normalized comparison only; raw-byte evidence and format metadata were NOT compared.')
    # Additions also fail: migration acceptance requires exactly zero differences.
    return int(any(total.values()))


def compare_migration(args):
    """Phase-6 group proof: only the task's group (including bridge calls that wrap it) may change."""
    import phase6_groups
    baseline, current = load_snapshots(args.baseline), load_snapshots(args.current)
    failures = 0
    for release in args.releases or sorted(baseline.keys() & current.keys()):
        old, new = baseline[release], current[release]
        a, b = ({identity(call): call for call in snapshot['calls']} for snapshot in (old, new))
        members = phase6_groups.group(args.migration, release, {key[1] for key in a})
        allowed = members | {phase6_groups.mapped(name, members) for name in members}

        def catalog(key):
            # Usage, search and category responses render the generated examples (Generate-ToolUsage --check), the schemas
            # (contract proof) and the descriptions (source rename check); their changes are listed, not failed.
            # The duplicate-argument refusal probes stay strict.
            try:
                arguments = json.loads(key[2])
            except ValueError:
                return False
            if key[1] == 'GetToolUsage':
                return set(arguments) <= {'toolName', 'operation', 'limit', 'offset', 'exampleKind'}
            return (key[1] == 'FindTools' and set(arguments) <= {'query', 'limit'}) or (key[1] == 'ListToolCategories' and not arguments)

        def in_group(key):
            if key[1] in allowed or catalog(key):
                return True
            if key[1] in ('CallTool', 'PreviewToolCall', 'PreflightToolCall'):
                try:
                    inner = json.loads(key[2]).get('name')
                except (ValueError, AttributeError):
                    return False
                return inner in allowed
            return False
        other_a = {key: value for key, value in a.items() if not in_group(key)}
        other_b = {key: value for key, value in b.items() if not in_group(key)}
        subs = phase6_groups.rename_map(args.migration)

        def renamed_only(key):
            # A response outside the group may differ only by this group's renames in its text; raw hashes follow the text.
            old_call, new_call = other_a.get(key), other_b.get(key)
            if old_call is None or new_call is None or 'response' not in old_call or not subs:
                return False
            strip = lambda call: {k: v for k, v in call.items() if k != 'rawTextBlocks'}
            return phase6_groups.renamed(strip(old_call), subs) == strip(new_call)
        differing = [key for key in sorted(other_a.keys() | other_b.keys()) if other_a.get(key) != other_b.get(key)]
        guidance = [key for key in differing if renamed_only(key)]
        outside = [key for key in differing if key not in guidance]
        for key in ('release', 'formatVersion', 'profiles', 'transport', 'maxResponseChars'):
            assert old.get(key) == new.get(key), (release, key)
        assert new['rawMaskRules'] == RAW_MASK_RULES
        merged = len(members) - len({phase6_groups.mapped(name, members) for name in members})
        assert new['coverage']['registeredTools'] == old['coverage']['registeredTools'] - merged, (release, 'registered tool count')
        expected = {phase6_groups.mapped(name, members) for name in old['coverage']['directRejectedTools']}
        assert set(new['coverage']['directRejectedTools']) == expected, (release, 'direct refusal roster')
        if old['coverage'].get('bridgeRejectedTools'):
            assert set(new['coverage']['bridgeRejectedTools']) == expected - {'CallTool'}, (release, 'bridge refusal roster')
        changed = sum(a[key] != b[key] for key in a.keys() & b.keys())
        catalog_changed = sorted(key for key in a.keys() & b.keys() if catalog(key) and key[1] not in allowed and a[key] != b[key])
        print(f'V{release} {args.migration}: changed={changed} added={len(b.keys() - a.keys())} removed={len(a.keys() - b.keys())}; '
              f'catalog changed={len(catalog_changed)}; guidance renamed={len(guidance)}; '
              f'unchanged outside group={len(other_a) - len(differing)}; FAILED outside group={len(outside)}')
        for key in catalog_changed:
            print('  catalog: ' + key[1] + '(' + key[2] + ')')
        for key in guidance:
            print('  guidance renamed: ' + key[1] + '(' + key[2] + ')')
        for key in outside:
            print('  unexpected: ' + key[0] + ' ' + key[1] + '(' + key[2] + ') ' + str(first_difference(other_a.get(key), other_b.get(key))))
        failures += len(outside)
    return int(failures != 0)


class RawResponseTests(unittest.TestCase):
    def test_classic_hmi_structured_timestamps(self):
        first, second = '2026-10-03T11:12:13Z', '2026-10-04T11:12:13Z'
        for tool, data in (
                ('BuildClassicHmiScreen', {'timestamp': first}),
                ('BuildClassicHmiTagTable', {'timestamp': first}),
                ('BuildClassicHmiMinimalPackage', {'screen': {'timestamp': first}, 'tagTable': {'timestamp': first}})):
            with self.subTest(tool=tool):
                call = {'tool': tool, 'response': {'result': {'structuredContent': {'data': data}}}}
                changed = json.loads(json.dumps(call).replace(first, second))
                self.assertEqual(normalize(call), normalize(changed))
                # The same field in another tool or a source string stays visible.
                call['tool'] = changed['tool'] = 'GetToolUsage'
                self.assertNotEqual(normalize(call), normalize(changed))
        source = {'tool': 'BuildClassicHmiScreen', 'response': {'result': {
            'structuredContent': {'data': {'xml': '<Time>' + first + '</Time>'}}}}}
        self.assertEqual(normalize(source), source)

    def test_v4_correlation_masks_only_actual_envelopes(self):
        first, second = 'a' * 32, 'b' * 32
        sample = '{"schemaVersion":4,"meta":{"timestamp":"2026-10-03T00:00:00Z","requestId":"' + first + '"},"data":{"example":{"requestId":"' + first + '"}}}'
        masked = mask_raw_text(sample, 'CallTool')
        self.assertIn('"requestId":"<string:requestId>"', masked)
        self.assertIn('"example":{"requestId":"' + first + '"}', masked)
        self.assertEqual(self.call(sample, 'CallTool'), self.call(sample.replace('"meta":{"timestamp":"2026-10-03T00:00:00Z","requestId":"' + first, '"meta":{"timestamp":"2026-10-03T00:00:00Z","requestId":"' + second), 'CallTool'))
        self.assertIn('"requestId":"' + first + '"', mask_raw_text(sample, 'GetState'))

    def reply(self, text):
        return {'id': 1, 'jsonrpc': '2.0', 'result': {'content': [{'type': 'text', 'text': text}]}}

    def call(self, text, tool='GetState', profile='full'):
        entries = {}
        recorder(lambda *a, **kw: self.reply(text), entries, profile)(tool, {})
        return compact(next(iter(entries.values())))

    def test_order_escaping_and_numbers(self):
        for left, right in [(' {"a":1,"b":2}', ' {"b":2,"a":1}'),
                            ('{"text":"中文"}', r'{"text":"\u4e2d\u6587"}'),
                            ('{"n":1.0}', '{"n":1.00}'),
                            ('{"n":1e2}', '{"n":100.0}'),
                            ('{"a":1}', '{ "a" : 1 }')]:
            with self.subTest(left=left):
                a, b = self.call(left), self.call(right)
                self.assertEqual(a['response'], b['response'])
                self.assertNotEqual(a['rawTextBlocks'], b['rawTextBlocks'])

    def test_every_reviewed_path(self):
        for rule in RAW_MASK_RULES:
            if rule.get('kind') == 'requestId': continue
            with self.subTest(rule=rule):
                value = '"2026-10-03T11:12:13.1234567-07:00"'
                for key in reversed(rule['path']):
                    value = '{ "' + key + '" : ' + value + ' }'
                tool = rule['tool'] if rule['tool'] != '*' else 'GetState'
                self.assertEqual(mask_raw_text(value, tool),
                                 value.replace('2026-10-03T11:12:13.1234567-07:00', '<string:timestamp>'))
                other = value.replace('2026-10-03T11:12:13.1234567-07:00', '2027-01-02T00:00:00Z')
                self.assertEqual(self.call(value, tool), self.call(other, tool))

    def test_no_unreviewed_masks(self):
        stamp = '2026-10-03T11:12:13Z'
        samples = [
            '{"data":{"timestamp":"' + stamp + '"}}',
            '{"meta":{"nested":{"timestamp":"' + stamp + '"}}}',
            '[{"meta":{"timestamp":"' + stamp + '"}}]',
            r'{"meta":{"time\u0073tamp":"' + stamp + '"}}',
            '{"Meta":{"timestamp":"' + stamp + '"}}',
            '{"meta":{"timestamp":"not-a-date","elapsedMs":123,"pid":456,"temp":"C:/tmp"}}',
            '{"meta":{"timestamp":"2026-10-03T11:12:13"}}',
            '{"meta":{"timestamp":123,"operationId":"123e4567-e89b-12d3-a456-426614174000"}}',
            r'{"meta":{"timestamp":"2026-10-03T11:12:13\u005a"}}',
            '{"message":"' + stamp + '"}',
        ]
        for value in samples:
            with self.subTest(value=value):
                self.assertEqual(mask_raw_text(value, 'GetState'), value)
        # Builder masks cannot reach tool usage examples or source XML strings.
        self.assertEqual(mask_raw_text(samples[0], 'GetToolUsage'), samples[0])

    def test_surrounding_bytes(self):
        value = r'{ "meta" : {"timestamp" : "2026-10-03T11:12:13Z", "n":1.00}, "text":"中文\n\"\\", "array":[{},[null,2]] }'
        masked = mask_raw_text(value, 'GetState')
        self.assertEqual(masked, value.replace('2026-10-03T11:12:13Z', '<string:timestamp>'))
        self.assertEqual(self.call(value)['rawTextBlocks'][0]['sha256'],
                         hashlib.sha256(masked.encode('utf-8')).hexdigest())

    def test_plain_text_and_invalid_json(self):
        for text in ('argument refused 中文', '', '{"meta":{"timestamp":"2026-10-03T11:12:13Z"}',
                     '{\u00a0"meta":{"timestamp":"2026-10-03T11:12:13Z"}}',
                     '{"meta":{"timestamp":"2026-10-03T11:12:13Z"}} extra'):
            with self.subTest(text=text):
                self.assertEqual(mask_raw_text(text, 'GetState'), text)
                self.assertEqual(self.call(text)['rawTextBlocks'][0]['sha256'],
                                 hashlib.sha256(text.encode('utf-8')).hexdigest())

    def test_multiple_blocks_and_protocol_error(self):
        reply = self.reply('first')
        reply['result']['content'] += [{'type': 'image', 'data': 'AA=='}, {'type': 'text', 'text': 'second'}]
        hashes = raw_text_blocks(reply, 'GetState')
        self.assertEqual([b['contentIndex'] for b in hashes], [0, 2])
        self.assertEqual(hashes[1]['sha256'], hashlib.sha256(b'second').hexdigest())
        self.assertEqual(raw_text_blocks({'error': {'message': 'refused'}}, 'GetState'), [])

    def test_digest_keeps_raw_hashes(self):
        a = self.call('{"b":2,"a":1}', 'GetToolUsage')
        b = self.call('{"a":1,"b":2}', 'GetToolUsage')
        self.assertEqual(a['responseDigest'], b['responseDigest'])
        self.assertNotEqual(a['rawTextBlocks'], b['rawTextBlocks'])

    def test_compare_and_format_migration(self):
        with scratch_directory(Path(tempfile.gettempdir())) as scratch:
            old, new = Path(scratch) / 'old', Path(scratch) / 'new'
            old.mkdir(); new.mkdir()
            a = {'formatVersion': 3, 'rawMaskRules': RAW_MASK_RULES, 'release': '20',
                 'calls': [self.call('{"a":1,"b":2}')]}
            b = dict(a, calls=[self.call('{"b":2,"a":1}')])
            def write(directory, value):
                (directory / '20.json').write_text(snapshot_text(value), encoding='utf-8')
            write(old, a); write(new, b)
            args = argparse.Namespace(baseline=old, current=new, releases=None, normalized_only=False)
            with redirect_stdout(io.StringIO()) as output:
                self.assertEqual(compare(args), 1)
            self.assertIn('rawChanged=1', output.getvalue())
            self.assertIn('full GetState({}) $/rawTextBlocks/0/sha256', output.getvalue())
            write(new, a)
            with redirect_stdout(io.StringIO()):
                self.assertEqual(compare(args), 0)
            a['formatVersion'] = 2
            a.pop('rawMaskRules'); a['calls'][0].pop('rawTextBlocks')
            write(old, a)
            with redirect_stdout(io.StringIO()):
                self.assertEqual(compare(args), 1)
                args.normalized_only = True
                self.assertEqual(compare(args), 0)
            b['calls'][0].pop('rawTextBlocks')
            write(new, b)
            with self.assertRaisesRegex(ValueError, 'raw text hashes'):
                load_snapshots(new)


def self_test():
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(RawResponseTests))
    print(f'SELF-TEST: passed={result.testsRun - len(result.failures) - len(result.errors)} '
          f'failed={len(result.failures) + len(result.errors)}')
    return int(not result.wasSuccessful())


def main():
    if sys.argv[1:] == ['--self-test']:
        return self_test()
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    capture_parser = commands.add_parser('capture')
    capture_parser.add_argument('--repo-root', required=True, type=Path)
    capture_parser.add_argument('--harness', required=True, type=Path)
    capture_parser.add_argument('--public-api-root', type=Path)
    capture_parser.add_argument('--dotnet-root', type=Path,
                                help='Private .NET/ASP.NET Core 10 root; sets DOTNET_ROOT and DOTNET_ROOT_X64 for Foundation only')
    capture_parser.add_argument('--exe', action='append', default=[], metavar='RELEASE=PATH')
    capture_parser.add_argument('--releases', nargs='+', choices=RELEASES, default=RELEASES)
    capture_parser.add_argument('--output', required=True, type=Path)
    capture_parser.add_argument('--temp-root', type=Path, default=Path(tempfile.gettempdir()),
                                help='Parent for disposable host journals (use a writable worktree directory in a sandbox)')
    capture_parser.set_defaults(run=capture)
    compare_parser = commands.add_parser('compare')
    compare_parser.add_argument('--baseline', required=True, type=Path)
    compare_parser.add_argument('--current', required=True, type=Path)
    compare_parser.add_argument('--migration', choices=list(__import__('phase6_groups').TASKS), help='Verify one phase-6 group migration; every call outside the group (and its bridge calls) must match')
    compare_parser.add_argument('--normalized-only', action='store_true',
                                help='Migration check against format 2 only; does not prove raw-byte compatibility')
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
