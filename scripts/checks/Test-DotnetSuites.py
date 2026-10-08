"""Run converted offline suites and reject incomplete or unsuccessful TRX results."""
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path
import re
import os
import subprocess
import sys
import unittest
from unittest.mock import patch
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / 'tests/test-suites.json'
RESULTS = ROOT / 'bin-build/test-results'
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
OUTCOMES = {'Passed': 'passed', 'Failed': 'failed', 'NotExecuted': 'skipped'}


def read_trx(path):
    root = ET.parse(path).getroot()
    summary = root.find('t:ResultSummary', NS)
    counters = root.find('t:ResultSummary/t:Counters', NS)
    if summary is None or counters is None:
        raise ValueError('TRX is missing ResultSummary/Counters')
    counts = {}
    for key in ('total', 'executed', 'passed', 'failed', 'notExecuted'):
        counts[key] = int(counters.attrib[key])
    if any(int(value) < 0 for value in counters.attrib.values()):
        raise ValueError('TRX has negative counters')
    errors = []
    for key, value in counters.attrib.items():
        if key not in counts and int(value):
            errors.append(f'TRX {key}={value}')
    if summary.get('outcome') not in ('Completed', 'Passed'):
        errors.append(f'TRX summary outcome={summary.get("outcome")}')
    if counts['executed'] != counts['passed'] + counts['failed']:
        errors.append('TRX executed count differs from passed + failed')
    # VSTest 17.12 writes NotExecuted rows for xUnit skips but leaves the notExecuted
    # counter at zero. Derive skips from total - executed, then verify every result row.
    skipped = counts['total'] - counts['executed']
    if skipped < 0 or counts['notExecuted'] not in (0, skipped):
        errors.append('TRX skipped counters are inconsistent')

    definitions = {}
    for test in root.findall('t:TestDefinitions/t:UnitTest', NS):
        method = test.find('t:TestMethod', NS)
        if method is None or not method.get('className') or not method.get('name'):
            raise ValueError('TRX test definition is missing its class/method')
        definitions[test.attrib['id']] = (method.attrib['className'], method.attrib['name'])
    groups = defaultdict(Counter)
    actual = Counter()
    executions = set()
    rows = root.findall('t:Results/t:UnitTestResult', NS)
    for row in rows:
        execution = row.attrib['executionId']
        if execution in executions:
            raise ValueError('TRX repeats an executionId')
        executions.add(execution)
        outcome = OUTCOMES.get(row.get('outcome'), 'other')
        actual[outcome] += 1
        groups[definitions[row.attrib['testId']]][outcome] += 1
    if (len(rows), actual['passed'], actual['failed'], actual['skipped']) != (
            counts['total'], counts['passed'], counts['failed'], skipped):
        errors.append('TRX result rows differ from Counters')
    methods = [dict(testClass=cls, testMethod=method, total=sum(values.values()),
                    **{key: values[key] for key in ('passed', 'failed', 'skipped', 'other')})
               for (cls, method), values in sorted(groups.items())]
    return dict(total=counts['total'], executed=counts['executed'], passed=counts['passed'],
                failed=counts['failed'], skipped=skipped, methods=methods), errors


def evaluate_trx(path, minimum, maximum):
    result, errors = read_trx(path)
    if result['executed'] == 0:
        errors.append('zero tests executed')
    if result['failed']:
        errors.append(f'{result["failed"]} failed checks')
    if result['passed'] < minimum:
        errors.append(f'passed {result["passed"]} below minimum {minimum}')
    if result['skipped'] > maximum:
        errors.append(f'skipped {result["skipped"]} above maximum {maximum}')
    return result, errors


def current_platform():
    return {'win32': 'windows', 'linux': 'linux', 'darwin': 'macos'}.get(sys.platform, sys.platform)


