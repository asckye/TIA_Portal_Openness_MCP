"""Capture and compare offline responses and pre-invocation dispatch refusals.

The executable/harness/PublicAPI options match Snapshot-ToolContracts.py. The
optional --engine-host mode launches the explicitly marked SDK fixture worker;
--transport http selects a local HTTP fixture. No TIA attachment is performed. check_usage owns
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
JSON walk locates literal paths; regex substitutes only reviewed timestamp or
request-ID contents, preserving quotes, whitespace, key order, escapes and all surrounding text. No
elapsed-time, PID, arbitrary GUID or temp-path mask is used by the capture set.
Unknown paths/encodings remain visible and must fail the consecutive-capture gate.
"""
import argparse
import copy
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

from offline_fixtures import fixture_directory
from tool_usage_checks import check_usage, unwrap_usage
from ported_families import additions as ported_additions


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
SESSION_APPROVAL_TOOLS = {'SaveProject', 'SaveProjectCopy', 'CloseProject'}
RESPONSE_LIMIT = 16 * 1024
RESPONSE_FORMAT_VERSION = 3
FULL_RESPONSE_FIELDS = {'formatVersion', 'rawMaskRules', 'release', 'profiles', 'transport',
                        'maxResponseChars', 'coverage', 'calls'}
LEGACY_FULL_RESPONSE_FIELDS = FULL_RESPONSE_FIELDS.copy()
FULL_RESPONSE_FIELDS.add('captureMode')
FOUNDATION_RESPONSE_FIELDS = {'formatVersion', 'rawMaskRules', 'release', 'profiles', 'transport',
                              'coverage', 'calls'}
FULL_COVERAGE_FIELDS = {'registeredTools', 'calledTools', 'behaviorCallTools', 'directRejectedTools',
                        'directSkipped', 'calledOperations', 'usageTools', 'usageOperations',
                        'offlineExamples', 'l1Domains', 'bridgeRejectedTools', 'bridgeSelfGuardTools',
                        'bridgeSkipped', 'liteAdvertisedTools'}
FOUNDATION_COVERAGE_FIELDS = {'registeredTools', 'calledTools', 'directRejectedTools', 'directSkipped',
                              'passiveTools', 'passiveSkipped', 'bridgeRejectedTools', 'bridgeSkipped'}

# Safety proof for every full-engine tool, including no-argument tools:
# ModelContextProtocol/Tools/McpServer.ArgDiagnostics.cs::WrapTools wraps both
# profiles in VersionPolicyTool (non-isolated server; isolation is never enabled).
# McpServer.VersionPolicy.cs::InvokeAsync delegates to V4Admission/BindV4Call,
# rejecting duplicate names BEFORE version/schema checks and inner.InvokeAsync.
# McpServer.Profile.cs::GetAllTools/GetLiteTools supply the advertised rosters.
# McpServer.ToolBridge.cs::CallTool(string,ToolArguments) binds through IToolInvoker
# and McpServer.InProcessTools.cs::BindV4Call, which
# rejects case-insensitive duplicate properties BEFORE schema validation, binding
# and InvokeToolMethod. It returns a V4 INVALID_ARGUMENT/not-started envelope.
# The two distinct JSON keys below trigger the same duplicate-name refusal on
# both paths. Do not replace this pair with a single unknown key or valid args.
# CallTool -> CallTool has an earlier self-recursion guard; record that exact
# refusal separately, never count it as duplicate-argument admission coverage.
REJECT_ARGUMENTS = {'SnapshotReject': True, 'snapshotReject': True}
SELF_MARKER = "CallTool cannot invoke itself. Pass the target tool's own name."


def v4_tools(release=None, engine_source=False):
    """Current and archived source names with V4 envelopes; preserve the reviewed masks for both capture paths.
    With a release: the product roster, or the retired engine's own source roster for the A/B capture."""
    import xml.etree.ElementTree as ET
    resource = Path(__file__).resolve().parents[2] / 'src/Logic/ModelContextProtocol/ToolProfiles.resx'
    data = json.loads(ET.parse(resource).find(".//data[@name='Catalog']/value").text)
    if release is None:
        releases = [*data['releases'].items(), *data.get('engineSourceReleases', {}).items()]
    else:
        releases = list((data.get('engineSourceReleases', {}) if engine_source else data['releases']).items())
    return {row['currentName'] for key, rows in releases if release in (None, key)
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
FOUNDATION_MARKERS.update({name: 'INVALID_ARGUMENT' for name in ('StageImportFiles', 'ListStagedImportFiles', 'CleanupStagedImportFiles')})

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
    ('CheckProductUpdate', {'repository': 'x'},
     'Invalid owner/name returns before HTTP; bin layout has no installRoot.'),
)

# Every current [L1][Domain] group from ToolTaxonomy's description prefixes.
# All native-facing representatives stop at a null project before SDK access.
# Portal/Exports have no connection prerequisite: disconnected Disconnect and
# missing export content are their passive/negative representatives instead.
DOMAIN_CALLS = {
    'Diagnostics': 'ValidateAutomationContext',
    'Exports': 'GetExport',
    'HMI': 'CompileHmiDiagnostics',
    'Hardware': 'GetProjectTopology',
    'PLC-Online': 'GetOnlineState',
    'PLC-Software': 'GetSoftwareTree',
    'Portal': 'DisconnectPortal',
    'Project': 'GetProjectTree',
    'VersionControl': 'ListVersionControlWorkspaces',
}

