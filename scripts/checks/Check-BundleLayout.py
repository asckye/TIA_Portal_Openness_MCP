"""Check the BCL resource table against Git and Validate-Bundle's enforced list; no dotnet."""
import argparse
import json
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
VALIDATOR = 'scripts/checks/Validate-Bundle.ps1'
LAUNCHER = 'src/Studio/Launcher/Launcher.cs'
GUI_PROJECT = 'src/Studio/Gui/TiaOpenness.Gui.csproj'
DELIVERY_RULES = 'scripts/operations/delivery-files.json'


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
    match = re.search(r'^\$bundleResourcePaths = @\(\s*(.*?)^\)', source, re.M | re.S)
    if not match or not re.search(r'foreach \(\$resource in \$bundleResourcePaths\)\s*\{\s*'
            r'if \(Test-Path -LiteralPath \(Join-Path \$root \$resource\)\)', source):
        raise ValueError('Validate-Bundle must enforce its bundleResourcePaths list')
    rows = re.findall(r"'([^']+)'", match[1])
    if not rows or re.sub(r"'[^']+'|[\s,]", '', match[1]):
        raise ValueError('Unrecognized Validate-Bundle resource list')
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
    for path in paths:
        target = root / path
        if not target.exists():
            errors.append('Missing resource: ' + path)
        # An extracted or staged bundle has no Git work tree; existence and validation still apply there.
        if tracked is not None:
            in_tree = path in tracked if not target.is_dir() else any(p.startswith(path + '/') for p in tracked)
            if not in_tree:
                errors.append('Resource is not in the Git file set: ' + path)
        if path not in validated:
            errors.append('Resource is not checked by Validate-Bundle: ' + path)
        if not delivery_resource(path, rules):
            errors.append('Resource is not in the delivery set: ' + path)
        if target.is_dir():
            for child in target.rglob('*'):
                relative = child.relative_to(root).as_posix()
                if child.is_file() and (tracked is None or relative in tracked) and not delivered(relative, rules):
                    errors.append('Resource child is not in the delivery set: ' + relative)
    return len(paths), errors


class LayoutChecks(unittest.TestCase):
    def test_python_installer_covers_external_runtime_dependencies(self):
        expected = set()
        for path in (ROOT / 'third_party/siemens-plc-tools').rglob('pyproject.toml'):
            project = tomllib.loads(path.read_text(encoding='utf-8'))['project']
            expected.update(d for d in project.get('dependencies', []) if not d.startswith('plc-'))
            for extra in ('opcua', 'web'):
                expected.update(d for d in project.get('optional-dependencies', {}).get(extra, []) if not d.startswith('plc-'))
        installer = (ROOT / 'scripts/ecosystem/Install-PlcTools.ps1').read_text(encoding='utf-8-sig')
        actual = re.search(r'\$dependencies = @\((.*?)\n\)', installer, re.S)
        self.assertIsNotNone(actual)
        self.assertEqual(set(re.findall(r"'([^']+)'", actual[1])), expected)
        self.assertIn('@dependencies pytest pytest-asyncio pytest-cov reportlab', installer)

    def test_shipped_license_copies_are_unchanged(self):
        for source, copy in (
                ('third_party/TiaGitAddIn.Core/LICENSE', 'TiaGitAddIn.Core-LICENSE.txt'),
                ('third_party/SiemensOpcUaModelled/LICENSE.md', 'SiemensOpcUaModelled-LICENSE.md'),
                ('third_party/eido-import-planner/LICENSE', 'Eido-LICENSE.txt'),
                ('third_party/tia-openness-studio/LICENSE', 'TiaOpennessStudio-LICENSE.txt'),
                ('src/Studio/Gui/Fonts/Manrope-OFL.txt', 'Manrope-OFL.txt'),
                ('src/Studio/Gui/Fonts/JetBrainsMono-OFL.txt', 'JetBrainsMono-OFL.txt'),
                ('reference/siemens-code-snippets/LICENSE.md', 'SiemensCodeSnippets-LICENSE.md')):
            with self.subTest(source=source):
                self.assertEqual((ROOT / source).read_text(encoding='utf-8-sig'),
                                 (ROOT / 'docs/licenses' / copy).read_text(encoding='utf-8-sig'))

    def test_delivery_includes_runtime_resources_and_plugin(self):
        rules = load_delivery(ROOT)
        for path in ('TiaMcpConfigurator.exe', 'runtime/v21/TiaMcpServer.exe',
                     'runtime/dotnet/LICENSE.txt', 'runtime/dotnet/ThirdPartyNotices.txt',
                     '.claude-plugin/plugin.json', 'hooks/hooks.json',
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
                     'src/Engine/Program.cs', 'docs/development/runtime-layout.md',
                     'scripts/checks/Validate-Bundle.ps1', 'reference/tool-examples/README.md',
                     'manifest/contracts/tools.json', 'manifest/history/old.json',
                     'third_party/siemens-plc-tools/packages/plc-code/tests/test_cli.py',
                     'third_party/simaticml-decoder/pyproject.toml',
                     'runtime-other/file.dll', 'templates-other/file.json', 'README.md.bak',
                     'runtime/../private.key', '/runtime/file.dll', 'runtime\\file.dll'):
            with self.subTest(path=path):
                self.assertFalse(delivered(path, rules))

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
            validated_paths(source.replace('foreach ($resource in $bundleResourcePaths)', 'foreach ($resource in @())'))

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
            validator.write_text(validator.read_text(encoding='utf-8').replace("    'templates'", "    'unrelated'"), encoding='utf-8')
            self.assertIn('Resource is not checked by Validate-Bundle: templates', check(root, tracked)[1])
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
