"""Deterministic merge-profile and latency-gate proofs; no real waits or services."""
import argparse
import importlib.util
import json
from pathlib import Path
from offline_fixtures import fixture_directory
import unittest
from unittest.mock import patch
from call_latency import Samples, promote, RELEASES


def load(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class SpeedupTests(unittest.TestCase):
    def test_promotion_requires_complete_formal_evidence_and_preserves_bytes(self):
        with fixture_directory('speedup-test-') as directory:
            root = Path(directory)
            candidate, destination = root / 'candidate.json', root / 'baseline.json'
            for producer in ('transport', 'responses'):
                for profile in ('merge', 'full'):
                    samples = Samples(producer, profile)
                    for release in RELEASES:
                        for transport in ('stdio', 'http'):
                            if producer == 'responses' and transport == 'http' and release not in ('20', '21'):
                                continue
                            samples.record(release, transport, .001)
                    samples.finish(root / 'evidence.json', candidate, 'a' * 40)
            self.assertEqual(52, promote(candidate, 'a' * 40, destination))
            self.assertEqual(candidate.read_bytes(), destination.read_bytes())
            with self.assertRaisesRegex(ValueError, 'Invalid formal producer'):
                promote(candidate, 'b' * 40, destination)
            data = json.loads(candidate.read_text('utf-8'))
            del data['samples']['transport/21/http/full']
            candidate.write_text(json.dumps(data), encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'all 52'):
                promote(candidate, 'a' * 40, destination)

    def test_latency_producer_records_medians_and_eight_ms_regression_fails_for_every_transport(self):
        with fixture_directory('speedup-test-') as directory:
            root = Path(directory)
            for producer in ('transport', 'responses'):
                for profile in ('merge', 'full'):
                    samples = Samples(producer, profile)
                    for release in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21'):
                        for transport in ('stdio', 'http'):
                            for seconds in (.001, .002, .003):
                                samples.record(release, transport, seconds)
                    baseline = root / 'baseline.json'
                    samples.finish(root / 'record.json', baseline, 'a' * 40)
                    samples.finish(root / 'passed.json', baseline)
                    self.assertTrue(all(row['medianMilliseconds'] == 2 for row in json.loads(baseline.read_text())['samples'].values()))
                    regressed = Samples(producer, profile)
                    for key in samples.values:
                        _, release, transport, _ = key.split('/')
                        regressed.record(release, transport, .010)
                    with self.assertRaisesRegex(ValueError, 'exceeds master gate'):
                        regressed.finish(root / 'failed.json', baseline)
                    self.assertEqual(16, len(json.loads((root / 'failed.json').read_text())['errors']))

    def test_missing_baseline_sample_is_a_failure(self):
        with fixture_directory('speedup-test-') as directory:
            root = Path(directory)
            first = Samples('transport')
            first.record('20', 'stdio', .001)
            first.finish(root / 'record.json', root / 'baseline.json', 'a' * 40)
            changed = Samples('transport')
            changed.record('21', 'stdio', .001)
            with self.assertRaisesRegex(ValueError, 'Missing master'):
                changed.finish(root / 'missing.json', root / 'baseline.json')

    def test_large_master_median_still_rejects_eight_ms_regression(self):
        with fixture_directory('speedup-test-') as directory:
            root = Path(directory)
            first = Samples('transport')
            first.record('21', 'http', .040)
            first.finish(root / 'record.json', root / 'baseline.json', 'a' * 40)
            changed = Samples('transport')
            changed.record('21', 'http', .048)
            with self.assertRaisesRegex(ValueError, 'exceeds master gate'):
                changed.finish(root / 'failed.json', root / 'baseline.json')

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
                    releases=['21'], packaged_no_tia=False, output=root / 'responses', transport='stdio', check_profile=profile,
                    latency_baseline=root / 'latency.json', record_latency_baseline='a' * 40)
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
                releases=['21'], packaged_no_tia=False, output=root / 'responses', transport='stdio', check_profile='full',
                latency_baseline=root / 'latency.json', record_latency_baseline='a' * 40)
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
