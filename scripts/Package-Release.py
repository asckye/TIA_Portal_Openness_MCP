"""Build the complete public delivery ZIP from clean, committed, validated files."""
import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import subprocess
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
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]

    def git(*values):
        return subprocess.check_output([args.git, *values], cwd=root)

    require(not git('status', '--porcelain', '--untracked-files=normal').strip(),
            'Review and commit the source, both runtimes and manifests before packaging')
    commit = git('rev-parse', 'HEAD').decode().strip()
    files = {}
    for name in git('ls-files', '-z').decode('utf-8').split('\0'):
        if not name:
            continue
        path = (root / name).resolve()
        require(path.is_relative_to(root) and path.is_file(), f'Invalid tracked path: {name}')
        require(not re.search(r'(^|/)(\.git|bin-build|PublicAPI|source-review|obj|obj-v20)(/|$)', name, re.I), f'Private/build path: {name}')
        require(not name.lower().endswith(('.log', '.pdb', '.patch', '.user', '.pfx', '.key')),
                f'Unexpected release file: {name}')
        require(not path.name.startswith('Siemens.Engineering'), f'PublicAPI must not be redistributed: {name}')
        files[name] = path.read_bytes()
    require(sorted(n for n in files if n.endswith('.exe')) ==
            ['runtime/v20/TiaMcpServer.exe', 'runtime/v21/TiaMcpServer.exe'], 'Both runtime EXEs are required')

    metadata = json.loads(files['manifest/release-build.json'].decode('utf-8-sig'))
    release, version, package = (metadata[k] for k in ('release', 'fileVersion', 'package'))
    require(re.fullmatch(r'\d+\.\d+\.\d+', release) is not None, 'Release version must be X.Y.Z')
    date = metadata['releaseDate']
    require(re.fullmatch(r'\d{8}', date) is not None, 'Release date must be YYYYMMDD')
    datetime.strptime(date, '%Y%m%d')
    require(package == f'TIA_MCP_Delivery_v{release}_{date}', 'Use TIA_MCP_Delivery_vX.Y.Z_YYYYMMDD for the complete V20/V21 delivery')
    for project in ('TiaMcpServer.csproj', 'TiaMcpServer.V20.csproj'):
        xml = ET.fromstring(files[f'tools/tiaportal-mcp/src/TiaMcpServer/{project}'])
        require(xml.findtext('.//FileVersion') == version and xml.findtext('.//InformationalVersion') == release, 'Source version differs from validated build')
    runtime_names = {n for n in files if n.startswith('runtime/')}
    require(runtime_names == {r['path'] for r in metadata['runtimeFiles']}, 'Runtime file inventory changed after validation')
    for row in metadata['runtimeFiles']:
        require(sha(files[row['path']]) == row['sha256'], f"Runtime changed: {row['path']}")
    source_names = {n for n in files if n.startswith(('tools/tiaportal-mcp/src/', 'tools/tiaportal-mcp/tests/')) and Path(n).suffix in ('.cs', '.csproj', '.props', '.targets')}
    require(source_names == {r['path'] for r in metadata['sourceFiles']}, 'Compiler/test input inventory changed')
    for row in metadata['sourceFiles']:
        data = files[row['path']].decode('utf-8-sig').replace('\r\n', '\n').encode('utf-8')
        require(sha(data) == row['sha256'], f"Source changed after validation: {row['path']}")
    require(metadata['validation']['offlinePassed'] > 0, 'No offline suite result')
    for major in (20, 21):
        checks = metadata['validation']['runtimes'][f'V{major}']
        require(checks['httpPassed'] > 0 and checks['hmiPassed'] > 0 and checks['migrationAssembly'] == 'passed', f'V{major} validation incomplete')
        require(checks.get('resourceDiscoveryPassed', 0) > 0, f'V{major} resource discovery validation missing')
        require(f'runtime/v{major}/Esprima.dll' in files, f'V{major} script parser missing')
    required = ('tia.cmd', 'tia-v20.cmd', '配置MCP.bat', '配置MCP-v20.bat', '开始使用.md',
                'scripts/预热.bat', 'scripts/生成工程.bat', 'README.md', 'README.zh-CN.md', 'LICENSE', 'NOTICE.md',
                'docs/UNIFIED_READ_ONLY_MIGRATION.md', 'docs/RELEASE_WORKFLOW.md',
                'tools/tiaportal-mcp/skill/SKILL.md', 'templates/project-blueprints/full_plc_hmi_project.json')
    require(all(n in files for n in required), 'Full delivery entries or documentation missing')
    require(any(n.startswith('templates/plc/') for n in files) and any(n.startswith('templates/hmi/') for n in files), 'PLC/HMI templates missing')
    for name, data in list(files.items()):
        for major, folder in ((20, 'bin-v20'), (21, 'bin')):
            prefix = f'runtime/v{major}/'
            if name.startswith(prefix):
                files[f'tools/tiaportal-mcp/src/TiaMcpServer/{folder}/Release/net48/' + name[len(prefix):]] = data
    files['RELEASE_STATUS.txt'] = (
        f'Complete V20/V21 delivery: {release}; FileVersion {version}.\r\n'
        f'Source commit: {commit}\r\n'
        'Both runtime directories and legacy bin paths contain the same validated binaries.\r\n'
        'Use the matching locally installed TIA/Openness and .NET Framework 4.8.\r\n'
        'Preserve existing connection configuration and secret. No user secret is bundled.\r\n'
        'Local validation results: manifest/release-build.json. Real-project acceptance remains separate.\r\n'
        'Migration gaps and acceptance procedure: docs/UNIFIED_READ_ONLY_MIGRATION.md.\r\n'
    ).encode('utf-8')
    files['manifest/release-file-hashes.json'] = json.dumps({
        'package': package, 'sourceCommit': commit,
        'files': {name: sha(data) for name, data in sorted(files.items())}
    }, ensure_ascii=False, indent=2).encode('utf-8')
    out = root / 'bin-build/releases' / ('v' + release)
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
                    str(stage / 'scripts/Validate-Bundle.ps1'), '-BundleRoot', str(stage), '-Strict'], check=True)
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
