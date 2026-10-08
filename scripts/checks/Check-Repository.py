"""Check local documentation links and repository entrypoints without TIA or dotnet."""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[2]
SKIP = {'.git', 'bin-build', 'bin', 'bin-v20', 'obj', 'obj-v20', '__pycache__', 'TiaMcp_Output', '.pytest_cache'}
_spec = importlib.util.spec_from_file_location('bundle_layout', Path(__file__).with_name('Check-BundleLayout.py'))
layout = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(layout)


def documents(root):
    for path in root.rglob('*.md'):
        if not SKIP.intersection(path.relative_to(root).parts):
            yield path


def local_target(root, source, target):
    target = unquote(target.strip('<>').split('#', 1)[0])
    if not target or re.match(r'^[\w+.-]+:', target):
        return None
    path = (source.parent / target).resolve()
    if not path.is_relative_to(root.resolve()):
        return 'link leaves repository: ' + target
    if not path.exists():
        return 'missing link: ' + target
    return None


def product_name_errors(root, names):
    # Frozen contracts and publication/native evidence retain the names they measured.
    historical = ('manifest/contracts/', 'manifest/history/', 'manifest/publication-',
                  'docs/releases/', 'docs/archive/', 'docs/development/evidence/',
                  'reference/siemens-openness/', 'reference/siemens-code-snippets/', 'third_party/')
    evidence = {'CHANGELOG.md', 'manifest/ecosystem-validation.json',
                'manifest/release-build.json', 'manifest/multi-version-build.json', 'manifest/configurator-build.json'}
    old_engine = 'TiaMcp' + 'Server'
    old_launcher = 'TiaMcp' + 'Configurator'
    pattern = re.compile(old_engine + r'\.(?:exe|dll|deps\.json|runtimeconfig\.json)\b|'
                         + old_launcher + r'\.exe\b', re.I)
    identity = re.compile(r'(?:Get-Process|GetProcessesByName|InternalsVisibleTo|Assembly\.Load|<AssemblyName>|-match).*'
                          + r'(?:' + old_engine + '|' + old_launcher + r')(?:[\"\x27<)|])', re.I)
    extensions = {'.cs', '.csproj', '.props', '.targets', '.ps1', '.psm1', '.py', '.md', '.json',
                  '.yml', '.yaml', '.config', '.bat', '.cmd', '.sh', '.toml', '.xml', '.xaml', '.svg'}
    errors = []
    for name in sorted(set(names)):
        if name.startswith(historical) or name in evidence or SKIP.intersection(Path(name).parts):
            continue
        if pattern.search(name):
            errors.append('Retired product filename: ' + name)
        if Path(name).suffix.lower() not in extensions and name != '.gitignore':
            continue
        path = root / name
        if not path.is_file():
            continue
        cleanup_paths = set()
        if name == 'scripts/operations/delivery-files.json':
            rules = json.loads(path.read_text(encoding='utf-8-sig'))
            cleanup_paths = set(rules['exclude']['files']) & set(rules['legacyCleanup']['files'])
        for line, text in enumerate(path.read_text(encoding='utf-8-sig').splitlines(), 1):
            if cleanup_paths:
                try:
                    if json.loads(text.strip().rstrip(',')) in cleanup_paths:
                        continue
                except (ValueError, TypeError):
                    pass
            if pattern.search(text) or identity.search(text):
                errors.append(f'{name}:{line}: retired product reference')
    return errors


def forbidden_script_errors(names):
    """Reject tracked Windows command and PowerShell files."""
    forbidden = {'.ps1', '.psm1', '.bat', '.cmd'}
    return ['Forbidden tracked script file: ' + name for name in sorted(set(names))
            if Path(name).suffix.lower() in forbidden]


