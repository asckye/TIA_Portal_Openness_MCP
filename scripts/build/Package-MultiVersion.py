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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    assert not args.output.exists(), 'Choose a new output file'
    assert not subprocess.check_output(['git', 'diff', 'HEAD', '--name-only'], cwd=ROOT).strip(), 'Commit reviewed source/manifests before packaging'
    tracked = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).decode('utf-8').split('\0')
    tracked = [name for name in tracked if name]
    files = {name: (ROOT / name).read_bytes() for name in tracked}
    records = {name: json.loads(files[f'manifest/{name}.json'].decode('utf-8-sig')) for name in ('release-build', 'configurator-build', 'multi-version-build')}
    multi = records['multi-version-build']
    assert multi['validation']['foundationTransportExecuted'] and multi['validation']['studioFunctionalTestsExecuted'], 'Run Build-MultiVersion.ps1 -Test first'
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
        assert path.suffix.lower() in ('.exe', '.dll', '.config', '.json', '.txt')
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
    main()
