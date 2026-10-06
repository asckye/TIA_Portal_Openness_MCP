"""Create a local all-version development bundle from committed source and validated binaries.

Does not tag, upload, replace an existing archive or claim native TIA acceptance.
The existing Package-Release.py remains the versioned V20/V21 publication workflow.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[2]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def runtime_path_allowed(name):
    # The pinned shared framework also contains .version files; inventory hashes bind every byte.
    if '..' in name.replace('\\', '/').split('/'):
        return False
    return name.startswith('runtime/dotnet/') or Path(name).suffix.lower() in ('.exe', '.dll', '.config', '.json', '.txt')


def self_test():
    import unittest

    class RuntimeInventoryTests(unittest.TestCase):
        def test_pinned_framework_metadata(self):
            self.assertTrue(runtime_path_allowed('runtime/dotnet/shared/Microsoft.NETCore.App/10.0.12/.version'))

        def test_binary_and_configuration_files(self):
            for name in ('runtime/studio/TiaOpenness.exe', 'runtime/v14sp1/TiaMcp.FoundationHost.dll',
                         'runtime/v21/TiaMcp.Engine.V21.exe.config', 'runtime/v14sp1/release-key.txt'):
                self.assertTrue(runtime_path_allowed(name))

        def test_unreviewed_extensions_outside_framework(self):
            self.assertFalse(runtime_path_allowed('runtime/studio/unreviewed.bin'))
            self.assertFalse(runtime_path_allowed('runtime/dotnet-other/.version'))
            self.assertFalse(runtime_path_allowed('runtime/dotnet/../studio/unreviewed.bin'))

    result = unittest.TextTestRunner().run(unittest.defaultTestLoader.loadTestsFromTestCase(RuntimeInventoryTests))
    return 0 if result.wasSuccessful() else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if args.output is None:
        parser.error('--output is required unless --self-test is used')
    assert not args.output.exists(), 'Choose a new output file'
    assert not subprocess.check_output(['git', 'diff', 'HEAD', '--name-only'], cwd=ROOT).strip(), 'Commit reviewed source/manifests before packaging'
    tracked = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).decode('utf-8').split('\0')
    tracked = [name for name in tracked if name]
    files = {name: (ROOT / name).read_bytes() for name in tracked}
    for name in ('release-build', 'configurator-build', 'multi-version-build'):
        assert f'manifest/{name}.json' in files, f'Missing {name} record; run the complete release build chain first'
    records = {name: json.loads(files[f'manifest/{name}.json'].decode('utf-8-sig')) for name in ('release-build', 'configurator-build', 'multi-version-build')}
    multi = records['multi-version-build']
    assert multi['validation']['foundationTransportExecuted'] and multi['validation']['studioFunctionalTestsExecuted'], 'Run dotnet run --project build-tools/release -- build-multi-version -Test first'
    assert set(multi['studioReleaseKeys']) == {'14sp1', '15.1', '16', '17', '18', '19', '20', '21'}
    for record in records.values():
        for row in record['sourceFiles']:
            actual = files[row['path']]
            if not row['path'].lower().endswith(('.ttf', '.otf')):
                actual = actual.decode('utf-8-sig').replace('\r\n', '\n').encode('utf-8')
            assert sha(actual) == row['sha256'], 'Source changed after testing: ' + row['path']
    binaries = multi['files'] + records['release-build']['runtimeFiles'] + [records['configurator-build']['executable']]
    for row in binaries:
        name = row['path']
        path = (ROOT / name).resolve()
        assert path.is_relative_to(ROOT)
        assert runtime_path_allowed(name), 'Unreviewed runtime file: ' + name
        assert not path.name.startswith('Siemens.Engineering'), 'Siemens PublicAPI must not be redistributed'
        data = path.read_bytes()
        assert sha(data) == row['sha256'], 'Binary changed after testing: ' + name
        files[name] = data
    for key in multi['studioReleaseKeys']:
        assert f"runtime/v{key}/{'TiaMcp.Engine.V' + key if key in ('20', '21') else 'TiaMcp.FoundationHost'}.exe" in files
        assert f'runtime/studio/bridge/adapters/v{key}/TiaOpenness.Openness.dll' in files
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT).decode().strip()
    files['MULTIVERSION-BUILD.json'] = json.dumps({'commit': commit, 'nativeAcceptance': 'NOT RUN for newly enabled targets', 'buildRecord': 'manifest/multi-version-build.json', 'toolMatrix': 'manifest/version-tools.json'}, indent=2).encode()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.output, 'x', zipfile.ZIP_DEFLATED) as archive:
        for name, data in sorted(files.items()): archive.writestr(name, data)
    with zipfile.ZipFile(args.output) as archive:
        assert archive.testzip() is None
        assert set(archive.namelist()) == set(files)
        for name, data in files.items(): assert sha(archive.read(name)) == sha(data), name
    digest = sha(args.output.read_bytes())
    args.output.with_suffix(args.output.suffix + '.sha256').write_text(digest + '  ' + args.output.name + '\n', 'utf-8')
    print(f'Validated {len(files)} files: {args.output}; SHA-256 {digest}')


if __name__ == '__main__':
    raise SystemExit(main())
