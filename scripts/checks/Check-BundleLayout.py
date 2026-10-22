"""Check the BCL resource table against Git and the C# bundle validator; no dotnet."""
import argparse
import json
import hashlib
import re as _re

ENGINE_RECORD = 'manifest/release-build.json'
MULTI_RECORD = 'manifest/multi-version-build.json'
# build-release builds and tests the net48 updater into bin-build/updater; the package carries it under runtime/tools.
# It is in neither runtime inventory: the records bind exactly these files in updaterFiles (P8-upd-bind).
UPDATER_OUTPUTS = {'runtime/tools/TiaMcp.Updater.exe': 'bin-build/updater/TiaMcp.Updater.exe',
                   'runtime/tools/TiaMcp.Updater.exe.config': 'bin-build/updater/TiaMcp.Updater.exe.config'}


def release_record_path(names):
    if MULTI_RECORD in names:
        return MULTI_RECORD
    if ENGINE_RECORD in names:
        return ENGINE_RECORD
    raise ValueError('Missing release build record (engine or eight-release)')


def release_record(files, delivery=None):
    """Select before parsing: a damaged preferred record must never trigger fallback."""
    name = release_record_path(files)
    record = json.loads(files[name].decode('utf-8-sig'))
    binding = 'engineBuildSha256' if name == ENGINE_RECORD else 'multiVersionBuildSha256'
    if delivery is not None and hashlib.sha256(files[name]).hexdigest() != delivery.get(binding):
        raise ValueError('Build record differs from delivery: ' + name)
    if name == MULTI_RECORD:
        validate_plc_record(record)
        if 'manifest/tools-list.json' in files:
            digest = hashlib.sha256(files['manifest/tools-list.json']).hexdigest()
            if any(runtime['toolsList']['sha256'] != digest for runtime in record['plcRuntimes'].values()):
                raise ValueError('PLC tools-list changed after validation')
    updater_inventory(record)
    return name, record


def record_inventory(record):
    rows = record['runtimeFiles'] if 'runtimeFiles' in record else record['files']
    if len({row['path'] for row in rows}) != len(rows):
        raise ValueError('Duplicate runtime inventory paths')
    if any(not plain_path(row['path']) or not row['path'].startswith('runtime/') for row in rows):
        raise ValueError('Invalid runtime inventory path')
    return rows


def updater_inventory(record):
    """The updater rows a record binds (package path, SHA-256, length); [] for a record written before the field
    (3.x records predate the C# updater). Same rules as ReleaseRecords.UpdaterFiles in the release tool."""
    if 'updaterFiles' not in record:
        return []
    rows = record['updaterFiles']
    if (not isinstance(rows, list) or not all(isinstance(row, dict) for row in rows)
            or sorted(str(row.get('path')) for row in rows) != sorted(UPDATER_OUTPUTS)
            or not all(_re.fullmatch('[0-9a-f]{64}', str(row.get('sha256', ''))) and type(row.get('length')) is int
                       and row['length'] > 0 for row in rows)):
        raise ValueError('Invalid updater build record: updaterFiles must bind ' + ' and '.join(sorted(UPDATER_OUTPUTS))
                         + ' with SHA-256 and length')
    engine = record.get('engineCompatibility')
    if isinstance(engine, dict) and 'updaterFiles' in engine and engine['updaterFiles'] != rows:
        raise ValueError('Updater build record differs from the shipped-engine record')
    if any(row.get('path') in UPDATER_OUTPUTS for key in ('runtimeFiles', 'files') for row in record.get(key) or []):
        raise ValueError('Updater files must come from the validated updater build output, not the runtime inventory')
    return rows


def shipped_engine_proof(record):
    # Until j2 the eight-release record retains the delivered engine's original
    # evidence separately. It is never labelled as a PlcWorker check result.
    return record.get('engineCompatibility', record)


def plc_record_jobs(key):
    jobs = {f'{name}-v{key}-plc': group for name, group in (
        ('native-coverage', 'native-coverage'), ('adapter-native-coverage', 'native-coverage'),
        ('jit-adapter-coverage', 'native-diagnostics'), ('engineering-api', 'plc-functional'),
        ('worker-robustness', 'plc-worker-isolation'), ('engine-host', 'http-concurrency'),
        ('native-safety', 'offline-suites'), ('worker-protocol', 'plc-worker-isolation'),
        ('approval', 'plc-approval'), ('resources', 'resource-discovery'), ('ecosystem', 'plc-ecosystem'),
        ('stability-c8', 'plc-stability'), ('stability-c16', 'plc-isolated-stability'),
        ('usage', 'resource-discovery'), ('campaign-inputs', 'plc-functional'))}
    modes = ['example-library-only', 'software-lookup-only', 'http', 'hmi-only', 'native-diagnostics-only',
             'process-leases-only', 'native-export-only', 'hmi-snapshot-only', 'global-script-only',
             'graphic-selection-only', 'runtime-settings-only', 'test-migration-read-assembly',
             'test-ecosystem-assembly', 'descriptor-catalog-only', 'check-modes-self-test']
    if key == '21':
        modes += ['test-download-route', 'test-match-plc-name']
    jobs.update({f'harness-v{key}-{mode}-plc': 'native-diagnostics' if mode == 'native-diagnostics-only' else 'plc-functional'
                 for mode in modes})
    jobs.update({f'{kind}{source}-{transport}-v{key}-plc': 'plc-' + kind
                 for kind in ('contracts', 'responses') for source in ('', '-source') for transport in ('stdio', 'http')})
    return jobs


