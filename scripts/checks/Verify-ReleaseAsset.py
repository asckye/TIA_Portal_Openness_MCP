"""Verify a delivery ZIP against the Git checkout it claims to come from.

The ZIP must equal the tag tree filtered by scripts/operations/delivery-files.json plus
recorded runtime binaries, excluding runtime/verification/. No generated extras are allowed.
The sidecar, tracked bytes, binary hashes and delivery package name are checked independently.

Run locally by the .NET release tool before upload and by the "Verify published release" workflow after it.
Exit code 0 = verified; anything else prints the first mismatch and exits 1.
"""
import argparse
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import re
import subprocess
import sys
import zipfile
import shutil
import unittest
from unittest.mock import patch
import uuid


_spec = importlib.util.spec_from_file_location('bundle_layout', Path(__file__).with_name('Check-BundleLayout.py'))
layout = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(layout)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('zip', nargs='?', type=Path, help='the TIA_MCP_Delivery_vX.Y.Z_YYYYMMDD.zip to verify')
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2], help='the Git checkout (default: this repository)')
    parser.add_argument('--commit', default='', help='expected source commit (default: HEAD of --root)')
    parser.add_argument('--git', default='git')
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(AssetChecks))
        return int(not result.wasSuccessful())
    if args.zip is None:
        parser.error('ZIP path is required')
    root = args.root.resolve()
    archive = args.zip.resolve()
    problems = []

    def check(condition, message):
        if not condition:
            problems.append(message)
        return condition

    sidecar = archive.with_suffix('.sha256')
    data = archive.read_bytes()
    digest = sha(data)
    if check(sidecar.is_file(), f'missing sidecar {sidecar.name}'):
        recorded = sidecar.read_text(encoding='ascii').split()[0].lower()
        check(recorded == digest, f'sidecar SHA-256 {recorded} != ZIP {digest}')
    print(f'ZIP {archive.name}: {len(data)} bytes, sha256 {digest}')

    commit = args.commit or subprocess.check_output([args.git, 'rev-parse', 'HEAD'], cwd=root).decode().strip()
    tree = subprocess.check_output([args.git, 'ls-tree', '-r', '--name-only', '-z', commit], cwd=root).decode('utf-8').split('\0')
    def committed(name):
        return subprocess.check_output([args.git, 'show', f'{commit}:{name}'], cwd=root)
    rules = json.loads(committed(layout.DELIVERY_RULES).decode('utf-8-sig'))
    tracked = [name for name in tree if name and layout.delivered(name, rules)]

    with zipfile.ZipFile(archive) as z:
        check(z.testzip() is None, 'ZIP integrity failure')
        names = z.namelist()
        check(len(names) == len(set(names)), 'Duplicate ZIP entries')
        check(all(layout.plain_path(n.rstrip('/')) for n in names), 'Unsafe ZIP path')
        tops = {n.split('/', 1)[0] for n in names}
        if not check(len(tops) == 1, f'ZIP must hold one top-level folder, found {sorted(tops)}'):
            return report(problems)
        package = tops.pop()
        check(package == archive.stem, f'top folder {package} != archive name {archive.stem}')
        inside = {n[len(package) + 1:]: n for n in names if not n.endswith('/')}
        content = {rel: z.read(full) for rel, full in inside.items()}

    # 2. tracked files identical - text files up to line endings: the ZIP is built from the maintainer's working
    #    tree while a CI checkout with core.autocrlf=true turns LF blobs into CRLF (12 files failed that way on the
    #    first 2.8.1 verification); binaries (any NUL byte) must match byte for byte
    def same(a, b):
        if a == b:
            return True
        if b'\x00' in a or b'\x00' in b:
            return False
        return a.replace(b'\r\n', b'\n') == b.replace(b'\r\n', b'\n')
    for name in tracked:
        if not check(name in content, f'tracked file missing from ZIP: {name}'):
            continue
        check(same(content[name], committed(name)), f'ZIP differs from checkout: {name}')
    tracked_set = set(tracked)

    # 3. binaries against the committed build records
    build = json.loads(committed('manifest/release-build.json').decode('utf-8-sig'))
    gui = json.loads(committed('manifest/configurator-build.json').decode('utf-8-sig'))
    delivery_record = json.loads(committed('manifest/delivery.json').decode('utf-8-sig'))
    check(sha(committed('manifest/release-build.json')) == delivery_record['engineBuildSha256'], 'Engine build record differs from delivery record')
    check(sha(committed('manifest/configurator-build.json')) == delivery_record['configuratorBuildSha256'], 'Configurator build record differs from delivery record')
    runtime_in_zip = {n for n in content if n.startswith('runtime/') and n not in tracked_set}
    recorded = {row['path']: row['sha256'] for row in build['runtimeFiles']}
    check(len(recorded) == len(build['runtimeFiles']), 'Duplicate engine runtime inventory paths')
    if delivery_record.get('multiVersionBuildSha256'):
        multi_bytes = committed('manifest/multi-version-build.json')
        check(sha(multi_bytes) == delivery_record['multiVersionBuildSha256'], 'Multi-version build record differs from delivery record')
        multi = json.loads(multi_bytes.decode('utf-8-sig'))
        check(multi['release'] == delivery_record['release'], 'Multi-version release mismatch')
        for row in multi['files']:
            check(row['path'] not in recorded or recorded[row['path']] == row['sha256'], 'Conflicting recorded runtime: ' + row['path'])
            recorded[row['path']] = row['sha256']
    check(all(layout.delivered(n, rules) or n.startswith('runtime/verification/') for n in recorded),
          'Delivery rules exclude a recorded runtime dependency')
    recorded = {n: digest for n, digest in recorded.items() if layout.delivered(n, rules)}
    check(runtime_in_zip == set(recorded), 'runtime inventory in ZIP differs from manifest/release-build.json: '
          + ', '.join(sorted(runtime_in_zip ^ set(recorded))[:10]))
    for path, expected in recorded.items():
        if path in content:
            check(sha(content[path]) == expected, f'runtime hash differs from release-build.json: {path}')
    exe = gui['executable']['path']
    if check(exe in content, f'{exe} missing from ZIP'):
        check(sha(content[exe]) == gui['executable']['sha256'], f'{exe} differs from manifest/configurator-build.json')
    check(not any((n.startswith('runtime/') and n != 'runtime/README.md') or n == 'TiaOpenness.exe' for n in tree),
          'binaries are tracked in Git although the 2.8.1 policy keeps them out')

    # 4. Exact set equality also rejects verification tools, development files and old extras.
    expected_files = tracked_set | set(recorded) | {exe}
    check(set(content) == expected_files, 'ZIP file set differs from delivery set: '
          + ', '.join(sorted(set(content) ^ expected_files)[:20]))
    for name in content:
        check(not re.search(r'(^|/)(\.git|bin-build|PublicAPI|source-review|obj|obj-v20)(/|$)', name, re.I)
              and not Path(name).name.startswith('Siemens.Engineering')
              and not name.lower().endswith(('.log', '.pdb', '.patch', '.user', '.pfx', '.key')),
              'Private/build file in ZIP: ' + name)

    # 5. delivery record
    delivery = json.loads(content['manifest/delivery.json'].decode('utf-8-sig')) if 'manifest/delivery.json' in content else {}
    check(delivery.get('package') == package, f"manifest/delivery.json package {delivery.get('package')} != {package}")
    return report(problems, f'{len(tracked)} tracked files, {len(runtime_in_zip)} runtime files, commit {commit}')