def load_catalog(path, platform=None):
    catalog = json.loads(path.read_text(encoding='utf-8-sig'))
    if not isinstance(catalog, dict) or not catalog:
        raise ValueError('Suite catalog must be a nonempty object')
    platform = platform or current_platform()
    for name, suite in catalog.items():
        if not re.fullmatch(r'[a-z0-9]+(?:-[a-z0-9]+)*', name):
            raise ValueError(f'Invalid suite name: {name}')
        # A check that only exists on one OS lowers the count elsewhere; the override names that
        # OS and the reason, so a lost check is still caught there.
        overrides = suite.get('platforms', {})
        if not isinstance(overrides, dict):
            raise ValueError(f'{name}: platforms must be an object')
        for os_name, override in overrides.items():
            if os_name not in ('windows', 'linux', 'macos') or not isinstance(override, dict) \
                    or not set(override) <= {'minimumPassed', 'maximumSkipped', 'reason'} \
                    or not isinstance(override.get('reason'), str) or not override['reason'].strip():
                raise ValueError(f'{name}: invalid platform override {os_name}')
        for key, value in overrides.get(platform, {}).items():
            if key != 'reason':
                suite[key] = value
        for key in ('minimumPassed', 'maximumSkipped'):
            if type(suite[key]) is not int or suite[key] < (1 if key == 'minimumPassed' else 0):
                raise ValueError(f'{name}: invalid {key}')
        if not isinstance(suite['project'], str) or not (ROOT / suite['project']).is_file():
            raise ValueError(f'{name}: project does not exist')
        if not isinstance(suite['arguments'], list) or not all(isinstance(arg, str) for arg in suite['arguments']):
            raise ValueError(f'{name}: arguments must be a string array')
    return catalog


