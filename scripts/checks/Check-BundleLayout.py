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


def check(root, tracked):
    paths = resource_paths((root / SOURCE).read_text(encoding='utf-8-sig'))
    validated = validated_paths((root / VALIDATOR).read_text(encoding='utf-8-sig'))
    errors = []
    for path in paths:
        target = root / path
        if not target.exists():
            errors.append('Missing resource: ' + path)
        in_tree = path in tracked if not target.is_dir() else any(p.startswith(path + '/') for p in tracked)
        if not in_tree:
            errors.append('Resource is not in the Git file set: ' + path)
        if path not in validated:
            errors.append('Resource is not checked by Validate-Bundle: ' + path)
    return len(paths), errors


class LayoutChecks(unittest.TestCase):
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
            for relative in (SOURCE, VALIDATOR):
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
            validator = root / VALIDATOR
            validator.write_text(validator.read_text(encoding='utf-8').replace("    'templates'", "    'unrelated'"), encoding='utf-8')
            self.assertIn('Resource is not checked by Validate-Bundle: templates', check(root, tracked)[1])
        finally:
            self.assertEqual(root.resolve().parent, parent.resolve())
            shutil.rmtree(root)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(LayoutChecks))
        return int(not result.wasSuccessful())
    try:
        tracked = set(subprocess.check_output(['git', '-C', str(args.root), 'ls-files', '-z']).decode('utf-8').split('\0'))
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