# A drive-letter or extended-length path; records ship in the package and must not name the build host's folders.
LOCAL_PATH = _re.compile(r'(?:\\\\\?\\)?\b[A-Za-z]:[\\/]')


def validate_plc_record(record):
    engine = record['engineCompatibility']
    if any(record[key] != engine[key] for key in ('release', 'fileVersion', 'tier', 'checkPlan')):
        raise ValueError('Shipped-engine compatibility proof differs from eight-release record')
    files = {row['path']: row['sha256'] for row in record_inventory(record)}
    selected = set(record['checkPlan']['selectedChecks'])
    # Without -Test the preparation runs only the PLC approval successors, and only for foundation-approval.
    if record.get('test', True) is False:
        selected = {'plc-approval'} if 'foundation-approval' in selected else set()
    # Each receipt names its log, which the record binds in validationArtifacts with the same SHA-256.
    bound = {row['path']: row['sha256'] for row in record.get('validationArtifacts', [])}
    if LOCAL_PATH.search(json.dumps(record['plcRuntimes'])):
        raise ValueError('PLC proof holds an absolute local path')
    for key in ('20', '21'):
        runtime = record['plcRuntimes']['V' + key]
        for field in ('worker', 'adapter', 'host'):
            row = runtime[field]
            if files.get(row['path']) != row['sha256']:
                raise ValueError('PLC artifact differs from runtime inventory: ' + field)
        if not runtime['worker']['path'].endswith(f'/TiaMcp.PlcWorker.{key}.exe'):
            raise ValueError('PLC evidence names another worker')
        for field in ('catalog', 'toolsList', 'sdkWorker'):
            if len(runtime[field]['sha256']) != 64:
                raise ValueError('PLC catalog/SDK hash missing: ' + field)
        checks = runtime['checks']
        required = {name for name, group in plc_record_jobs(key).items() if group in selected
                    or group == 'plc-approval' and 'foundation-approval' in selected
                    or group == 'resource-discovery' and 'foundation-transport' in selected}
        if not required <= set(checks):
            raise ValueError('Missing PLC check receipts: ' + ', '.join(sorted(required - set(checks))))
        for name, check in checks.items():
            if (check.get('status') != 'passed' or not _re.fullmatch('[0-9a-f]{64}', str(check.get('sha256', '')))
                    or not isinstance(check.get('length'), int) or check['length'] <= 0 or not isinstance(check.get('counts'), list)
                    or check.get('path') != f'bin-build/multi-version/{name}.log' or bound.get(check['path']) != check['sha256']):
                raise ValueError('Incomplete PLC check receipt: ' + name)
        for name, group in (('localStability', 'plc-stability'), ('isolatedLocalStability', 'plc-isolated-stability')):
            if group not in selected:
                continue
            proof = runtime[name]
            if (proof.get('status') != 'passed' or proof.get('rounds', 0) < 10 or proof.get('isolatedWorker') is not True
                    or proof.get('runtimeSha256') != runtime['sdkWorker']['sha256']
                    or proof.get('sdkWorkerSha256') != runtime['sdkWorker']['sha256']
                    or proof.get('productionWorkerSha256') != runtime['worker']['sha256']
                    or proof.get('hostSha256') != runtime['host']['sha256']
                    or proof.get('workerModel') != 'foundation-host-plc-worker'):
                raise ValueError('PLC stability runtime/hash mismatch: ' + key)
            runs = proof.get('runs', [])
            if (len(runs) != 4 or {(r['transport'], r['profile']) for r in runs} !=
                    {('stdio', 'full'), ('stdio', 'lite'), ('http', 'full'), ('http', 'lite')}
                    or not all(r['unexpectedExits'] == 0 and r['unexpectedFailures'] == 0 and r['tiaConnected'] is False
                               and r['unmatchedJournalEntries'] == 0 and r['measuredToolCalls'] > 0 for r in runs)):
                raise ValueError('PLC stability matrix incomplete: ' + key)


def release_checks(root, package, *records):
    """Validate candidate tier evidence before delivery projection removes sources."""
    policy = json.loads((root / 'build-tools/release/release-checks.json').read_text('utf-8'))
    if policy.get('includeSourceRoots'):
        policy['rules'] += source_check_rules(root)
    tier = package.get('tier')
    if tier not in ('package', 'quick', 'full'):
        raise ValueError('Package needs an explicit package/quick/full tier; run run-release-build -Tier')
    selected = package.get('checksSelected', [])
    skipped = package.get('checksSkipped', [])
    if (len(selected + skipped) != len(policy['checks']) or len(set(selected + skipped)) != len(selected + skipped)
            or set(selected + skipped) != set(policy['checks']) or not set(policy['always']) <= set(selected)
            or (tier == 'full' and skipped)
            or (tier == 'package' and set(selected) != set(policy['always']))):
        raise ValueError('Invalid candidate check selection')
    required = set(policy['always'])
    for path in package.get('changedPaths', []) if tier == 'quick' else []:
        matches = [rule for rule in policy['rules'] if
                   (path.startswith(rule['path']) if rule['path'].endswith('/') else path == rule['path'])]
        if not matches:
            required.update(policy['checks'])
        for rule in matches:
            if len(rule['path']) == max(len(match['path']) for match in matches):
                required.update(rule['checks'])
    if not required <= set(selected):
        raise ValueError('Changed paths require additional candidate checks')
    status = package.get('checkStatus')
    if status not in ('pending', 'passed') or package.get('checksRan') != (selected if status == 'passed' else []):
        raise ValueError('Invalid candidate check completion evidence')
    for record in records:
        if record is None:
            continue
        plan = record.get('checkPlan', {})
        if (record.get('tier') != tier or plan.get('tier') != tier or plan.get('selectedChecks') != selected
                or plan.get('skippedChecks') != skipped or plan.get('changedPaths') != package.get('changedPaths', [])):
            raise ValueError('Build record check plan differs from candidate package')
    return set(selected)
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import shutil
import unittest
import uuid
import tomllib

