"""Verify a delivery ZIP against the Git checkout it claims to come from.

Since 2.8.1 the binaries are not in Git and the maintainer uploads the ZIP from the local machine, so this is the
independent check that the published asset is exactly the reviewed tree plus the validated binaries:

  1. the ZIP's SHA-256 equals the .sha256 sidecar next to it;
  2. every file tracked in the checkout is inside the ZIP with identical bytes, and nothing tracked is missing;
  3. runtime/v20 + runtime/v21 inside the ZIP are exactly the inventory of manifest/release-build.json with the
     recorded hashes, and TiaMcpConfigurator.exe matches manifest/configurator-build.json;
  4. the only extra files are RELEASE_STATUS.txt and manifest/release-file-hashes.json, whose "Source commit" /
     sourceCommit equal the checkout HEAD (or --commit);
  5. manifest/delivery.json names the package the ZIP is called after.

Run locally by Release.ps1 before the upload and by the "Verify published release" workflow after it.
Exit code 0 = verified; anything else prints the first mismatch and exits 1.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import zipfile


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('zip', type=Path, help='the TIA_MCP_Delivery_vX.Y.Z_YYYYMMDD.zip to verify')
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2], help='the Git checkout (default: this repository)')
    parser.add_argument('--commit', default='', help='expected source commit (default: HEAD of --root)')
    parser.add_argument('--git', default='git')
    args = parser.parse_args()
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
    tracked = [n for n in subprocess.check_output([args.git, 'ls-files', '-z'], cwd=root).decode('utf-8').split('\0') if n]

    with zipfile.ZipFile(archive) as z:
        check(z.testzip() is None, 'ZIP integrity failure')
        names = z.namelist()
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
        local = (root / name)
        if not check(name in content, f'tracked file missing from ZIP: {name}'):
            continue
        check(same(content[name], local.read_bytes()), f'ZIP differs from checkout: {name}')
    tracked_set = set(tracked)

    # 3. binaries against the committed build records
    build = json.loads((root / 'manifest/release-build.json').read_text(encoding='utf-8-sig'))
    gui = json.loads((root / 'manifest/configurator-build.json').read_text(encoding='utf-8-sig'))
    runtime_in_zip = {n for n in content if n.startswith(('runtime/v20/', 'runtime/v21/'))}
    recorded = {row['path']: row['sha256'] for row in build['runtimeFiles']}
    check(runtime_in_zip == set(recorded), 'runtime inventory in ZIP differs from manifest/release-build.json: '
          + ', '.join(sorted(runtime_in_zip ^ set(recorded))[:10]))
    for path, expected in recorded.items():
        if path in content:
            check(sha(content[path]) == expected, f'runtime hash differs from release-build.json: {path}')
    exe = gui['executable']['path']
    if check(exe in content, f'{exe} missing from ZIP'):
        check(sha(content[exe]) == gui['executable']['sha256'], f'{exe} differs from manifest/configurator-build.json')
    check(not any(n.startswith(('runtime/v20/', 'runtime/v21/')) or n == 'TiaMcpConfigurator.exe' for n in tracked_set),
          'binaries are tracked in Git although the 2.8.1 policy keeps them out')

    # 4. extras
    allowed_extra = {'RELEASE_STATUS.txt', 'manifest/release-file-hashes.json'}
    extras = set(content) - tracked_set - runtime_in_zip - {exe}
    check(extras <= allowed_extra, 'unexpected files in ZIP: ' + ', '.join(sorted(extras - allowed_extra)[:10]))
    if 'RELEASE_STATUS.txt' in content:
        status = content['RELEASE_STATUS.txt'].decode('utf-8')
        found = re.search(r'Source commit: ([0-9a-f]{40})', status)
        check(found is not None and found.group(1) == commit, f'RELEASE_STATUS.txt source commit != {commit}')
    if 'manifest/release-file-hashes.json' in content:
        hashes = json.loads(content['manifest/release-file-hashes.json'].decode('utf-8'))
        check(hashes.get('sourceCommit') == commit, f'release-file-hashes.json sourceCommit != {commit}')
        check(hashes.get('package') == package, 'release-file-hashes.json package name differs')
        for name, expected in hashes.get('files', {}).items():
            if name != 'manifest/release-file-hashes.json' and name in content:
                check(sha(content[name]) == expected, f'release-file-hashes.json hash differs: {name}')

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


if __name__ == '__main__':
    sys.exit(main())