def product_name_self_test():
    import uuid
    scratch = ROOT / 'bin-build' / ('product-names-' + uuid.uuid4().hex)
    scratch.mkdir(parents=True)
    try:
        root = scratch
        old_engine = 'TiaMcp' + 'Server'
        old = old_engine + '.exe'
        launcher = 'TiaMcp' + 'Configurator.exe'
        cases = {'scripts/start.ps1': old, 'src/find.cs': old.upper(),
                 'docs/current.md': launcher, 'manifest/current.json': old,
                 'docs/releases/v3.md': old, 'manifest/contracts/v4/responses/21.json': old,
                 'src/namespace.cs': 'namespace TiaMcpServer; namespace TiaMcpConfigurator;',
                 'scripts/current.ps1': 'TiaMcp.Engine.V20.exe TiaMcp.Engine.V21.exe TiaMcp.FoundationHost.exe TiaOpenness.exe',
                 'scripts/process.ps1': 'Get-Process -Name "' + old_engine + '"',
                 'src/identity.csproj': '<AssemblyName>' + old_engine + '</AssemblyName>',
                 'scripts/dumps.ps1': "-match '^(Portal|" + old_engine + r")\.'",
                 'scripts/build/Build-Release.ps1': old + '; runtime/v21/' + old,
                 launcher: 'retired launcher filename'}
        for name, value in cases.items():
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(value, encoding='utf-8')
        errors = product_name_errors(root, cases)
        assert len(errors) == 9, errors
        assert len(product_name_errors(root, ['runtime/v21/' + old])) == 1
        print('Product-name self-tests: 13 passed, 0 failed.')
    finally:
        assert scratch.resolve().parent == (ROOT / 'bin-build').resolve()
        for path in sorted(scratch.rglob('*'), key=lambda p: len(p.parts), reverse=True):
            if path.is_file():
                path.unlink()
            else:
                path.rmdir()
        scratch.rmdir()


