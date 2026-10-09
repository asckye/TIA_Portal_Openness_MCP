"""Ratchet CJK string literals in MCP runtime sources, including indirect messages.

This is a conservative lexical guard, not interprocedural reachability analysis.
Direct sinks are labelled; other literals are also guarded so assigning a message
to a local/constant or returning it through a helper cannot evade the ratchet.
Reviewed bilingual/report/TIA data is an exact-literal multiset allowlist in the
baseline, with a reason per entry. No file, type or builder is blanket-exempt.
--rename-product-references migrates only the reviewed EnvironmentDoctor filename
segments once, preserving the Chinese text and the shrink-only allowance policy.
"""
from collections import Counter
import argparse
import importlib.util
import json
from pathlib import Path
import re
import sys
import unittest

spec = importlib.util.spec_from_file_location('mcp_text_hygiene', Path(__file__).with_name('Check-CommentHygiene.py'))
hygiene = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = hygiene
spec.loader.exec_module(hygiene)
lexer = hygiene.lexer
BASELINE = Path('scripts/checks/mcp-text-baseline.json')
KINDS = ('description', 'exception', 'message', 'meta', 'other-literal')
UI_PROJECTS = {'Gui', 'Client', 'Launcher'}
CJK = re.compile('[\u3007\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\U00020000-\U0002ffff\U00030000-\U000323af]')
ESCAPE = re.compile(r'\\(?:u([0-9a-fA-F]{4})|U([0-9a-fA-F]{8})|x([0-9a-fA-F]{1,4})|(.))', re.S)
DATA_CATEGORIES = {'project-content', 'bilingual-table', 'report-artifact', 'cli-console', 'search-alias'}


def unescape(text):
    def replace(match):
        digits = next((group for group in match.groups()[:3] if group is not None), None)
        if digits is not None:
            return chr(int(digits, 16))
        return {'n': '\n', 'r': '\r', 't': '\t', '0': '\0'}.get(match[4], match[4])
    decoded = ESCAPE.sub(replace, text)
    # A C# UTF-16 surrogate pair represents one supplementary Han character.
    return decoded.encode('utf-16-le', errors='surrogatepass').decode('utf-16-le', errors='surrogatepass')


def literal_parts(token):
    """Return own decoded text and fingerprint input; nested strings count once."""
    if token.kind != 'literal' or token.value.startswith("'"):
        return '', None
    value = token.value
    if value.startswith('['):
        prefix, quotes, pieces = json.loads(value)
        text = ''.join(piece for piece in pieces if isinstance(piece, str))
        identity = [prefix, quotes, [piece if isinstance(piece, str) else None for piece in pieces]]
        if quotes < 3 and '@' not in prefix:
            text = unescape(text)
        return text, identity
    if value.startswith(':'):
        return value[1:], value  # interpolation format text
    if value.endswith('u8'):
        value = value[:-2]
    if value.startswith('@"'):
        return value[2:-1].replace('""', '"'), token.value
    if value.startswith('"""'):
        width = len(value) - len(value.lstrip('"'))
        return value[width:-width], token.value
    if value.startswith('"'):
        return unescape(value[1:-1]), token.value
    return '', None


def expression_end(tokens, pairs, start):
    i = start
    while i < len(tokens):
        if tokens[i].value in (';', ',', '}', ')', ']'):
            break
        if i in pairs:
            i = pairs[i] + 1
        else:
            i += 1
    return i


def sink_ranges(tokens, pairs):
    ranges = []
    values = [token.value.lstrip('@') if token.kind == 'identifier' else token.value for token in tokens]
    for i, token in enumerate(tokens):
        value = values[i]
        if value in ('Description', 'DescriptionAttribute') and values[i + 1:i + 2] == ['(']:
            if any(tokens[lo].value == '[' and lo < i < hi for lo, hi in pairs.items()):
                ranges.append((i + 1, pairs[i + 1], 'description'))
        if value == 'new':
            opening = i + 1
            while opening < len(tokens) and (tokens[opening].kind == 'identifier' or values[opening] in ('.', '::')):
                opening += 1
            if opening in pairs and values[opening] == '(':
                typed = opening > i + 1 and values[opening - 1].endswith('Exception')
                target_typed = opening == i + 1 and i > 0 and values[i - 1] == 'throw'
                if typed or target_typed:
                    ranges.append((opening, pairs[opening], 'exception'))
        if value in ('=', '+=', '??=', '=>') and i:
            lhs = values[i - 1]
            if lhs == ']' and i >= 3:
                lhs = literal_parts(tokens[i - 2])[0]
            kind = 'message' if lhs.lower() in ('message', 'error') else None
            if lhs.lower() == 'meta':
                kind = 'meta'
            if i >= 4 and values[i - 1] == ']' and values[i - 4].lower() == 'meta':
                kind = 'meta'
            if kind:
                ranges.append((i, expression_end(tokens, pairs, i + 1), kind))
        if value == 'ResponseMeta' and values[i + 1:i + 2] == ['.'] and values[i + 3:i + 4] == ['(']:
            ranges.append((i + 3, pairs[i + 3], 'meta'))
    return ranges