def report(problems, summary=''):
    if problems:
        for p in problems:
            print('[FAIL] ' + p)
        print(f'Release asset verification FAILED ({len(problems)} issue(s)).')
        return 1
    print(f'Release asset verified: {summary}')
    return 0


class AssetChecks(unittest.TestCase):
    def setUp(self):
        self.parent = Path(__file__).resolve().parents[2] / 'bin-build'
        self.root = self.parent / ('asset-test-' + uuid.uuid4().hex)
        self.root.mkdir(parents=True)
        self.commit = 'a' * 40
        self.package = 'TIA_MCP_Delivery_v3.3.1_20261004'
        self.archive = self.root / (self.package + '.zip')
        rules = (Path(__file__).resolve().parents[2] / layout.DELIVERY_RULES).read_bytes()
        self.tree = {layout.DELIVERY_RULES: rules, 'README.md': b'User documentation\n',
                     'Version.props': b'excluded compiler input', 'tools/source.cs': b'excluded source'}
        self.runtime = {'runtime/v21/TiaMcp.Engine.V21.exe': b'engine',
                        'runtime/verification/NativeCallWeaver.dll': b'release-only verifier'}
        build = {'runtimeFiles': [{'path': n, 'sha256': sha(b)} for n, b in self.runtime.items()]}
        gui = {'executable': {'path': 'TiaOpenness.exe', 'sha256': sha(b'launcher')}}
        self.tree['manifest/release-build.json'] = json.dumps(build).encode()
        self.tree['manifest/configurator-build.json'] = json.dumps(gui).encode()
        delivery = {'package': self.package, 'engineBuildSha256': sha(self.tree['manifest/release-build.json']),
                    'configuratorBuildSha256': sha(self.tree['manifest/configurator-build.json'])}
        self.tree['manifest/delivery.json'] = json.dumps(delivery).encode()
        self.content = {n: b for n, b in self.tree.items() if layout.delivered(n, json.loads(rules))}
        self.content.update({'runtime/v21/TiaMcp.Engine.V21.exe': b'engine', 'TiaOpenness.exe': b'launcher'})

    def tearDown(self):
        self.assertEqual(self.root.resolve().parent, self.parent.resolve())
        shutil.rmtree(self.root)

    def verify(self, sidecar=True):
        with zipfile.ZipFile(self.archive, 'w') as archive:
            for name, data in self.content.items():
                archive.writestr(self.package + '/' + name, data)
        self.archive.with_suffix('.sha256').write_text(sha(self.archive.read_bytes()) if sidecar else '0' * 64, encoding='ascii')
        def git(command, **kwargs):
            if command[1] == 'rev-parse':
                return self.commit.encode()
            if command[1] == 'ls-tree':
                self.assertIn(self.commit, command)
                return '\0'.join(self.tree).encode()
            self.assertEqual(command[1], 'show')
            self.assertTrue(command[2].startswith(self.commit + ':'))
            return self.tree[command[2].split(':', 1)[1]]
        with patch.object(sys, 'argv', ['verify', str(self.archive), '--root', str(self.root)]), \
                patch.object(subprocess, 'check_output', side_effect=git), contextlib.redirect_stdout(io.StringIO()) as output:
            result = main()
        return result, output.getvalue()

    def test_exact_filtered_tree_passes_without_source_or_verifier(self):
        self.assertEqual(self.verify()[0], 0)

    def test_missing_included_document_fails(self):
        del self.content['README.md']
        self.assertEqual(self.verify()[0], 1)

    def test_excluded_source_and_old_generated_extras_fail(self):
        for name in ('tools/source.cs', 'RELEASE_STATUS.txt', 'manifest/release-file-hashes.json'):
            with self.subTest(name=name):
                self.content[name] = b'unexpected'
                self.assertEqual(self.verify()[0], 1)
                del self.content[name]

    def test_recorded_verifier_is_still_excluded(self):
        self.content['runtime/verification/NativeCallWeaver.dll'] = b'release-only verifier'
        self.assertEqual(self.verify()[0], 1)

    def test_missing_or_modified_binary_fails(self):
        del self.content['runtime/v21/TiaMcp.Engine.V21.exe']
        self.assertEqual(self.verify()[0], 1)
        self.content['runtime/v21/TiaMcp.Engine.V21.exe'] = b'changed'
        self.assertEqual(self.verify()[0], 1)

    def test_sidecar_digest_fails(self):
        self.assertEqual(self.verify(sidecar=False)[0], 1)

    def test_tag_bytes_win_over_checkout_bytes(self):
        (self.root / 'README.md').write_bytes(b'unrelated worktree content')
        self.assertEqual(self.verify()[0], 0)
        self.content['README.md'] = b'unrelated worktree content'
        self.assertEqual(self.verify()[0], 1)


if __name__ == '__main__':
    sys.exit(main())
