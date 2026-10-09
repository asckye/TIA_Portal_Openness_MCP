"""Usage retrieval and allowlisted in-memory example execution; no native TIA calls."""
import hashlib
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mcp_results import successful
import xml.etree.ElementTree as ET


WITHDRAWN_FROM_PRODUCT = {'ConnectProject'}


def unwrap_usage(reply):
    reply = successful(reply)
    body = dict(reply['data'])
    paging = reply['meta']['paging']
    if paging:
        following = paging['offset'] + paging['limit']
        body['nextOffset'] = following if following < body.get('totalLines', body.get('totalMatches', 0)) else None
        body['nextToolOffset'] = following if following < body.get('toolCount', 0) else None
    return body


def check_usage(call, tools, release, exhaustive=True, verify_documents=False, registered_only=False):
    """call returns the decoded MCP text envelope. Never call the sampled tool."""
    assert any(t['name'] == 'GetToolUsage' for t in tools), 'Usage tool is absent from this catalog'
    first = unwrap_usage(call('GetToolUsage', {'limit': 1}))
    assert first['referenceOnly'] and len(first['sources']) == 2
    names = []
    offset = 0
    while True:
        page = unwrap_usage(call('GetToolUsage', {'offset': offset, 'limit': 40}))
        names.extend(page['tools'])
        offset = page['nextToolOffset']
        if offset is None:
            break
    assert len(names) == first['toolCount'] == len(set(names))
    assert {t['name'] for t in tools} <= set(names)
    schemas = {t['name']: t['inputSchema'] for t in tools}
    records = []
    problems = []
    builder_examples = 0
    offline_calls = []
    selected = names if exhaustive else ['GetToolUsage', 'ManageStartdriveParameter']
    # ConnectProject is withdrawn from the V20/V21 product catalog (P7-04); the engine host still lists it until it is retired
    # (P7-08), and its usage lookup is refused. Every other listed name must describe itself.
    selected = [name for name in selected if name not in WITHDRAWN_FROM_PRODUCT]
    if registered_only:
        selected = [name for name in selected if name in schemas]
    for name in selected:
        usage = unwrap_usage(call('GetToolUsage', {'toolName': name}))
        assert usage['available'] and usage['releaseKey'] == str(release) and usage['toolName'] == name
        schema = usage['inputSchema']
        if name in schemas:
            assert schema == schemas[name], name + ': differs from tools/list'
        example = usage['example']
        assert example['kind'] == 'parameterized-call-example', (release, name, 'missing call record')
        request = example['request']
        assert request['method'] == 'tools/call' and request['params']['name'] == name
        args = request['params']['arguments']
        props = schema['properties']
        assert set(schema.get('required', [])) <= set(args) <= set(props), name
        for key, value in args.items():
            prop = props[key]
            kind = prop.get('type')
            kinds = kind if isinstance(kind, list) else [kind]
            actual = ('null' if value is None else 'boolean' if isinstance(value, bool) else
                      'integer' if isinstance(value, int) else 'number' if isinstance(value, float) else
                      'string' if isinstance(value, str) else 'array' if isinstance(value, list) else 'object')
            if not (actual in kinds or actual == 'integer' and 'number' in kinds or kind is None):
                problems.append((name, key, actual, kinds))
            if 'enum' in prop:
                if value not in prop['enum']:
                    problems.append((name, key, value, prop['enum']))
        if 'dryRun' in props:
            assert args['dryRun'] is True, name
        if 'confirm' in props:
            assert args['confirm'] is False, name
        reference = usage['officialReference']
        assert reference['relationship'] in ('related-api-patterns', 'no-direct-official-example')
        assert set(usage['parameterSources']) == set(props), name
        assert usage['resultContract'] and usage['resultReading']
        if name in ('ImportPlcBlock', 'ImportPlcType', 'ImportPlcTagTable'):
            note = example['note']
            if 'dryRun' not in props:
                assert not {'overwrite', 'dryRun', 'confirm', 'expectedProjectFile'} & set(props), (release, name)
                assert 'current V20/V21 schema has no overwrite' in note, (release, name)
            else:
                assert args['overwrite'] is False, (release, name, 'explicit replacement refusal')
                execution = example['execution']['arguments']
                assert execution['overwrite'] is True and execution['expectedProjectFile'], (release, name)
                assert set(execution) <= set(props), (release, name, 'execution schema')
                for token in ('overwrite=false refuses replacement.', 'parameter=overwrite',
                              'no Workbench request', 'normalized full path', 'C:/Examples/exports/Tags.xml',
                              'C:\\Examples\\exports\\Tags.xml', 'An existing Openness XML file is required.'):
                    assert token in note, (release, name, token)
        result_fields = usage['resultContract'].get('fields')
        if result_fields:
            for case in usage.get('interpretation', {}).get('resultCases', []):
                assert set(case['example']) <= set(result_fields), (name, case['example'], result_fields)
        assert example['request']['method'] == 'tools/call'
        assert not any(k in usage for k in ('workflow', 'onFailure', 'nativeAcceptance'))
        operation_records = []
        selector = 'action' if 'action' in props else 'operation' if 'operation' in props and name != 'GetToolUsage' else None
        if selector:
            choices = [v for v in props[selector].get('enum', []) if v]
            assert choices, name + ': action alternatives are missing'
            assert [o['operation'] for o in usage['operations']] == choices
            if exhaustive:
                for choice in choices:
                    selected_usage = unwrap_usage(call('GetToolUsage', {'toolName': name, 'operation': choice}))
                    selected_args = selected_usage['example']['request']['params']['arguments']
                    assert selected_usage['example']['kind'] == 'parameterized-call-example', (release, name, choice, 'missing operation example')
                    assert selected_args[selector] == choice, (name, choice)
                    assert set(schema.get('required', [])) <= set(selected_args) <= set(props), (name, choice)
                    operation_records.append({'operation': choice, 'exampleKind': selected_usage['example']['kind'],
                        'releaseProblem': selected_usage['example']['releaseProblem']})
        records.append({'toolName': name, 'exampleKind': example['kind'], 'relationship': reference['relationship'], 'documents': reference['documents'], 'operations': operation_records})
        # Consume the SAME example returned to AI callers, with exact source args.
        # Nothing outside this literal allowlist can execute during this audit.
        offline_xml = {'BuildPlcUdt', 'BuildPlcTagTable', 'BuildPlcGlobalDb',
                       'BuildStructuredText', 'BuildFlgNetCall', 'BuildPlcFcBlock',
                       'BuildPlcFbBlock', 'BuildPlcLadFcBlock'}
        if exhaustive and name in offline_xml:
            built = call(name, args)
            assert built.get('ok', True) and built.get('meta', {}).get('success', True), (name, built)
            payload = built['data'] if built.get('schemaVersion') == 4 else built
            root = ET.fromstring(payload['xml'])
            assert len(list(root.iter())) > 3, name + ': empty XML'
            if name in ('BuildPlcUdt', 'BuildPlcGlobalDb'):
                target = args['outputReleaseKey']
                expected_target = '21' if 'outputReleaseKey' in schema.get('required', []) else str(release)
                assert target == expected_target, (name, target, release)
                engineering, interface = {
                    '14sp1': ('V14 SP1', 2), '15.1': ('V15.1', 3),
                    '16': ('V16', 4), '17': ('V17', 4), '18': ('V18', 5),
                    '19': ('V19', 5), '20': ('V20', 5), '21': ('V21', 5)
                }[target]
                namespace = f'http://www.siemens.com/automation/Openness/SW/Interface/v{interface}'
                assert root.find('Engineering').attrib['version'] == engineering, (name, target)
                assert root.find('.//{' + namespace + '}Sections') is not None, (name, namespace)
                details = payload if built.get('schemaVersion') == 4 else built['data']
                assert details['outputReleaseKey'] == target, built
                assert details['interfaceNamespace'] == namespace, built
                assert (root.find('.//AttributeList/Namespace') is not None) == (int(target[:2]) >= 18), (name, target)
            if name == 'BuildPlcUdt':
                assert any(e.tag.endswith('Member') and e.attrib.get('Name') == 'Ready' and e.attrib.get('Datatype') == 'Bool' for e in root.iter())
            if name == 'BuildPlcTagTable':
                assert any(e.tag.endswith('LogicalAddress') and e.text == '%M0.0' for e in root.iter())
            if name in ('BuildStructuredText', 'BuildPlcFcBlock', 'BuildPlcFbBlock'):
                assert any(e.tag.endswith('Token') and e.attrib.get('Text') == ':=' for e in root.iter())
            if name in ('BuildFlgNetCall', 'BuildPlcLadFcBlock'):
                assert any(e.tag.endswith('CallInfo') and e.attrib.get('Name') == 'FC_Ready' for e in root.iter())
            offline_calls.append(name)
        if exhaustive and name == 'PlanArtifactImportOrder':
            built = call(name, args)
            data = built['data'] if built.get('schemaVersion') == 4 else built.get('meta', {})
            plan = data.get('plan', data)
            valid, order = ('Valid', 'Order') if 'Valid' in plan else ('valid', 'order')
            assert plan[valid] and plan[order] == ['UDT_Status', 'FB_Motor'], built
            offline_calls.append(name)
        if exhaustive and name == 'BuildUnifiedHmiButtonActionScript':
            built = call(name, args)
            recipe = built['data']['evidence']
            assert built['schemaVersion'] == 4 and built['ok'] and built['error'] is None, built
            assert recipe['ok'] and recipe['event'] == 'Down' and 'SetBitInTag' in recipe['script'] and 'Ready' in recipe['script'], built
            offline_calls.append(name)
    assert not problems, problems
    if verify_documents:
        docs, offset = [], 0
        while True:
            page = unwrap_usage(call('GetToolUsage', {'offset': offset, 'limit': 40}))
            docs.extend(page['matches'])
            offset = page['nextOffset']
            if offset is None:
                break
        assert len(docs) == first['documentCount'] == len({d['id'] for d in docs})
        for doc in docs:
            lines, offset = [], 0
            while True:
                page = unwrap_usage(call('GetToolUsage', {'documentId': doc['id'], 'offset': offset, 'limit': 100}))
                lines.extend(page['lines'])
                offset = page['nextOffset']
                if offset is None:
                    break
            text = '\n'.join(lines)
            assert hashlib.sha256(text.encode()).hexdigest() == doc['sha256'], doc['id']
        ids = {d['id'] for d in docs}
        assert all(set(r['documents']) <= ids for r in records)
        library = first['exampleLibrary']
        assert len(library['languages']) >= 12
        for language in library['languages']:
            selected_library = unwrap_usage(call('GetToolUsage', {'language': language['id']}))
            assert selected_library['examples'] or selected_library.get('sourceDocuments'), language
            assert set(language['tools']) <= set(names)
        for summary in library['examples']:
            detail = unwrap_usage(call('GetToolUsage', {'exampleId': summary['id']}))['examples']
            assert len(detail) == 1 and detail[0]['id'] == summary['id']
            example = detail[0]
            assert example.get('files') or example.get('steps'), summary['id']
            for file in example.get('files', []):
                assert hashlib.sha256(file['content'].encode()).hexdigest() == file['contentSha256']
            for step in example.get('steps', []):
                if not example['profileMatches'] or not example['releaseMatches']:
                    continue
                target = schemas[step['tool']]
                values = step['arguments']
                assert set(target.get('required', [])) <= set(values) <= set(target['properties']), (summary['id'], step)
            if example['profileMatches'] and example['releaseMatches']:
                if example['id'] == 'sequence/foundation-tag-table-round-trip':
                    assert example['steps'][-1]['tool'] == 'ListPlcTags'
                    assert 'tag added after export is gone' in example['steps'][-1]['expect']
                elif example['id'] == 'sequence/foundation-block-round-trip':
                    assert example['steps'][-1]['tool'] == 'CompilePlcSoftware'
                    assert example['steps'][-1]['arguments']['dryRun'] is False
                    assert 'remove or move an old Main.xml' in example['preconditions']
                elif example['id'] == 'sequence/foundation-approval-precheck':
                    assert all('no Workbench request' in step['expect'] for step in example['steps'][1:])
                elif example['id'] == 'sequence/plc-xml-round-trip':
                    assert all('overwrite' not in step['arguments'] or 'overwrite' in schemas[step['tool']]['properties'] for step in example['steps'])
            if example['id'] in ('udt-builder-json', 'db-builder-json') and str(release) in ('20', '21'):
                data = json.loads(example['files'][0]['content'])['json']
                tool, argument = ('BuildPlcUdt', 'udt') if example['id'] == 'udt-builder-json' else ('BuildPlcGlobalDb', 'globalDb')
                built = call(tool, {argument: data, 'outputReleaseKey': str(release)})
                xml = built['data']['xml'] if built.get('schemaVersion') == 4 else built['xml']
                root = ET.fromstring(xml)
                assert any(e.tag.endswith('Name') and e.text == (data.get('udtName') or data.get('dbName') or data.get('name')) for e in root.iter()), example['id']
                builder_examples += 1
    return {'releaseKey': str(release), 'registeredToolCount': len(names), 'checkedToolCount': len(records),
            'documentCount': first['documentCount'], 'allSourceTextRetrievedAndHashChecked': verify_documents,
            'operationExampleCount': sum(len(r['operations']) for r in records),
            'programmingExampleCount': sum(not e['id'].startswith('sequence/') for e in first['exampleLibrary']['examples']),
            'callSequenceCount': sum(e['id'].startswith('sequence/') for e in first['exampleLibrary']['examples']),
            'offlineBuilderExamplesExecuted': builder_examples,
            'offlineCallExamplesExecuted': offline_calls,
            'exampleCallsExecuted': False, 'tools': records}
