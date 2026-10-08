"""Check the BCL resource table against Git and the C# bundle validator; no dotnet."""
import argparse
import json


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
GENERATED_RESOURCES = {'runtime/tools/TiaMcp.WriteGuard.exe', 'runtime/tools/TiaMcp.Updater.exe',
                       'runtime/tools/TiaMcp.Updater.exe.config'}
SOURCE_ROOTS = 'build-tools/release/source-roots.json'


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
    errors = []
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
        installer = (ROOT / 'src/Engine/Cli/InstallPlcToolsCommand.cs').read_text(encoding='utf-8-sig')
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
                ('src/Studio/Gui/Fonts/JetBrainsMono-OFL.txt', 'JetBrainsMono-OFL.txt'),
                ('src/Studio/Gui/Fonts/NotoSansSC-OFL.txt', 'NotoSansSC-OFL.txt'),
                ('reference/siemens-code-snippets/LICENSE.md', 'SiemensCodeSnippets-LICENSE.md')):
            with self.subTest(source=source):
                self.assertEqual((ROOT / source).read_text(encoding='utf-8-sig'),
                                 (ROOT / 'docs/licenses' / copy).read_text(encoding='utf-8-sig'))

    def test_primer_sources_and_retained_fonts_stay_in_all_required_lists(self):
        gui = 'src/Studio/Gui/'
        required = ('Themes/Primer.xaml', 'Themes/Palette.Light.xaml', 'Themes/Palette.Dark.xaml',
                    'Controls/WorkbenchLogView.cs', 'Controls/WorkbenchMessageBox.cs', 'Controls/ResultPresentation.cs', 'Controls/LogTailView.cs',
                    'Fonts/JetBrainsMono-Regular.ttf', 'Fonts/JetBrainsMono-Medium.ttf', 'Fonts/JetBrainsMono-OFL.txt',
                    'Fonts/NotoSansSC-Regular.otf', 'Fonts/NotoSansSC-Bold.otf', 'Fonts/NotoSansSC-OFL.txt')
        for path in required:
            with self.subTest(path=path):
                self.assertTrue((ROOT / gui / path).is_file())
                for consumer in ('bundle', 'package', 'repository'):
                    self.assertIn(gui + path, required_sources(ROOT, consumer))
        self.assertEqual({p.name for p in (ROOT / gui / 'Fonts').iterdir() if p.suffix in ('.ttf', '.otf')},
                         {'JetBrainsMono-Regular.ttf', 'JetBrainsMono-Medium.ttf', 'NotoSansSC-Regular.otf', 'NotoSansSC-Bold.otf'})
        self.assertFalse((ROOT / 'docs/licenses/Manrope-OFL.txt').exists())

    def test_delivery_includes_runtime_resources_and_plugin(self):
        rules = load_delivery(ROOT)
        for path in ('TiaOpenness.exe', 'runtime/v21/TiaMcp.Engine.V21.exe',
                     'runtime/tools/TiaMcp.Updater.exe', 'runtime/tools/TiaMcp.Updater.exe.config',
                     'runtime/dotnet/LICENSE.txt', 'runtime/dotnet/ThirdPartyNotices.txt',
                     '.claude-plugin/plugin.json', 'hooks/hooks.json',
                     'runtime/tools/TiaMcp.WriteGuard.exe',
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
