"""PLC harness wiring regressions. Every process launch is mocked; no native TIA."""
import argparse
from contextlib import contextmanager, redirect_stdout
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch

from offline_fixtures import fixture_directory

spec = importlib.util.spec_from_file_location('plc_harness', Path(__file__).with_name('Test-PlcHarness.py'))
harness = importlib.util.module_from_spec(spec)
spec.loader.exec_module(harness)


class PlcHarnessSuccessorTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixture_directory('plc-harness-selftest-')
        self.root = Path(self.fixture.__enter__())
        self.host = self.root / 'TiaMcp.FoundationHost.exe'
        self.worker = self.root / 'TiaMcp.PlcWorker.21.exe'
        self.host.write_bytes(b'host')
        self.worker.write_bytes(b'worker')
        self.args = argparse.Namespace(release='21', mode='hmi-snapshot-only', host=self.host, worker=self.worker,
                                       harness=self.root / 'TiaMcp.Engine.Harness.exe', api=self.root / 'api',
                                       public_api_root=self.root / 'sdk', output=self.root / 'output',
                                       adapter_inventory=self.root / 'adapter.json')
        self.launches = []
        self.shapes = []

    def tearDown(self):
        self.fixture.__exit__(None, None, None)

    def rpc(self, method, *_, **__):
        if method == 'tools/call':
            return {'result': {'structuredContent': dict(schemaVersion=4, ok=True, data={}, error=None, meta={}), 'isError': False}}
        return {'result': {'tools': [{'name': 'Fixture', 'inputSchema': {}}]}} if method == 'tools/list' else {'result': {}}

    @contextmanager
    def server(self, *args, **kwargs):
        self.launches.append((args, kwargs))
        yield self.rpc, None, []

    def shape(self, args, *values, **kwargs):
        self.shapes.append((values, kwargs))
        if values[0] == 'metadata':
            Path(values[3]).write_text('{}', encoding='utf-8')
            return 'COMPLETE: 2200 descriptor catalog checks passed\n'
        return {'parser-self-test': 'COMPLETE: 2 pre-dispatch JSON rejection checks passed\n',
                'coverage-self-test': 'COMPLETE: 4 descriptor coverage self-tests passed\n'}.get(values[0], '')

    def execute(self, child_output='PASS native harness\n'):
        @contextmanager
        def bridge(*_):
            yield 'http://127.0.0.1:12345/'
        with patch.object(harness.resources, 'server', self.server), patch.object(harness, 'shape', self.shape), \
             patch.object(harness, 'bridge', bridge), patch.object(harness, 'run', return_value=child_output) as child, \
             redirect_stdout(io.StringIO()) as output:
            harness.execute(self.args)
        return child, output.getvalue()

    def test_native_modes_launch_only_plc_subject(self):
        for mode in ('hmi-only', 'hmi-snapshot-only', 'native-export-only', 'global-script-only', 'graphic-selection-only',
                     'runtime-settings-only', 'software-lookup-only', 'process-leases-only', 'test-migration-read-assembly',
                     'test-ecosystem-assembly', 'test-download-route', 'test-match-plc-name'):
            self.args.mode, self.args.output = mode, self.root / mode
            child, _ = self.execute()
            command = child.call_args.args[0]
            self.assertEqual(command[:5], [self.args.harness, '--plc', self.worker, mode, self.args.api])
            self.assertNotIn('--engine-worker', command)
            self.assertEqual(child.call_args.kwargs['env']['TIA_MCP_PLC_HARNESS_RPC'], 'http://127.0.0.1:12345/')
            # hmi-only compares the worker/adapter release file version with this host's.
            self.assertEqual(child.call_args.kwargs['env']['TIA_MCP_PLC_HARNESS_HOST'], str(self.host))

    def test_shared_server_gets_explicit_worker_and_public_api(self):
        self.execute()
        args, kwargs = self.launches[0]
        self.assertEqual(args[:5], (self.host, self.args.api, '21', 'stdio', 'full'))
        self.assertEqual(kwargs['plc_worker'], self.worker)
        self.assertEqual(kwargs['public_api'], self.args.api)
        self.assertNotIn('engine_worker', kwargs)
        self.assertNotEqual(kwargs.get('sdk_ready'), False)

    def test_descriptor_uses_real_host_roster_and_worker(self):
        self.args.mode = 'descriptor-catalog-only'
        child, output = self.execute()
        child.assert_not_called()
        self.assertIn('2200 descriptor catalog checks passed', output)
        values = self.shapes[0][0]
        self.assertEqual(values, ('metadata', '21', self.args.output / 'tools.json', self.args.output / 'metadata.json', self.worker, self.args.api))
        self.assertEqual(json.loads((self.args.output / 'tools.json').read_text()), [{'name': 'Fixture', 'inputSchema': {}}])
        with patch.object(harness, 'run', return_value='compiled output') as runner:
            self.assertEqual(harness.shape(self.args, 'metadata', '21'), 'compiled output')
        command = runner.call_args.args[0]
        self.assertEqual(command[:3], ['dotnet', 'run', '--file'])
        self.assertEqual(command[3], Path(harness.__file__).with_suffix('.cs'))
        self.assertIn('--no-build', command)
        self.assertIn('--no-restore', command)
        self.assertEqual(command[-2:], ['metadata', '21'])

    def test_self_test_keeps_campaign_parser_and_coverage_negatives(self):
        self.args.mode = 'check-modes-self-test'
        # The validator prints one COMPLETE line per inner run before its own total.
        child, output = self.execute('COMPLETE: 1 script input checks passed (0 explicit rejections); 0 failed\n'
                                     'COMPLETE: 14 kept-validator campaign self-tests passed; no dispatch\n')
        self.assertEqual(child.call_count, 1)
        self.assertEqual(child.call_args.args[0][-1], '--self-test')
        self.assertEqual([value[0][0] for value in self.shapes], ['metadata', 'parser-self-test', 'coverage-self-test'])
        # The total is the sum of the three reported counts, not a constant.
        self.assertIn('COMPLETE: 20 check mode self-tests passed', output)
        self.args.output = self.root / 'missing-count'
        with self.assertRaisesRegex(AssertionError, 'did not report its complete count'):
            self.execute('PASS without a count\n')

    def test_file_apps_find_the_build_below_the_original_temp(self):
        # check-all gives each job a private TEMP; dotnet run --file --no-build finds the program below the original one.
        with patch.dict(harness.os.environ, {'TEMP': 'private', 'TMP': 'private', 'TIA_MCP_CHECK_SYSTEM_TEMP_ROOT': 'system'}), \
             patch.object(harness, 'run', return_value='') as runner:
            harness.shape(self.args, 'parser-self-test')
            harness.shape(self.args, 'guards', env={'TEMP': 'private', 'TIA_MCP_CHECK_SYSTEM_TEMP_ROOT': 'system', 'X': '1'})
        first, second = (call.kwargs['env'] for call in runner.call_args_list)
        self.assertEqual((first['TEMP'], first['TMP']), ('system', 'system'))
        self.assertEqual((second['TEMP'], second['TMP'], second['X']), ('system', 'system', '1'))

    def test_evidence_hashes_the_selected_worker_and_host(self):
        self.execute()
        value = json.loads((self.args.output / 'result.json').read_text())
        self.assertFalse(value['nativeTiaExecuted'])
        self.assertEqual(value['workerModel'], 'foundation-host-plc-worker')
        self.assertEqual(value['runtimeSha256'], value['sdkWorkerSha256'])
        self.assertNotEqual(value['runtimeSha256'], value['hostSha256'])

    def test_transport_reuses_full_plc_transport_matrix(self):
        self.args.mode = 'http'
        def transport(inputs, _, counts):
            self.assertEqual(inputs.releases, ['21'])
            self.assertFalse(inputs.stdio_only)
            self.assertEqual(inputs.plc_sdk_worker, [f'21={self.worker}'])
            self.assertEqual(inputs.host_exe, self.host)
            counts['21'] = 64
        with patch.object(harness, 'load', return_value=argparse.Namespace(plc_transports=transport)), redirect_stdout(io.StringIO()) as output:
            harness.execute(self.args)
        self.assertIn('192 Foundation transport checks passed', output.getvalue())

    def test_engine_shape_mode_requires_d5_and_cannot_fall_back(self):
        self.args.mode = 'engineering-api-only'
        with self.assertRaisesRegex(AssertionError, 'No PLC successor'):
            self.execute()
        self.assertFalse(self.args.output.exists())

    def test_native_diagnostics_keeps_inventory_reader_guards_and_suite(self):
        self.args.mode = 'native-diagnostics-only'
        with patch.object(harness, 'native_reader') as reader, patch.object(harness, 'suites') as suite:
            child, _ = self.execute()
        self.assertEqual(child.call_args.kwargs['env']['TIA_MCP_PLC_HARNESS_INVENTORY'], str(self.args.adapter_inventory))
        reader.assert_called_once()
        suite.assert_called_once_with(self.args)
        self.assertEqual(self.shapes[-1][0][0], 'guards')
        self.assertIn('enabled=false', (self.args.output / 'sdk-guard-data/config/approval.settings').read_text())

    def test_failed_v4_envelope_is_preserved(self):
        value = dict(schemaVersion=4, ok=False, data=None, error={'code': 'INVALID_ARGUMENT'}, meta={})
        def rpc(*_, **__):
            return {'result': {'structuredContent': value, 'isError': True}}
        self.assertEqual(harness.call_tool(rpc, 'Fixture', {}), value)

    def test_staged_tool_reply_is_fully_reassembled(self):
        logical = dict(schemaVersion=4, ok=True, data={'tools': [1]}, error=None, meta={})
        text = json.dumps(logical)
        # Both staged shapes: a usage export (data.content) and the host's parking of a large V4 reply (data.text).
        for head_key in ('content', 'text'):
            calls = []
            def rpc(_, params):
                calls.append(params)
                if params['name'] == 'GetExportContent':
                    offset = params['arguments']['offset']
                    end = None if offset >= 20 else 20
                    value = dict(schemaVersion=4, ok=True, data={'text': text[offset:end], 'returned': 10, 'export': {'id': 'owned'}}, error=None,
                                 meta={'paging': {'offset': offset, 'nextOffset': end}})
                else:
                    value = dict(schemaVersion=4, ok=True, data={'export': {'id': 'owned'}, head_key: text[:10]}, error=None,
                                 meta={'paging': {'offset': 0, 'nextOffset': 10}})
                return {'result': {'structuredContent': value, 'isError': False}}
            self.assertEqual(harness.call_tool(rpc, 'Fixture', {}), logical)
            self.assertEqual([call['arguments'].get('offset') for call in calls], [None, 10, 20])

    def test_staged_reply_refuses_a_missing_page(self):
        def rpc(_, params):
            if params['name'] == 'GetExportContent':
                value = dict(schemaVersion=4, ok=True, data={'text': 'x', 'export': {'id': 'owned'}}, error=None, meta={'paging': {'offset': 99, 'nextOffset': None}})
            else:
                value = dict(schemaVersion=4, ok=True, data={'export': {'id': 'owned'}, 'text': '{'}, error=None, meta={'paging': {'offset': 0, 'nextOffset': 1}})
            return {'result': {'structuredContent': value, 'isError': False}}
        with self.assertRaisesRegex(AssertionError, 'page is incomplete'):
            harness.call_tool(rpc, 'Fixture', {})


if __name__ == '__main__':
    unittest.main()
