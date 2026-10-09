"""Inventory response construction and ratchet hand-written assignments across source moves.

This is a lexical inventory, not type/control-flow analysis. B1-B9 are overlapping
shape/method hints, not a partition of tools. Scan all conditional branches; skip
comments, literal text and generated/build files. Interpolation expressions count.
"""
import argparse
from collections import Counter
from contextlib import redirect_stdout
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE_ROOT = Path('src')
BASELINE = Path('scripts/checks/response-envelope-baseline.json')
RATCHET = ('timestamp_now_assignments', 'success_assignments')
METRICS = ('timestamp_indexers', 'success_indexers', 'ok_indexers',
           'timestamp_assignments', *RATCHET, 'ok_assignments', 'new_json_object',
           'new_response_message', 'throw_mcp_exception', 'throw_portal_exception',
           'timestamp_local_datetime', 'datetime_now_roundtrip', 'datetime_utcnow',
           'datetimeoffset_utcnow_roundtrip', 'datetimeoffset_now_roundtrip',
           'created_utc_custom', 'B1', 'B2', 'B3', 'B4', 'B5a', 'B5b', 'B5c',
           'B5d', 'B6', 'B7', 'B8', 'B9')

# Reuse the reviewed C# lexer, including interpolation and preprocessor handling.
spec = importlib.util.spec_from_file_location('swallowed_envelope_lexer', Path(__file__).with_name('Check-SwallowedExceptions.py'))
lexer = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = lexer
spec.loader.exec_module(lexer)


def literal(token):
    if token.kind != 'literal' or token.expressions:
        return None
    value = token.value
    if value.startswith('@"'):
        return value[2:-1].replace('""', '"')
    if value.startswith('"""'):
        width = len(value) - len(value.lstrip('"'))
        return value[width:-width].strip()
    if value.startswith('"'):
        try:
            return json.loads(value)
        except ValueError:
            return None
    return None


def literal_text(token):
    if token.kind == 'literal' and token.value.startswith('['):
        # Lexer stores interpolation text separately from normalized expression tokens.
        return ' '.join(piece for piece in json.loads(token.value)[2] if isinstance(piece, str))
    return literal(token) or ''


def method_ranges(tokens, pairs):
    ranges = []
    for start, end in pairs.items():
        if tokens[start].value != '(' or start == 0 or tokens[start - 1].kind != 'identifier':
            continue
        cursor = start - 2
        while cursor >= 0 and tokens[cursor].value not in (';', '{', '}', '=>'):
            cursor -= 1
        prefix = [t.value for t in tokens[cursor + 1:start - 1]]
        if not set(prefix).intersection(('public', 'private', 'internal', 'protected')):
            continue
        body = end + 1
        if body < len(tokens) and tokens[body].value in ('{', '=>'):
            if tokens[body].value == '{':
                finish = pairs[body]
            else:
                finish = next((i for i in range(body + 1, len(tokens)) if tokens[i].value == ';'), len(tokens))
            ranges.append((body, finish, tokens[start - 1].value))
    return ranges


def builder_ranges(tokens, pairs):
    # Central envelope/evidence builders remain in the inventory. Match their
    # qualified owner, not their path, so moving them cannot alter the allowance.
    namespace = ''
    ranges = []
    for i, token in enumerate(tokens):
        if token.value == 'namespace':
            end = next(j for j in range(i + 1, len(tokens)) if tokens[j].value in ('{', ';'))
            namespace = ''.join(t.value for t in tokens[i + 1:end])
        if (namespace == 'TiaMcpServer.ModelContextProtocol' and token.value == 'class'
                and [t.value for t in tokens[i + 1:i + 3]] == ['ResponseMeta', '{']):
            ranges.append((i + 2, pairs[i + 2]))
        if (namespace == 'TiaMcp.Adapters' and token.value == 'class'
                and [t.value for t in tokens[i + 1:i + 3]] == ['PlcFoundationEngine', '{']):
            # This worker-only Dictionary evidence preserves the released step
            # wrapper on old releases. It does not construct an MCP envelope;
            # Check-AdapterBoundary separately forbids JSON in this layer.
            for lo, hi, name in method_ranges(tokens, pairs):
                if i + 2 < lo < hi < pairs[i + 2] and name == 'RunHardwareAddressStep':
                    ranges.append((lo, hi))
    return ranges