ROOT = Path(__file__).resolve().parents[2]
SOURCE = 'src/Shared/BundleLayout.cs'
VALIDATOR = 'build-tools/release/BundleManifestRequirements.cs'
LAUNCHER = 'src/Studio/Launcher/Launcher.cs'
GUI_PROJECT = 'src/Studio/Gui/TiaOpenness.Gui.csproj'
DELIVERY_RULES = 'scripts/operations/delivery-files.json'
GENERATED_RESOURCES = {'runtime/tools/TiaMcp.WriteGuard.exe', 'runtime/tools/TiaMcp.PluginLauncher.exe', 'runtime/tools/TiaMcp.Updater.exe',
                       'runtime/tools/TiaMcp.Updater.exe.config'}
SOURCE_ROOTS = 'build-tools/release/source-roots.json'
COLLABORATION_ASSEMBLIES = (
    'Siemens.Collaboration.Net.dll', 'Siemens.Collaboration.Net.CoreExtensions.dll',
    'Siemens.Collaboration.Net.Logging.dll', 'Siemens.Collaboration.Net.OperatingSystem.Windows.dll',
    'Siemens.Collaboration.Net.Windows.Authentication.dll', 'Siemens.Collaboration.Net.TiaPortal.Openness.Resolver.dll',
)


def openness_group_delivery_errors(rules):
    errors = []
    for key in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21'):
        for assembly in COLLABORATION_ASSEMBLIES:
            host = f'runtime/v{key}/{assembly}'
            if delivered(host, rules):
                errors.append('Delivery policy permits retired host group dependency: ' + host)
            worker = f'runtime/v{key}/worker/{assembly}'
            # V20/V21 retain the engine until P8-04l; do not silently exclude its dependencies.
            if key not in ('20', '21') and delivered(worker, rules):
                errors.append('Delivery policy permits retired PlcWorker group dependency: ' + worker)
    return errors


def source_roots(root):
    # Delivery directories omit build inputs; use the invoking checker's policy there.
    path = root / SOURCE_ROOTS
    policy = json.loads((path if path.is_file() else ROOT / SOURCE_ROOTS).read_text(encoding='utf-8'))
    if (policy.get('schemaVersion') != 1 or not policy['roots']
            or len({row['path'] for row in policy['roots']}) != len(policy['roots'])
            or len({row['path'] for row in policy['requiredFiles']}) != len(policy['requiredFiles'])
            or any(not plain_path(row['path']) or set(row['inventories']) - {'engine', 'multi', 'validation', 'validationMulti'}
                   for row in policy['roots'])
            or any(not plain_path(row['path']) or set(row['consumers']) - {'repository', 'bundle', 'package'}
                   or ('repository' in row['consumers'] and not row.get('repositoryLabel')) for row in policy['requiredFiles'])):
        raise ValueError('Invalid source root policy')
    return policy


def source_check_rules(root):
    return [dict(path=row['path'] + '/', checks=row['checks']) for row in source_roots(root)['roots'] if 'checks' in row]


def required_sources(root, consumer):
    return [row['path'] for row in source_roots(root)['requiredFiles'] if consumer in row['consumers']]


def compiler_sources(root, files):
    roots = [row['path'] for row in source_roots(root)['roots'] if 'engine' in row['inventories']]
    return {name for name in files if any(name.startswith(path + '/') for path in roots)
            and Path(name).suffix in ('.cs', '.csproj', '.props', '.targets', '.xml', '.json', '.config', '.manifest', '.resx')} | (
                {'Version.props', *(path for path in roots if (root / path).is_file())} & set(files))


def load_delivery(root):
    rules = json.loads((root / DELIVERY_RULES).read_text(encoding='utf-8-sig'))
    if rules.get('schemaVersion') != 1:
        raise ValueError('Unsupported delivery-files schema')
    for group in ('include', 'exclude', 'legacyCleanup'):
        for kind in ('files', 'prefixes'):
            rows = rules[group][kind]
            if not isinstance(rows, list) or len(rows) != len(set(rows)):
                raise ValueError('Invalid delivery rule list: ' + group + '/' + kind)
            for path in rows:
                if (not isinstance(path, str) or not plain_path(path.rstrip('/'))
                        or path.endswith('/') != (kind == 'prefixes')):
                    raise ValueError('Invalid delivery path: ' + str(path))
    required = rules.get("requiredFiles", [])
    if not required or len(required) != len(set(required)) or any(not plain_path(path) or not delivered(path, rules) for path in required):
        raise ValueError("Invalid required delivery files")
    return rules


def plain_path(path):
    return (bool(path) and not any(c in path for c in '\\:*?[]')
            and all(part not in ('', '.', '..') for part in path.split('/')))


def matches(path, rules):
    return path in rules['files'] or any(path.startswith(prefix) for prefix in rules['prefixes'])


