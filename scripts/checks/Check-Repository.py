"""Check local documentation links and repository entrypoints without TIA or Git."""
import argparse
import json
from pathlib import Path
import re
import sys
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[2]
SKIP = {'.git', 'bin-build', 'bin', 'bin-v20', 'obj', 'obj-v20', '__pycache__'}


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


def check(root):
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
        if not target.is_relative_to(root.resolve()) or not target.exists():
            errors.append(f'{label}: missing or external path: {name}')
    package = read('manifest/package-manifest.json')
    for key, value in package['entrypoints'].items():
        required(value, 'package entry ' + key)
    required(package['cli']['exe'], 'CLI')
    for name in read('templates/project-blueprints/full_plc_hmi_project.json')['requiredBundleFiles']:
        required(name, 'blueprint')
    roster = read('manifest/tools-list.json')
    names = [row['name'] for row in roster['tools']]
    if len(names) != len(set(names)) or len(names) != roster['toolCount'] or len(names) != package['capabilities']['mcpToolCount']:
        errors.append('Tool inventory count/uniqueness differs from package metadata')
    for name in ('tia.cmd', 'tia-v20.cmd', '配置MCP.bat', '配置MCP-v20.bat'):
        if (root / name).exists():
            errors.append('Replaced launcher returned: ' + name)
    for version in ('v20', 'v21'):
        required(f'runtime/{version}/TiaMcpServer.exe', 'runtime')
    return count, errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    args = parser.parse_args()
    # Negative sentinel: a broken link and a traversal must fail; a valid file must pass.
    source = args.root / 'docs/README.md'
    assert local_target(args.root, source, '../README.md') is None
    assert local_target(args.root, source, '../__missing_repository_check__.md')
    assert local_target(args.root, source, '../../../__outside__.md')
    count, errors = check(args.root)
    for error in errors:
        print('[FAIL] ' + error)
    print(f'Checked {count} Markdown files and repository entrypoints; {len(errors)} issue(s).')
    return bool(errors)


if __name__ == '__main__':
    sys.exit(main())