def scan_tokens(tokens):
    pairs = lexer.matching_pairs(tokens)
    values = [literal(t) if t.kind == 'literal' else t.value.lstrip('@') for t in tokens]
    counts, handwritten = Counter(), Counter()
    methods, builders = method_ranges(tokens, pairs), builder_ranges(tokens, pairs)

    def is_at(index, pattern):
        return values[index:index + len(pattern)] == pattern

    def method(index):
        owners = [(lo, name) for lo, hi, name in methods if lo < index < hi]
        return max(owners, default=(0, ''))[1]

    def fields(opening):
        result = []
        i = opening + 1
        while i < pairs[opening]:
            if is_at(i, ['[', values[i + 1], ']', '=']) and tokens[i + 1].kind == 'literal':
                result.append((values[i + 1], i + 4))
            if i in pairs:
                i = pairs[i] + 1
            else:
                i += 1
        return result

    for i, token in enumerate(tokens):
        if token.kind == 'literal':
            if token.expressions:
                inner, written = scan_tokens(list(token.expressions))
                counts.update(inner)
                if not any(lo < i < hi for lo, hi in builders):
                    handwritten.update(written)
            continue
        if (i + 2 < len(values) and values[i] == '[' and values[i + 2] == ']'
                and tokens[i + 1].kind == 'literal' and values[i + 1] in ('timestamp', 'success', 'ok')):
            counts[values[i + 1] + '_indexers'] += 1
        if is_at(i, ['[', values[i + 1] if i + 1 < len(values) else None, ']', '=']) and tokens[i + 1].kind == 'literal':
            key = values[i + 1]
            name = {'timestamp': 'timestamp_assignments', 'success': 'success_assignments', 'ok': 'ok_assignments'}.get(key)
            if name:
                counts[name] += 1
            now = key == 'timestamp' and is_at(i + 4, ['DateTime', '.', 'Now'])
            if now:
                counts['timestamp_now_assignments'] += 1
                if i + 7 == len(values) or values[i + 7] != '.':
                    counts['timestamp_local_datetime'] += 1
            if not any(lo < i < hi for lo, hi in builders):
                if now:
                    handwritten['timestamp_now_assignments'] += 1
                if key == 'success':
                    handwritten['success_assignments'] += 1
            if key == 'createdUtc':
                tail = values[i + 4:next((j for j in range(i + 4, len(values)) if values[j] in (',', ';', '}')), len(values))]
                if ['ToString', '(', 'yyyy-MM-dd HH:mm:ss', ')', '+', 'Z'] == tail[-6:]:
                    counts['created_utc_custom'] += 1
            if key == 'timestamp' and is_at(i + 4, ['DateTime', '.', 'Now', '.', 'ToString', '(', 'O', ')']):
                counts['B9'] += 1
        for pattern, name in (
                (['new', 'JsonObject'], 'new_json_object'),
                (['new', 'ResponseMessage'], 'new_response_message'),
                (['throw', 'new', 'McpException'], 'throw_mcp_exception'),
                (['throw', 'new', 'PortalException'], 'throw_portal_exception'),
                (['DateTime', '.', 'Now', '.', 'ToString', '(', 'O', ')'], 'datetime_now_roundtrip'),
                (['DateTime', '.', 'UtcNow'], 'datetime_utcnow')):
            if is_at(i, pattern):
                counts[name] += 1
        for clock, name in (('UtcNow', 'datetimeoffset_utcnow_roundtrip'), ('Now', 'datetimeoffset_now_roundtrip')):
            if (is_at(i, ['DateTimeOffset', '.', clock, '.', 'ToString', '('])
                    and values[i + 6:i + 8] in (['o', ')'], ['O', ')'])):
                counts[name] += 1
        if is_at(i, ['RuntimeMeta', '(']) and method(i) == 'RunPlcSimTool':
            counts['B5b'] += 1
        if not (is_at(i, ['new', 'JsonObject']) or is_at(i, ['new', 'ResponseMessage'])):
            continue
        opening = i + 2
        if opening in pairs and values[opening] == '(':
            opening = pairs[opening] + 1
        if opening >= len(values) or values[opening] != '{':
            continue
        if values[i + 1] == 'ResponseMessage':
            body = tokens[opening + 1:pairs[opening]]
            if (not any(t.value == 'Meta' for t in body)
                    and any(re.search(r'fail|refus|not found|is null|no project|not available|not accessible|could not|error|are required|must be',
                                      literal_text(t), re.I) for t in body)):
                counts['B8'] += 1
            continue
        entries = fields(opening)
        keys = [key for key, _ in entries]
        owner = method(i)
        local = any(key == 'timestamp' and is_at(rhs, ['DateTime', '.', 'Now']) and values[rhs + 3] != '.' for key, rhs in entries)
        if local and keys == ['timestamp', 'success']:
            counts['B1'] += 1
        if local and keys == ['timestamp']:
            counts['B2'] += 1
        if local and 'success' in keys and keys.index('success') > 1:
            counts['B3'] += 1
        for owner_name, name in (('RunHmiStepTool', 'B4'), ('RunOfflineAnalysisTool', 'B5a'),
                                 ('BatchResult', 'B5c'), ('BuildOfflineXmlBuilderReport', 'B5d')):
            if owner == owner_name and 'timestamp' in keys and 'success' in keys:
                counts[name] += 1
        if 'timestamp' in keys and 'success' in keys and (owner.endswith('Meta') or owner == 'Create'):
            counts['B6'] += 1
        if 'ok' in keys and 'success' not in keys and 'timestamp' not in keys:
            counts['B7'] += 1
    return counts, handwritten


