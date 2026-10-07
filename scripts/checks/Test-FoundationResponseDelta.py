"""Prove P6-64 response changes are example-library updates and typed recovery warnings only."""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path


def key(call):
    return call['profile'], call['tool'], json.dumps(call['arguments'], sort_keys=True)


def strip_guidance(value):
    if isinstance(value, list):
        return [strip_guidance(item) for item in value]
    if not isinstance(value, dict):
        return value
    result = {k: strip_guidance(v) for k, v in value.items()}
    if isinstance(result.get('meta'), dict) and isinstance(result['meta'].get('warnings'), list):
        result['meta']['warnings'] = [w for w in result['meta']['warnings'] if w.get('code') != 'RECOVERY_GUIDANCE']
    return result


def strip_raw(text):
    # Remove only the appended JSON warning object, preserving every other raw byte.
    marker = '{"code":"RECOVERY_GUIDANCE"'
    while marker in text:
        start = text.index(marker)
        depth, quoted, escaped, end = 0, False, False, None
        for index in range(start, len(text)):
            char = text[index]
            if quoted:
                if escaped:
                    escaped = False
                elif char == '\\':
                    escaped = True
                elif char == '"':
                    quoted = False
            elif char == '"':
                quoted = True
            elif char in '{[':
                depth += 1
            elif char in '}]':
                depth -= 1
                if depth == 0:
                    end = index + 1
                    break
        assert end is not None
        if start > 0 and text[start - 1] == ',':
            start -= 1
        text = text[:start] + text[end:]
    return text


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--before-hints', type=Path, required=True)
    parser.add_argument('--current', type=Path, required=True)
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location('responses', Path(__file__).with_name('Snapshot-ToolResponses.py'))
    responses = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(responses)
    changed_usage = {'CompilePlcDiagnostics', 'DeletePlcExternalSource', 'GenerateBlocksFromExternalSource',
                     'GetPlcBlockInfo', 'GetProjectTree', 'GetSessionState', 'ImportPlcExternalSource'}
    total, hints, examples = 0, 0, 0
    for release in responses.RELEASES:
        load = lambda directory: json.loads((directory / (release + '.json')).read_text('utf-8'))
        baseline, before, current = load(args.baseline), load(args.before_hints), load(args.current)
        assert {k: v for k, v in baseline.items() if k != 'calls'} == {k: v for k, v in current.items() if k != 'calls'}, release
        old = {key(c): c for c in baseline['calls']}
        intermediate = {key(c): c for c in before['calls']}
        assert old.keys() == intermediate.keys() == {key(c) for c in current['calls']}, release
        usage_examples = {}
        for call in current['calls']:
            if call['tool'] == 'GetToolUsage' and call['arguments'].get('toolName'):
                reply = responses.validated_evidence(call)['response'].get('result', {})
                body = reply.get('structuredContent')
                if not isinstance(body, dict):
                    body = next((b['text'] for b in reply.get('content', []) if isinstance(b.get('text'), dict)), {})
                example = body.get('data', {}).get('example')
                if example:
                    usage_examples[(call['arguments']['toolName'], call['arguments'].get('operation', ''))] = example
        for identity, call in intermediate.items():
            if call != old[identity]:
                query = call['arguments']
                assert call['tool'] == 'GetToolUsage' and release in ('20', '21'), identity
                assert (set(query) <= {'limit', 'offset'} or query.get('toolName') in changed_usage), identity
                examples += 1
        for call in current['calls']:
            evidence = responses.validated_evidence(call)
            response = evidence['response']
            blocks = response.get('result', {}).get('content', [])
            envelope = response.get('result', {}).get('structuredContent')
            if not isinstance(envelope, dict):
                envelope = next((b['text'] for b in blocks if isinstance(b.get('text'), dict) and 'meta' in b['text']), {})
            guidance = [w for w in envelope.get('meta', {}).get('warnings', []) if w.get('code') == 'RECOVERY_GUIDANCE']
            code = envelope.get('error', {}).get('code') if isinstance(envelope.get('error'), dict) else None
            supported = code in ('INVALID_ARGUMENT', 'PRECONDITION_FAILED', 'SESSION_RESET_REQUIRED', 'CONFIRMATION_REQUIRED')
            assert len(guidance) == (1 if supported else 0), (release, key(call), code)
            if guidance:
                details = guidance[0]['details']
                assert details['releaseKey'] == release
                assert details['getToolUsage']['toolName'] == envelope['meta']['tool']
                if details['exampleArguments'] is not None:
                    assert len(json.dumps(details['exampleArguments'], ensure_ascii=True, separators=(',', ':')).encode()) <= 4096
                    query = details['getToolUsage']
                    selected = usage_examples.get((query['toolName'], query.get('operation', '')))
                    if selected:
                        assert details['exampleArguments'] == selected['request']['params']['arguments'], (release, query, 'example differs from GetToolUsage')
                hints += 1
            raw = [dict(contentIndex=b['contentIndex'], sha256=hashlib.sha256(strip_raw(b['text']).encode()).hexdigest())
                   for b in evidence['rawText']]
            rebuilt = responses.compact(dict(profile=call['profile'], tool=call['tool'], arguments=call['arguments'],
                response=strip_guidance(copy.deepcopy(response)), rawTextBlocks=raw))
            assert rebuilt == intermediate[key(call)], (release, key(call), responses.first_difference(rebuilt, intermediate[key(call)]))
            total += 1
        print(f'PASS V{release}: original responses/raw bytes retained after removing only RECOVERY_GUIDANCE')
    print(f'PASS: {total} response calls; {hints} typed recovery hints; {examples} intended usage-example changes; 0 other changes')


if __name__ == '__main__':
    main()
