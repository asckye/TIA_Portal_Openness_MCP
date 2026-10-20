"""No-TIA regression tests for script successor wiring; every process launch is mocked."""
import argparse
import ast
from contextlib import contextmanager
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

from offline_fixtures import fixture_directory

ROOT = Path(__file__).resolve().parents[2]


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class LaunchIntercepted(Exception):
    pass


class PlcScriptSuccessorTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixture_directory('plc-script-selftest-')
        self.root = Path(self.fixture.__enter__())
        self.addCleanup(self.fixture.__exit__, None, None, None)
        self.host = self.root / 'TiaMcp.FoundationHost.exe'
        self.host.write_bytes(b'host identity')
        self.worker = self.root / 'TiaMcp.PlcWorker.21.exe'
        self.worker.write_bytes(b'worker identity')
        self.api_root = self.root / 'api'
        for key in ('20', '21'):
            api = self.api_root / f'TIA_V{key}_PublicAPI' / f'V{key}'
            if key == '21':
                api /= 'net48'
            api.mkdir(parents=True)
            (api / 'Siemens.Engineering.dll').write_bytes(b'fixture SDK')
        self.api = self.api_root / 'TIA_V21_PublicAPI/V21/net48'

    def test_shared_helper_launches_both_transports_and_hashes_the_worker(self):
        resources = load('Test-ResourceDiscovery')
        for key in (20, 21):
            for transport in ('stdio', 'http'):
                for profile in ('full', 'lite'):
                    command = resources.host_command(self.host, self.root, key, transport, profile, self.api,
                                                     plc_worker=self.worker, port=1234, key='private-key')
                    self.assertEqual(command[0], str(self.host))
                    for option, value in (('--release-key', str(key)), ('--worker-exe', str(self.worker)),
                                          ('--public-api', str(self.api)), ('--profile', profile), ('--transport', transport)):
                        self.assertEqual(command[command.index(option) + 1], value)
                    self.assertIn('--plc-worker', command)
                    self.assertNotIn('--engine-worker', command)
                    self.assertNotIn('--engine-catalog', command)
        evidence = resources.plc_fixture_evidence(self.worker, self.host)
        self.assertEqual(evidence, {'hostSha256': hashlib.sha256(self.host.read_bytes()).hexdigest(),
            'sdkWorkerSha256': hashlib.sha256(self.worker.read_bytes()).hexdigest(),
            'runtimeSha256': hashlib.sha256(self.worker.read_bytes()).hexdigest(), 'workerModel': 'foundation-host-plc-worker'})
        catalog = self.root / 'tool-catalog.json'
        catalog.write_text('{}', encoding='utf-8')
        command = resources.host_command(self.host, None, 21, 'stdio', 'full',
                                        engine_worker=self.worker, engine_catalog=catalog)
        self.assertIn('--engine-worker', command)
        self.assertIn('--engine-catalog', command)
        self.assertNotIn('--plc-worker', command)
        with self.assertRaises(AssertionError):
            resources.host_command(self.host, None, 21, 'stdio', 'full', self.api,
                                   engine_worker=self.worker, engine_catalog=catalog, plc_worker=self.worker)

    def test_helper_readiness_and_cleanup_share_the_existing_context(self):
        resources = load('Test-ResourceDiscovery')
        launches = []
        class Process:
            def __init__(self, command, **kwargs):
                self.args = command
                self.stdin, self.stdout, self.stderr = io.StringIO(), io.StringIO(), io.StringIO()
                launches.append(dict(kwargs, command=command))
            def wait(self, **kwargs):
                return 0
        for ready in (False, True):
            with patch.object(subprocess, 'Popen', Process):
                with resources.server(self.host, self.api, 21, 'stdio', 'full', public_api=self.api,
                                      plc_worker=self.worker, sdk_ready=ready):
                    pass
            self.assertEqual(launches[-1]['env']['TIA_MCP_PLC_WORKER_SDK_READY'], '1' if ready else '0')
            self.assertNotIn('TIA_MCP_ENGINE_WORKER_SDK_READY', launches[-1]['env'])
            self.assertIn('--tia-portal-location', launches[-1]['command'])
        # The six old releases' synthetic worker takes exactly the protocol-2 arguments: no staged installation.
        with patch.object(subprocess, 'Popen', Process):
            with resources.server(self.host, None, '14sp1', 'stdio', 'full', public_api=self.root,
                                  plc_worker=self.worker, sdk_ready=False, sdk_installation=False):
                pass
        self.assertNotIn('--tia-portal-location', launches[-1]['command'])
        command = launches[-1]['command']
        self.assertEqual(command[command.index('--public-api') + 1], str(self.root.resolve()))

    def test_stability_and_resources_forward_the_explicit_worker(self):
        for script in ('Test-LocalStability', 'Test-ResourceDiscovery'):
            module = load(script)
            resources = module.resources if hasattr(module, 'resources') else module
            argv = [script, '--exe', str(self.host), '--public-api', str(self.api), '--major', '21',
                    '--plc-worker', str(self.worker)]
            argv += ['--output', str(self.root / script)] if script.endswith('Stability') else ['--portal-root', str(self.api)]
            with patch.object(sys, 'argv', argv), patch.object(resources, 'server', side_effect=LaunchIntercepted) as server:
                with self.assertRaises(LaunchIntercepted):
                    module.main()
            self.assertEqual(server.call_args.kwargs['plc_worker'], self.worker)
            self.assertIsNone(server.call_args.kwargs['engine_worker'])

    def test_stability_evidence_preserves_bounds_and_rekeys_runtime(self):
        module = load('Test-LocalStability')
        output = self.root / 'stability'
        argv = ['stability', '--exe', str(self.host), '--public-api', str(self.api), '--major', '21',
                '--plc-worker', str(self.worker), '--output', str(output), '--transports', 'stdio', '--profiles', 'full']
        run = {'measuredToolCalls': 1, 'warmupToolCalls': 1, 'recoveryToolCalls': 1}
        with patch.object(sys, 'argv', argv), patch.object(module, 'run_profile', return_value=run):
            module.main()
        evidence = json.loads((output / 'result.json').read_text('utf-8'))
        self.assertEqual(evidence['runtimeSha256'], hashlib.sha256(self.worker.read_bytes()).hexdigest())
        self.assertEqual(evidence['hostSha256'], hashlib.sha256(self.host.read_bytes()).hexdigest())
        self.assertEqual(evidence['rounds'], 50)
        self.assertEqual(evidence['bounds'], {'maxPrivateMiB': 512, 'maxHandleGrowth': 128})

    def test_approval_keeps_seven_checks_and_uses_the_plc_evidence(self):
        module = load('Test-ReleaseApprovalGate')
        output = self.root / 'approval'
        argv = ['approval', '--product', 'engine', '--major', '21', '--exe', str(self.host),
                '--plc-worker', str(self.worker), '--temp-root', str(self.root), '--output', str(output)]
        report = {'product': 'engine', 'major': 21, 'checksPassed': 7, 'checksExpected': 7, 'results': {}}
        with patch.object(sys, 'argv', argv), patch.object(module, 'run_engine', return_value=report) as run:
            module.main()
        self.assertEqual(run.call_args.args[0].plc_worker, self.worker)
        self.assertEqual(report['checksExpected'], 7)
        self.assertEqual(report['runtimeSha256'], hashlib.sha256(self.worker.read_bytes()).hexdigest())
        args = argparse.Namespace(exe=self.host, portal_root=self.api, public_api=self.api, host_harness=None,
            plc_worker=self.worker, engine_worker=None, engine_catalog=None, temp_root=self.root, major=21)
        with patch.object(module.resources, 'server', side_effect=LaunchIntercepted) as server:
            with self.assertRaises(LaunchIntercepted):
                module.run_engine(args)
        self.assertEqual(server.call_args.kwargs['plc_worker'], self.worker)

    def test_campaign_uses_served_schema_and_the_kept_validator_without_a_harness(self):
        module = load('Test-CampaignInputs')
        argv = ['campaign', '--exe', str(self.host), '--public-api', str(self.api), '--major', '21',
                '--plc-worker', str(self.worker), '--output', str(self.root / 'campaign')]
        with patch.object(sys, 'argv', argv), patch.object(module.resources, 'server', side_effect=LaunchIntercepted) as server:
            with self.assertRaises(LaunchIntercepted):
                module.main()
        self.assertEqual(server.call_args.kwargs['plc_worker'], self.worker)
        command = module.validator_command(21, 'catalog', 'inputs', 'result')
        self.assertEqual(command[-4:], ['21', 'catalog', 'inputs', 'result'])
        self.assertIn('--no-build', command)
        self.assertTrue(any(str(arg).endswith('Test-CampaignInputs.cs') for arg in command))
        # check-all isolates TEMP per job; the validator must still find the program compiled below the original TEMP.
        with patch.dict(os.environ, {'TIA_MCP_CHECK_SYSTEM_TEMP_ROOT': str(self.root), 'TEMP': 'job-temp', 'TMP': 'job-temp'}):
            environment = module.validator_environment()
        self.assertEqual((environment['TEMP'], environment['TMP']), (str(self.root), str(self.root)))
        with patch.object(sys, 'argv', ['campaign', '--validator-self-test']), \
                patch.object(module.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0)) as run:
            self.assertEqual(module.main(), 0)
        self.assertEqual(run.call_args.args[0][-2:], ['--', '--self-test'])
        self.assertIn('--no-build', run.call_args.args[0])

    def test_usage_forwards_plc_workers_and_keeps_full_profile(self):
        module = load('Test-ToolUsage')
        snapshots = module.helper('Snapshot-ToolResponses')
        argv = ['usage', '--foundation-host', str(self.host), '--plc-worker', '21=' + str(self.worker),
                '--public-api-root', str(self.api_root), '--releases', '21', '--evidence', str(self.root / 'usage'),
                '--coverage-output', str(self.root / 'usage.json')]
        @contextmanager
        def fake_server(*args, **kwargs):
            yield None, None, None
        record = {'releaseKey': '21', 'checkedToolCount': 1, 'operationExampleCount': 1}
        with patch.object(sys, 'argv', argv), patch.object(module, 'helper', return_value=snapshots), \
                patch.object(snapshots.resources, 'server', side_effect=fake_server) as server, \
                patch.object(snapshots, 'initialize', return_value=[]), patch.object(module, 'check_usage', return_value=record):
            module.main()
        self.assertEqual(server.call_args.args[4], 'full')
        self.assertEqual(server.call_args.kwargs['plc_worker'], self.worker)
        receipt = json.loads((self.root / 'usage.json').read_text('utf-8'))[0]
        self.assertEqual(receipt['runtimeSha256'], hashlib.sha256(self.worker.read_bytes()).hexdigest())

    def test_contract_and_response_capture_reach_the_shared_plc_launcher(self):
        for name in ('Snapshot-ToolContracts', 'Snapshot-ToolResponses'):
            module = load(name)
            argv = [name, 'capture', '--repo-root', str(ROOT), '--public-api-root', str(self.api_root),
                '--releases', '21', '--engine-host', str(self.host), '--exe', '21=' + str(self.host),
                '--plc-worker', '21=' + str(self.worker), '--output', str(self.root / name)]
            if name.endswith('Responses'):
                argv += ['--temp-root', str(self.root)]
            def intercept(command, **kwargs):
                self.assertEqual(command[0], str(self.host))
                self.assertIn('--plc-worker', command)
                self.assertEqual(command[command.index('--worker-exe') + 1], str(self.worker))
                self.assertEqual(kwargs['env']['TIA_MCP_PLC_WORKER_SDK_READY'], '1')
                raise LaunchIntercepted
            with patch.object(sys, 'argv', argv), patch.object(subprocess, 'Popen', side_effect=intercept) as launch, \
                    patch('tempfile.gettempdir', return_value=str(self.root)):
                with self.assertRaises(LaunchIntercepted):
                    module.main()
            self.assertEqual(launch.call_count, 1)

    def test_ecosystem_and_evidence_wiring_is_shared(self):
        module = load('Test-V21Ecosystem')
        def decode(*args, **kwargs):
            name = Path(json.loads(kwargs['input'])['filePath']).stem
            value = {'success': True, 'readableScl': 'Input Output', 'reimportable': False,
                     'nativeFormatQualified': False, 'dataComplete': False, 'hasWarnings': True}
            return subprocess.CompletedProcess([], 0 if name == 'Demo' else 1, stdout=json.dumps(value))
        argv = ['ecosystem', '--exe', str(self.host), '--public-api', str(self.api), '--major', '21',
                '--host-harness', str(self.host), '--plc-worker', str(self.worker), '--schema-root', str(self.root),
                '--output', str(self.root / 'ecosystem')]
        with patch.object(sys, 'argv', argv), patch.object(subprocess, 'run', side_effect=decode), \
                patch.object(module.resources, 'server', side_effect=LaunchIntercepted) as server:
            with self.assertRaises(LaunchIntercepted):
                module.main()
        self.assertEqual(server.call_args.kwargs['plc_worker'], self.worker)
        argv += ['--self-test']
        with patch.object(sys, 'argv', argv), patch.object(subprocess, 'run', side_effect=decode):
            module.main()
        result = next((self.root / 'ecosystem').rglob('result.json'))
        evidence = json.loads(result.read_text('utf-8'))
        self.assertEqual(evidence['runtimeSha256'], hashlib.sha256(self.worker.read_bytes()).hexdigest())
        self.assertEqual(evidence['hostSha256'], hashlib.sha256(self.host.read_bytes()).hexdigest())
        # Inspect actual call expressions, rather than run the decoder subprocesses.
        for name in ('Test-V21Ecosystem', 'Test-LocalStability', 'Test-ReleaseApprovalGate', 'Test-CampaignInputs'):
            tree = ast.parse(Path(__file__).with_name(name + '.py').read_text('utf-8'))
            calls = [node for node in ast.walk(tree) if isinstance(node, ast.Call)]
            self.assertTrue(any(isinstance(call.func, ast.Attribute) and call.func.attr == 'server'
                and any(kw.arg == 'plc_worker' and ast.unparse(kw.value) == 'args.plc_worker' for kw in call.keywords) for call in calls), name)
            self.assertTrue(any(isinstance(call.func, ast.Attribute) and call.func.attr == 'plc_fixture_evidence'
                                for call in calls), name)

    def test_transport_all_eight_releases_share_selection_and_profile_checks(self):
        module = load('Test-FoundationTransport')
        args = argparse.Namespace(plc_sdk_worker=['20=' + str(self.worker), '21=' + str(self.worker)],
                                  public_api_root=self.api_root, fixture=self.host)
        for key in module.KEYS:
            worker, api, full = module.plc_transport_inputs(args, key, self.root)
            self.assertEqual(full, key in ('20', '21'))
            self.assertEqual(worker, self.worker if full else self.host)
        with self.assertRaises(ValueError):
            module.resources_helper_overrides(['21=a', '21=b'])
        with self.assertRaises(AssertionError):
            module.plc_transport_inputs(argparse.Namespace(plc_sdk_worker=[], public_api_root=self.api_root), '21', self.root)


if __name__ == '__main__':
    unittest.main()