def scan(root):
    rows, errors = [], []
    directory = root / SOURCE_ROOT
    if not directory.is_dir():
        return rows, [f'missing source root: {SOURCE_ROOT.as_posix()}']
    for parent, dirs, names in os.walk(directory, onerror=lambda exc: errors.append(str(exc))):
        dirs[:] = sorted(d for d in dirs if d.lower() not in lexer.SKIP_DIRS)
        for name in sorted(names):
            if not name.lower().endswith('.cs') or name.lower().endswith(lexer.GENERATED_SUFFIXES):
                continue
            path = Path(parent) / name
            try:
                source = path.read_text(encoding='utf-8-sig')
                header = re.match(r'\s*(?:(?://[^\n]*(?:\n|$)|/\*.*?\*/)\s*)*', source, re.S)[0]
                if re.search(r'<auto-generated\b', header, re.I):
                    continue
                counts, handwritten = scan_tokens(lexer.Lexer(source).scan()[0])
                rows.append(dict(path=path.relative_to(root).as_posix(), counts={key: counts[key] for key in METRICS},
                                 handwritten={key: handwritten[key] for key in RATCHET}))
            except (OSError, ValueError, IndexError) as exc:
                errors.append(f'{path.relative_to(root).as_posix()}: {exc}')
    return sorted(rows, key=lambda row: row['path']), errors


def totals(rows, field, keys):
    return {key: sum(row[field][key] for row in rows) for key in keys}


def read_baseline(path):
    data = json.loads(path.read_text(encoding='utf-8'))
    if (not isinstance(data, dict) or data.get('format') != 1 or not isinstance(data.get('counts'), dict)
            or set(data['counts']) != set(RATCHET)
            or any(type(n) is not int or n < 0 for n in data['counts'].values())):
        raise ValueError(f'{path}: invalid response envelope baseline')
    return data['counts']


def growth(current, previous):
    return [f'{key} grew from {previous[key]} to {current[key]}' for key in RATCHET if current[key] > previous[key]]


