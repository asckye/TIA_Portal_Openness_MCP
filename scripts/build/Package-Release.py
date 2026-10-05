"""Build the complete public delivery ZIP from the clean, committed tree plus the local build outputs.

Since 2.8.1 the engine runtimes (runtime/v20, runtime/v21) and TiaOpenness.exe are not tracked in Git:
Build-Release.ps1 produces them locally and records their hashes in manifest/release-build.json and
manifest/configurator-build.json, which ARE committed. Packaging filters tracked files through
scripts/operations/delivery-files.json, adds the recorded local binaries and refuses when a binary is missing or differs from its validated hash. Release.ps1 uploads the ZIP.
"""
import argparse
from collections import Counter
import importlib.util
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import zipfile
import xml.etree.ElementTree as ET


CHECKS = Path(__file__).resolve().parents[1] / 'checks'
_spec = importlib.util.spec_from_file_location('bundle_layout', CHECKS / 'Check-BundleLayout.py')
layout = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(layout)


def preview(root, tracked, rules, stage=None):
    selected = sorted(name for name in tracked if layout.delivered(name, rules))
    selected_set = set(selected)
    excluded = Counter(name.split('/')[0] for name in tracked if name not in selected_set)
    for name in selected:
        print(name)
    print(f'Dry run: {len(selected)} delivery files; {sum(excluded.values())} excluded files; no binaries built or ZIP published')
    print('Excluded top-level groups: ' + json.dumps(dict(sorted(excluded.items())), ensure_ascii=False))
    if stage:
        stage = stage.resolve()
        require(stage.is_relative_to(root) and stage != root, 'Dry-run stage must be a new directory inside this worktree')
        require(not stage.exists(), 'Dry-run stage already exists')
        for name in selected:
            target = stage / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((root / name).read_bytes())
        print('Staged filtered tree: ' + str(stage))


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--git', default='git')
    parser.add_argument('--output-directory', type=Path, help='Optional new local output directory; existing archives/stages are never overwritten')
    parser.add_argument('--local', action='store_true', help='Build a review-only bundle from this worktree without committing; all binary and validation gates still apply')
    parser.add_argument('--dry-run', action='store_true', help='List the filtered working tree without build outputs, clean-tree checks or publication')
    parser.add_argument('--include-untracked', action='store_true', help='Dry run only: also preview new, non-ignored files awaiting review')
    parser.add_argument('--stage-directory', type=Path, help='Dry run only: copy the filtered tree into a new worktree-local directory')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    rules = layout.load_delivery(root)

    def git(*values):
        return subprocess.check_output([args.git, *values], cwd=root)

    tracked = [name for name in git('ls-files', '-z').decode('utf-8').split('\0') if name]
    if args.dry_run:
        if args.include_untracked:
            tracked += [name for name in git('ls-files', '--others', '--exclude-standard', '-z').decode('utf-8').split('\0') if name]
        preview(root, tracked, rules, args.stage_directory)
        return
    require(not args.include_untracked and args.stage_directory is None, 'Preview options require --dry-run')
    subprocess.run([sys.executable, str(CHECKS / 'Check-Repository.py')], check=True)
    require(args.local or not git('status', '--porcelain', '--untracked-files=normal').strip(),
            'Review and commit the source and manifests before packaging')
    commit = git('rev-parse', 'HEAD').decode().strip()
    files = {}
    require(not any((name.startswith('runtime/') and name != 'runtime/README.md') or name == 'TiaOpenness.exe' for name in tracked),
            'Binaries must not be tracked in Git (2.8.1 policy): git rm --cached runtime/v20 runtime/v21 TiaOpenness.exe')
    # Local build outputs (ignored by Git): exactly the runtime inventory that Build-Release recorded plus the
    # configurator. Other local output files are excluded from the delivery package.
    metadata = json.loads((root / 'manifest/release-build.json').read_text(encoding='utf-8-sig'))
    delivery = json.loads((root / 'manifest/delivery.json').read_text(encoding='utf-8-sig'))
    multi_path = root / 'manifest/multi-version-build.json'
    multi = json.loads(multi_path.read_text('utf-8-sig')) if delivery.get('multiVersionBuildSha256') else None
    inventory = metadata['runtimeFiles'].copy()
    if multi is not None:
        require(sha(multi_path.read_bytes()) == delivery['multiVersionBuildSha256'], 'Multi-version build record changed after delivery preparation')
        require(multi['release'] == metadata['release'] and multi['fileVersion'] == metadata['fileVersion'], 'Multi-version binaries belong to another release')
        require(all(multi['validation'].get(key) for key in ('foundationTransportExecuted', 'studioFunctionalTestsExecuted', 'configurationFunctionalTestsExecuted', 'toolUsageCoverageExecuted')), 'Run Build-MultiVersion.ps1 -Test before publication')
        require(multi['studioReleaseKeys'] == ['14sp1', '15.1', '16', '17', '18', '19', '20', '21'], 'Eight release adapters are required')
        existing = {row['path']: row['sha256'] for row in inventory}
        for row in multi['files']:
            require(row['path'].startswith('runtime/'), 'Unexpected multi-version runtime path: ' + row['path'])
            require(row['path'] not in existing or existing[row['path']] == row['sha256'], 'Conflicting runtime inventories: ' + row['path'])
            if row['path'] not in existing:
                inventory.append(row)
                existing[row['path']] = row['sha256']
    binaries = ['TiaOpenness.exe'] + sorted(row['path'] for row in inventory)
    on_disk = {p.relative_to(root).as_posix() for p in (root / 'runtime').rglob('*') if p.is_file() and p.name != 'README.md'}
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
    required_exes = ['TiaOpenness.exe', 'runtime/v20/TiaMcp.Engine.V20.exe', 'runtime/v21/TiaMcp.Engine.V21.exe']
    if multi is not None:
        required_exes += [f'runtime/v{key}/TiaMcp.FoundationHost.exe' for key in multi['studioReleaseKeys'][:6]]
        required_exes += ['runtime/studio/TiaOpenness.exe']
        pin = json.loads((root / 'scripts/build/bundled-dotnet.json').read_text(encoding='utf-8'))
        required_exes += [f"runtime/dotnet/host/fxr/{pin['version']}/hostfxr.dll", 'runtime/dotnet/LICENSE.txt', 'runtime/dotnet/ThirdPartyNotices.txt']
        required_exes += [f"runtime/dotnet/shared/{name}/{pin['version']}/{name}.deps.json" for name in pin['frameworks']]
        for key in multi['studioReleaseKeys']:
            require(f'runtime/studio/bridge/adapters/v{key}/TiaOpenness.Openness.dll' in files, 'Studio adapter missing: ' + key)
    require(all(name in files for name in required_exes), 'A required runtime executable is missing')

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
    require(package == f'TIA_MCP_Delivery_v{release}_{date}', 'Use TIA_MCP_Delivery_vX.Y.Z_YYYYMMDD for the complete delivery')
    source_release = ET.fromstring(files['Version.props']).findtext('.//TiaMcpRelease')
    require(source_release + '.0' == version and source_release == metadata['release'], 'Source version differs from validated build')
    runtime_names = {n for n in files if n.startswith('runtime/') and n != 'runtime/README.md'}
    require(runtime_names == {r['path'] for r in inventory}, 'Runtime file inventory changed after validation')
    for row in inventory:
        require(sha(files[row['path']]) == row['sha256'], f"Runtime changed: {row['path']}")
    source_names = {n for n in files if n.startswith(('src/Engine/','src/FoundationHost/','src/Worker/','src/Logic/','src/Runtime/','src/WorkerChannel/','src/Adapters/','src/Adapters.Contracts/', 'tests/Engine/', 'build-tools/native-call-weaver/', 'src/Shared/', 'third_party/TiaGitAddIn.Core/', 'third_party/SiemensOpcUaModelled/')) and Path(n).suffix in ('.cs', '.csproj', '.props', '.targets', '.xml', '.json')} | ({'Version.props', 'tests/test-suites.json'} & set(files))
    require(source_names == {r['path'] for r in metadata['sourceFiles']}, 'Compiler/test input inventory changed')
    for row in metadata['sourceFiles']:
        data = files[row['path']].decode('utf-8-sig').replace('\r\n', '\n').encode('utf-8')
        require(sha(data) == row['sha256'], f"Source changed after validation: {row['path']}")
    if multi is not None:
        for row in multi['sourceFiles']:
            data = files[row['path']].decode('utf-8-sig').replace('\r\n', '\n').encode('utf-8')
            require(sha(data) == row['sha256'], f"Multi-version source changed after validation: {row['path']}")
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
            require(stability['runtimeSha256'] == sha(files[f'runtime/v{major}/TiaMcp.Engine.V{major}.exe']),
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
    studio = 'src/Studio/'
    desktop_projects = tuple(studio + name + '/' for name in ('Gui', 'Client', 'Core', 'Contracts', 'Bridge'))
    gui_inputs = {n for n in files if n.startswith(studio + 'Gui/Fonts/') or
                  (n.startswith(desktop_projects) and Path(n).suffix in ('.cs', '.xaml', '.csproj')) or
                  (n.startswith('tests/Studio/TiaOpenness.Configuration.Tests/') and Path(n).suffix in ('.cs', '.csproj'))} | {
        'scripts/build/Build-Configurator.ps1', 'Version.props',
        studio + 'Launcher/Launcher.cs',
        studio + 'Directory.Build.props', 'tests/Studio/Directory.Build.props',
        'src/Logic/Siemens/TiaVersionCatalog.cs',
        'src/Shared/OpennessEnvironment.cs',
        'src/Shared/BundleLayout.cs',
        'src/Shared/ProcessArguments.cs',
        'src/Shared/LocalProcess.cs',
    }
    require(gui_inputs == {r['path'] for r in gui['sourceFiles']}, 'Configurator input inventory changed')
    for row in gui['sourceFiles']:
        data = files[row['path']]
        if not row['path'].lower().endswith(('.ttf', '.otf')):
            data = data.decode('utf-8-sig').replace('\r\n', '\n').encode('utf-8')
        require(sha(data) == row['sha256'], f"Configurator source changed: {row['path']}")
    require(not any(n in files for n in ('tia.cmd', 'tia-v20.cmd', '配置MCP.bat', '配置MCP-v20.bat')), 'Replaced launchers must not be shipped')
    required = rules['include']['files'] + [layout.DELIVERY_RULES, 'docs/README.md',
                'src/Shared/BundleLayout.cs',
                'scripts/checks/Check-BundleLayout.py',
                'tests/Engine/TiaMcpServer.Tests/BundleLayoutTests.cs',
                'src/Adapters.Contracts/TiaMcp.Adapters.Contracts.csproj',
                'src/Adapters.Contracts/packages.lock.json',
                'TiaOpenness.exe', 'scripts/build/Build-Configurator.ps1', 'docs/getting-started/configuration.md',
                'src/Studio/Launcher/Launcher.cs',
                'src/Studio/Gui/Themes/Glass.xaml',
                'src/Studio/Gui/Controls/GlassLogView.cs',
                'src/Studio/Gui/Fonts/Manrope-Regular.ttf',
                'src/Studio/Gui/Fonts/Manrope-Medium.ttf',
                'src/Studio/Gui/Fonts/Manrope-SemiBold.ttf',
                'src/Studio/Gui/Fonts/Manrope-Bold.ttf',
                'src/Studio/Gui/Fonts/Manrope-OFL.txt',
                'src/Studio/Gui/Fonts/JetBrainsMono-Regular.ttf',
                'src/Studio/Gui/Fonts/JetBrainsMono-Medium.ttf',
                'src/Studio/Gui/Fonts/JetBrainsMono-OFL.txt',
                'src/Studio/Gui/Fonts/NotoSansSC-Regular.otf',
                'src/Studio/Gui/Fonts/NotoSansSC-Medium.otf',
                'src/Studio/Gui/Fonts/NotoSansSC-Bold.otf',
                'src/Studio/Gui/Fonts/NotoSansSC-OFL.txt',
                'src/Studio/Gui/Fonts/SOURCES.txt',
                'src/Studio/Gui/TiaOpenness.Gui.csproj',
                'src/Studio/Gui/Configuration/ConfigurationView.xaml',
                'src/Studio/Gui/Configuration/ConfigurationView.xaml.cs',
                'src/Studio/Gui/Configuration/ConfigCore.cs',
                'src/Studio/Gui/Configuration/ClientProfiles.cs',
                'src/Studio/Gui/Configuration/UpdateCheck.cs',
                'src/Studio/Gui/Configuration/ModernJson.cs',
                'src/Studio/Gui/Localization/Strings.cs',
                'src/Studio/Gui/Themes/Palette.Light.xaml',
                'src/Studio/Gui/Themes/Palette.Dark.xaml',
                'tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj',
                'tests/Studio/TiaOpenness.Configuration.Tests/Tests.cs',
                'scripts/operations/预热.bat', 'scripts/operations/生成工程.bat', 'README.md', 'README.zh-CN.md', 'LICENSE', 'NOTICE.md',
                'docs/guides/hmi/read-only-migration.md', 'docs/development/release-workflow.md',
                'plugin/skill/SKILL.md', 'templates/project-blueprints/full_plc_hmi_project.json']
    required += ['src/Runtime/' + name for name in
                 ('TiaMcp.Runtime.csproj', 'S7LiveReader.cs', 'OpcUaLiveReader.cs', 'S7WebApiChannel.cs', 'UnifiedOpenPipeChannel.cs')]
    required += [f'runtime/v{major}/TiaMcp.Runtime.dll' for major in (20, 21)]
    required += [f'runtime/v{major}/TiaMcp.Adapter.{major}.dll' for major in (20, 21)]
    required += [f'runtime/v{major}/TiaMcp.Adapters.Contracts.dll' for major in (20, 21)]
    required += ['runtime/verification/' + name for name in ('NativeCallWeaver.dll', 'NativeCallWeaver.deps.json', 'NativeCallWeaver.runtimeconfig.json', 'Mono.Cecil.dll')]
    required += ['src/' + name for name in ('Adapters/Native/Plc/PlcServices.cs', 'Engine/ModelContextProtocol/InvocationJournal.Adapter.cs')]
    required += ['tests/Engine/TiaMcpServer.HttpTests/AdapterIntegrationChecks.cs']
    required += ['src/WorkerChannel/' + name for name in ('ChannelMessage.cs', 'LineFraming.cs', 'ChannelCodec.cs', 'ChannelClient.cs', 'ChannelServer.cs', 'TiaMcp.WorkerChannel.csproj', 'packages.lock.json')]
    required += ['src/Studio/Core/Rpc/BridgeChannel.cs', 'runtime/studio/TiaMcp.WorkerChannel.dll']
    required += ['runtime/studio/bridge/' + name for name in ('TiaMcp.WorkerChannel.dll', 'System.Text.Json.dll', 'System.Text.Encodings.Web.dll', 'System.IO.Pipelines.dll', 'Microsoft.Bcl.AsyncInterfaces.dll', 'System.Buffers.dll', 'System.Memory.dll', 'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll', 'System.Threading.Tasks.Extensions.dll')]
    for key in ('14sp1', '15.1', '16', '17', '18', '19'):
        # The .NET 10 Foundation hosts take System.Text.Json, Encodings.Web and IO.Pipelines from the bundled shared framework.
        required += [f'runtime/v{key}/TiaMcp.WorkerChannel.dll']
        required += [f'runtime/v{key}/worker/' + name for name in ('TiaMcp.WorkerChannel.dll', 'System.Text.Json.dll', 'System.Text.Encodings.Web.dll', 'System.IO.Pipelines.dll', 'Microsoft.Bcl.AsyncInterfaces.dll', 'System.Buffers.dll', 'System.Memory.dll', 'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll', 'System.Threading.Tasks.Extensions.dll')]
    required += ['manifest/history/contracts-v3/README.md', 'manifest/history/contracts-v3/provenance.json']
    required += [f'{directory}/{category}/{key}.json'
                 for directory in ('manifest/history/contracts-v3', 'manifest/contracts/v4')
                 for category in ('baseline', 'responses') for key in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21')]
    require(all(n in files for n in required), 'Full delivery entries or documentation missing')
    require(any(n.startswith('templates/plc/') for n in files) and any(n.startswith('templates/hmi/') for n in files), 'PLC/HMI templates missing')
    # Validate compiler inputs and release-only IL verifier in the repository before
    # projecting the delivery set. Source/build evidence never enters the ZIP.
    subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                    str(CHECKS / 'Validate-Bundle.ps1'), '-BundleRoot', str(root), '-Strict'], check=True)
    files = {name: data for name, data in files.items() if layout.delivered(name, rules)}
    require(all(name in files for name in required_exes), 'Delivery rules exclude a required executable/license')
    require(all(layout.delivered(row['path'], rules) or row['path'].startswith('runtime/verification/')
                for row in inventory), 'Delivery rules exclude a recorded runtime dependency')
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
                    str(CHECKS / 'Validate-Bundle.ps1'), '-BundleRoot', str(stage), '-Strict', '-PackageMode'], check=True)
    subprocess.run([sys.executable, str(CHECKS / 'Check-Repository.py'), '--root', str(stage), '--package-mode'], check=True)
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
    if args.local:
        result['sourceState'] = 'worktree; local review only, not for publication'
    (out / 'package-result.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