def delivered(path, rules):
    return plain_path(path) and matches(path, rules['include']) and not matches(path, rules['exclude'])


def delivery_resource(path, rules):
    # A directory resource needs its entire subtree, not just one included child.
    return delivered(path, rules) or delivered(path.rstrip('/') + '/__resource__', rules)


def resource_paths(source):
    table = re.search(r'new Dictionary<BundleResource, string>\s*\{(.*?)\};', source, re.S)
    enum = re.search(r'internal enum BundleResource\s*\{(.*?)\}', source, re.S)
    if not table or not enum:
        raise ValueError('Missing BundleResource enum or literal resource table')
    row = re.compile(r'\{ BundleResource\.(\w+), "([^"\\]+)" \}\s*,?')
    entries = row.findall(table[1])
    if row.sub('', table[1]).strip():
        raise ValueError('Resource table contains an unrecognized row')
    ids = [name for name, _ in entries]
    paths = [path for _, path in entries]
    enum_ids = [name.strip() for name in enum[1].split(',') if name.strip()]
    if not entries or len(set(ids)) != len(ids) or set(ids) != set(enum_ids):
        raise ValueError('Resource IDs must cover the enum exactly once')
    if len(set(paths)) != len(paths):
        raise ValueError('Duplicate resource path')
    for path in paths:
        if (PurePosixPath(path).is_absolute() or ':' in path or '\\' in path
                or any(part in ('', '.', '..') for part in path.split('/'))):
            raise ValueError('Resource path must be a plain root-relative path: ' + path)
    return paths


def validated_paths(source):
    match = re.search(r'BundleResourcePaths\s*=\s*\[(.*?)\];', source, re.S)
    if not match or not re.search(r'MissingBundleResources\s*\(.*?=>\s*BundleResourcePaths\s*\.Where\(resource\s*=>\s*'
            r'!File\.Exists\(Path\.Combine\(root,\s*resource\.Replace', source, re.S):
        raise ValueError('C# bundle validator must enforce its BundleResourcePaths list')
    rows = re.findall(r'"([^"\\]+)"', match[1])
    if not rows or re.sub(r'"[^"\\]+"|[\s,]', '', match[1]):
        raise ValueError('Unrecognized C# bundle resource list')
    return set(rows)


def launcher_paths(layout, launcher, gui_project):
    # The C# 5 bootstrapper cannot link the resolver. Its sole installed probe
    # must match the resolver's Studio anchor and the GUI's executable name.
    anchor = re.search(r'var studioRoot = FromAnchor\(directory, "([^"]+)"\);', layout)
    assembly = re.search(r'<AssemblyName>([^<]+)</AssemblyName>', gui_project)
    probes = re.findall(r'Path\.Combine\(root, ((?:"[^"\\]+"\s*,?\s*)+)\)', launcher)
    if (not anchor or not assembly or len(probes) != 1
            or len(re.findall(r'Path\.Combine\s*\(', launcher)) != len(probes)
            or not re.search(r'string root = AppDomain\.CurrentDomain\.BaseDirectory;', launcher)
            or not re.search(r'string desktop = Path\.Combine\(root,', launcher)
            or re.findall(r'File\.Exists\(([^)]+)\)', launcher) != ['desktop']):
        raise ValueError('Unrecognized Launcher lookup; review every relative probe against BundleLayout')
    actual = '/'.join(re.findall(r'"([^"]+)"', probes[0]))
    expected = anchor[1] + '/' + assembly[1] + '.exe'
    if actual != expected:
        raise ValueError(f'Launcher path differs from BundleLayout/GUI output: {actual} != {expected}')
    return [actual]


def check(root, tracked):
    rules = load_delivery(root)
    layout = (root / SOURCE).read_text(encoding='utf-8-sig')
    paths = resource_paths(layout)
    launcher_paths(layout, (root / LAUNCHER).read_text(encoding='utf-8-sig'),
                   (root / GUI_PROJECT).read_text(encoding='utf-8-sig'))
    validated = validated_paths((root / VALIDATOR).read_text(encoding='utf-8-sig'))
    errors = openness_group_delivery_errors(rules)
    for name in tracked or ():
        if name.startswith(('runtime/', 'hooks/')) and Path(name).suffix.lower() in ('.cs', '.csproj'):
            errors.append('Shipped runtime/hook tree contains source: ' + name)
    for path in paths:
        target = root / path
        if not target.exists() and not (tracked is not None and path in GENERATED_RESOURCES):
            errors.append('Missing resource: ' + path)
        # An extracted or staged bundle has no Git work tree; existence and validation still apply there.
        if tracked is not None:
            in_tree = path in tracked if not target.is_dir() else any(p.startswith(path + '/') for p in tracked)
            if not in_tree and path not in GENERATED_RESOURCES:
                errors.append('Resource is not in the Git file set: ' + path)
        if path not in validated:
            errors.append('Resource is not checked by the C# bundle validator: ' + path)
        if not delivery_resource(path, rules):
            errors.append('Resource is not in the delivery set: ' + path)
        if target.is_dir():
            for child in target.rglob('*'):
                relative = child.relative_to(root).as_posix()
                if child.is_file() and (tracked is None or relative in tracked) and not delivered(relative, rules):
                    errors.append('Resource child is not in the delivery set: ' + relative)
    return len(paths), errors