def check(root, no_binaries=False, package_mode=False):
    rules = layout.load_delivery(root)
    package_mode = package_mode or not (root / 'Version.props').is_file()
    errors = archive_errors(root) if not package_mode else []
    if not package_mode:
        errors.extend(changelog_version_errors(root))
    if (root / '.git').exists():
        names = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=root).decode('utf-8').split('\0')
        tracked_names = subprocess.check_output(['git', 'ls-files', '-z'], cwd=root).decode('utf-8').split('\0')
    else:
        names = [path.relative_to(root).as_posix() for path in root.rglob('*') if path.is_file()]
        tracked_names = names
    remaining_scripts = forbidden_script_errors(name for name in tracked_names if name and (root / name).is_file())
    errors.extend(remaining_scripts)
    errors.extend(product_name_errors(root, filter(None, names)))
    count = 0
    for source in documents(root):
        count += 1
        text = source.read_text(encoding='utf-8-sig')
        # Ignore code fences: examples can contain placeholder Markdown.
        text = re.sub(r'^```[^\n]*\n.*?^```\s*$', '', text, flags=re.M | re.S)
        relative = source.relative_to(root).as_posix()
        shipped = not package_mode and layout.delivered(relative, rules)
        for match in re.finditer(r'\]\((<[^>]+>|[^\s)]+)(?:\s+"[^"]*")?\)', text):
            error = local_target(root, source, match[1])
            if error:
                errors.append(f'{relative}: {error}')
            elif shipped:
                # A document in the runtime-only delivery may only link to files that ship with it; anything else
                # needs an absolute GitHub link (the package check would otherwise fail only at release time).
                target = unquote(match[1].strip('<>').split('#', 1)[0])
                if target and not re.match(r'^[\w+.-]+:', target):
                    linked = (source.parent / target).resolve().relative_to(root.resolve()).as_posix()
                    if not layout.delivery_resource(linked, rules):
                        errors.append(f'{relative}: shipped document links outside the delivery: {target}')
    def read(name):
        return json.loads((root / name).read_text(encoding='utf-8-sig'))
    def required(name, label):
        target = (root / name).resolve()
        if not target.is_relative_to(root.resolve()):
            errors.append(f'{label}: external path: {name}')
        elif no_binaries and (name.endswith('.exe') or name == 'runtime/tools/TiaMcp.Updater.exe.config' or (name.startswith('runtime/') and name != 'runtime/README.md')):
            return   # build outputs live outside Git since 2.8.1
        elif (not package_mode and name in ('runtime/tools/TiaMcp.Updater.exe', 'runtime/tools/TiaMcp.Updater.exe.config')
              and (root / 'bin-build' / 'updater' / Path(name).name).is_file()):
            return   # outside a package the updater is staged in bin-build/updater; Package-Release copies it to runtime/tools
        elif not target.exists():
            errors.append(f'{label}: missing or external path: {name}')
    package = read('manifest/package-manifest.json')
    for key, value in package['entrypoints'].items():
        if key == 'mcpServerArgs':
            if value != ['--release-key', '21']:
                errors.append('package entry mcpServerArgs: expected the default release key')
            continue
        # The generated manifest schema still carries this legacy repository-only key;
        # the validator now lives in the C# release tool and is not shipped.
        if key == 'bundleValidationScript' and (package_mode or not (root / value).is_file()):
            continue
        required(value, 'package entry ' + key)
    required(package['cli']['exe'], 'CLI')
    for path in set(rules['include']['files']) | set(rules['requiredFiles']):
        required(path, 'delivery file')
    for prefix in rules['include']['prefixes']:
        if no_binaries and prefix == 'runtime/':
            continue
        required(prefix, 'delivery folder')
    if package_mode:
        for path in layout.resource_paths((ROOT / layout.SOURCE).read_text(encoding='utf-8-sig')):
            required(path, 'bundle resource')
            if not layout.delivery_resource(path, rules):
                errors.append('Bundle resource excluded from delivery: ' + path)
        for path in read('templates/project-blueprints/full_plc_hmi_project.json')['requiredBundleFiles']:
            required(path, 'blueprint')
        roster = read('manifest/tools-list.json')
        names = [row['name'] for row in roster['tools']]
        if len(names) != len(set(names)) or len(names) != roster['toolCount'] or len(names) != package['capabilities']['mcpToolCount']:
            errors.append('Tool inventory count/uniqueness differs from package metadata')
        for path in root.rglob('*'):
            if path.is_file() and not layout.delivered(path.relative_to(root).as_posix(), rules):
                errors.append('File outside delivery set: ' + path.relative_to(root).as_posix())
        if not no_binaries:
            inventory = read('manifest/release-build.json')['runtimeFiles'] + read('manifest/multi-version-build.json')['files']
            for row in inventory:
                if layout.delivered(row['path'], rules):
                    required(row['path'], 'recorded runtime')
        return count, errors
    for row in layout.source_roots(root)['requiredFiles']:
        if 'repository' in row['consumers']:
            required(row['path'], row['repositoryLabel'])
    for major in (20, 21):
        required(f'runtime/v{major}/worker/TiaMcp.Runtime.dll', 'runtime channel assembly')
        required(f'runtime/v{major}/worker/TiaMcp.Adapter.{major}.dll', 'engine worker adapter')
        required(f'runtime/v{major}/worker/TiaMcp.Adapters.Contracts.dll', 'adapter contracts assembly')
    for name in ('NativeCallWeaver.dll', 'NativeCallWeaver.deps.json', 'NativeCallWeaver.runtimeconfig.json', 'Mono.Cecil.dll'):
        required('runtime/verification/' + name, 'packaged native verifier')
    for key in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21'):
        # The .NET 10 Foundation hosts take System.Text.Json, Encodings.Web and IO.Pipelines from the bundled shared framework.
        required(f'runtime/v{key}/TiaMcp.WorkerChannel.dll', 'worker channel host')
        for name in ('TiaMcp.WorkerChannel.dll', 'System.Text.Json.dll', 'System.Text.Encodings.Web.dll', 'System.IO.Pipelines.dll', 'Microsoft.Bcl.AsyncInterfaces.dll', 'System.Buffers.dll', 'System.Memory.dll', 'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll', 'System.Threading.Tasks.Extensions.dll'):
            required(f'runtime/v{key}/worker/' + name, 'worker channel dependency')
    for name in read('templates/project-blueprints/full_plc_hmi_project.json')['requiredBundleFiles'] + contract_required_paths():
        required(name, 'blueprint/contract snapshot')
    required('runtime/studio/TiaMcp.WorkerChannel.dll', 'Studio client channel')
    for name in ('TiaMcp.WorkerChannel.dll', 'System.Text.Json.dll', 'System.Text.Encodings.Web.dll', 'System.IO.Pipelines.dll', 'Microsoft.Bcl.AsyncInterfaces.dll', 'System.Buffers.dll', 'System.Memory.dll', 'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll', 'System.Threading.Tasks.Extensions.dll'):
        required('runtime/studio/bridge/' + name, 'Studio bridge channel dependency')
    roster = read('manifest/tools-list.json')
    names = [row['name'] for row in roster['tools']]
    if len(names) != len(set(names)) or len(names) != roster['toolCount'] or len(names) != package['capabilities']['mcpToolCount']:
        errors.append('Tool inventory count/uniqueness differs from package metadata')
    swallowed = Path(__file__).with_name('Check-SwallowedExceptions.py')
    result = subprocess.run([sys.executable, str(swallowed), '--root', str(root)])
    if result.returncode:
        errors.append('Swallowed-exception baseline check failed (see diagnostics above)')
    envelopes = Path(__file__).with_name('Inventory-ResponseEnvelopes.py')
    result = subprocess.run([sys.executable, str(envelopes), '--root', str(root)])
    if result.returncode:
        errors.append('Response-envelope baseline check failed (see diagnostics above)')
    for name in ('Check-CommentHygiene.py', 'Check-McpText.py'):
        checker = Path(__file__).with_name(name)
        result = subprocess.run([sys.executable, str(checker), '--root', str(root)])
        if result.returncode:
            errors.append(f'{name} baseline check failed (see diagnostics above)')
    layout_script = Path(__file__).with_name('Check-BundleLayout.py')
    result = subprocess.run([sys.executable, str(layout_script), '--root', str(root)])
    if result.returncode:
        errors.append('Bundle-layout resource check failed (see diagnostics above)')
    return count, errors


