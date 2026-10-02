"""Build the complete public delivery ZIP from the clean, committed tree plus the local build outputs.

Since 2.8.1 the engine runtimes (runtime/v20, runtime/v21) and TiaMcpConfigurator.exe are not tracked in Git:
Build-Release.ps1 produces them locally and records their hashes in manifest/release-build.json and
manifest/configurator-build.json, which ARE committed. Packaging takes the tracked files from Git, adds the local
binaries and refuses when a binary is missing or differs from its validated hash. Release.ps1 uploads the ZIP.
"""
import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import zipfile
import xml.etree.ElementTree as ET


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--git', default='git')
    parser.add_argument('--output-directory', type=Path, help='Optional new local output directory; existing archives/stages are never overwritten')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    subprocess.run([sys.executable, str(root / 'scripts/checks/Check-Repository.py')], check=True)

    def git(*values):
        return subprocess.check_output([args.git, *values], cwd=root)

    require(not git('status', '--porcelain', '--untracked-files=normal').strip(),
            'Review and commit the source and manifests before packaging')
    commit = git('rev-parse', 'HEAD').decode().strip()
    files = {}
    tracked = [name for name in git('ls-files', '-z').decode('utf-8').split('\0') if name]
    require(not any(name.startswith(('runtime/v20/', 'runtime/v21/')) or name == 'TiaMcpConfigurator.exe' for name in tracked),
            'Binaries must not be tracked in Git (2.8.1 policy): git rm --cached runtime/v20 runtime/v21 TiaMcpConfigurator.exe')
    # Local build outputs (ignored by Git): exactly the runtime inventory that Build-Release recorded plus the
    # configurator - never "whatever is on disk" (an engine started locally leaves TiaMcpServer.startup.log there).
    inventory = json.loads((root / 'manifest/release-build.json').read_text(encoding='utf-8-sig'))['runtimeFiles']
    binaries = ['TiaMcpConfigurator.exe'] + sorted(row['path'] for row in inventory)
    on_disk = {p.relative_to(root).as_posix() for folder in ('runtime/v20', 'runtime/v21') for p in (root / folder).rglob('*') if p.is_file()}
    for extra in sorted(on_disk - set(binaries)):
        print(f'note: {extra} is on disk but not in the validated runtime inventory; left out of the package')
    for name in tracked + binaries:
        path = (root / name).resolve()
        require(path.is_relative_to(root) and path.is_file(), f'Missing file (run Build-Release.ps1 for the binaries): {name}')
        require(not re.search(r'(^|/)(\.git|bin-build|PublicAPI|source-review|obj|obj-v20)(/|$)', name, re.I), f'Private/build path: {name}')
        require(not name.lower().endswith(('.log', '.pdb', '.patch', '.user', '.pfx', '.key')),
                f'Unexpected release file: {name}')
        require(not path.name.startswith('Siemens.Engineering'), f'PublicAPI must not be redistributed: {name}')
        files[name] = path.read_bytes()
    require(sorted(n for n in files if n.endswith('.exe')) ==
            ['TiaMcpConfigurator.exe', 'runtime/v20/TiaMcpServer.exe', 'runtime/v21/TiaMcpServer.exe'], 'Configurator and both runtime EXEs are required')

    metadata = json.loads(files['manifest/release-build.json'].decode('utf-8-sig'))
    delivery = json.loads(files['manifest/delivery.json'].decode('utf-8-sig'))
    require(sha(files['manifest/release-build.json']) == delivery['engineBuildSha256'], 'Engine build record changed after delivery preparation')
    require(sha(files['manifest/configurator-build.json']) == delivery['configuratorBuildSha256'], 'Configurator build record changed after delivery preparation')
    require(delivery['engineRelease'] == metadata['release'] and delivery['fileVersion'] == metadata['fileVersion'], 'Engine version differs from delivery record')
    release, version, package = (delivery[k] for k in ('release', 'fileVersion', 'package'))
    require(re.fullmatch(r'\d+\.\d+\.\d+', release) is not None, 'Release version must be X.Y.Z')
    date = delivery['releaseDate']
    require(re.fullmatch(r'\d{8}', date) is not None, 'Release date must be YYYYMMDD')
    datetime.strptime(date, '%Y%m%d')
    require(package == f'TIA_MCP_Delivery_v{release}_{date}', 'Use TIA_MCP_Delivery_vX.Y.Z_YYYYMMDD for the complete V20/V21 delivery')
    for project in ('TiaMcpServer.V21.csproj', 'TiaMcpServer.V20.csproj'):
        xml = ET.fromstring(files[f'tools/tiaportal-mcp/src/TiaMcpServer/{project}'])
        require(xml.findtext('.//FileVersion') == version and xml.findtext('.//InformationalVersion') == metadata['release'], 'Source version differs from validated build')
    runtime_names = {n for n in files if n.startswith(('runtime/v20/', 'runtime/v21/'))}
    require(runtime_names == {r['path'] for r in metadata['runtimeFiles']}, 'Runtime file inventory changed after validation')
    for row in metadata['runtimeFiles']:
        require(sha(files[row['path']]) == row['sha256'], f"Runtime changed: {row['path']}")
    source_names = {n for n in files if n.startswith(('tools/tiaportal-mcp/src/', 'tools/tiaportal-mcp/tests/', 'tools/native-call-weaver/', 'tools/third-party/TiaGitAddIn.Core/', 'tools/third-party/SiemensOpcUaModelled/')) and Path(n).suffix in ('.cs', '.csproj', '.props', '.targets', '.xml')}
    require(source_names == {r['path'] for r in metadata['sourceFiles']}, 'Compiler/test input inventory changed')
    for row in metadata['sourceFiles']:
        data = files[row['path']].decode('utf-8-sig').replace('\r\n', '\n').encode('utf-8')
        require(sha(data) == row['sha256'], f"Source changed after validation: {row['path']}")
    require(metadata['validation']['offlinePassed'] > 0, 'No offline suite result')
    for major in ('V20', 'V21'):
        proof = metadata['validation']['runtimes'][major].get('nativeDiagnostics', {})
        require(proof.get('status') == 'passed' and proof.get('uncoveredSupportedBoundaries') == 0 and proof.get('sites', 0) > 1000,
                f'{major}: missing native diagnostic coverage validation')
        require(proof['jitPrepared'] + proof['openGenericWrappers'] == proof['sites'], f'{major}: wrapper inventory/JIT mismatch')
        require(proof['scriptSha256'] == sha(files['scripts/checks/Test-NativeDiagnostics.py']), f'{major}: diagnostic test driver changed after validation')
    extended = json.loads(files['manifest/local-stability-extended.json'].decode('utf-8-sig')) if 'manifest/local-stability-extended.json' in files else None
    if extended is not None:
        require(extended['release'] == release and extended['status'] == 'passed', 'Extended stability record does not match this release')
    for major in (20, 21):
        checks = metadata['validation']['runtimes'][f'V{major}']
        require(checks['httpPassed'] > 0 and checks['hmiPassed'] > 0 and checks['migrationAssembly'] == 'passed', f'V{major} validation incomplete')
        require(checks.get('resourceDiscoveryPassed', 0) > 0, f'V{major} resource discovery validation missing')
        require(checks.get('nativeExportRemotingPassed', 0) > 0, f'V{major} native export remoting validation missing')
        proofs = [checks.get('localStability', {})]
        isolation = checks.get('workerIsolation')
        if isolation is not None:
            require(isolation.get('faultChecksPassed', 0) >= 25 and isolation.get('protocolChecksPassed', 0) >= 58,
                    f'V{major} worker fault/protocol validation incomplete')
            require(isolation.get('protocolScriptSha256') == sha(files['scripts/checks/Test-WorkerIsolation.py']),
                    f'V{major} worker protocol script changed after validation')
            require(checks.get('isolatedLocalStability', {}).get('isolatedWorker') is True,
                    f'V{major} isolated stability proof missing')
            proofs.append(checks['isolatedLocalStability'])
        if extended is not None:
            proofs.append(extended['runtimes'][f'V{major}'])
            if 'isolatedRuntimes' in extended:
                isolated_extended = extended['isolatedRuntimes'][f'V{major}']
                require(isolated_extended.get('isolatedWorker') is True,
                        f'V{major} extended isolated proof is not an isolated run')
                proofs.append(isolated_extended)
        for stability in proofs:
            require(stability.get('status') == 'passed' and stability.get('rounds', 0) >= 10,
                    f'V{major} local stability validation missing')
            require(stability['runtimeSha256'] == sha(files[f'runtime/v{major}/TiaMcpServer.exe']),
                    f'V{major} stability test used a different runtime')
            for field, path in (('scriptSha256', 'scripts/checks/Test-LocalStability.py'),
                                ('resourceHelperSha256', 'scripts/checks/Test-ResourceDiscovery.py')):
                require(stability.get(field) == sha(files[path]), f'Stability test script changed after validation: {path}')
            runs = stability.get('runs', [])
            require(len(runs) == 4 and {(r['transport'], r['profile']) for r in runs} ==
                    {('stdio', 'full'), ('stdio', 'lite'), ('http', 'full'), ('http', 'lite')},
                    f'V{major} stability profile/transport coverage incomplete')
            require(all(r['unexpectedExits'] == 0 and r['unexpectedFailures'] == 0 and r['tiaConnected'] is False
                        and r['unmatchedJournalEntries'] == 0 and r['measuredToolCalls'] > 0 for r in runs),
                    f'V{major} local stability checks failed')
        require(f'runtime/v{major}/Esprima.dll' in files, f'V{major} script parser missing')
    gui = json.loads(files['manifest/configurator-build.json'].decode('utf-8-sig'))
    require(gui['testsPassed'] > 0, 'Missing configurator test result')
    require(sha(files[gui['executable']['path']]) == gui['executable']['sha256'], 'Configurator EXE changed after validation')
    gui_inputs = {n for n in files if n.startswith('tools/mcp-configurator/') and Path(n).suffix in ('.cs', '.xaml')} | {
        'scripts/build/Build-Configurator.ps1',
        'tools/tiaportal-mcp/src/TiaMcpServer/Siemens/TiaVersionCatalog.cs',
    }
    require(gui_inputs == {r['path'] for r in gui['sourceFiles']}, 'Configurator input inventory changed')
    for row in gui['sourceFiles']:
        data = files[row['path']].decode('utf-8-sig').replace('\r\n', '\n').encode('utf-8')
        require(sha(data) == row['sha256'], f"Configurator source changed: {row['path']}")
    require(not any(n in files for n in ('tia.cmd', 'tia-v20.cmd', '配置MCP.bat', '配置MCP-v20.bat')), 'Replaced launchers must not be shipped')
    required = ('docs/README.md',
                'TiaMcpConfigurator.exe', 'scripts/build/Build-Configurator.ps1', 'docs/getting-started/configuration.md',
                'scripts/operations/预热.bat', 'scripts/operations/生成工程.bat', 'README.md', 'README.zh-CN.md', 'LICENSE', 'NOTICE.md',
                'docs/guides/hmi/read-only-migration.md', 'docs/development/release-workflow.md',
                'tools/tiaportal-mcp/skill/SKILL.md', 'templates/project-blueprints/full_plc_hmi_project.json')
    require(all(n in files for n in required), 'Full delivery entries or documentation missing')
    require(any(n.startswith('templates/plc/') for n in files) and any(n.startswith('templates/hmi/') for n in files), 'PLC/HMI templates missing')
    files['RELEASE_STATUS.txt'] = (
        f'Complete V20/V21 delivery: {release}; engine FileVersion {version}.\r\n'
        f'Engine build validation date: {metadata["generatedAt"]}; unchanged inputs verified by hashes.\r\n'
        f'Configurator isolated tests passed: {gui["testsPassed"]}; see manifest/configurator-build.json.\r\n'
        f'Source commit: {commit}\r\n'
        'Use runtime/v20 or runtime/v21; duplicate legacy bin paths are no longer shipped.\r\n'
        'Use the matching locally installed TIA/Openness and .NET Framework 4.8.\r\n'
        'Preserve existing connection configuration and secret. No user secret is bundled.\r\n'
        'Local validation results: manifest/release-build.json. Real-project acceptance remains separate.\r\n'
        'Migration gaps and acceptance procedure: docs/guides/hmi/read-only-migration.md.\r\n'
    ).encode('utf-8')
    files['manifest/release-file-hashes.json'] = json.dumps({
        'package': package, 'sourceCommit': commit,
        'files': {name: sha(data) for name, data in sorted(files.items())}
    }, ensure_ascii=False, indent=2).encode('utf-8')
    out = args.output_directory.resolve() if args.output_directory else root / 'bin-build/releases' / ('v' + release)
    out.mkdir(parents=True, exist_ok=True)
    stage, archive = out / package, out / (package + '.zip')
    require(not stage.exists() and not archive.exists(), 'Output already exists; inspect it before choosing to remove it')
    for name, data in files.items():
        path = stage / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    # The validator checks configuration launcher targets, blueprints, JSON, tool roster,
    # exact binary versions and build hashes in the actual delivery directory.
    subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                    str(stage / 'scripts/checks/Validate-Bundle.ps1'), '-BundleRoot', str(stage), '-Strict'], check=True)
    subprocess.run([sys.executable, str(stage / 'scripts/checks/Check-Repository.py')], check=True)
    with zipfile.ZipFile(archive, 'x', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, data in sorted(files.items()):
            z.writestr(package + '/' + name, data)
    with zipfile.ZipFile(archive) as z:
        require(z.testzip() is None and len(z.namelist()) == len(files), 'ZIP integrity failure')
        for name, data in files.items():
            require(z.read(package + '/' + name) == data, f'ZIP content differs: {name}')
    digest = sha(archive.read_bytes())
    archive.with_suffix('.sha256').write_text(digest + '  ' + archive.name + '\n', encoding='ascii')
    result = {'path': str(archive), 'size': archive.stat().st_size, 'sha256': digest, 'files': len(files), 'sourceCommit': commit}
    (out / 'package-result.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
