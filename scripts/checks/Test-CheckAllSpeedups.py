"""Deterministic merge-profile and report-only latency proofs; no real waits or services."""
import argparse
import importlib.util
import json
import sys
from pathlib import Path
from offline_fixtures import fixture_directory
import unittest
from unittest.mock import patch
from call_latency import Samples


def load(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class SpeedupTests(unittest.TestCase):
    def test_medians_are_report_only_for_large_calls_and_empty_samples(self):
        with fixture_directory('speedup-test-') as directory:
            samples = Samples('responses', 'merge')
            for seconds in (1000, 3000, 2000):
                samples.record('21', 'stdio', seconds)
            output = Path(directory) / 'latency.json'
            evidence = samples.finish(output)
            self.assertFalse(evidence['gated'])
            self.assertEqual([], evidence['errors'])
            self.assertEqual({'calls': 3, 'medianMilliseconds': 2000000}, evidence['samples']['responses/21/stdio/merge'])
            self.assertEqual(evidence, json.loads(output.read_text(encoding='utf-8')))
            self.assertEqual({}, Samples('transport').finish(output)['samples'])

    def test_capture_producers_record_latency_regressions_without_gating(self):
        with fixture_directory('speedup-test-') as directory:
            root = Path(directory)
            responses = load('Snapshot-ToolResponses')
            args = argparse.Namespace(repo_root=root, public_api_root=root, exe=[], engine_host=None,
                releases=['21'], packaged_no_tia=False, output=root / 'responses', transport='stdio', check_profile='merge')
            def capture(*_):
                responses.LATENCIES.record('21', 'stdio', 1000)
                return {'calls': [], 'coverage': {'registeredTools': 1, 'directRejectedTools': [], 'bridgeRejectedTools': []}}
            with patch.object(responses, 'capture_release', side_effect=capture):
                self.assertEqual(0, responses.capture(args))
            transport = load('Test-FoundationTransport')
            def exercise(*_):
                transport.LATENCIES.record('21', 'stdio', 1000)
                return {}
            with patch.object(sys, 'argv', ['test', '--plc-worker', '--releases', '21', '--stdio-only',
                    '--check-profile', 'merge', '--temp-root', str(root / 'transport-temp'),
                    '--output', str(root / 'transport')]), \
                    patch.object(transport, 'plc_transports', side_effect=exercise):
                transport.main()
            for path in (root / 'responses-latency-21.json', root / 'transport/call-latency.json'):
                evidence = json.loads(path.read_text('utf-8'))
                self.assertFalse(evidence['gated'])
                self.assertEqual(1000000, next(iter(evidence['samples'].values()))['medianMilliseconds'])

    def test_merge_omits_only_the_stdio_usage_walk_and_full_keeps_it(self):
        module = load('Test-FoundationTransport')
        class Client:
            def call(self, method, params):
                return {'instructions': 'GetToolUsage'} if method == 'initialize' else {'tools': []}
        class HttpClient(module.Http):
            def __init__(self):
                pass
            call = Client.call
        for profile in ('merge', 'full'):
            for http in (False, True):
                with self.subTest(profile=profile, http=http), patch.object(module, 'check_usage') as usage:
                    module.CHECK_PROFILE = profile
                    # Stop after discovery with an intentionally empty catalog; no RPC or file work follows.
                    with self.assertRaises(AssertionError):
                        module.exercise(HttpClient() if http else Client(), '19', Path('unused'), 0)
                    self.assertEqual(int(profile == 'full' or http), usage.call_count)

    def test_merge_response_capture_runs_once_and_full_runs_twice(self):
        module = load('Snapshot-ToolResponses')
        for profile, count in (('merge', 1), ('full', 2)):
            with self.subTest(profile=profile), fixture_directory('speedup-test-') as directory:
                root = Path(directory)
                args = argparse.Namespace(repo_root=root, public_api_root=root, exe=[], engine_host=None,
                    releases=['21'], packaged_no_tia=False, output=root / 'responses', transport='stdio', check_profile=profile)
                def capture(*_):
                    module.LATENCIES.record('21', 'stdio', .001)
                    return {'calls': [], 'coverage': {'registeredTools': 1, 'directRejectedTools': [], 'bridgeRejectedTools': []}}
                with patch.object(module, 'capture_release', side_effect=capture) as producer:
                    self.assertEqual(0, module.capture(args))
                    self.assertEqual(count, producer.call_count)
                self.assertTrue((root / 'responses/21.json').exists())

    def test_merge_cannot_regenerate_response_baselines(self):
        module = load('Snapshot-ToolResponses')
        with fixture_directory('speedup-test-') as directory:
            root = Path(directory)
            args = argparse.Namespace(repo_root=root, check_profile='merge', transport='stdio',
                                      output=root / 'manifest/contracts/v4/responses')
            with self.assertRaisesRegex(ValueError, 'full two-capture'):
                module.capture(args)
            self.assertFalse(args.output.exists())

    def test_full_response_capture_still_rejects_nondeterminism(self):
        module = load('Snapshot-ToolResponses')
        with fixture_directory('speedup-test-') as directory:
            root = Path(directory)
            args = argparse.Namespace(repo_root=root, public_api_root=root, exe=[], engine_host=None,
                releases=['21'], packaged_no_tia=False, output=root / 'responses', transport='stdio', check_profile='full')
            with patch.object(module, 'capture_release', side_effect=[{'calls': [], 'changed': 1}, {'calls': [], 'changed': 2}]):
                with self.assertRaisesRegex(ValueError, 'consecutive captures differ'):
                    module.capture(args)
            self.assertFalse((root / 'responses/21.json').exists())

    def test_http_isolation_proves_catalog_sid_and_lifecycle_without_usage_walk(self):
        module = load('Test-FoundationTransport')
        class Client:
            def __init__(self, sid):
                self.sid, self.calls, self.closed = sid, [], False
                self._tool_inventory = [{'name': name} for name in ('ConnectPortal', 'GetProjectTree', 'DisconnectPortal')]
            def call(self, method, params):
                self.calls.append((method, params))
                if method == 'initialize':
                    return {'capabilities': {'tools': {}, 'resources': {}, 'prompts': {}}}
                if method == 'tools/list':
                    return {'tools': self._tool_inventory}
                name = params['name']
                data = {'ConnectPortal': {'pid': 31415}, 'GetProjectTree': {'name': '生产线'},
                        'DisconnectPortal': {'workerAcknowledged': True}}[name]
                body = {'schemaVersion': 4, 'ok': True, 'error': None, 'data': data,
                        'meta': {'tool': name, 'outcome': 'succeeded'}}
                return {'content': [{'text': json.dumps(body)}], 'structuredContent': body}
            def close(self):
                self.closed = True
        first, second = Client('first'), Client('second')
        self.assertEqual(3, module.exercise_isolation(second, first, '19'))
        self.assertTrue(second.closed)
        self.assertEqual(['ConnectPortal', 'GetProjectTree', 'DisconnectPortal'],
                         [params['name'] for method, params in second.calls if method == 'tools/call'])
        same = Client('first')
        with self.assertRaises(AssertionError):
            module.exercise_isolation(same, first, '19')
        changed = Client('different')
        changed._tool_inventory.append({'name': 'Unexpected'})
        with self.assertRaisesRegex(AssertionError, 'catalogs differ'):
            module.exercise_isolation(changed, first, '19')


if __name__ == '__main__':
    unittest.main()
