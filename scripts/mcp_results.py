"""Strict V4 result decoding shared by first-party MCP script clients."""
import json
import unittest


def envelope(reply):
    """Accept a JSON-RPC reply, CallToolResult, or already decoded V4 envelope."""
    result = None
    if not isinstance(reply, dict): raise ValueError('Expected an object result')
    if 'schemaVersion' in reply:
        value = reply
    else:
        if 'error' in reply:
            raise ValueError('JSON-RPC error: ' + json.dumps(reply['error']))
        result = reply.get('result', reply)
        if not isinstance(result, dict): raise ValueError('Expected a CallToolResult object')
        value = result.get('structuredContent')
        texts = [block['text'] for block in result.get('content', []) if block.get('type') == 'text']
        if value is None:
            if len(texts) != 1:
                raise ValueError('Expected structuredContent or one V4 TextContent')
            value = json.loads(texts[0])
        elif texts and (len(texts) != 1 or json.loads(texts[0]) != value):
            raise ValueError('V4 structuredContent/text mismatch')
    if (not isinstance(value, dict) or value.get('schemaVersion') != 4 or type(value.get('ok')) is not bool
            or not all(key in value for key in ('data', 'error', 'meta'))
            or not isinstance(value['meta'], dict)
            or (value['ok'] and value['error'] is not None)
            or (not value['ok'] and not isinstance(value['error'], dict))):
        raise ValueError('Invalid V4 envelope')
    if result is not None and bool(result.get('isError')) != (value['ok'] is False):
        raise ValueError('V4 isError/ok mismatch')
    return value


def successful(reply):
    value = envelope(reply)
    if not value['ok']:
        raise ValueError('Tool failed: ' + json.dumps(value['error']))
    return value


class ResultTests(unittest.TestCase):
    def test_structured_and_text(self):
        value = {'schemaVersion': 4, 'ok': True, 'data': {'items': []}, 'error': None, 'meta': {}}
        for result in ({'structuredContent': value}, {'content': [{'type': 'text', 'text': json.dumps(value)}]},
                       {'structuredContent': value, 'content': [{'type': 'text', 'text': json.dumps(value)}]}):
            self.assertEqual(successful({'result': result}), value)

    def test_failures_and_malformed_results(self):
        value = {'schemaVersion': 4, 'ok': False, 'data': None,
                 'error': {'code': 'OUTCOME_UNKNOWN'}, 'meta': {'outcome': 'unknown'}}
        self.assertEqual(envelope({'isError': True, 'structuredContent': value}), value)
        with self.assertRaises(ValueError): successful(value)
        for result in ({'meta': {'success': True}}, {'error': {'code': -32602}},
                       {'structuredContent': value}, {'content': []},
                       {'structuredContent': dict(value, ok=True), 'isError': True}):
            with self.subTest(result=result), self.assertRaises(ValueError): envelope(result)

    def test_batch_items_keep_each_envelope(self):
        item = {'schemaVersion': 4, 'ok': False, 'data': None,
                'error': {'code': 'NOT_EXECUTED'}, 'meta': {'execution': 'not-started'}}
        value = dict(item, data={'items': [{'index': 0, 'target': 'SaveProject', 'result': item}]})
        self.assertFalse(envelope(value)['data']['items'][0]['result']['ok'])


if __name__ == '__main__':
    unittest.main()