# ToolVersionPolicy.V21Only. Both direct admission and bridge admission are
# exercised on V20; never execute these target-bound examples on V21.
V21_ONLY = (
    'ListCommunicationConnections', 'ManageCommunicationConnection',
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
    *[{'tool': tool, 'path': ['error', 'details', 'requestId'],
       'reason': 'Workbench approval request correlation ID on save/close refusal.'}
      for tool in ('SaveProject', 'SaveProjectCopy', 'CloseProject')],
    {'tool': 'CallTool', 'path': ['error', 'details', 'requestId'],
     'reason': 'Workbench approval request correlation ID on a lite save/close target refusal.'},
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
                   or (tool in SESSION_APPROVAL_TOOLS or tool == 'CallTool')
                   and path == ('"error"', '"details"', '"requestId"')
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
        approval_target = (call['tool'] in SESSION_APPROVAL_TOOLS
                           or call['tool'] == 'CallTool'
                           and call['arguments'].get('name') in SESSION_APPROVAL_TOOLS)
        if approval_target:
            details = (envelope.get('error') or {}).get('details')
            if isinstance(details, dict) and re.fullmatch('[0-9a-f]{32}', str(details.get('requestId', ''))):
                details['requestId'] = '<string:requestId>'
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


def compact(call, include_text_evidence=False):
    call = normalize(call)
    response = call['response']
    raw_evidence = call.pop('rawTextEvidence', None)
    if include_text_evidence:
        call['textEvidence'] = {'response': response, 'rawText': raw_evidence}
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


def session_readiness_refusal(reply, name):
    resources.require(reply.get('schemaVersion') == 4 and reply['ok'] is False
                      and reply['error']['code'] == 'RESOURCE_UNAVAILABLE'
                      and reply['error']['details']['resource'] == 'tia-openness-environment'
                      and reply['meta']['outcome'] == 'rejected-before-operation'
                      and reply['meta']['execution'] == 'not-started'
                      and reply['meta']['requiresSessionReset'] is False,
                      name + ': expected the no-TIA readiness refusal before approval: ' + canonical(reply))


def session_approval_refusal(reply, name):
    resources.require(reply.get('schemaVersion') == 4 and reply.get('ok') is False
                      and reply.get('error', {}).get('code') == 'CONFIRMATION_REQUIRED'
                      and reply.get('meta', {}).get('outcome') == 'rejected-before-operation'
                      and reply.get('meta', {}).get('execution') == 'not-started'
                      and reply.get('meta', {}).get('requiresSessionReset') is False,
                      name + ': expected product-default approval refusal before operation: ' + canonical(reply))


def session_write_refusal(reply, name, packaged_no_tia):
    if packaged_no_tia:
        session_readiness_refusal(reply, name)
    else:
        session_approval_refusal(reply, name)


def foundation_apply_refusal(reply, name):
    resources.require(reply.get('schemaVersion') == 4 and reply.get('ok') is False
        and reply['error']['code'] == 'INVALID_ARGUMENT'
        and reply['error']['details']['parameter'] == 'confirm'
        and reply['meta']['execution'] == 'not-started'
        and reply['meta']['requiresSessionReset'] is False
        and any(w['code'] == 'APPROVAL_PRECHECK_REFUSED' for w in reply['meta']['warnings']),
        name + ': expected Foundation confirmation precheck: ' + canonical(reply))


def capture_readiness_overrides(harness, packaged_no_tia):
    if harness is not None and not packaged_no_tia:
        return {'TIA_MCP_TEST_READINESS_READY': '1'}
    return {}


def sdk_only_harness(args, release):
    if getattr(args, 'engine_host', None):
        return None
    if args.packaged_no_tia or release not in FULL_RELEASES:
        return args.harness.resolve() if args.harness else None
    if args.harness:
        return args.harness.resolve()
    harness = args.repo_root / 'tests/Engine/TiaMcp.Engine.Harness/bin/Release/net48/TiaMcp.Engine.Harness.exe'
    resources.require(harness.is_file(),
                      'Build TiaMcp.Engine.Harness or pass --harness for V20/V21 sdk-only capture; the real EXE requires TIA readiness')
    return harness.resolve()


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


def behavior_entries(release):
    import xml.etree.ElementTree as ET
    resource = Path(__file__).resolve().parents[2] / 'src/Logic/ModelContextProtocol/ToolProfiles.resx'
    catalog = json.loads(ET.parse(resource).find(".//data[@name='Catalog']/value").text)
    current = {r['family'] for r in catalog['behaviorPolicies'] if r['releaseKey'] == release and r['state'] == 'current'}
    return {r['entry'] for r in catalog['behaviorEntries'] if r['releaseKey'] == release and r['family'] in current}


def require_behavior_disclosure(response, label):
    meta = body(response)['meta']
    resources.require(meta['behaviorPolicy'] == 'current' and any(w['code'] == 'UNVERIFIED_BEHAVIOR' for w in meta['warnings']),
                      label + ': missing current D1 behavior disclosure')


def recorder(rpc, entries, profile, release):
    inventory = behavior_entries(release)
    def call(name, arguments):
        key = profile, name, canonical(arguments)
        if key not in entries:
            reply = rpc('tools/call', params={'name': name, 'arguments': arguments})
            raw_blocks = raw_text_blocks(reply, name)
            raw_evidence = [dict(contentIndex=index, text=mask_raw_text(block['text'], name))
                            for index, block in enumerate(reply.get('result', {}).get('content', []))
                            if block.get('type') == 'text']
            response = decode_reply(reply)
            target = arguments.get('name') if name == 'CallTool' else name
            if target in inventory:
                require_behavior_disclosure(response, target)
            entries[key] = {'profile': profile, 'tool': name, 'arguments': arguments,
                            'response': response, 'rawTextBlocks': raw_blocks, 'rawTextEvidence': raw_evidence}
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


def stage_packaged_engine_without_siemens(exe, release, scratch):
    runtime = scratch / 'runtime' / ('v' + release)

    def omit_siemens(_directory, names):
        return {name for name in names
                if name.lower().startswith('siemens.engineering') and name.lower().endswith('.dll')}

    shutil.copytree(exe.parent, runtime, ignore=omit_siemens)
    remaining = [path for path in runtime.rglob('*.dll')
                 if path.name.lower().startswith('siemens.engineering')]
    resources.require(not remaining,
                      'Packaged snapshot layout retained Siemens.Engineering DLLs: ' + str(remaining[:3]))
    staged = runtime / exe.name
    resources.require(staged.is_file(), 'Packaged snapshot engine was not staged: ' + str(staged))
    return staged


def capture_release(args, release, exe, public_api):
    harness = sdk_only_harness(args, release)
    with scratch_directory(args.temp_root) as scratch:
        data_directory = scratch / 'data'
        data_directory.mkdir()
        resources.require(not (data_directory / 'config' / 'approval.settings').exists(),
                          'Capture data directory must use product-default approval settings')
        env = {'TEMP': str(scratch), 'TMP': str(scratch), 'TIA_MCP_DATA_DIRECTORY': str(data_directory),
               'TIA_MCP_MAX_RESPONSE_CHARS': '2000000',
               'TIA_MCP_DIAGNOSTICS_DIRECTORY': str(scratch / 'diagnostics')}
        capture_mode = 'packaged-no-tia' if args.packaged_no_tia else 'sdk-only-fixture'
        if args.packaged_no_tia:
            resources.require(args.harness is None, 'Packaged no-TIA capture must launch the real EXE')
            capture_exe = stage_packaged_engine_without_siemens(exe, release, scratch)
            portal_root = None
            env['TiaPortalLocation'] = ''
            env['TIA_MCP_BUNDLE_ROOT'] = str(scratch)
        else:
            capture_exe = exe
            if release in FULL_RELEASES:
                env['TIA_MCP_BUNDLE_ROOT'] = str(args.repo_root.resolve())
                env.update(capture_readiness_overrides(harness, args.packaged_no_tia))
            portal_root = (resources.sdk_only_installation(public_api, int(release), scratch)
                           if release in FULL_RELEASES else public_api)
        capture_args = copy.copy(args)
        if args.packaged_no_tia and args.engine_host:
            capture_args.engine_host = capture_exe
            capture_args.repo_root = scratch
        server = (contracts.engine_host_server(capture_args, release, portal_root, 'full', env) if args.engine_host
                  else resources.server(capture_exe, portal_root, int(release), 'stdio', 'full',
                              harness, public_api, env_overrides=env
                              ))
        with server as (rpc, _, logs):
            tools = initialize(rpc)
            entries = {}
            call = recorder(rpc, entries, 'full', release)

            def decoded(name, arguments):
                return body(call(name, arguments))

            if args.packaged_no_tia:
                bootstrap = decoded('InitializeEnvironment', {})
                resources.require(bootstrap['data']['ready'] is False
                                  and 'no TIA Portal V' in bootstrap['data']['recommendedReason'],
                                  'Packaged no-TIA bootstrap omitted its readiness cause')
                doctor = decoded('GetEnvironmentDiagnostics', {'fix': False})
                resources.require(doctor['data']['ready'] is False
                                  and 'no TIA Portal V' in canonical(doctor['data']),
                                  'Packaged no-TIA diagnostics omitted their readiness cause')
                state = decoded('GetSessionState', {})
                session_readiness_refusal(state, 'GetSessionState')
                resources.require('no TIA Portal V' in state['data']['environment']['cause'],
                                  'GetSessionState readiness refusal omitted the no-TIA cause')
            else:
                state = decoded('GetSessionState', {})
                resources.require(state['data'].get('isAttached', state['data'].get('isConnected')) is False, 'Capture requires a disconnected host')
                resources.require(args.engine_host or state['data']['evidence']['journalHealth']['failedWrites'] == 0,
                                  'Journal is not writable; use --temp-root inside the writable worktree')
            # Packaged no-TIA runs the product readiness gate. The SDK fixture
            # uses TiaMcp.Engine.Harness-only readiness and must reach the product-default
            # approval gate without dispatching the native write.
            for name, arguments in ((('SaveProject', {'dryRun': False, 'confirm': False, 'expectedProjectFile': 'C:/P6-49-response-snapshot.ap21'}), ('CloseProject', {'dryRun': False, 'confirm': False, 'expectedProjectFile': 'C:/P6-49-response-snapshot.ap21'})) if args.engine_host else (
                    ('SaveProject', {}),
                    ('SaveProjectCopy', {'newProjectPath': 'C:/P6-49-response-snapshot.ap21'}),
                    ('CloseProject', {}))):
                result = decoded(name, arguments)
                if args.engine_host:
                    foundation_apply_refusal(result, name)
                else: session_write_refusal(result, name, args.packaged_no_tia)
            decoded('GetPortalInfo', {'includeProcesses': False, 'includeSessions': False,
                                       'includeProducts': False})
            decoded('ListToolCategories', {})
            decoded('FindTools', {'query': 'ManageMotionAxis', 'limit': 1})
            decoded('FindTools', {'query': 'no-such-snapshot-tool', 'limit': 3})
            decoded('PreviewToolCall', {'name': 'ListDevices', 'arguments': {}})
            decoded('PreviewToolCall', {'name': 'ListPlcBlocks', 'arguments': {}})

            for name, arguments, reason in PASSIVE_RESOURCE_CALLS:
                result = decoded(name, arguments)
                meta = result['meta']
                if name == 'CheckProductUpdate':
                    if args.packaged_no_tia:
                        session_readiness_refusal(result, name)
                    else:
                        meta = result['data']['evidence']
                        resources.require(meta['installRoot'] is None and meta['updaterScript'] is None
                                          and meta['success'] is False and 'releaseApiUrl' not in meta
                                          and result['data']['summary'].startswith("repository must be 'owner/name'"),
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
            resources.require(set(domains) <= set(DOMAIN_CALLS) if args.engine_host else set(domains) == set(DOMAIN_CALLS),
                              'L1 taxonomy changed; review offline domain representatives')
            domain_calls = dict(DOMAIN_CALLS)
            if 'GetExportContent' in domains['Exports']:
                domain_calls['Exports'] = 'GetExportContent'
            for domain, name in sorted(domain_calls.items()):
                if args.engine_host and (domain not in domains or name not in domains[domain]):
                    # Shared Foundation descriptions use the V19 taxonomy. Their
                    # explicit safe calls above and admission sweep cover them.
                    continue
                resources.require(name in domains[domain], name + ' moved out of its L1 domain')
                call(name, example(name))

            # Unknown argument, wrong JSON type, and missing required argument.
            # Safe targets ensure even a diagnostic regression cannot attach TIA.
            call('GetSessionState', {'unknownParameter': True})
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
            resources.require(registered == v4_tools(release, args.engine_source), 'Every full-engine entry must have a generated V4 contract')
            for name in sorted(registered):
                reply = call(name, REJECT_ARGUMENTS)
                # Invalid-argument probes keep their ordinary V4 rejection;
                # valid save/close calls are asserted separately at the gate.
                v4_rejection(reply, name)
            snapshot = {'formatVersion': 3, 'rawMaskRules': RAW_MASK_RULES,
                'release': release, 'profiles': ['full', 'lite'], 'transport': 'stdio',
                'maxResponseChars': 2000000, 'captureMode': capture_mode,
                'coverage': {'registeredTools': len(tools), 'calledTools': sorted(registered),
                    'behaviorCallTools': behavior_calls, 'directRejectedTools': sorted(registered),
                    'directSkipped': {},
                    'calledOperations': [list(pair) for pair in selected_operations],
                    'usageTools': usage['checkedToolCount'], 'usageOperations': usage['operationExampleCount'],
                    'offlineExamples': sorted(usage['offlineCallExamplesExecuted'] + list(PURE_EXAMPLES)),
                    'l1Domains': domain_calls}}
        resources.require(not any('Invocation journal unavailable' in line for line in logs),
                          'Invocation journal failed during capture')
        server = (contracts.engine_host_server(args, release, portal_root, 'lite', env) if args.engine_host
                  else resources.server(capture_exe, portal_root, int(release), 'stdio', 'lite',
                              args.harness.resolve() if args.harness else None, public_api, env_overrides=env))
        with server as (rpc, _, logs):
            lite = initialize(rpc)
            resources.require('CallTool' in {t['name'] for t in lite}, 'Lite bridge is not advertised')
            bridge = recorder(rpc, entries, 'lite', release)
            for name in sorted(registered):
                v4_rejection(bridge('CallTool', {'name': name, 'arguments': REJECT_ARGUMENTS}), name)
            for name, arguments in ((('SaveProject', {'dryRun': False, 'confirm': False, 'expectedProjectFile': 'C:/P6-49-response-snapshot.ap21'}), ('CloseProject', {'dryRun': False, 'confirm': False, 'expectedProjectFile': 'C:/P6-49-response-snapshot.ap21'})) if args.engine_host else (
                    ('SaveProject', {}),
                    ('SaveProjectCopy', {'newProjectPath': 'C:/P6-49-response-snapshot.ap21'}),
                    ('CloseProject', {}))):
                if args.engine_host:
                    foundation_apply_refusal(body(bridge('CallTool', {'name': name, 'arguments': arguments})), 'CallTool -> ' + name)
                else: session_write_refusal(body(bridge('CallTool', {'name': name, 'arguments': arguments})), 'CallTool -> ' + name, args.packaged_no_tia)
            snapshot['coverage'].update(bridgeRejectedTools=sorted(registered - {'CallTool'}),
                bridgeSelfGuardTools=['CallTool'], bridgeSkipped={},
                liteAdvertisedTools=sorted(t['name'] for t in lite))
        resources.require(not any('Invocation journal unavailable' in line for line in logs),
                          'Invocation journal failed during bridge capture')
        snapshot['calls'] = [compact(entries[key], args.include_text_evidence) for key in sorted(entries)]
        return snapshot


@contextmanager
def foundation_v4_capture(release):
    # Foundation has no ToolProfiles rows. Enable the existing V4 correlation
    # masks only during this profile's capture; full-engine capture is unchanged.
    previous = V4_TOOLS.copy()
    V4_TOOLS.update(FOUNDATION_WORKER_TOOLS | set(FOUNDATION_MARKERS)
                    | ported_additions(Path(__file__).resolve().parents[2], release))
    try:
        yield
    finally:
        V4_TOOLS.clear()
        V4_TOOLS.update(previous)


def capture_foundation(args, release, exe):
    worker_tools = FOUNDATION_WORKER_TOOLS | ported_additions(args.repo_root, release)
    with foundation_v4_capture(release), scratch_directory(args.temp_root) as scratch:
        # Shared server launcher accepts release keys verbatim. LegacyHost
        # HostOptions.Parse accepts --tia-major-version/--tia-portal-location;
        # no harness, --catalog, worker executable or native-session flag is used.
        data_directory = scratch / 'data'
        data_directory.mkdir()
        resources.require(not (data_directory / 'config' / 'approval.settings').exists(),
                          'Capture data directory must use product-default approval settings')
        env = {'TEMP': str(scratch), 'TMP': str(scratch), 'TIA_MCP_DATA_DIRECTORY': str(data_directory)}
        if args.dotnet_root:
            env.update(DOTNET_ROOT=str(args.dotnet_root.resolve()),
                       DOTNET_ROOT_X64=str(args.dotnet_root.resolve()))
        with resources.server(exe, scratch, release, 'stdio', 'full', env_overrides=env) as (rpc, _, _):
            tools = initialize(rpc)
            entries, rejected, skipped = {}, [], {}
            call = recorder(rpc, entries, 'plc-foundation', release)
            for tool in sorted(tools, key=lambda t: t['name']):
                name = tool['name']
                marker = ('INVALID_ARGUMENT'
                          if name in worker_tools else FOUNDATION_MARKERS.get(name))
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
                'calls': [compact(entries[key], args.include_text_evidence) for key in sorted(entries)]}


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
    requested_host = args.engine_host
    for release in args.releases:
        exe = executables.get(release, root / 'runtime' / ('v' + release) / (f'worker/TiaMcp.Engine.V{release}.exe' if args.engine_source and release in ('20', '21') else 'TiaMcp.FoundationHost.exe'))
        if release in FULL_RELEASES:
            args.engine_host = requested_host or (None if args.engine_source else exe)
            api = None if args.packaged_no_tia else api_root / ('TIA_V' + release + '_PublicAPI') / ('V' + release)
            if api is not None and release == '21':
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
        return compare_text_migration(args) if args.migration == 'P6-26' else compare_migration(args)
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


def raw_string_spans(text):
    tokens = list(RAW_TOKEN.finditer(text))
    spans, index = {}, 0

    def take(expected=None):
        nonlocal index
        token = tokens[index]
        index += 1
        if expected is not None and token.group() != expected:
            raise ValueError('Invalid raw text evidence')
        return token

    def value(path):
        token = take()
        raw = token.group()
        if raw == '{':
            if tokens[index].group() != '}':
                while True:
                    key = json.loads(take().group())
                    take(':')
                    value(path + (key,))
                    if tokens[index].group() != ',': break
                    take(',')
            take('}')
        elif raw == '[':
            item = 0
            if tokens[index].group() != ']':
                while True:
                    value(path + (item,))
                    item += 1
                    if tokens[index].group() != ',': break
                    take(',')
            take(']')
        elif raw.startswith('"'):
            spans[path] = (token.start(), token.end(), raw)
    value(())
    if index != len(tokens): raise ValueError('Trailing raw text evidence')
    return spans


def validated_evidence(call):
    evidence = call.get('textEvidence')
    if not isinstance(evidence, dict): raise ValueError('P6-26 requires --include-text-evidence captures')
    rebuilt = compact(dict(profile=call['profile'], tool=call['tool'], arguments=call['arguments'],
                           response=evidence['response'], rawTextBlocks=call['rawTextBlocks']))
    if rebuilt != {k: v for k, v in call.items() if k != 'textEvidence'}:
        raise ValueError('Response evidence does not match the frozen digest/content')
    hashes = [dict(contentIndex=b['contentIndex'], sha256=hashlib.sha256(b['text'].encode('utf-8')).hexdigest())
              for b in evidence['rawText']]
    if hashes != call['rawTextBlocks']: raise ValueError('Raw text evidence hash mismatch')
    return evidence


def compare_text_migration(args):
    from snapshot_text_migration import differences, response_text
    if args.normalized_only: raise ValueError('P6-26 requires raw-byte evidence')
    if args.text_baseline is None: raise ValueError('P6-26 requires --text-baseline captured before editing')
    frozen, before, current = (load_snapshots(p) for p in (args.baseline, args.text_baseline, args.current))
    failures = 0
    for release in args.releases or sorted(frozen.keys() | current.keys()):
        old, original, new = frozen[release], before[release], current[release]
        a, anchor, b = ({identity(c): c for c in s['calls']} for s in (old, original, new))
        problems, counts = [], Counter()
        if a.keys() != anchor.keys() or a.keys() != b.keys(): problems.append('call roster changed')
        for key in a.keys() & anchor.keys() & b.keys():
            label = key[0] + ' ' + key[1] + '(' + key[2] + ')'
            if a[key] != {k: v for k, v in anchor[key].items() if k != 'textEvidence'}:
                problems.append(label + ': before evidence differs from frozen baseline')
                continue
            x, y = validated_evidence(anchor[key]), validated_evidence(b[key])
            allowed = lambda path: response_text(key[1], path)
            c, p = differences(x['response'], y['response'], allowed)
            counts.update(c)
            problems.extend(label + ': ' + problem for problem in p)
            if len(x['rawText']) != len(y['rawText']):
                problems.append(label + ': raw block count changed')
                continue
            for raw_old, raw_new in zip(x['rawText'], y['rawText']):
                if raw_old['contentIndex'] != raw_new['contentIndex']:
                    problems.append(label + ': raw content index changed')
                left, right = raw_old['text'], raw_new['text']
                if left == right: continue
                c, p = differences(json.loads(left), json.loads(right), allowed)
                problems.extend(label + ': raw ' + problem for problem in p)
                ls, rs = raw_string_spans(left), raw_string_spans(right)
                replacements = [(start, end, ls[path][2]) for path, (start, end, raw) in rs.items()
                                if path in ls and raw != ls[path][2] and allowed(path)]
                for start, end, replacement in sorted(replacements, reverse=True):
                    right = right[:start] + replacement + right[end:]
                if left != right: problems.append(label + ': raw bytes outside allowed prose changed')
        for snapshot in (original, new):
            if {k: v for k, v in old.items() if k != 'calls'} != {k: v for k, v in snapshot.items() if k != 'calls'}:
                problems.append('capture metadata changed')
        print(f'V{release} P6-26: changed-text={dict(sorted(counts.items()))}; unexpected={len(problems)}')
        for problem in problems: print('  unexpected: ' + problem)
        failures += len(problems)
    return int(failures != 0)


def compare_migration(args):
    """Phase-6 group proof: only the task's group (including bridge calls that wrap it) may change."""
    import phase6_groups
    baseline, current = load_snapshots(args.baseline), load_snapshots(args.current)
    failures = 0
    for release in args.releases or sorted(baseline.keys() & current.keys()):
        old, new = baseline[release], current[release]
        a, b = ({identity(call): call for call in snapshot['calls']} for snapshot in (old, new))
        members = phase6_groups.group(args.migration, release, {key[1] for key in a})
        allowed = members | {phase6_groups.mapped(name, members) for name in members} | phase6_groups.additions(args.migration, release)

        def catalog(key):
            # Usage, search and category responses render the generated examples (Generate-ToolUsage --check), the schemas
            # (contract proof) and the descriptions (source rename check); their changes are listed, not failed.
            # The duplicate-argument refusal probes stay strict.
            try:
                arguments = json.loads(key[2])
            except ValueError:
                return False
            # These two passive Foundation calls include the registered-tool roster.
            # P6-67 adds exactly three tools; verify() and the coverage assertions
            # below still require the complete, exact refusal and contract rosters.
            if (args.migration == 'P6-67' and release not in ('20', '21')
                    and key[1] in ('InitializeEnvironment', 'RunCapabilitySelfTest') and not arguments):
                return True
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
        def capability_catalog_only(key):
            if args.migration not in ('P7-04', 'P7-04b') or key[1] != 'GetPortalInfo' or key not in other_a or key not in other_b:
                return False
            before, after = copy.deepcopy(other_a[key]), copy.deepcopy(other_b[key])
            before.pop('rawTextBlocks', None)
            after.pop('rawTextBlocks', None)
            for call in (before, after):
                result = call['response']['result']
                envelopes = [result['structuredContent'], result['content'][0]['text']]
                for envelope in envelopes:
                    capabilities = envelope['data'].pop('behaviorCapabilities')
                    if call is after:
                        expected = json.loads((Path(__file__).resolve().parents[2] / f'manifest/contracts/v4/baseline/{release}.json').read_text('utf-8'))['behaviorCapabilities']
                        if capabilities != expected:
                            return False
            return before == after

        capability_catalog = [key for key in differing if capability_catalog_only(key)]
        guidance = [key for key in differing if renamed_only(key)]
        outside = [key for key in differing if key not in guidance and key not in capability_catalog]
        for key in ('release', 'formatVersion', 'profiles', 'transport', 'maxResponseChars'):
            assert old.get(key) == new.get(key), (release, key)
        assert new['rawMaskRules'] == RAW_MASK_RULES
        merged = len(members) - len({phase6_groups.mapped(name, members) for name in members})
        removed = phase6_groups.removals(args.migration)
        assert new['coverage']['registeredTools'] == old['coverage']['registeredTools'] - merged + len(phase6_groups.additions(args.migration, release)) - len(removed), (release, 'registered tool count')
        expected = ({phase6_groups.mapped(name, members) for name in old['coverage']['directRejectedTools']} | phase6_groups.additions(args.migration, release)) - removed
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
        for key in capability_catalog:
            print('  generated behaviorCapabilities only; other response fields equal: ' + key[1])
        for key in outside:
            print('  unexpected: ' + key[0] + ' ' + key[1] + '(' + key[2] + ') ' + str(first_difference(other_a.get(key), other_b.get(key))))
        failures += len(outside)
    return int(failures != 0)


class RawResponseTests(unittest.TestCase):
    def test_lifecycle_description_group_is_release_scoped(self):
        import phase6_groups
        members = {'RetrieveProjectArchive', 'ManageMultiuserSession'}
        task = 'P7-04b-followup'
        self.assertEqual(members, phase6_groups.names(task))
        self.assertEqual(set(), phase6_groups.removals(task))
        self.assertEqual({}, phase6_groups.rename_map(task))
        for release in RELEASES:
            self.assertEqual(set(), phase6_groups.additions(task, release))
            self.assertEqual(members if release in ('20', '21') else set(), phase6_groups.group(task, release, members))
            self.assertEqual(set(), phase6_groups.group(task, release, {'Existing'}))

    def test_restored_lifecycle_group_is_release_scoped(self):
        import phase6_groups
        restored = {'RetrieveProjectArchive', 'SaveProjectCopy', 'ManageMultiuserSession', 'BuildProjectScaffold', 'ConnectIsolatedPortal'}
        self.assertEqual(restored, phase6_groups.names('P7-04b'))
        self.assertEqual(set(), phase6_groups.removals('P7-04b'))
        self.assertEqual({}, phase6_groups.rename_map('P7-04b'))
        for release in RELEASES:
            self.assertEqual(restored if release in ('20', '21') else set(), phase6_groups.additions('P7-04b', release))
            self.assertEqual(set(), phase6_groups.group('P7-04b', release, {'Existing'}))
            self.assertEqual(restored if release in ('20', '21') else set(), phase6_groups.group('P7-04b', release, restored))

    def test_write_refusal_matches_capture_readiness_mode(self):
        readiness = {'schemaVersion': 4, 'ok': False,
            'error': {'code': 'RESOURCE_UNAVAILABLE', 'details': {'resource': 'tia-openness-environment'}},
            'meta': {'outcome': 'rejected-before-operation', 'execution': 'not-started', 'requiresSessionReset': False}}
        approval = {'schemaVersion': 4, 'ok': False,
            'error': {'code': 'CONFIRMATION_REQUIRED'},
            'meta': {'outcome': 'rejected-before-operation', 'execution': 'not-started', 'requiresSessionReset': False}}
        session_write_refusal(readiness, 'SaveProject', True)
        session_write_refusal(approval, 'SaveProject', False)
        with self.assertRaises(Exception):
            session_write_refusal(readiness, 'SaveProject', False)
        with self.assertRaises(Exception):
            session_write_refusal(approval, 'SaveProject', True)

    def test_fixture_capture_marks_readiness_only_in_the_http_tests_harness(self):
        self.assertEqual(capture_readiness_overrides(Path('TiaMcp.Engine.Harness.exe'), False),
                         {'TIA_MCP_TEST_READINESS_READY': '1'})
        self.assertEqual(capture_readiness_overrides(None, False), {})
        self.assertEqual(capture_readiness_overrides(Path('TiaMcp.Engine.Harness.exe'), True), {})

    def test_sdk_capture_defaults_to_a_built_http_tests_harness(self):
        with fixture_directory('response-capture-harness-test-') as temporary:
            repo = Path(temporary)
            harness = repo / 'tests/Engine/TiaMcp.Engine.Harness/bin/Release/net48/TiaMcp.Engine.Harness.exe'
            harness.parent.mkdir(parents=True)
            harness.write_bytes(b'test harness')
            args = argparse.Namespace(packaged_no_tia=False, harness=None, repo_root=repo)
            self.assertEqual(sdk_only_harness(args, '20'), harness.resolve())
            args.packaged_no_tia = True
            self.assertIsNone(sdk_only_harness(args, '20'))
            args.packaged_no_tia = False
            self.assertIsNone(sdk_only_harness(args, '19'))

    def test_engine_host_capture_uses_its_worker_instead_of_the_http_harness(self):
        args = argparse.Namespace(packaged_no_tia=False, harness=None,
                                  repo_root=Path.cwd(), engine_host=Path('FoundationHost.exe'))
        self.assertIsNone(sdk_only_harness(args, '20'))
        self.assertIsNone(sdk_only_harness(args, '21'))

    def test_frozen_snapshot_fields(self):
        root = Path(__file__).resolve().parents[2]
        snapshot = json.loads((root / 'manifest/contracts/v4/responses/21.json').read_text(encoding='utf-8'))
        validate_response_snapshot(snapshot, '21.json')
        invalid_cases = []
        changed = json.loads(json.dumps(snapshot))
        changed['formatVersion'] = RESPONSE_FORMAT_VERSION + 1
        invalid_cases.append(changed)
        changed = json.loads(json.dumps(snapshot))
        changed['unexpected'] = True
        invalid_cases.append(changed)
        changed = json.loads(json.dumps(snapshot))
        changed['coverage'].pop('registeredTools')
        invalid_cases.append(changed)
        changed = json.loads(json.dumps(snapshot))
        changed['calls'][0]['unexpected'] = True
        invalid_cases.append(changed)
        for invalid in invalid_cases:
            with self.assertRaises(ValueError):
                validate_response_snapshot(invalid, 'negative.json')

    def test_current_behavior_disclosure_is_required(self):
        response = {'result': {'content': [{'text': {'schemaVersion': 4, 'ok': False, 'meta': {
            'behaviorPolicy': 'current', 'warnings': [{'code': 'UNVERIFIED_BEHAVIOR'}]}}}]}}
        require_behavior_disclosure(response, 'fixture')
        response['result']['content'][0]['text']['meta']['warnings'] = []
        with self.assertRaises(Exception): require_behavior_disclosure(response, 'fixture')
        response['result']['content'][0]['text']['meta']['warnings'] = [{'code': 'UNVERIFIED_BEHAVIOR'}]
        response['result']['content'][0]['text']['meta']['behaviorPolicy'] = 'not-applicable'
        with self.assertRaises(Exception): require_behavior_disclosure(response, 'fixture')

    def test_text_migration_rejects_schema_code_outcome_and_data_changes(self):
        from snapshot_text_migration import differences, response_text, contract_text
        allowed = lambda path: response_text('Probe', path)
        old = {'error': {'code': 'INVALID_ARGUMENT', 'message': '旧说明'}, 'meta': {'outcome': 'rejected-before-operation'}, 'data': {'name': '原名称'}}
        new = json.loads(json.dumps(old))
        new['error']['message'] = 'Translated explanation'
        counts, problems = differences(old, new, allowed)
        self.assertEqual({'error-message': 1}, counts)
        self.assertEqual([], problems)
        for path, replacement in [(('error', 'code'), 'INTERNAL_ERROR'), (('meta', 'outcome'), 'unknown'), (('data', 'name'), 'changed')]:
            changed = json.loads(json.dumps(new))
            changed[path[0]][path[1]] = replacement
            self.assertTrue(differences(old, changed, allowed)[1])
        schema = {'inputSchema': {'type': 'object', 'properties': {'x': {'type': 'string', 'description': '旧说明'}}}}
        translated = json.loads(json.dumps(schema))
        translated['inputSchema']['properties']['x']['description'] = 'Explanation'
        self.assertEqual([], differences(schema, translated, contract_text)[1])
        translated['inputSchema']['properties']['x']['type'] = 'integer'
        self.assertTrue(differences(schema, translated, contract_text)[1])

    def test_text_evidence_must_match_digest_and_raw_hash(self):
        entries = {}
        recorder(lambda *a, **kw: self.reply('{"error":{"code":"INVALID_ARGUMENT","message":"Explanation"}}'), entries, 'lite', '21')('GetSessionState', {})
        call = compact(next(iter(entries.values())), True)
        validated_evidence(call)
        call['textEvidence']['response']['result']['content'][0]['text']['error']['code'] = 'INTERNAL_ERROR'
        with self.assertRaises(ValueError): validated_evidence(call)

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
        self.assertIn('"requestId":"' + first + '"', mask_raw_text(sample, 'GetSessionState'))
        for tool in ('SaveProject', 'SaveProjectCopy', 'CloseProject'):
            refusal = ('{"schemaVersion":4,"ok":false,"meta":{"timestamp":"2026-10-03T00:00:00Z",'
                       '"requestId":"' + first + '","behaviorPolicy":"current",'
                       '"warnings":[{"code":"UNVERIFIED_BEHAVIOR"}]},"error":{"code":"CONFIRMATION_REQUIRED",'
                       '"details":{"requestId":"' + first + '"}}}')
            changed = refusal.replace(first, second)
            self.assertIn('"requestId":"<string:requestId>"', mask_raw_text(refusal, tool))
            self.assertEqual(self.call(refusal, tool), self.call(changed, tool))
            self.assertEqual(self.call(refusal, 'CallTool', 'lite',
                                       {'name': tool, 'arguments': {}}),
                             self.call(changed, 'CallTool', 'lite',
                                       {'name': tool, 'arguments': {}}))

    def reply(self, text):
        return {'id': 1, 'jsonrpc': '2.0', 'result': {'content': [{'type': 'text', 'text': text}]}}

    def call(self, text, tool='GetSessionState', profile='full', arguments=None):
        entries = {}
        recorder(lambda *a, **kw: self.reply(text), entries, profile, '21')(tool, arguments or {})
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
            if rule['tool'] == 'V4 infrastructure only': continue
            with self.subTest(rule=rule):
                is_request_id = rule.get('kind') == 'requestId' or rule['path'][-1] == 'requestId'
                original = 'a' * 32 if is_request_id else '2026-10-03T11:12:13.1234567-07:00'
                replacement = '<string:requestId>' if is_request_id else '<string:timestamp>'
                value = '"' + original + '"'
                for key in reversed(rule['path']):
                    value = '{ "' + key + '" : ' + value + ' }'
                tool = rule['tool'] if rule['tool'] != '*' else 'GetSessionState'
                self.assertEqual(mask_raw_text(value, tool),
                                 value.replace(original, replacement))
                other_value = 'b' * 32 if is_request_id else '2027-01-02T00:00:00Z'
                other = value.replace(original, other_value)
                if not is_request_id:
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
                self.assertEqual(mask_raw_text(value, 'GetSessionState'), value)
        # Builder masks cannot reach tool usage examples or source XML strings.
        self.assertEqual(mask_raw_text(samples[0], 'GetToolUsage'), samples[0])

    def test_surrounding_bytes(self):
        value = r'{ "meta" : {"timestamp" : "2026-10-03T11:12:13Z", "n":1.00}, "text":"中文\n\"\\", "array":[{},[null,2]] }'
        masked = mask_raw_text(value, 'GetSessionState')
        self.assertEqual(masked, value.replace('2026-10-03T11:12:13Z', '<string:timestamp>'))
        self.assertEqual(self.call(value)['rawTextBlocks'][0]['sha256'],
                         hashlib.sha256(masked.encode('utf-8')).hexdigest())

    def test_plain_text_and_invalid_json(self):
        for text in ('argument refused 中文', '', '{"meta":{"timestamp":"2026-10-03T11:12:13Z"}',
                     '{\u00a0"meta":{"timestamp":"2026-10-03T11:12:13Z"}}',
                     '{"meta":{"timestamp":"2026-10-03T11:12:13Z"}} extra'):
            with self.subTest(text=text):
                self.assertEqual(mask_raw_text(text, 'GetSessionState'), text)
                self.assertEqual(self.call(text)['rawTextBlocks'][0]['sha256'],
                                 hashlib.sha256(text.encode('utf-8')).hexdigest())

    def test_multiple_blocks_and_protocol_error(self):
        reply = self.reply('first')
        reply['result']['content'] += [{'type': 'image', 'data': 'AA=='}, {'type': 'text', 'text': 'second'}]
        hashes = raw_text_blocks(reply, 'GetSessionState')
        self.assertEqual([b['contentIndex'] for b in hashes], [0, 2])
        self.assertEqual(hashes[1]['sha256'], hashlib.sha256(b'second').hexdigest())
        self.assertEqual(raw_text_blocks({'error': {'message': 'refused'}}, 'GetSessionState'), [])

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
            self.assertIn('full GetSessionState({}) $/rawTextBlocks/0/sha256', output.getvalue())
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
    capture_parser.add_argument('--engine-source', action='store_true', help='A/B capture through the retired engine host')
    capture_parser.add_argument('--engine-host', type=Path, help='Capture 20/21 through FoundationHost with SDK fixture workers')
    capture_parser.add_argument('--engine-worker', action='append', default=[], metavar='RELEASE=PATH')
    capture_parser.add_argument('--engine-catalog', action='append', default=[], metavar='RELEASE=PATH')
    capture_parser.add_argument('--transport', choices=('stdio', 'http'), default='stdio', help='EngineHost transport')
    capture_parser.add_argument('--harness', type=Path,
                                help='TiaMcp.Engine.Harness host harness for V20/V21 sdk-only captures (defaults to the built harness)')
    capture_parser.add_argument('--public-api-root', type=Path)
    capture_parser.add_argument('--packaged-no-tia', action='store_true',
                                help='Stage V20/V21 without Siemens.Engineering DLLs and require no-TIA readiness')
    capture_parser.add_argument('--dotnet-root', type=Path,
                                help='Private .NET/ASP.NET Core 10 root; sets DOTNET_ROOT and DOTNET_ROOT_X64 for Foundation only')
    capture_parser.add_argument('--exe', action='append', default=[], metavar='RELEASE=PATH')
    capture_parser.add_argument('--releases', nargs='+', choices=RELEASES, default=RELEASES)
    capture_parser.add_argument('--output', required=True, type=Path)
    capture_parser.add_argument('--include-text-evidence', action='store_true',
                                help='Keep complete normalized and masked raw responses for a text-only migration proof')
    capture_parser.add_argument('--temp-root', type=Path, default=Path(tempfile.gettempdir()),
                                help='Parent for disposable host journals (use a writable worktree directory in a sandbox)')
    capture_parser.set_defaults(run=capture)
    verify_parser = commands.add_parser('verify', help='Check static V4 inventories, rosters and captured response coverage')
    verify_parser.add_argument('--repo-root', type=Path, default=Path(__file__).resolve().parents[2])
    verify_parser.add_argument('--baseline', type=Path, help='Contract directory (default: <repo-root>/manifest/contracts/v4/baseline)')
    verify_parser.add_argument('--responses', type=Path, help='Response directory (default: <repo-root>/manifest/contracts/v4/responses)')
    verify_parser.set_defaults(run=verify)
    compare_parser = commands.add_parser('compare')
    compare_parser.add_argument('--baseline', required=True, type=Path)
    compare_parser.add_argument('--current', required=True, type=Path)
    compare_parser.add_argument('--migration', choices=[*__import__('phase6_groups').TASKS, 'P6-26'], help='Verify a phase-6 migration; P6-26 permits only explicit prose fields')
    compare_parser.add_argument('--text-baseline', type=Path, help='Before capture with complete evidence anchored to the frozen baseline (P6-26)')
    compare_parser.add_argument('--normalized-only', action='store_true',
                                help='Migration check against format 2 only; does not prove raw-byte compatibility')
    compare_parser.add_argument('--releases', nargs='+', choices=RELEASES,
                                help='Compare only these releases (default: compare all releases strictly)')
    compare_parser.set_defaults(run=compare)
    args = parser.parse_args()
    try:
        return args.run(args)
    except (OSError, ValueError, KeyError, TypeError, AssertionError, queue.Empty, subprocess.SubprocessError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        return 1


def verify_coverage(release, baseline, snapshot, lite):
    names = {tool['name'] for tool in baseline['tools']}
    coverage = snapshot['coverage']

    def roster(field, expected):
        actual = contracts.unique_names(coverage[field], f'V{release} {field}')
        if actual != expected:
            raise ValueError(f'V{release}: {field} differs from capture roster: {sorted(actual ^ expected)}')

    def skipped(field, expected):
        reasons = coverage[field]
        if not isinstance(reasons, dict) or set(reasons) != expected or any(
                not isinstance(reason, str) or not reason for reason in reasons.values()):
            raise ValueError(f'V{release}: {field} must give a reason for each skipped tool')

    if coverage['registeredTools'] != len(names):
        raise ValueError(f'V{release}: registeredTools differs from baseline')
    calls = {identity(call): call for call in snapshot['calls']}
    for call in snapshot['calls']:
        # compact writes either the complete protocol response or its reviewed digest.
        if ('response' in call) == ('responseDigest' in call):
            raise ValueError(f'V{release}: call must carry one response or responseDigest: {identity(call)}')
        if 'responseDigest' in call:
            digest = call['responseDigest']
            if not re.fullmatch('[0-9a-f]{64}', digest['sha256']) or type(digest['length']) is not int or digest['length'] <= 0:
                raise ValueError(f'V{release}: invalid response digest: {identity(call)}')
        elif not isinstance(call['response'], dict) or not call['response']:
            raise ValueError(f'V{release}: empty protocol response: {identity(call)}')

    def required_call(profile, tool, arguments):
        key = (profile, tool, canonical(arguments))
        if key not in calls:
            raise ValueError(f'V{release}: missing response coverage for {profile}/{tool}/{canonical(arguments)}')

    def stored_v4_body(profile, tool, arguments):
        call = calls[(profile, tool, canonical(arguments))]
        response = call.get('response')
        if not isinstance(response, dict):
            raise ValueError(f'V{release}: packaged no-TIA response must be stored inline: {tool}')
        protocol = response.get('result', {})
        body = protocol.get('structuredContent')
        if isinstance(body, dict):
            return body
        content = protocol.get('content', [])
        if content and isinstance(content[0].get('text'), dict):
            return content[0]['text']
        raise ValueError(f'V{release}: packaged no-TIA response has no V4 body: {tool}')

    if release in FULL_RELEASES:
        if snapshot['profiles'] != ['full', 'lite']:
            raise ValueError(f'V{release}: invalid response profiles')
        rejected, passive, profile = names, set(), 'full'
        skipped('directSkipped', set())
        skipped('bridgeSkipped', set())
        roster('bridgeRejectedTools', names - {'CallTool'})
        roster('bridgeSelfGuardTools', {'CallTool'})
        roster('liteAdvertisedTools', lite)
        for name in names:
            required_call('lite', 'CallTool', {'name': name, 'arguments': REJECT_ARGUMENTS})
        if snapshot.get('captureMode') == 'packaged-no-tia':
            bootstrap = stored_v4_body('full', 'InitializeEnvironment', {})
            doctor = stored_v4_body('full', 'GetEnvironmentDiagnostics', {'fix': False})
            state = stored_v4_body('full', 'GetSessionState', {})
            if (bootstrap.get('data', {}).get('ready') is not False
                    or 'no TIA Portal V' not in bootstrap.get('data', {}).get('recommendedReason', '')):
                raise ValueError(f'V{release}: packaged snapshot does not prove no-TIA bootstrap readiness')
            if (doctor.get('data', {}).get('ready') is not False
                    or 'no TIA Portal V' not in canonical(doctor.get('data', {}))):
                raise ValueError(f'V{release}: packaged snapshot does not prove no-TIA diagnostics readiness')
            if (state.get('error', {}).get('code') != 'RESOURCE_UNAVAILABLE'
                    or state.get('error', {}).get('details', {}).get('resource') != 'tia-openness-environment'
                    or state.get('meta', {}).get('outcome') != 'rejected-before-operation'
                    or state.get('meta', {}).get('execution') != 'not-started'
                    or 'no TIA Portal V' not in state.get('data', {}).get('environment', {}).get('cause', '')):
                raise ValueError(f'V{release}: packaged snapshot does not prove GetSessionState readiness refusal')
    else:
        if snapshot['profiles'] != ['plc-foundation'] or 'liteAdvertisedTools' in coverage:
            raise ValueError(f'V{release}: Foundation must not advertise a lite roster')
        # Match capture_foundation's reviewed host-side admission allowlist.
        worker_tools = FOUNDATION_WORKER_TOOLS | ported_additions(Path(__file__).resolve().parents[2], release)
        rejected = {tool['name'] for tool in baseline['tools']
                    if (tool['name'] in worker_tools or tool['name'] in FOUNDATION_MARKERS)
                    and not any(key.lower() == 'snapshotreject'
                                for key in tool['inputSchema'].get('properties', {}))}
        passive, profile = {'InitializeEnvironment', 'RunCapabilitySelfTest'}, 'plc-foundation'
        skipped('directSkipped', names - rejected)
        skipped('bridgeSkipped', names)
        skipped('passiveSkipped', {'GetSessionState'})
        roster('bridgeRejectedTools', set())
        roster('passiveTools', passive)
        for name in passive:
            required_call(profile, name, {})
    roster('directRejectedTools', rejected)
    roster('calledTools', rejected | passive)
    for name in rejected:
        required_call(profile, name, REJECT_ARGUMENTS)


def verify(args):
    root = args.repo_root.resolve()
    baseline = contracts.verified_contracts(args.baseline or root / 'manifest/contracts/v4/baseline', root)
    directory = args.responses or root / 'manifest/contracts/v4/responses'
    contracts.exact_release_files(directory)
    snapshots = load_snapshots(directory)
    rosters = contracts.catalog_rosters(root)
    for release, snapshot in snapshots.items():
        validate_response_snapshot(snapshot, directory / (release + '.json'))
        verify_coverage(release, baseline[release], snapshot, rosters[release][1])
        inventory = {name for row in baseline[release]['behaviorCapabilities'] if row['state'] == 'current' for name in row['entries']}
        for call in snapshot['calls']:
            if call['tool'] in inventory and 'response' in call:
                require_behavior_disclosure(call['response'], call['tool'])
        # Digest-only bridge replies are asserted by recorder before hashing; the
        # actual built-product inventory tests also exercise every bridge target.
    print(f'V4 responses verified: {len(snapshots)} releases, '
          f'{sum(len(snapshot["calls"]) for snapshot in snapshots.values())} calls; 0 issues.')
    return 0


def validate_response_snapshot(snapshot, path):
    release = snapshot.get('release')
    full = release in FULL_RELEASES
    expected_fields = FULL_RESPONSE_FIELDS if full else FOUNDATION_RESPONSE_FIELDS
    allowed_fields = (expected_fields, LEGACY_FULL_RESPONSE_FIELDS) if full else (expected_fields,)
    if set(snapshot) not in allowed_fields:
        raise ValueError(f'{path}: response fields differ; missing={sorted(expected_fields - set(snapshot))}, '
                         f'unknown={sorted(set(snapshot) - expected_fields)}')
    if full and 'captureMode' in snapshot and snapshot['captureMode'] not in ('sdk-only-fixture', 'packaged-no-tia'):
        raise ValueError(f'{path}: unsupported full-engine captureMode')
    if type(snapshot['formatVersion']) is not int or snapshot['formatVersion'] != RESPONSE_FORMAT_VERSION:
        raise ValueError(f'{path}: expected response formatVersion {RESPONSE_FORMAT_VERSION}')
    if (snapshot['profiles'] != (['full', 'lite'] if full else ['plc-foundation'])
            or snapshot['transport'] != 'stdio'):
        raise ValueError(f'{path}: invalid response profile or transport')
    if full and (type(snapshot['maxResponseChars']) is not int or snapshot['maxResponseChars'] != 2000000):
        raise ValueError(f'{path}: invalid maxResponseChars')
    coverage_fields = FULL_COVERAGE_FIELDS if full else FOUNDATION_COVERAGE_FIELDS
    coverage = snapshot['coverage']
    if not isinstance(coverage, dict) or set(coverage) != coverage_fields:
        actual = set(coverage) if isinstance(coverage, dict) else set()
        raise ValueError(f'{path}: coverage fields differ; missing={sorted(coverage_fields - actual)}, '
                         f'unknown={sorted(actual - coverage_fields)}')
    rules = snapshot['rawMaskRules']
    if not isinstance(rules, list):
        raise ValueError(f'{path}: rawMaskRules must be an array')
    for rule in rules:
        if not isinstance(rule, dict) or set(rule) not in ({'tool', 'path', 'reason'},
                                                           {'tool', 'kind', 'path', 'reason'}):
            raise ValueError(f'{path}: invalid raw mask rule fields')
        if (not isinstance(rule['tool'], str) or not isinstance(rule['path'], list)
                or any(not isinstance(part, str) or not part for part in rule['path'])
                or not isinstance(rule['reason'], str) or not rule['reason']):
            raise ValueError(f'{path}: invalid raw mask rule')
        if 'kind' in rule and rule['kind'] != 'requestId':
            raise ValueError(f'{path}: unknown raw mask kind')
    if not isinstance(snapshot['calls'], list) or not snapshot['calls']:
        raise ValueError(f'{path}: calls must be a nonempty array')
    for call in snapshot['calls']:
        if not isinstance(call, dict):
            raise ValueError(f'{path}: call must be an object')
        fields = set(call)
        expected_call_fields = {'profile', 'tool', 'arguments', 'rawTextBlocks'}
        if fields == expected_call_fields | {'response'}:
            if not isinstance(call['response'], dict) or not call['response']:
                raise ValueError(f'{path}: response must be a nonempty object')
        elif fields == expected_call_fields | {'responseDigest'}:
            digest = call['responseDigest']
            if (not isinstance(digest, dict) or set(digest) != {'length', 'sha256', 'shape', 'textShapes'}
                    or type(digest['length']) is not int or digest['length'] <= 0
                    or not re.fullmatch(r'[0-9a-f]{64}', digest['sha256'])
                    or not isinstance(digest['shape'], dict) or not isinstance(digest['textShapes'], list)):
                raise ValueError(f'{path}: invalid responseDigest')
        else:
            raise ValueError(f'{path}: call fields differ; unknown={sorted(fields - expected_call_fields - {"response", "responseDigest"})}, '
                             f'missing={sorted(expected_call_fields - fields)}')
        if (not isinstance(call['profile'], str) or not isinstance(call['tool'], str)
                or not isinstance(call['arguments'], dict)):
            raise ValueError(f'{path}: invalid call identity')
        if not isinstance(call['rawTextBlocks'], list) or any(
                not isinstance(block, dict) or set(block) != {'contentIndex', 'sha256'}
                or type(block['contentIndex']) is not int or block['contentIndex'] < 0
                or not re.fullmatch(r'[0-9a-f]{64}', block['sha256'])
                for block in call['rawTextBlocks']):
            raise ValueError(f'{path}: invalid rawTextBlocks')


if __name__ == '__main__':
    sys.exit(main())
