"""Contract/transport checks for AI-facing usage, without executing example calls."""
import hashlib
import json


def unwrap_usage(reply):
    body = reply.get('meta', reply)
    assert body.get('success'), reply
    return body['usage']


def check_usage(call, tools, release, exhaustive=True, verify_documents=False):
    """call returns the decoded MCP text envelope. Never call the sampled tool."""
    assert all('GetToolUsage' in t['description'] for t in tools), 'A listed tool has no usage route'
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
    selected = names if exhaustive else ['GetToolUsage', 'ManageStartdriveParameter']
    for name in selected:
        usage = unwrap_usage(call('GetToolUsage', {'toolName': name}))
        assert usage['available'] and usage['releaseKey'] == str(release) and usage['toolName'] == name
        schema = usage['inputSchema']
        if name in schemas:
            assert schema == schemas[name], name + ': differs from tools/list'
        example = usage['example']
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
        assert usage['workflow'] and usage['onFailure'] and 'NOT RUN' in usage['nativeAcceptance']
        records.append({'toolName': name, 'exampleKind': example['kind'], 'relationship': reference['relationship'], 'documents': reference['documents']})
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
    return {'releaseKey': str(release), 'registeredToolCount': len(names), 'checkedToolCount': len(records),
            'documentCount': first['documentCount'], 'allSourceTextRetrievedAndHashChecked': verify_documents,
            'exampleCallsExecuted': False, 'tools': records}
