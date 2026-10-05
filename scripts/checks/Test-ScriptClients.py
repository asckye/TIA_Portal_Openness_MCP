"""Synthetic script-client checks. No process, Git operation, TIA or network access."""
import importlib.util
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))


def module(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


camp = module('campaign_client', 'scripts/diagnostics/campaign/camp.py')
watch = module('watch_client', 'scripts/operations/vci-watch/watch.py')
sweep = module('sweep_client', 'scripts/diagnostics/Sweep-WrongPathHonesty.py')
exports = module('export_client', 'scripts/diagnostics/campaign/export_full.py')
rawmsg = module('raw_client', 'scripts/diagnostics/campaign/rawmsg.py')


def result(ok=True, data=None, outcome=None):
    return {'schemaVersion': 4, 'ok': ok, 'data': data or {},
            'error': None if ok else {'code': 'OUTCOME_UNKNOWN', 'message': 'Inspect evidence'},
            'meta': {'outcome': outcome or ('succeeded' if ok else 'unknown'),
                     'requiresSessionReset': not ok}}


class ScriptClientTests(unittest.TestCase):
    def test_campaign_import_is_passive(self):
        self.assertIsNone(camp.p)
        self.assertEqual(camp.LITE, set())

    def test_campaign_direct_and_typed_bridge(self):
        value = result(data={'items': []})
        probe = Mock()
        probe.rpc.return_value = {'result': {'structuredContent': value}}
        with patch.object(camp, 'p', probe), patch.object(camp, 'LITE', {'GetSessionState'}):
            self.assertEqual(camp.call_raw('GetSessionState', {})[0], value)
            self.assertEqual(probe.rpc.call_args.args[1], {'name': 'GetSessionState', 'arguments': {}})
            camp.call_raw('RunReadOnlyToolBatch', {'operations': [{'name': 'GetSessionState', 'arguments': {}}]})
            args = probe.rpc.call_args.args[1]
            self.assertEqual(args['name'], 'CallTool')
            self.assertIsInstance(args['arguments']['arguments']['operations'], list)
            self.assertNotIn('argumentsJson', args['arguments'])

    def test_unknown_outcome_and_old_success_are_not_success(self):
        self.assertFalse(camp.classify(result(False))[0])
        self.assertEqual(camp.step_verdict(result(False), 'error'), 'UNCONFIRMED')
        self.assertEqual(camp.step_verdict(result(False), 'any'), 'UNCONFIRMED')
        known_failure = result(False, outcome='failed')
        known_failure['meta']['requiresSessionReset'] = False
        known_failure['error']['code'] = 'PROJECT_NOT_BOUND'
        self.assertEqual(camp.step_verdict(known_failure, 'error'), 'PASS')
        with self.assertRaises(ValueError): camp.classify({'message': 'success', 'meta': {'success': True}})

    def test_sweep_decodes_v4_without_starting_a_host(self):
        engine = sweep.Engine.__new__(sweep.Engine)
        engine.request = Mock(return_value={'result': {'structuredContent': result()}})
        self.assertTrue(engine.call('ListDevices', {})['ok'])
        engine.request.return_value = {'result': {'isError': True, 'structuredContent': result(False)}}
        self.assertFalse(engine.call('ListDevices', {})['ok'])

    def test_campaign_export_identity_and_batch_results(self):
        value = result(data={'items': [{'result': result(False)}], 'export': {'id': 'local'}})
        self.assertEqual(camp.result_fields(value)['exportId'], 'local')
        self.assertFalse(camp.result_fields(value)['items'][0]['result']['ok'])

    def test_export_pages_are_assembled_and_failures_stop(self):
        first = result(data={'text': 'abc'})
        first['meta']['paging'] = {'nextOffset': 3}
        last = result(data={'text': 'def'})
        last['meta']['paging'] = {'nextOffset': None}
        call = Mock(side_effect=[(first, ''), (last, '')])
        self.assertEqual(exports.assemble('fixture', call), 'abcdef')
        self.assertEqual(call.call_args.args[1]['offset'], 3)
        call = Mock(side_effect=[(first, ''), (result(False), '')])
        with self.assertRaises(ValueError): exports.assemble('fixture', call)
        first['meta']['paging']['nextOffset'] = 0
        with self.assertRaises(ValueError): exports.assemble('fixture', Mock(return_value=(first, '')))

    def test_watcher_sends_typed_arguments_and_reads_v4(self):
        engine = watch.Engine.__new__(watch.Engine)
        engine._send = Mock(return_value={'result': {'structuredContent': result()}})
        self.assertTrue(engine.call('SynchronizeVersionControlWorkspace', dryRun=False)['ok'])
        self.assertEqual(engine._send.call_args.args, ('tools/call', {
            'name': 'SynchronizeVersionControlWorkspace', 'arguments': {'dryRun': False}}))
        engine._send.return_value = {'result': {'isError': True, 'structuredContent': result(False)}}
        self.assertFalse(engine.call('CompilePlcSoftware', softwarePath='PLC')['ok'])

    def test_watcher_project_probe_stops_on_failure(self):
        engine = Mock()
        engine.call.return_value = result(data={'items': ['PID=1 project=Fixture']})
        self.assertTrue(watch.has_open_project(engine))
        engine.call.return_value = result(data={'items': ['PID=1 projects=<empty>']})
        self.assertFalse(watch.has_open_project(engine))
        engine.call.return_value = result(False, {'items': ['PID=1 project=Fixture']})
        self.assertFalse(watch.has_open_project(engine))
        engine.call.side_effect = ValueError('Invalid V4 envelope')
        self.assertFalse(watch.has_open_project(engine))


if __name__ == '__main__':
    unittest.main()