def changelog_version_errors(root):
    """The strict delivery check compares the newest CHANGELOG entry with the packaged version, so an entry written ahead of
    Version.props fails every release candidate; the C# release command adds the entry and bumps the version in one release commit."""
    props = (root / 'Version.props').read_text(encoding='utf-8-sig')
    release = re.search(r'<TiaMcpRelease>([^<]+)</TiaMcpRelease>', props)
    entry = re.search(r'(?m)^## \[(\d+\.\d+\.\d+)\]', (root / 'CHANGELOG.md').read_text(encoding='utf-8-sig'))
    if not release or not entry:
        return ['CHANGELOG.md or Version.props has no release version']
    if entry.group(1) != release.group(1).strip():
        return [f'Newest CHANGELOG entry {entry.group(1)} differs from Version.props {release.group(1).strip()}; '
                'write the entry in the release commit (release command), keep drafts in docs/releases']
    return []


def changelog_self_test():
    import shutil
    import uuid
    root = ROOT / 'bin-build' / ('changelog-check-' + uuid.uuid4().hex)
    root.mkdir(parents=True)
    try:
        (root / 'Version.props').write_text('<Project><PropertyGroup><TiaMcpRelease>3.3.0</TiaMcpRelease></PropertyGroup></Project>', encoding='utf-8')
        (root / 'CHANGELOG.md').write_text('# Changes\n\n## [3.3.0] - 2026-10-03\n', encoding='utf-8')
        assert not changelog_version_errors(root)
        (root / 'CHANGELOG.md').write_text('# Changes\n\n## [4.0.0] - draft\n\n## [3.3.0] - 2026-10-03\n', encoding='utf-8')
        assert changelog_version_errors(root), 'an entry ahead of Version.props must fail'
    finally:
        shutil.rmtree(root)
    print('CHANGELOG version self-tests: 2 passed, 0 failed.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--self-test', action='store_true', help='Exercise retired product reference rejection and historical/namespace exemptions')
    parser.add_argument('--package-mode', action='store_true', help='Validate the runtime-only delivery, also detected when Version.props is absent')
    parser.add_argument('--no-binaries', action='store_true',
                        help='source checkout without build outputs: skip the existence of runtime/*/TiaMcp.Engine.V21.exe and TiaOpenness.exe (not tracked since 2.8.1)')
    args = parser.parse_args()
    if args.self_test:
        script_names = ['scripts/old.ps1', 'hooks/old.PSM1', 'scripts/old.bat', 'scripts/old.cmd', 'scripts/current.py']
        assert len(forbidden_script_errors(script_names)) == 4
        assert forbidden_script_errors(['scripts/current.py']) == []
        print('Tracked script language self-tests: 5 passed, 0 failed.')
        product_name_self_test()
        archive_self_test()
        changelog_self_test()
        return 0
    # Negative sentinel: a broken link and a traversal must fail; a valid file must pass.
    source = args.root / 'docs/README.md'
    assert local_target(args.root, source, '../README.md') is None
    assert local_target(args.root, source, '../__missing_repository_check__.md')
    assert local_target(args.root, source, '../../../__outside__.md')
    count, errors = check(args.root, args.no_binaries, args.package_mode)
    for error in errors:
        print('[FAIL] ' + error)
    print(f'Checked {count} Markdown files and repository entrypoints; {len(errors)} issue(s).')
    return bool(errors)