def scan_source(source, path, project):
    tokens, _ = lexer.Lexer(source).scan()
    rows = []

    def visit(tokens, inherited='other-literal'):
        pairs = lexer.matching_pairs(tokens)
        ranges = sink_ranges(tokens, pairs)
        for i, token in enumerate(tokens):
            if token.kind != 'literal':
                continue
            kinds = [kind for lo, hi, kind in ranges if lo < i < hi]
            kind = min([inherited, *kinds], key=KINDS.index)
            text, identity = literal_parts(token)
            count = len(CJK.findall(text))
            if count:
                rows.append(dict(kind=kind, fingerprint=hygiene.fingerprint(kind, identity),
                                 path=path, line=source.count('\n', 0, token.start) + 1,
                                 project=project, cjk=count, literal=identity))
            if token.expressions:
                visit(list(token.expressions), kind)
    visit(tokens)
    return rows


def scan(root):
    rows, errors, projects = [], [], Counter()
    for path, project, source in hygiene.source_files(root, errors, lambda project: project not in UI_PROJECTS):
        projects[project] += 1
        try:
            rows.extend(scan_source(source, path, project))
        except (ValueError, IndexError, KeyError) as exc:
            errors.append(f'{path}: {exc}')
    return rows, errors, projects


def read_baseline(path, updating=False):
    entries = hygiene.read_baseline(path, KINDS)
    data = json.loads(path.read_text(encoding='utf-8'))
    allowed = data.get('allowlist')
    if not isinstance(allowed, list):
        raise ValueError(f'{path}: missing reviewed allowlist')
    for row in [*entries, *allowed]:
        if (not isinstance(row, dict) or row.get('kind') not in KINDS
                or type(row.get('cjk')) is not int or row['cjk'] < 1
                or not isinstance(row.get('literal'), (str, list))
                or row.get('fingerprint') != hygiene.fingerprint(row['kind'], row['literal'])
                or not isinstance(row.get('path'), str)
                or type(row.get('line')) is not int or row['line'] < 1):
            raise ValueError(f'{path}: invalid literal entry: {row!r}')
    for row in allowed:
        if (not isinstance(row.get('reason'), str) or not row['reason'].strip()
                or not updating and row.get('dataCategory') not in DATA_CATEGORIES):
            raise ValueError(f'{path}: allowlist needs a data literal and a nonempty review reason')
    if entries and not updating:
        raise ValueError(f'{path}: first-party MCP baseline entries must be empty')
    return entries, allowed


def partition(rows, allowed):
    remaining = {}
    for row in allowed:
        remaining.setdefault(row['fingerprint'], []).append(row)
    guarded, consumed = [], []
    for row in rows:
        matches = remaining.get(row['fingerprint'], [])
        if matches:
            review = matches.pop(0)
            consumed.append(dict(row, reason=review['reason'], dataCategory=review.get('dataCategory')))
        else:
            guarded.append(row)
    return guarded, consumed


def write_baseline(path, rows, previous, allowed, initialize=False):
    if initialize and path.exists():
        raise ValueError('--allow-growth is only permitted when creating a reviewed initial baseline')
    guarded, consumed = partition(rows, allowed)
    if guarded:
        raise ValueError(f'refusing first-party MCP baseline text: {len(guarded)} Chinese literal(s) require translation or exact data review')
    data = dict(format=1, fingerprint='sha256 of sink category and own literal text/segments; '
                'interpolation expressions counted separately; paths/lines informational; multiplicity retained',
                entries=hygiene.baseline_rows(guarded), allowlist=hygiene.baseline_rows(consumed))
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n', encoding='utf-8', newline='\n')


def rename_product_references(path):
    # One reviewed filename-only migration. Do not admit arbitrary changed messages.
    entries, allowed = read_baseline(path)
    changed = 0
    old_name = 'TiaMcp' + 'Server.exe'
    for row in [*entries, *allowed]:
        literal = row['literal']
        if row['path'] != 'src/Shared/Host/EnvironmentDoctor.cs' or not isinstance(literal, list):
            continue
        segments = literal[2]
        for index, segment in enumerate(segments):
            if isinstance(segment, str) and old_name in segment:
                prefix, suffix = segment.split(old_name)
                segments[index:index + 1] = [prefix + 'TiaMcp.Engine.V', None, '.exe' + suffix]
                row['fingerprint'] = hygiene.fingerprint(row['kind'], literal)
                changed += 1
                break
    if changed:
        data = json.loads(path.read_text(encoding='utf-8'))
        data['entries'] = hygiene.baseline_rows(entries)
        data['allowlist'] = hygiene.baseline_rows(allowed)
        path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n', encoding='utf-8', newline='\n')
    print(f'Product filename baseline migration: {changed} literal(s); no Chinese text or allowance added.')