def write_baseline(path, current, previous, allow_growth=False):
    if growth(current, previous) and not allow_growth:
        raise ValueError('refusing baseline growth; --allow-growth is only for reviewed initialization')
    data = dict(format=1, description='Path-independent totals of hand-written assignments; timestamp includes DateTime.Now.ToString. '
                'The qualified ResponseMeta builder is counted in the inventory but excluded from this ratchet.', counts=current)
    path.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8', newline='\n')


def check(root, baseline=None, update=False, allow_growth=False, as_json=False):
    rows, errors = scan(root)
    current = totals(rows, 'handwritten', RATCHET)
    report = dict(files=rows, totals=totals(rows, 'counts', METRICS), handwritten=current)
    path = baseline if baseline is not None else root / BASELINE
    removed = {}
    try:
        previous = {key: 0 for key in RATCHET} if update and allow_growth and not path.exists() else read_baseline(path)
        removed = {key: previous[key] - current[key] for key in RATCHET if current[key] < previous[key]}
        if update and not errors:
            write_baseline(path, current, previous, allow_growth)
        else:
            errors.extend(growth(current, previous))
    except (OSError, ValueError) as exc:
        errors.append(str(exc))
    report.update(shrunk=removed, errors=errors)
    if as_json:
        print(json.dumps(report, indent=2, ensure_ascii=False))
    else:
        for row in rows:
            print(row['path'] + ': ' + ', '.join(f'{key}={value}' for key, value in row['counts'].items() if value))
        print(f'Total: {len(rows)} files; ' + ', '.join(f'{key}={value}' for key, value in report['totals'].items()))
        print('Hand-written ratchet: ' + json.dumps(current, sort_keys=True))
        for key, count in removed.items():
            print(f'[SHRUNK] {key}: {count} fewer (use --update-baseline to retain the lower allowance)')
        for error in errors:
            print('[FAIL] ' + error)
        print(f'Response-envelope check: {len(errors)} issue(s).')
    return bool(errors)