def contract_required_paths():
    return ['manifest/history/contracts-v3/README.md', 'manifest/history/contracts-v3/provenance.json'] + [
        f'{directory}/{category}/{release}.json'
        for directory in ('manifest/history/contracts-v3', 'manifest/contracts/v4')
        for category in ('baseline', 'responses')
        for release in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21')]


def archive_errors(root):
    import hashlib
    archive = root / 'manifest/history/contracts-v3'
    errors = []
    for category in ('baseline', 'responses'):
        if (root / 'manifest/contracts' / category).exists():
            errors.append('Retired contract directory returned: manifest/contracts/' + category)
    try:
        provenance_path = archive / 'provenance.json'
        # Freeze the provenance too: editing recorded hashes must not bless changed evidence.
        provenance_sha256 = '53cd89d882e8668d5bf3fdb15d6e34b20758424552ccdbf0fbad33db5eba82ce'
        if hashlib.sha256(provenance_path.read_bytes()).hexdigest() != provenance_sha256:
            raise ValueError('Contract archive provenance changed')
        provenance = json.loads(provenance_path.read_text(encoding='utf-8'))
        expected = {f'{category}/{release}.json' for category in ('baseline', 'responses')
                    for release in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21')}
        records = {record['path']: record for record in provenance['files']}
        if set(records) != expected or len(provenance['files']) != 16 or provenance['readOnly'] is not True:
            raise ValueError('Contract archive provenance inventory/read-only rule changed')
        actual = {path.relative_to(archive).as_posix() for path in archive.rglob('*') if path.is_file()}
        expected |= {'README.md', 'provenance.json'}
        if actual != expected:
            errors.append(f'Contract archive inventory differs: missing={sorted(expected - actual)}, extra={sorted(actual - expected)}')
        for name, digest in [(name, row['sha256']) for name, row in records.items()] + [
                ('README.md', provenance['readmeSha256'])]:
            path = archive / name
            if not path.is_file() or path.is_symlink() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
                errors.append('Contract archive SHA-256 mismatch or missing file: ' + name)
    except (OSError, ValueError, KeyError, TypeError) as error:
        errors.append('Contract archive: ' + str(error))
    return errors


def archive_self_test():
    import shutil
    import uuid
    (ROOT / 'bin-build').mkdir(exist_ok=True)
    root = ROOT / 'bin-build' / ('archive-guard-' + uuid.uuid4().hex)
    root.mkdir()
    try:
        archive = root / 'manifest/history/contracts-v3'
        shutil.copytree(ROOT / 'manifest/history/contracts-v3', archive)
        assert not archive_errors(root), archive_errors(root)
        path = archive / 'baseline/21.json'
        original = path.read_bytes()
        path.write_bytes(bytes([original[0] ^ 1]) + original[1:])
        assert any('SHA-256 mismatch' in error for error in archive_errors(root))
        path.unlink()
        assert any('missing=' in error for error in archive_errors(root))
        path.write_bytes(original)
        extra = archive / 'responses/22.json'
        extra.write_bytes(b'{}')
        assert any('extra=' in error for error in archive_errors(root))
        extra.unlink()
        for name in ('README.md', 'provenance.json'):
            path = archive / name
            original = path.read_bytes()
            path.write_bytes(original + b' ')
            assert archive_errors(root), name
            path.write_bytes(original)
        for category in ('baseline', 'responses'):
            path = root / 'manifest/contracts' / category
            path.mkdir(parents=True)
            (path / 'unexpected.txt').write_bytes(b'old-location write')
            assert any('Retired contract directory' in error for error in archive_errors(root))
            (path / 'unexpected.txt').unlink()
            path.rmdir()
    finally:
        assert root.resolve().parent == (ROOT / 'bin-build').resolve()
        shutil.rmtree(root)
    print('Contract archive self-tests: 8 passed, 0 failed.')


if __name__ == '__main__':
    sys.exit(main())