class LayoutChecks(unittest.TestCase):
    def test_openness_group_delivery_excludes_plc_and_host_but_retains_engine(self):
        rules = load_delivery(ROOT)
        self.assertEqual([], openness_group_delivery_errors(rules))
        for assembly in COLLABORATION_ASSEMBLIES:
            for key in ('20', '21'):
                self.assertTrue(delivered(f'runtime/v{key}/worker/{assembly}', rules))
        planted = 'runtime/v19/worker/Siemens.Collaboration.Net.dll'
        rules['exclude']['files'].remove(planted)
        self.assertTrue(any(planted in error for error in openness_group_delivery_errors(rules)))

    @staticmethod
    def record_fixture():
        engine = dict(release='4.0.0', fileVersion='4.0.0.0', tier='package',
                      checkPlan=dict(tier='package', selectedChecks=[], skippedChecks=[], changedPaths=[]),
                      sourceFiles=[], validationInputs=[], runtimeFiles=[], validation={})
        multi = {key: value for key, value in engine.items() if key != 'runtimeFiles'}
        multi.update(files=[], engineCompatibility=engine, plcRuntimes={})
        for key in ('20', '21'):
            runtime = dict(checks={})
            for field, path in (
                    ('worker', f'runtime/v{key}/worker/TiaMcp.PlcWorker.{key}.exe'),
                    ('adapter', f'runtime/v{key}/worker/TiaMcp.Adapter.{key}.dll'),
                    ('host', f'runtime/v{key}/TiaMcp.FoundationHost.exe')):
                runtime[field] = dict(path=path, sha256='a' * 64)
                multi['files'].append(runtime[field].copy())
            for field in ('catalog', 'toolsList', 'sdkWorker'):
                runtime[field] = dict(path=field, sha256='b' * 64)
            multi['plcRuntimes']['V' + key] = runtime
        return engine, multi

    def test_record_selection_binding_and_inventory_for_both_shapes(self):
        engine, multi = self.record_fixture()
        for name, record, binding in ((ENGINE_RECORD, engine, 'engineBuildSha256'),
                                      (MULTI_RECORD, multi, 'multiVersionBuildSha256')):
            with self.subTest(record=name):
                data = json.dumps(record).encode()
                files = {name: data}
                delivery = {binding: hashlib.sha256(data).hexdigest()}
                self.assertEqual(release_record(files, delivery), (name, record))
                self.assertEqual(record_inventory(record), record.get('runtimeFiles', record.get('files')))
                with self.assertRaises(ValueError):
                    release_record(files, {binding: '0' * 64})
        files = {MULTI_RECORD: b'{bad', ENGINE_RECORD: json.dumps(engine).encode()}
        self.assertEqual(release_record_path(files), MULTI_RECORD)
        with self.assertRaises(ValueError):
            release_record(files)
        with self.assertRaises(ValueError):
            release_record({})

    @staticmethod
    def updater_rows():
        return [dict(path=name, sha256=hashlib.sha256(name.encode()).hexdigest(), length=len(name)) for name in sorted(UPDATER_OUTPUTS)]

    def test_updater_record_binds_exactly_the_two_files_outside_the_runtime_inventory(self):
        engine, multi = self.record_fixture()
        self.assertEqual(updater_inventory(multi), [])
        engine['updaterFiles'] = self.updater_rows()
        multi['updaterFiles'] = self.updater_rows()
        self.assertEqual(updater_inventory(engine), engine['updaterFiles'])
        self.assertEqual(updater_inventory(multi), multi['updaterFiles'])
        data = json.dumps(multi).encode()
        self.assertEqual(release_record({MULTI_RECORD: data}, {'multiVersionBuildSha256': hashlib.sha256(data).hexdigest()}), (MULTI_RECORD, multi))
        for mutation in ('missing-file', 'extra-file', 'bad-hash', 'zero-length', 'text-length', 'engine-copy-differs', 'in-runtime-inventory', 'not-a-list'):
            with self.subTest(mutation=mutation):
                _, record = self.record_fixture()
                record['updaterFiles'] = self.updater_rows()
                record['engineCompatibility']['updaterFiles'] = self.updater_rows()
                rows = record['updaterFiles']
                if mutation == 'missing-file':
                    rows.pop()
                elif mutation == 'extra-file':
                    rows.append(dict(path='runtime/tools/Other.exe', sha256='c' * 64, length=1))
                elif mutation == 'bad-hash':
                    rows[0]['sha256'] = 'C' * 64
                elif mutation == 'zero-length':
                    rows[0]['length'] = 0
                elif mutation == 'text-length':
                    rows[0]['length'] = '12'
                elif mutation == 'engine-copy-differs':
                    record['engineCompatibility']['updaterFiles'][0]['sha256'] = 'd' * 64
                elif mutation == 'in-runtime-inventory':
                    record['files'].append(dict(rows[0]))
                else:
                    record['updaterFiles'] = {'path': rows[0]['path']}
                with self.assertRaises(ValueError):
                    updater_inventory(record)
                data = json.dumps(record).encode()
                with self.assertRaises(ValueError):
                    release_record({MULTI_RECORD: data}, {'multiVersionBuildSha256': hashlib.sha256(data).hexdigest()})

    def test_plc_record_requires_every_selected_receipt_and_exact_hashes(self):
        _, multi = self.record_fixture()
        multi['checkPlan']['selectedChecks'] = ['plc-functional']
        with self.assertRaisesRegex(ValueError, 'Missing PLC check receipts'):
            validate_plc_record(multi)
        for key in ('20', '21'):
            runtime = multi['plcRuntimes']['V' + key]
            for name, group in plc_record_jobs(key).items():
                if group == 'plc-functional':
                    runtime['checks'][name] = self.receipt(multi, name)
        validate_plc_record(multi)
        receipt = multi['plcRuntimes']['V21']['checks']['harness-v21-http-plc']
        for field, value in (('sha256', 'c' * 64), ('path', 'bin-build/multi-version/other.log'), ('counts', None), ('length', 0)):
            original = receipt[field]
            receipt[field] = value
            with self.assertRaisesRegex(ValueError, 'Incomplete PLC check receipt'):
                validate_plc_record(multi)
            receipt[field] = original
        row = next(row for row in multi['validationArtifacts'] if row['path'] == receipt['path'])
        multi['validationArtifacts'].remove(row)
        with self.assertRaisesRegex(ValueError, 'Incomplete PLC check receipt'):
            validate_plc_record(multi)
        multi['validationArtifacts'].append(row)
        validate_plc_record(multi)
        receipt['counts'] = ['COMPLETE: 1500 checks; evidence: D:\\Build\\checkout\\x']
        with self.assertRaisesRegex(ValueError, 'absolute local path'):
            validate_plc_record(multi)
        receipt['counts'] = ['COMPLETE: 1500 checks']
        multi['plcRuntimes']['V20']['worker']['sha256'] = '0' * 64
        with self.assertRaisesRegex(ValueError, 'artifact differs'):
            validate_plc_record(multi)

    @staticmethod
    def receipt(record, name):
        sha256 = hashlib.sha256(name.encode()).hexdigest()
        path = f'bin-build/multi-version/{name}.log'
        record.setdefault('validationArtifacts', []).append(dict(path=path, sha256=sha256))
        return dict(path=path, sha256=sha256, length=17, status='passed', counts=['COMPLETE: 1 checks passed'])

    def test_record_without_test_flag_requires_only_the_approval_receipts(self):
        _, multi = self.record_fixture()
        multi['checkPlan']['selectedChecks'] = ['foundation-approval', 'plc-functional']
        multi['test'] = False
        with self.assertRaisesRegex(ValueError, 'Missing PLC check receipts'):
            validate_plc_record(multi)
        for key in ('20', '21'):
            multi['plcRuntimes']['V' + key]['checks'][f'approval-v{key}-plc'] = self.receipt(multi, f'approval-v{key}-plc')
        validate_plc_record(multi)
        multi['test'] = True
        with self.assertRaisesRegex(ValueError, 'Missing PLC check receipts'):
            validate_plc_record(multi)

    def test_quick_package_requires_the_same_build_plan(self):
        policy = json.loads((ROOT / 'build-tools/release/release-checks.json').read_text('utf-8'))
        selected = [check for check in policy['checks'] if check in policy['always']]
        skipped = [check for check in policy['checks'] if check not in selected]
        package = dict(tier='quick', checkStatus='passed', checksSelected=selected, checksRan=selected,
                       checksSkipped=skipped, changedPaths=['docs/development/validation.md'])
        record = dict(tier='quick', checkPlan=dict(tier='quick', selectedChecks=selected,
                                                  skippedChecks=skipped, changedPaths=package['changedPaths']))
        self.assertEqual(set(selected), release_checks(ROOT, package, record))
        with self.assertRaises(ValueError):
            release_checks(ROOT, package, dict(tier='full', checkPlan=record['checkPlan']))

    def test_package_tier_has_only_installable_checks(self):
        policy = json.loads((ROOT / 'build-tools/release/release-checks.json').read_text('utf-8'))
        selected = [check for check in policy['checks'] if check in policy['always']]
        package = dict(tier='package', checkStatus='passed', checksSelected=selected, checksRan=selected,
                       checksSkipped=[check for check in policy['checks'] if check not in selected], changedPaths=['unknown/new.cs'])
        self.assertEqual(set(policy['always']), release_checks(ROOT, package))
        package['checksSelected'] += ['offline-suites']
        package['checksSkipped'].remove('offline-suites')
        with self.assertRaises(ValueError):
            release_checks(ROOT, package)

    def test_package_rejects_unknown_path_with_reduced_checks(self):
        policy = json.loads((ROOT / 'build-tools/release/release-checks.json').read_text('utf-8'))
        selected = policy['always']
        package = dict(tier='quick', checkStatus='passed', checksSelected=selected, checksRan=selected,
                       checksSkipped=[check for check in policy['checks'] if check not in selected],
                       changedPaths=['unreviewed/Example.cs'])
        with self.assertRaises(ValueError):
            release_checks(ROOT, package)

    def test_full_package_requires_all_checks_and_complete_pass_list(self):
        policy = json.loads((ROOT / 'build-tools/release/release-checks.json').read_text('utf-8'))
        package = dict(tier='full', checkStatus='passed', checksSelected=policy['checks'], checksRan=policy['checks'],
                       checksSkipped=[], changedPaths=[])
        self.assertEqual(set(policy['checks']), release_checks(ROOT, package))
        package['checksRan'] = policy['always']
        with self.assertRaises(ValueError):
            release_checks(ROOT, package)

    def test_csharp_installer_covers_external_runtime_dependencies(self):
        expected = set()
        for path in (ROOT / 'third_party/siemens-plc-tools').rglob('pyproject.toml'):
            project = tomllib.loads(path.read_text(encoding='utf-8'))['project']
            expected.update(d for d in project.get('dependencies', []) if not d.startswith('plc-'))
            for extra in ('opcua', 'web'):
                expected.update(d for d in project.get('optional-dependencies', {}).get(extra, []) if not d.startswith('plc-'))
        installer = (ROOT / 'src/Shared/Host/InstallPlcToolsCommand.cs').read_text(encoding='utf-8-sig')
        for project in ('src/FoundationHost/TiaMcp.FoundationHost.csproj', 'src/Engine/TiaMcp.Engine.V20.csproj', 'src/Engine/TiaMcp.Engine.V21.csproj'):
            self.assertIn('../Shared/Host/InstallPlcToolsCommand.cs', (ROOT / project).read_text(encoding='utf-8-sig'))
        actual = re.search(r'ExternalPackages\s*=\s*\{(.*?)\n\s*\};', installer, re.S)
        self.assertIsNotNone(actual)
        self.assertEqual(set(re.findall(r'"([^"\n]+)"', actual[1])), expected)
        self.assertTrue(all(f'"{name}"' in installer for name in ('pytest', 'pytest-asyncio', 'pytest-cov', 'reportlab')))

    def test_shipped_license_copies_are_unchanged(self):
        for source, copy in (
                ('third_party/TiaGitAddIn.Core/LICENSE', 'TiaGitAddIn.Core-LICENSE.txt'),
                ('third_party/SiemensOpcUaModelled/LICENSE.md', 'SiemensOpcUaModelled-LICENSE.md'),
                ('third_party/eido-import-planner/LICENSE', 'Eido-LICENSE.txt'),
                ('third_party/tia-openness-studio/LICENSE', 'TiaOpennessStudio-LICENSE.txt'),
                ('src/Studio/Gui/Fonts/LICENSE.txt', 'MapleMono-CN-OFL.txt'),
                ('reference/siemens-code-snippets/LICENSE.md', 'SiemensCodeSnippets-LICENSE.md')):
            with self.subTest(source=source):
                self.assertEqual((ROOT / source).read_text(encoding='utf-8-sig'),
                                 (ROOT / 'docs/licenses' / copy).read_text(encoding='utf-8-sig'))

    def test_primer_sources_and_retained_fonts_stay_in_all_required_lists(self):
        gui = 'src/Studio/Gui/'
        required = ('Themes/Primer.xaml', 'Themes/Palette.Light.xaml', 'Themes/Palette.Dark.xaml',
                    'Controls/WorkbenchLogView.cs', 'Controls/WorkbenchMessageBox.cs', 'Controls/ResultPresentation.cs', 'Controls/LogTailView.cs',
                    'Fonts/MapleMono-CN-Regular.ttf', 'Fonts/MapleMono-CN-SemiBold.ttf', 'Fonts/LICENSE.txt')
        for path in required:
            with self.subTest(path=path):
                self.assertTrue((ROOT / gui / path).is_file())
                for consumer in ('bundle', 'package', 'repository'):
                    self.assertIn(gui + path, required_sources(ROOT, consumer))
        self.assertEqual({p.name for p in (ROOT / gui / 'Fonts').iterdir() if p.suffix in ('.ttf', '.otf')},
                         {'MapleMono-CN-Regular.ttf', 'MapleMono-CN-SemiBold.ttf'})
        self.assertFalse((ROOT / 'docs/licenses/Manrope-OFL.txt').exists())

    def test_delivery_includes_runtime_resources_and_plugin(self):
        rules = load_delivery(ROOT)
        for path in ('TiaOpenness.exe', 'runtime/v21/TiaMcp.FoundationHost.exe', 'runtime/v21/worker/TiaMcp.PlcWorker.21.exe',
                     'runtime/tools/TiaMcp.Updater.exe', 'runtime/tools/TiaMcp.Updater.exe.config',
                     'runtime/dotnet/LICENSE.txt', 'runtime/dotnet/ThirdPartyNotices.txt',
                     '.claude-plugin/plugin.json', 'hooks/hooks.json',
                     'runtime/tools/TiaMcp.WriteGuard.exe', 'runtime/tools/TiaMcp.PluginLauncher.exe',
                     'plugin/skill/SKILL.md',
                     'third_party/siemens-plc-tools/packages/plc-code/src/plc_code/cli.py',
                     'third_party/simaticml-decoder/src/simaticml_decoder/parse.py'):
            with self.subTest(path=path):
                self.assertTrue(delivered(path, rules))
        for path in resource_paths((ROOT / SOURCE).read_text(encoding='utf-8-sig')):
            with self.subTest(resource=path):
                self.assertTrue(delivery_resource(path, rules))

    def test_delivery_excludes_development_and_prefix_lookalikes(self):
        rules = load_delivery(ROOT)
        for path in ('runtime/verification/NativeCallWeaver.dll', 'runtime/verification/Mono.Cecil.dll',
                     'AGENTS.md', 'Version.props', 'RELEASE_STATUS.txt', '.github/workflows/release.yml',
                     'TiaMcp.Updater.exe', 'TiaMcp.Updater.exe.config',
                     'src/Engine/Program.cs', 'docs/development/runtime-layout.md',
                     'build-tools/release/TiaMcp.ReleaseTool.csproj', 'reference/tool-examples/README.md',
                     'manifest/contracts/tools.json', 'manifest/history/old.json',
                     'third_party/siemens-plc-tools/packages/plc-code/tests/test_cli.py',
                     'third_party/simaticml-decoder/pyproject.toml',
                     'runtime-other/file.dll', 'templates-other/file.json', 'README.md.bak',
                     'runtime/../private.key', '/runtime/file.dll', 'runtime\\file.dll'):
            with self.subTest(path=path):
                self.assertFalse(delivered(path, rules))
        self.assertIn('TiaMcp.Updater.exe', rules['legacyCleanup']['files'])
        self.assertIn('TiaMcp.Updater.exe.config', rules['legacyCleanup']['files'])
        self.assertFalse(delivered('scripts/operations/Update-Engine.ps1', rules))

    def test_launcher_matches_installed_anchor_and_gui_filename(self):
        layout, launcher, project = [(ROOT / path).read_text(encoding='utf-8-sig')
                                     for path in (SOURCE, LAUNCHER, GUI_PROJECT)]
        self.assertEqual(launcher_paths(layout, launcher, project), ['runtime/studio/TiaOpenness.exe'])
        for changed in (launcher.replace('"studio"', '"other"'),
                        launcher.replace('"TiaOpenness.exe"', '"Other.exe"'),
                        launcher + '\nPath.Combine(root, "other", "TiaOpenness.exe");',
                        launcher.replace('File.Exists(desktop)', 'File.Exists("other.exe")')):
            with self.subTest(source=changed), self.assertRaises(ValueError):
                launcher_paths(layout, changed, project)
        with self.assertRaises(ValueError):
            launcher_paths(layout.replace('"runtime/studio"', '"runtime/desktop"'), launcher, project)
        with self.assertRaises(ValueError):
            launcher_paths(layout, launcher, project.replace('<AssemblyName>TiaOpenness', '<AssemblyName>Other'))

    def test_real_table_and_validator(self):
        paths = resource_paths((ROOT / SOURCE).read_text(encoding='utf-8-sig'))
        self.assertTrue(set(paths) <= validated_paths((ROOT / VALIDATOR).read_text(encoding='utf-8-sig')))

    def test_table_rejects_unreviewed_syntax_and_paths(self):
        source = (ROOT / SOURCE).read_text(encoding='utf-8-sig')
        for replacement in ('../outside', '/absolute', 'C:/absolute', 'manifest//file', 'manifest/./file'):
            with self.subTest(path=replacement), self.assertRaises(ValueError):
                resource_paths(source.replace('manifest/package-manifest.json', replacement))
        with self.assertRaises(ValueError):
            resource_paths(source.replace('{ BundleResource.PackageManifest,', '{ BundleResource.Templates,'))
        with self.assertRaises(ValueError):
            resource_paths(source.replace('"manifest/package-manifest.json"', 'ComputePath()'))

    def test_validator_must_actually_use_list(self):
        source = (ROOT / VALIDATOR).read_text(encoding='utf-8-sig')
        with self.assertRaises(ValueError):
            validated_paths(source.replace('=> BundleResourcePaths', '=> Array.Empty<string>()'))

    def test_missing_untracked_and_unvalidated_resources(self):
        parent = ROOT / 'bin-build'
        parent.mkdir(exist_ok=True)
        # Inherit the worktree ACL; Python's Windows mkdtemp(mode=0700) prevents
        # sandboxed child processes (and subsequent fixture operations) using it.
        root = parent / ('bundle-check-' + uuid.uuid4().hex)
        root.mkdir()
        try:
            for relative in (SOURCE, VALIDATOR, LAUNCHER, GUI_PROJECT, DELIVERY_RULES):
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text((ROOT / relative).read_text(encoding='utf-8-sig'), encoding='utf-8')
            paths = resource_paths((root / SOURCE).read_text(encoding='utf-8'))
            tracked = set()
            for path in paths:
                target = root / path
                if path in ('templates', 'reference/siemens-openness/skills'):
                    target /= 'fixture.md'
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text('fixture', encoding='utf-8')
                tracked.add(target.relative_to(root).as_posix())
            self.assertEqual(check(root, tracked), (len(paths), []))
            (root / 'manifest/delivery.json').unlink()
            self.assertIn('Missing resource: manifest/delivery.json', check(root, tracked)[1])
            tracked.remove('templates/fixture.md')
            self.assertIn('Resource is not in the Git file set: templates', check(root, tracked)[1])
            bundle_errors = check(root, None)[1]
            self.assertFalse(any('Git file set' in error for error in bundle_errors))
            self.assertIn('Missing resource: manifest/delivery.json', bundle_errors)
            validator = root / VALIDATOR
            validator.write_text(validator.read_text(encoding='utf-8').replace('"templates"', '"unrelated"'), encoding='utf-8')
            self.assertIn('Resource is not checked by the C# bundle validator: templates', check(root, tracked)[1])
        finally:
            self.assertEqual(root.resolve().parent, parent.resolve())
            shutil.rmtree(root)


def git_tracked(root):
    """Tracked files when root is the top of a Git work tree; None for an extracted or staged bundle."""
    try:
        top = subprocess.run(['git', '-C', str(root), 'rev-parse', '--show-toplevel'], capture_output=True, text=True)
    except OSError:
        return None
    if top.returncode != 0 or Path(top.stdout.strip()).resolve() != Path(root).resolve():
        return None
    return set(subprocess.check_output(['git', '-C', str(root), 'ls-files', '-z']).decode('utf-8').split('\0'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(LayoutChecks))
        return int(not result.wasSuccessful())
    try:
        tracked = git_tracked(args.root)
        count, errors = check(args.root, tracked)
        for error in errors:
            print('FAIL: ' + error)
        print(f'Bundle layout: {count} resources checked; {len(errors)} failures')
        return int(bool(errors))
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print('FAIL: ' + str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
