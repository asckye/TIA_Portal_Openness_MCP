"""Reject unregistered tool calls in first-party Python, PowerShell and campaign inputs.

Runtime registrations are authoritative. Historical maps and the frozen write-guard
baseline are data; negative tests are allowed only at their reviewed call sites.
"""
import argparse
import ast
import json
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
DATA_ALLOWLIST = {
    'scripts/diagnostics/campaign/historical_tool_names.json': 'V3 evidence index rename map',
    'scripts/checks/write-guard-operations-v3.3.0.json': 'Frozen released operation baseline',
}
NEGATIVE_CALLS = {
    'scripts/checks/Test-LocalStability.py': {'__soak_unknown__'},
    'scripts/checks/Snapshot-ToolResponses.py': {'__contract_unknown_tool__'},
}
CALL_HELPERS = {'call', 'call_raw', 'call_guide_tool', 'S', 'Call', 'invoke_tool'}


def registrations(root):
    names = set()
    for p in (root / 'src/Engine').rglob('*.cs'):
        if 'obj' in p.parts or 'bin' in p.parts: continue
        names.update(re.findall(r'McpServerTool\(Name\s*=\s*"([^"]+)"', p.read_text('utf-8-sig')))
    profiles = ET.parse(root / 'src/Logic/ModelContextProtocol/ToolProfiles.resx')
    catalog = json.loads(profiles.find(".//data[@name='Catalog']/value").text)
    # Foundation's registry is generated from these reviewed per-release rows.
    names.update(row['name'] for rows in catalog['releases'].values() for row in rows)
    return names


def python_calls(source):
    """Follow literal call helpers, request dictionaries and data-driven case tables."""
    tree = ast.parse(source)
    tables = {target.id: node.value for node in tree.body if isinstance(node, ast.Assign)
              for target in node.targets if isinstance(target, ast.Name)}
    def literal(node):
        return node.value if isinstance(node, ast.Constant) and isinstance(node.value, str) else None
    def tool_literal(value):
        # Other scripts also use call() to construct IL signatures or canned JSON
        # responses. Those literals are payloads rather than outbound tool names.
        if not value or '::' in value: return False
        try:
            json.loads(value)
            return False
        except ValueError:
            return True
    for node in ast.walk(tree):
        if isinstance(node, ast.For) and isinstance(node.target, ast.Name) and isinstance(node.iter, ast.Name):
            table = tables.get(node.iter.id)
            dispatched = any(isinstance(call, ast.Call) and call.args
                and isinstance(call.args[0], ast.Name) and call.args[0].id == node.target.id
                and (call.func.id if isinstance(call.func, ast.Name) else
                     call.func.attr if isinstance(call.func, ast.Attribute) else '') in CALL_HELPERS
                for call in ast.walk(node))
            if dispatched and isinstance(table, (ast.List, ast.Tuple, ast.Set)):
                for item in table.elts:
                    if literal(item): yield item.lineno, literal(item)
        if isinstance(node, ast.Call):
            helper = node.func.id if isinstance(node.func, ast.Name) else node.func.attr if isinstance(node.func, ast.Attribute) else ''
            if helper in CALL_HELPERS and node.args:
                value = literal(node.args[0])
                if tool_literal(value) and value not in {'initialize', 'notifications/initialized', 'tools/list', 'tools/call', 'ping', 'resources/list', 'resources/read', 'prompts/list', 'prompts/get'}: yield node.lineno, value
        if isinstance(node, ast.Dict):
            fields = {literal(k): v for k, v in zip(node.keys, node.values)}
            for key in ('tool', 'name'):
                value = literal(fields.get(key))
                if value and (('args' in fields) if key == 'tool' else ('arguments' in fields)) :
                    yield node.lineno, value
        if isinstance(node, (ast.Tuple, ast.List)) and len(node.elts) >= 3:
            # (tool, case/parameter, arguments) and (label, tool, arguments, expectation).
            if any(isinstance(x, (ast.Dict, ast.Call)) for x in node.elts[2:]):
                for x in node.elts[:2]:
                    value = literal(x)
                    if value and re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', value) and (value[:1].isupper() or value.startswith('__')): yield node.lineno, value
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'calls' for t in node.targets) and isinstance(node.value, ast.Dict):
            for key, value in zip(node.value.keys, node.value.values):
                if literal(key) and literal(key) != 'profiles' and isinstance(value, ast.Dict): yield key.lineno, literal(key)