def print_inventory(rows, allowed, projects):
    print('Project | files | ' + ' | '.join(kind + ' literals/CJK' for kind in KINDS) + ' | allowed literals/CJK')
    for project in sorted(projects):
        groups = [[row for row in rows if row['project'] == project and row['kind'] == kind] for kind in KINDS]
        groups.append([row for row in allowed if row['project'] == project])
        print(f'{project} | {projects[project]} | ' + ' | '.join(f'{len(group)}/{sum(r["cjk"] for r in group)}' for group in groups))
    print(f'Total: {sum(projects.values())} files; {len(rows)} guarded literals, {sum(r["cjk"] for r in rows)} CJK characters; '
          f'{len(allowed)} allowed data literals, {sum(r["cjk"] for r in allowed)} CJK characters.')


def reviewed_data(rows, path):
    reviews = json.loads(path.read_text(encoding='utf-8'))
    allowed = []
    for review in reviews:
        if review.get('dataCategory') not in DATA_CATEGORIES or not review.get('reason', '').strip():
            raise ValueError('Data review requires an approved category and reason')
        matches = [r for r in rows if all(r[k] == review[k] for k in ('path', 'line', 'literal'))]
        if len(matches) != 1:
            raise ValueError(f'Data review must identify one current literal: {review}')
        allowed.append(dict(matches[0], reason=review['reason'], dataCategory=review['dataCategory']))
    return allowed


def check(root, path, update=False, allow_growth=False, review_data=None):
    rows, errors, projects = scan(root)
    try:
        previous, allowed = ([], []) if update and allow_growth and not path.exists() else read_baseline(path, updating=update)
        if review_data:
            if not update: raise ValueError('--review-data requires --update-baseline')
            allowed = reviewed_data(rows, review_data)
        guarded, consumed = partition(rows, allowed)
        print_inventory(guarded, consumed, projects)
        added, removed = lexer.difference(guarded, previous)
        if update and not errors:
            write_baseline(path, rows, previous, allowed, allow_growth)
        else:
            errors.extend(f'{row["path"]}:{row["line"]}: new/changed Chinese {row["kind"]} '
                          f'({row["cjk"]} CJK) {row["fingerprint"]}' for row in guarded)
        missing_allowed = len(allowed) - len(consumed)
        print(f'Baseline: {len(guarded)} current, {len(added)} added, {len(removed)} disappeared; '
              f'{missing_allowed} allowlist entries disappeared (--update-baseline shrinks both lists).')
    except (OSError, ValueError) as exc:
        errors.append(str(exc))
    for error in errors:
        print('[FAIL] ' + error)
    print(f'MCP-text check: {len(errors)} issue(s).')
    return bool(errors)


