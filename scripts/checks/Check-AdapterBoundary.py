"""Reject host policy and JSON dependencies in the per-release native adapters.

This source gate needs only Python. The diagnostic console additionally inspects
all eight built worker/adapter PE files, which require the licensed Siemens SDKs
to produce and are therefore unavailable on hosted CI runners.
"""
import argparse
from pathlib import Path
import re
import shutil
import unittest
import uuid
import xml.etree.ElementTree as ET


FORBIDDEN = re.compile(r"(?:TiaMcp\.Logic|Newtonsoft(?:\.Json)?|System\.Text\.Json|System\.Json|System\.Web\.Extensions|Utf8Json|Jil)(?:\b|/)", re.I)


def check(root):
    errors = []
    visited = set()

    def inspect(path):
        path = path.resolve()
        if path in visited:
            return
        visited.add(path)
        tree = ET.parse(path)
        for node in tree.iter():
            kind = node.tag.rsplit('}', 1)[-1]
            if kind not in ('Reference', 'PackageReference', 'ProjectReference'):
                continue
            value = node.get('Include', '')
            if FORBIDDEN.search(value):
                errors.append(f'{path.relative_to(root)}: forbidden {kind}: {value}')
            if kind == 'ProjectReference':
                if node.get('ReferenceOutputAssembly', '').lower() == 'false':
                    continue
                target = value.replace('$(MSBuildThisFileDirectory)', str(path.parent) + '/')
                target = target.replace('\\', '/')
                if '$(' in target:
                    errors.append(f'{path.relative_to(root)}: unevaluated project reference: {value}')
                else:
                    target_path = path.parent / target
                    if not target_path.is_file():
                        errors.append(f'{path.relative_to(root)}: missing project reference: {value}')
                    else:
                        inspect(target_path)

    for folder in ('src/Adapters', 'src/Adapters.Contracts'):
        for path in (root / folder).rglob('*'):
            if any(part in ('bin', 'obj') for part in path.parts):
                continue
            if path.suffix in ('.csproj', '.props', '.targets'):
                inspect(path)
            elif path.suffix == '.cs' and FORBIDDEN.search(path.read_text(encoding='utf-8-sig')):
                errors.append(f'{path.relative_to(root)}: forbidden host/JSON source dependency')
    return errors


class BoundaryTests(unittest.TestCase):
    def fixture(self, reference, dependency=None):
        parent = Path(__file__).resolve().parents[2] / 'bin-build/adapter-boundary-self-test'
        root = parent / uuid.uuid4().hex
        root.mkdir(parents=True)
        try:
            adapter = root / 'src/Adapters'
            adapter.mkdir(parents=True)
            (adapter / 'Adapter.csproj').write_text('<Project>' + reference + '</Project>', encoding='utf-8')
            if dependency is not None:
                (adapter / 'Dependency.csproj').write_text(dependency, encoding='utf-8')
            return check(root)
        finally:
            if root.resolve().parent != parent.resolve():
                raise ValueError('Self-test cleanup escaped its fixture directory')
            shutil.rmtree(root)

    def test_primitives_only(self):
        self.assertEqual([], self.fixture('<ItemGroup><Reference Include="System.Xml.Linq" /></ItemGroup>'))

    def test_logic_project(self):
        self.assertTrue(self.fixture('<ItemGroup><ProjectReference Include="TiaMcp.Logic.csproj" /></ItemGroup>'))

    def test_json_packages_and_assemblies(self):
        for kind in ('Reference', 'PackageReference'):
            for name in ('System.Text.Json', 'Newtonsoft.Json'):
                with self.subTest(kind=kind, name=name):
                    self.assertTrue(self.fixture(f'<ItemGroup><{kind} Include="{name}" /></ItemGroup>'))

    def test_transitive_reference(self):
        self.assertTrue(self.fixture('<ItemGroup><ProjectReference Include="Dependency.csproj" /></ItemGroup>',
                                    '<Project><ItemGroup><PackageReference Include="System.Text.Json" /></ItemGroup></Project>'))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        unittest.main(argv=['Check-AdapterBoundary.py'])
    else:
        failures = check(args.root.resolve())
        for failure in failures:
            print('FAIL:', failure)
        print(f'Adapter boundary: {len(failures)} failure(s); no Siemens SDK or native execution required.')
        raise SystemExit(bool(failures))