def script_calls(path, source):
    if path.endswith('.py'): return list(python_calls(source))
    if path.endswith(('.ps1', '.cmd', '.bat', '.sh')):
        patterns = [r"\bCall\s+['\"]([^'\"\r\n]+)['\"]",
                    r"(?<![$?A-Za-z_])name\s*=\s*['\"]([^'\"\r\n]+)['\"]",
                    r"\b--tool\s+([A-Za-z_][A-Za-z0-9_]*)\b",
                    r"(?:\.exe|\$[A-Za-z_]+)\s+(?:call|bridge)\s+([A-Za-z_][A-Za-z0-9_]*)\b"]
        return [(source[:m.start()].count('\n') + 1, m[1]) for pattern in patterns for m in re.finditer(pattern, source)]
    if path.endswith('.json'):
        found = []
        def walk(value):
            if isinstance(value, dict):
                if 'tool' in value and 'args' in value: found.append((1, value['tool']))
                if 'name' in value and 'arguments' in value: found.append((1, value['name']))
                for item in value.values(): walk(item)
            elif isinstance(value, list):
                for item in value: walk(item)
        walk(json.loads(source))
        return found
    return []


def check(path, source, registered):
    if path in DATA_ALLOWLIST: return []
    return [(line, name) for line, name in script_calls(path, source)
            if name not in registered and name not in NEGATIVE_CALLS.get(path, set())]


class CheckerTests(unittest.TestCase):
    def test_literals_bridge_batches_and_tables(self):
        for source in ("call('RemovedTool', {})", "call('removedtool', {})", "call('Removed-Tool', {})", "S('RemovedTool', x=1)",
                       "p.rpc('tools/call', {'name': 'CallTool', 'arguments': {'name': 'RemovedTool', 'arguments': {}}})",
                       "cases = [('RemovedTool', 'probe', {})]", "calls = {'RemovedTool': {}}"):
            with self.subTest(source=source): self.assertTrue(check('scripts/probe.py', source, {'CallTool'}))
        self.assertTrue(check('scripts/probe.py', "TOOLS = ['RemovedTool']\nfor name in TOOLS: call(name, {})", set()))

    def test_powershell_and_json(self):
        self.assertTrue(check('scripts/probe.ps1', "Call 'RemovedTool' @{calls=@(@{name='RemovedNested';arguments=@{}})}", set()))
        self.assertTrue(check('scripts/plan.json', '[{"tool":"RemovedTool","args":{}}]', set()))

    def test_registered_native_names_comments_and_negative_scope(self):
        self.assertFalse(check('scripts/probe.py', "call('GetSessionState', {'methodName': 'GetCrossReferences'}) # call('OldTool')", {'GetSessionState'}))
        self.assertFalse(check('scripts/probe.py', "call('Siemens::Read()')\ncall('{\"fixture\":true}')", set()))
        self.assertFalse(check('scripts/probe.ps1', "$url + '?name=' + [Uri]::EscapeDataString($name)", set()))
        source = "call('__soak_unknown__', {})"
        self.assertFalse(check('scripts/checks/Test-LocalStability.py', source, set()))
        self.assertTrue(check('scripts/probe.py', source, set()))
        self.assertTrue(check('scripts/checks/Test-LocalStability.py', "call('RemovedTool', {})", set()))

    def test_allowlist_is_exact(self):
        path = 'scripts/diagnostics/campaign/historical_tool_names.json'
        self.assertFalse(check(path, '{"OldTool":"GetSessionState"}', set()))
        self.assertTrue(check(path.replace('historical_tool_names', 'other'), '[{"tool":"OldTool","args":{}}]', set()))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner().run(unittest.defaultTestLoader.loadTestsFromTestCase(CheckerTests))
        return 0 if result.wasSuccessful() else 1
    registered = registrations(ROOT)
    bad, scanned = [], 0
    for p in sorted((ROOT / 'scripts').rglob('*')):
        if p.suffix not in ('.py', '.ps1', '.cmd', '.bat', '.sh', '.json') or '__pycache__' in p.parts: continue
        path = p.relative_to(ROOT).as_posix()
        scanned += 1
        bad.extend((path, line, name) for line, name in check(path, p.read_text('utf-8-sig'), registered))
    for path, line, name in sorted(set(bad)): print(f'FAIL {path}:{line}: unregistered tool {name}')
    print(f'Script tool calls: {scanned} files, {len(registered)} registered names, {len(set(bad))} failures')
    return 1 if bad else 0


if __name__ == '__main__':
    raise SystemExit(main())
