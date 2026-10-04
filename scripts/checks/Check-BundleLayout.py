"""Check the BCL resource table against Git and Validate-Bundle's enforced list; no dotnet."""
import argparse
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import shutil
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]
SOURCE = 'tools/openness-shared/BundleLayout.cs'
VALIDATOR = 'scripts/checks/Validate-Bundle.ps1'
LAUNCHER = 'tools/tia-openness-studio/src/TiaOpenness.Launcher/Launcher.cs'
GUI_PROJECT = 'tools/tia-openness-studio/src/TiaOpenness.Gui/TiaOpenness.Gui.csproj'


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
    return len(paths), errors


class LayoutChecks(unittest.TestCase):
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
            for relative in (SOURCE, VALIDATOR, LAUNCHER, GUI_PROJECT):
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