def run_suite(name, suite, directory, dotnet='dotnet', arguments=()):
    directory = directory.resolve()
    directory.mkdir(parents=True, exist_ok=True)
    trx = directory / f'{name}.trx'
    report = directory / f'{name}.json'
    # A previous successful run must never stand in for a failed build or a zero-test project.
    trx.unlink(missing_ok=True)
    report.unlink(missing_ok=True)
    # Suites that compile a full engine need the PublicAPI copies; a worktree has no local sdk/ folder, so an explicit
    # TIA_MCP_TEST_PUBLIC_API_ROOT becomes the MSBuild PublicAPI root (without it, src/Shared/TiaPublicApi.props decides).
    api_root = os.environ.get('TIA_MCP_TEST_PUBLIC_API_ROOT')
    api = [f'-p:TiaPublicApiRoot={api_root}'] if suite.get('publicApiRoot') and api_root else []
    command = [dotnet, 'test', str(ROOT / suite['project']), '-c', 'Release',
               *suite['arguments'], *api, *arguments,
               '--logger', f'trx;LogFileName={name}.trx', '--results-directory', str(directory)]
    print(f'RUN {name}: {subprocess.list2cmdline(command)}', flush=True)
    errors = []
    result = {}
    exit_code = None
    try:
        exit_code = subprocess.run(command, cwd=ROOT, check=False).returncode
        if exit_code:
            errors.append(f'dotnet test exited {exit_code}')
        if not trx.is_file():
            errors.append('missing TRX (no tests discovered or build/test host failed)')
        else:
            result, trx_errors = evaluate_trx(trx, suite['minimumPassed'], suite['maximumSkipped'])
            errors.extend(trx_errors)
    except (OSError, ValueError, KeyError, ET.ParseError) as exc:
        errors.append(str(exc))
    report.write_text(json.dumps(dict(suite=name, project=suite['project'],
        minimumPassed=suite['minimumPassed'], maximumSkipped=suite['maximumSkipped'],
        exitCode=exit_code, **result, errors=errors), ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    if errors:
        print(f'FAIL: {name}: ' + '; '.join(errors), flush=True)
        return False
    print(f'COMPLETE: {result["passed"]} {name} checks passed', flush=True)
    return True


def synthetic_trx(path, outcomes, **overrides):
    root = ET.Element('TestRun', xmlns=NS['t'])
    summary = ET.SubElement(root, 'ResultSummary', outcome='Completed')
    counts = dict(total=len(outcomes), executed=sum(x != 'NotExecuted' for x in outcomes),
                  passed=outcomes.count('Passed'), failed=outcomes.count('Failed'),
                  notExecuted=outcomes.count('NotExecuted'))
    counts.update(overrides)
    ET.SubElement(summary, 'Counters', **{key: str(value) for key, value in counts.items()})
    definitions = ET.SubElement(root, 'TestDefinitions')
    results = ET.SubElement(root, 'Results')
    for i, outcome in enumerate(outcomes):
        test = ET.SubElement(definitions, 'UnitTest', id=str(i))
        ET.SubElement(test, 'TestMethod', className='Example.Checks', name='Check')
        ET.SubElement(results, 'UnitTestResult', testId=str(i), executionId=str(i), outcome=outcome)
    ET.ElementTree(root).write(path, encoding='utf-8', xml_declaration=True)


class GateTests(unittest.TestCase):
    def setUp(self):
        RESULTS.mkdir(parents=True, exist_ok=True)
        self.directory = RESULTS / ('self-test-' + uuid.uuid4().hex)
        self.directory.mkdir()
        self.trx = self.directory / 'example.trx'
        self.suite = dict(project='example.csproj', minimumPassed=2, maximumSkipped=0,
                          arguments=['-p:DefineConstants=TIA_V20'])

    def tearDown(self):
        for path in self.directory.iterdir():
            path.unlink()
        self.directory.rmdir()

    def check_result(self, outcomes, minimum=2, maximum=0, **overrides):
        synthetic_trx(self.trx, outcomes, **overrides)
        return evaluate_trx(self.trx, minimum, maximum)

    def test_platform_override(self):
        catalog = self.directory / 'catalog.json'
        catalog.write_text(json.dumps({'example': dict(project='scripts/checks/Test-DotnetSuites.py', minimumPassed=3,
            maximumSkipped=0, arguments=[], platforms={'linux': {'minimumPassed': 2, 'reason': 'one Windows-only check'}})}))
        self.assertEqual(load_catalog(catalog, 'linux')['example']['minimumPassed'], 2)
        self.assertEqual(load_catalog(catalog, 'windows')['example']['minimumPassed'], 3)
        catalog.write_text(json.dumps({'example': dict(project='scripts/checks/Test-DotnetSuites.py', minimumPassed=3,
            maximumSkipped=0, arguments=[], platforms={'linux': {'minimumPassed': 2}})}))
        with self.assertRaises(ValueError):
            load_catalog(catalog, 'linux')

    def test_minimum_and_growth(self):
        for count in (2, 3):
            with self.subTest(count=count):
                result, errors = self.check_result(['Passed'] * count)
                self.assertEqual(errors, [])
                self.assertEqual(result['methods'][0]['passed'], count)

    def test_deliberate_failure(self):
        self.assertIn('1 failed checks', self.check_result(['Passed', 'Passed', 'Failed'])[1])

    def test_below_minimum(self):
        self.assertIn('passed 1 below minimum 2', self.check_result(['Passed'])[1])

    def test_zero_executed_even_with_zero_minimum(self):
        self.assertIn('zero tests executed', self.check_result([], minimum=0)[1])

    def test_skip_limit_and_allowed_skip(self):
        outcomes = ['Passed', 'Passed', 'NotExecuted']
        self.assertIn('skipped 1 above maximum 0', self.check_result(outcomes)[1])
        result, errors = self.check_result(outcomes, maximum=1)
        self.assertEqual(errors, [])
        self.assertEqual(result['methods'][0]['skipped'], 1)

    def test_all_skipped_is_not_execution(self):
        self.assertIn('zero tests executed', self.check_result(['NotExecuted'], minimum=0, maximum=1)[1])

    def test_vstest_skip_counter_is_zero(self):
        outcomes = ['Passed', 'Passed', 'NotExecuted']
        result, errors = self.check_result(outcomes, maximum=1, notExecuted=0)
        self.assertEqual(errors, [])
        self.assertEqual(result['skipped'], 1)
        self.assertIn('skipped 1 above maximum 0', self.check_result(outcomes, notExecuted=0)[1])

    def test_other_failures(self):
        for counter in ('error', 'timeout', 'aborted', 'inconclusive'):
            with self.subTest(counter=counter):
                self.assertTrue(self.check_result(['Passed'] * 2, **{counter: 1})[1])

    def test_inflated_counters(self):
        self.assertTrue(self.check_result(['Passed'], total=2, executed=2, passed=2)[1])

    def test_unknown_result(self):
        self.assertTrue(self.check_result(['Passed', 'Error'])[1])

    def test_invalid_xml_and_counters(self):
        for contents in ('<broken', '<TestRun />'):
            self.trx.write_text(contents, encoding='utf-8')
            with self.assertRaises((ValueError, ET.ParseError)):
                evaluate_trx(self.trx, 2, 0)
        with self.assertRaises(ValueError):
            self.check_result(['Passed'], passed=-1)
        with self.assertRaises(ValueError):
            self.check_result(['Passed'], passed='bad')

    def test_missing_trx_removes_stale_success(self):
        synthetic_trx(self.trx, ['Passed'] * 2)
        with patch('subprocess.run', return_value=subprocess.CompletedProcess([], 0)):
            self.assertFalse(run_suite('example', self.suite, self.directory))
        self.assertFalse(self.trx.exists())
        self.assertIn('missing TRX', (self.directory / 'example.json').read_text())

    def test_runner_command_report_and_nonzero_exit(self):
        def run(command, **kwargs):
            self.assertEqual(command[1], 'test')
            self.assertIn('-p:DefineConstants=TIA_V20', command)
            self.assertIn('--no-restore', command)
            self.assertIn('trx;LogFileName=example.trx', command)
            synthetic_trx(self.trx, ['Passed'] * 2)
            return subprocess.CompletedProcess(command, code)
        for code in (0, 1):
            with self.subTest(code=code), patch('subprocess.run', side_effect=run):
                self.assertEqual(run_suite('example', self.suite, self.directory,
                                          arguments=['--no-restore']), code == 0)
                report = json.loads((self.directory / 'example.json').read_text())
                self.assertEqual(report['passed'], 2)
                self.assertEqual(report['methods'][0]['testMethod'], 'Check')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--suite', action='append', default=[], help='Suite name; repeat to run in the given order')
    parser.add_argument('--catalog', type=Path, default=CATALOG)
    parser.add_argument('--results-directory', type=Path, default=RESULTS)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--no-restore', action='store_true')
    parser.add_argument('--dotnet-arg', action='append', default=[], help='Extra argument; use --dotnet-arg=-p:Name=Value')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(GateTests))
        if not result.wasSuccessful():
            return 1
        print(f'COMPLETE: {result.testsRun} dotnet gate self-checks passed', flush=True)
        if not args.suite:
            return 0
    if not args.suite:
        parser.error('at least one --suite or --self-test is required')
    try:
        catalog = load_catalog(args.catalog)
        unknown = set(args.suite) - catalog.keys()
        if unknown:
            raise ValueError('Unknown suites: ' + ', '.join(sorted(unknown)))
    except (OSError, ValueError, KeyError, TypeError) as exc:
        parser.error(str(exc))
    arguments = args.dotnet_arg + (['--no-restore'] if args.no_restore else [])
    success = True
    # These two offline variants share build outputs and must never run concurrently.
    for name in args.suite:
        success = run_suite(name, catalog[name], args.results_directory, args.dotnet, arguments) and success
    return 0 if success else 1


if __name__ == '__main__':
    sys.exit(main())
