"""Check local documentation links and repository entrypoints without TIA or dotnet."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[2]
SKIP = {'.git', 'bin-build', 'bin', 'bin-v20', 'obj', 'obj-v20', '__pycache__', 'TiaMcp_Output', '.pytest_cache'}


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


def check(root, no_binaries=False):
    errors = []
    count = 0
    for source in documents(root):
        count += 1
        text = source.read_text(encoding='utf-8-sig')
        # Ignore code fences: examples can contain placeholder Markdown.
        text = re.sub(r'^```[^\n]*\n.*?^```\s*$', '', text, flags=re.M | re.S)
        for match in re.finditer(r'\]\((<[^>]+>|[^\s)]+)(?:\s+"[^"]*")?\)', text):
            error = local_target(root, source, match[1])
            if error:
                errors.append(f'{source.relative_to(root).as_posix()}: {error}')
    def read(name):
        return json.loads((root / name).read_text(encoding='utf-8-sig'))
    def required(name, label):
        target = (root / name).resolve()
        if not target.is_relative_to(root.resolve()):
            errors.append(f'{label}: external path: {name}')
        elif no_binaries and (name.endswith('.exe') or name.startswith('runtime/')):
            return   # build outputs live outside Git since 2.8.1
        elif not target.exists():
            errors.append(f'{label}: missing or external path: {name}')
    package = read('manifest/package-manifest.json')
    for key, value in package['entrypoints'].items():
        required(value, 'package entry ' + key)
    required(package['cli']['exe'], 'CLI')
    required('tools/tiaportal-mcp/src/TiaMcp.Adapters.Contracts/TiaMcp.Adapters.Contracts.csproj', 'adapter contracts')
    required('tools/tiaportal-mcp/src/TiaMcp.Adapters.Contracts/packages.lock.json', 'adapter contracts lock')
    required('tools/openness-shared/BundleLayout.cs', 'bundle layout')
    required('scripts/checks/Check-BundleLayout.py', 'bundle layout check')
    required('tools/tiaportal-mcp/tests/TiaMcpServer.Tests/BundleLayoutTests.cs', 'bundle layout tests')
    for name in read('templates/project-blueprints/full_plc_hmi_project.json')['requiredBundleFiles']:
        required(name, 'blueprint')
    studio = 'tools/tia-openness-studio/src/'
    required(studio + 'TiaOpenness.Launcher/Launcher.cs', 'GUI entry')
    for name in ('Themes/Glass.xaml', 'Controls/GlassLogView.cs',
                 'Fonts/Manrope-Regular.ttf', 'Fonts/Manrope-Medium.ttf',
                 'Fonts/Manrope-SemiBold.ttf', 'Fonts/Manrope-Bold.ttf', 'Fonts/Manrope-OFL.txt',
                 'Fonts/JetBrainsMono-Regular.ttf', 'Fonts/JetBrainsMono-Medium.ttf',
                 'Fonts/JetBrainsMono-OFL.txt', 'Fonts/SOURCES.txt'):
        required(studio + 'TiaOpenness.Gui/' + name, 'GUI entry')
    roster = read('manifest/tools-list.json')
    names = [row['name'] for row in roster['tools']]
    if len(names) != len(set(names)) or len(names) != roster['toolCount'] or len(names) != package['capabilities']['mcpToolCount']:
        errors.append('Tool inventory count/uniqueness differs from package metadata')
    for name in ('tia.cmd', 'tia-v20.cmd', '配置MCP.bat', '配置MCP-v20.bat'):
        if (root / name).exists():
            errors.append('Replaced launcher returned: ' + name)
    for version in ('v20', 'v21'):
        required(f'runtime/{version}/TiaMcpServer.exe', 'runtime')
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
    layout = Path(__file__).with_name('Check-BundleLayout.py')
    result = subprocess.run([sys.executable, str(layout), '--root', str(root)])
    if result.returncode:
        errors.append('Bundle-layout resource check failed (see diagnostics above)')
    return count, errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--no-binaries', action='store_true',
                        help='source checkout without build outputs: skip the existence of runtime/*/TiaMcpServer.exe and TiaMcpConfigurator.exe (not tracked since 2.8.1)')
    args = parser.parse_args()
    # Negative sentinel: a broken link and a traversal must fail; a valid file must pass.
    source = args.root / 'docs/README.md'
    assert local_target(args.root, source, '../README.md') is None
    assert local_target(args.root, source, '../__missing_repository_check__.md')
    assert local_target(args.root, source, '../../../__outside__.md')
    count, errors = check(args.root, args.no_binaries)
    for error in errors:
        print('[FAIL] ' + error)
    print(f'Checked {count} Markdown files and repository entrypoints; {len(errors)} issue(s).')
    return bool(errors)


if __name__ == '__main__':
    sys.exit(main())