class SelfTests(unittest.TestCase):
    def rows(self, source, path='sample.cs'):
        return scan_source(source, path, 'sample')

    def test_all_sinks(self):
        source = '''[System.ComponentModel.DescriptionAttribute("工具")]
            void F([Description("参数")] string x) {
              throw new System.ArgumentException("失败" + "原因", nameof(x));
              var r = new Reply { Message = $"消息{x}", Meta = new JsonObject { ["note"] = "元数据" } };
              meta["note"] = "说明"; response.Message += "补充";
              var m = ResponseMeta.Create("附注");
            }'''
        self.assertEqual(Counter(description=2, exception=2, message=2, meta=3), Counter(r['kind'] for r in self.rows(source)))
        self.assertEqual(19, sum(r['cjk'] for r in self.rows(source)))

    def test_indirect_messages_are_guarded(self):
        rows = self.rows('const string Error = "中文"; var s = "失败"; throw new Exception(s); return Helper("拒绝");')
        self.assertEqual(3, len(rows))

    def test_comments_and_english_are_ignored(self):
        self.assertEqual([], self.rows('''// throw new Exception("中文");
            /* [Description("中文")] */ var x = "// English"; var c = '中';
            var s = @"/* English ""quoted"" */"; Message = "OK";'''))

    def test_literals_and_escape_decoding(self):
        rows = self.rows(r'''Message = "\u4e2d\x6587"; Message = @"\u4e2d中文";
            Message = """中文 \u4e2d"""; Message = "\U00020000";
            Message = "\uD840\uDC00"; Message = "\\u4e2d";''')
        self.assertEqual([2, 2, 2, 1, 1], [r['cjk'] for r in rows])

    def test_interpolation_counts_nested_literals_once(self):
        rows = self.rows('''Message = $"中文 {F("内文", /* 假消息 */ 1)}";
            throw new Exception($$"""失败 {{F("原因")}}""");
            Message = @$"文本 {F(@"内文")}";''')
        self.assertEqual([2] * 6, [r['cjk'] for r in rows])
        self.assertEqual(['message', 'message', 'exception', 'exception', 'message', 'message'], [r['kind'] for r in rows])

    def test_branches_and_target_typed_exception(self):
        rows = self.rows('''#if V20
            if (Ready()) {
            #else
            if (Other()) {
            #endif
            throw new("中文"); }
            #if V20
            Message = "旧版";
            #else
            Message = "新版";
            #endif''')
        self.assertEqual(['exception', 'message', 'message'], [r['kind'] for r in rows])

    def test_exact_allowlist_cannot_hide_new_sinks_or_duplicates(self):
        data = self.rows('NameZh = "中文";')[0]
        allowed = [dict(data, reason='Reviewed bilingual doctor label.')]
        for source in ('throw new Exception("中文");', 'Message = "中文";', 'NameZh = "新增";'):
            self.assertEqual(1, len(partition(self.rows(source), allowed)[0]))
        self.assertEqual([1, 1], [len(group) for group in partition([data, data], allowed)])
        moved = self.rows('\nNameZh = "中文";', 'moved.cs')
        self.assertEqual(0, len(partition(moved, allowed)[0]))

    def test_multiset_and_shrink_only_baseline(self):
        previous = self.rows('throw new Exception("中文");')
        moved = self.rows('\nthrow new Exception("中文");', 'other/project.cs')
        self.assertEqual(([], []), lexer.difference(moved, previous))
        with lexer.scratch_directory() as root:
            path = root / 'baseline.json'
            with self.assertRaises(ValueError):
                write_baseline(path, previous, [], [], True)
            write_baseline(path, [], [], [], True)
            old = path.read_bytes()
            for rows, init in ((previous * 2, False), (self.rows('Message = "新文";'), False), ([], True)):
                with self.assertRaises(ValueError):
                    write_baseline(path, rows, previous, [], init)
                self.assertEqual(old, path.read_bytes())
            allowed = [dict(self.rows('NameZh = "数据";')[0], reason='Reviewed bilingual data.', dataCategory='bilingual-table')]
            write_baseline(path, [], previous, allowed)
            self.assertEqual(([], []), read_baseline(path))

    def test_allowlist_requires_review_reason_and_data_category(self):
        with lexer.scratch_directory() as root:
            path = root / 'baseline.json'
            for row in (dict(self.rows('NameZh = "中文";')[0], reason=''),
                        dict(self.rows('Message = "中文";')[0], reason='Not data.')):
                path.write_text(json.dumps(dict(format=1, entries=[], allowlist=[row])), encoding='utf-8')
                with self.assertRaises(ValueError):
                    read_baseline(path)

    def test_scope_exclusions_and_scan_errors(self):
        with lexer.scratch_directory() as root:
            for relative in lexer.SOURCE_ROOTS:
                (root / relative).mkdir(parents=True)
            for project in ('TiaMcpServer', 'TiaMcp.Logic', 'TiaMcp.FoundationHost', 'TiaMcp.Adapters'):
                path = root / lexer.SOURCE_ROOTS[0] / project / 'A.cs'
                path.parent.mkdir()
                path.write_text('throw new Exception("中文");', encoding='utf-8')
            path = root / lexer.SOURCE_ROOTS[2] / 'Gui/A.cs'
            path.parent.mkdir()
            path.write_text('Message = "界面";', encoding='utf-8')
            rows, errors, _ = scan(root)
            self.assertEqual((4, []), (len(rows), errors))
            path = root / lexer.SOURCE_ROOTS[0] / 'TiaMcpServer/A.cs'
            path.write_text('Message = "unterminated', encoding='utf-8')
            self.assertEqual(1, len(scan(root)[1]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=hygiene.ROOT)
    parser.add_argument('--baseline', type=Path)
    parser.add_argument('--update-baseline', action='store_true')
    parser.add_argument('--allow-growth', action='store_true')
    parser.add_argument('--review-data', type=Path, help='Exact path/line/literal reviews; fingerprints are computed by this checker')
    parser.add_argument('--rename-product-references', action='store_true',
                        help='Rewrite reviewed baseline literals to the 4.0 executable names before checking')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        return not unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(SelfTests)).wasSuccessful()
    if args.rename_product_references:
        rename_product_references(args.baseline or args.root / BASELINE)
    return check(args.root, args.baseline or args.root / BASELINE, args.update_baseline, args.allow_growth, args.review_data)


if __name__ == '__main__':
    sys.exit(main())