class SelfTests(unittest.TestCase):
    def counts(self, source):
        return scan_tokens(lexer.Lexer(source).scan()[0])[0]

    def test_assignments_not_reads(self):
        counts = self.counts('var x = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }; '
                             'x["success"] = true; x["ok"] = false; Use(x["timestamp"], x["success"] == true);')
        self.assertEqual(1, counts['timestamp_now_assignments'])
        self.assertEqual(2, counts['success_assignments'])
        self.assertEqual(1, counts['ok_assignments'])
        self.assertEqual(2, counts['timestamp_indexers'])
        self.assertEqual(3, counts['success_indexers'])
        self.assertEqual(1, counts['B1'])

    def test_comments_and_strings(self):
        source = '''// ["success"] = true; new JsonObject
            /* throw new PortalException(); */
            var a = "new JsonObject"; var b = @"[""success""] = false";
            var c = """throw new McpException()"""; var d = '{';'''
        self.assertEqual(Counter(), self.counts(source))

    def test_interpolation_and_branches(self):
        counts = self.counts('''var s = $"new JsonObject {new JsonObject { ["success"] = true }}";
            #if V20
            throw new McpException();
            #else
            throw new PortalException();
            #endif''')
        for name in ('new_json_object', 'success_assignments', 'throw_mcp_exception', 'throw_portal_exception'):
            self.assertEqual(1, counts[name])

    def test_six_timestamp_encodings(self):
        counts = self.counts('''x["timestamp"] = DateTime.Now; x["timestamp"] = DateTime.Now.ToString("O");
            x["timestamp"] = DateTime.UtcNow; var a = DateTimeOffset.UtcNow.ToString("o");
            var b = DateTimeOffset.Now.ToString("o"); x["createdUtc"] = e.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss") + "Z";''')
        for name in ('timestamp_local_datetime', 'datetime_now_roundtrip', 'datetime_utcnow',
                     'datetimeoffset_utcnow_roundtrip', 'datetimeoffset_now_roundtrip', 'created_utc_custom'):
            self.assertEqual(1, counts[name], name)
        self.assertEqual(2, counts['timestamp_now_assignments'])

    def test_shapes_and_nested_keys(self):
        counts = self.counts('''new JsonObject { ["timestamp"] = DateTime.Now };
            new JsonObject { ["timestamp"] = DateTime.Now, ["tool"] = "x", ["success"] = false };
            new JsonObject { ["ok"] = true, ["child"] = new JsonObject { ["success"] = true } };
            new ResponseMessage { Message = "failed" }; new ResponseMessage { Message = "success" };
            new ResponseMessage { Message = "failed", Meta = new JsonObject() };''')
        for name in ('B2', 'B3', 'B7', 'B8'):
            self.assertEqual(1, counts[name], name)
        self.assertEqual(3, counts['new_response_message'])
        self.assertEqual(2, self.counts('''new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            new ResponseMessage { Message = "No project open." };''')['B8'])

    def test_executor_and_factory_hints(self):
        for method, metric in (('RunHmiStepTool', 'B4'), ('RunOfflineAnalysisTool', 'B5a'),
                               ('BatchResult', 'B5c'), ('BuildOfflineXmlBuilderReport', 'B5d'), ('RuntimeMeta', 'B6')):
            source = 'private static JsonObject ' + method + '() { return new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }; }'
            self.assertEqual(1, self.counts(source)[metric], method)
        self.assertEqual(1, self.counts('private Response RunPlcSimTool() { var meta = RuntimeMeta(false); }')['B5b'])

    def test_whitespace_and_escaped_keys(self):
        counts = self.counts(r'''x [ /*comment*/ "\u0073uccess" ] = true; x[@"timestamp"] = DateTime . Now;''')
        self.assertEqual(1, counts['success_assignments'])
        self.assertEqual(1, counts['timestamp_now_assignments'])

    def test_builder_exemption_is_qualified_and_inventory_remains(self):
        source = 'class ResponseMeta { void X() { x["success"] = true; } }'
        counts, handwritten = scan_tokens(lexer.Lexer('namespace TiaMcpServer.ModelContextProtocol { ' + source + ' }').scan()[0])
        self.assertEqual(1, counts['success_assignments'])
        self.assertEqual(0, handwritten['success_assignments'])
        self.assertEqual(1, scan_tokens(lexer.Lexer('namespace Other { ' + source + ' }').scan()[0])[1]['success_assignments'])

    def test_worker_evidence_builder_is_qualified_and_method_bounded(self):
        source = '''class PlcFoundationEngine {
            private HardwareAddressingReply RunHardwareAddressStep() { meta["success"] = false; return reply; }
            private void Other() { meta["success"] = true; }
        }'''
        counts, written = scan_tokens(lexer.Lexer('namespace TiaMcp.Adapters { ' + source + ' }').scan()[0])
        self.assertEqual(2, counts['success_assignments'])
        self.assertEqual(1, written['success_assignments'])
        self.assertEqual(2, scan_tokens(lexer.Lexer('namespace Other { ' + source + ' }').scan()[0])[1]['success_assignments'])

    def test_move_exclusions_and_determinism(self):
        with lexer.scratch_directory() as root:
            directory = root / SOURCE_ROOT
            directory.mkdir(parents=True)
            source = directory / 'one.cs'
            source.write_text('x["success"] = false;', encoding='utf-8')
            for name in ('obj/a.cs', 'bin-v20/a.cs', 'Generated/a.cs', 'a.g.cs', 'a.Designer.cs'):
                path = directory / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('"unterminated', encoding='utf-8')
            (directory / 'auto.cs').write_text('// <auto-generated>\n"unterminated', encoding='utf-8')
            before, errors = scan(root)
            self.assertEqual([], errors)
            self.assertEqual(1, len(before))
            source.rename(directory / 'moved.cs')
            after, errors = scan(root)
            self.assertEqual([], errors)
            self.assertEqual(totals(before, 'handwritten', RATCHET), totals(after, 'handwritten', RATCHET))
            self.assertEqual(after, scan(root)[0])
            (directory / 'new.cs').write_text('x["success"] = false;', encoding='utf-8')
            self.assertEqual(1, len(growth(totals(scan(root)[0], 'handwritten', RATCHET), totals(before, 'handwritten', RATCHET))))

    def test_ratchet_growth_shrink_and_update(self):
        previous = dict(zip(RATCHET, (2, 3)))
        current = dict(zip(RATCHET, (1, 4)))
        self.assertEqual(1, len(growth(current, previous)))
        with lexer.scratch_directory() as root:
            path = root / 'baseline.json'
            write_baseline(path, previous, {key: 0 for key in RATCHET}, True)
            original = path.read_bytes()
            with self.assertRaises(ValueError):
                write_baseline(path, current, previous)
            self.assertEqual(original, path.read_bytes())
            current['success_assignments'] = 2
            write_baseline(path, current, previous)
            self.assertEqual(current, read_baseline(path))

    def test_invalid_baselines_and_source(self):
        with lexer.scratch_directory() as root:
            path = root / 'baseline.json'
            for data in ([], {'format': 2}, {'format': 1, 'counts': {}},
                         {'format': 1, 'counts': dict(zip(RATCHET, (-1, True)))}):
                path.write_text(json.dumps(data), encoding='utf-8')
                with self.assertRaises(ValueError):
                    read_baseline(path)
        for source in ('new JsonObject {', '"unterminated', '/* unterminated'):
            with self.assertRaises(ValueError):
                self.counts(source)

    def test_report_and_ratchet_integration(self):
        with lexer.scratch_directory() as root:
            directory = root / SOURCE_ROOT
            directory.mkdir(parents=True)
            source = directory / 'sample.cs'
            source.write_text('x["timestamp"] = DateTime.Now; x["success"] = true;', encoding='utf-8')
            baseline = root / 'baseline.json'
            initial = dict.fromkeys(RATCHET, 1)
            write_baseline(baseline, initial, initial)
            outputs = []
            for _ in range(2):
                with redirect_stdout(io.StringIO()) as output:
                    self.assertFalse(check(root, baseline, as_json=True))
                outputs.append(output.getvalue())
            self.assertEqual(outputs[0], outputs[1])
            self.assertEqual(initial, json.loads(outputs[0])['handwritten'])
            source.write_text(source.read_text(encoding='utf-8') + 'x["success"] = false;', encoding='utf-8')
            for update in (False, True):
                with redirect_stdout(io.StringIO()):
                    self.assertTrue(check(root, baseline, update=update))
            self.assertEqual(initial, read_baseline(baseline))
            source.write_text('x["ok"] = true;', encoding='utf-8')
            with redirect_stdout(io.StringIO()) as output:
                self.assertFalse(check(root, baseline))
            self.assertIn('[SHRUNK] timestamp_now_assignments: 1 fewer', output.getvalue())
            self.assertIn('[SHRUNK] success_assignments: 1 fewer', output.getvalue())
            with redirect_stdout(io.StringIO()):
                self.assertFalse(check(root, baseline, update=True))
            self.assertEqual(dict.fromkeys(RATCHET, 0), read_baseline(baseline))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--baseline', type=Path)
    parser.add_argument('--json', action='store_true', help='emit deterministic per-file counts, totals and diagnostics as JSON')
    parser.add_argument('--update-baseline', action='store_true', help='retain only current counts; refuse growth')
    parser.add_argument('--allow-growth', action='store_true', help='allow reviewed baseline initialization with --update-baseline')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.allow_growth and not args.update_baseline:
        parser.error('--allow-growth requires --update-baseline')
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(SelfTests))
        return not result.wasSuccessful()
    return check(args.root.resolve(), args.baseline, args.update_baseline, args.allow_growth, args.json)


if __name__ == '__main__':
    sys.exit(main())
