"""Check C# swallowed catches against a path-independent, shrinking multiset baseline.

This is a lexical guard, not control-flow or symbol analysis. All preprocessor
branches are scanned. Literal text is opaque; interpolation expressions count as
code when looking for exception references, throws and known logging calls.
"""
import argparse
from bisect import bisect_right
from collections import Counter
from contextlib import contextmanager
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]
BASELINE = Path('scripts/checks/swallowed-exceptions-baseline.json')
SOURCE_ROOTS = ('tools/tiaportal-mcp/src', 'tools/openness-shared',
                'tools/tia-openness-studio/src')
CATEGORIES = ('cleanup', 'teardown', 'logging-failure', 'probe-optional',
              'enumerate-optional', 'native-fallback', 'parse-fallback',
              'env-probe', 'ui', 'fail-open-guard', 'privacy')
SKIP_DIRS = {'bin', 'obj', 'bin-v20', 'obj-v20', 'generated', '__pycache__'}
GENERATED_SUFFIXES = ('.g.cs', '.g.i.cs', '.generated.cs', '.designer.cs')
IDENTIFIER = re.compile(r'@?[^\W\d]\w*', re.UNICODE)
NUMBER = re.compile(r'(?:0[xX][\da-fA-F_]+|0[bB][01_]+|(?:\d[\d_]*(?:\.\d[\d_]*)?|'
                    r'\.\d[\d_]*)(?:[eE][+-]?[\d_]+)?)[uUlLfFdDmM]*')
SPACE = re.compile(r'\s+')
OPERATORS = re.compile(r'>>>=|>>>|<<=|>>=|\?\?=|=>|\?\.|\?\?|::|\+\+|--|&&|\|\||'
                       r'==|!=|<=|>=|<<|>>|\+=|-=|\*=|/=|%=|&=|\|=|\^=|\.\.')
MARKER_START = re.compile(r'^(?:/\*|//)\s*swallow\s*\(')
MARKER = re.compile(r'/\*\s*swallow\(([^()]*)\):\s*(.*?)\s*\*/', re.S)
LOG_METHODS = {'Log', 'log', 'LogDiag', 'LogExceptionSafe', 'LogTrace', 'LogDebug',
               'LogInformation', 'LogWarning', 'LogError', 'LogCritical'}


@dataclass(frozen=True)
class Token:
    kind: str
    value: str
    start: int
    end: int
    expressions: tuple = ()


def normalized(tokens):
    # Token boundaries matter: `a + +b` must not hash like `a++ + b`.
    return [(token.kind, token.value) for token in tokens]


class Lexer:
    def __init__(self, source):
        self.source = source
        self.pos = 0

    def error(self, message):
        line = self.source.count('\n', 0, self.pos) + 1
        raise ValueError(f'line {line}: {message}')

    def literal(self):
        source, start = self.source, self.pos
        match = re.match(r'(\$+@?|@\$?)?("+)', source[start:])
        if source[start] == "'":
            prefix, quotes, quote = '', 1, "'"
            self.pos += 1
        elif match:
            prefix, quote = match[1] or '', '"'
            quotes = len(match[2]) if len(match[2]) >= 3 and '@' not in prefix else 1
            self.pos += len(prefix) + quotes
        else:
            return None
        raw, verbatim = quotes >= 3, '@' in prefix
        dollars = prefix.count('$')
        pieces, expressions = [], []
        segment = self.pos
        while self.pos < len(source):
            if source.startswith(quote * quotes, self.pos):
                if verbatim and source.startswith('""', self.pos):
                    self.pos += 2
                    continue
                pieces.append(source[segment:self.pos])
                self.pos += quotes
                if source.startswith('u8', self.pos) and quote == '"':
                    self.pos += 2
                value = source[start:self.pos]
                if dollars:
                    value = json.dumps([prefix, quotes, pieces], ensure_ascii=False,
                                       separators=(',', ':'))
                return Token('literal', value, start, self.pos, tuple(expressions))
            if not raw and not verbatim and source[self.pos] == '\\':
                self.pos += 2
                continue
            if dollars and source[self.pos] == '{':
                if not raw and source.startswith('{{', self.pos):
                    self.pos += 2
                    continue
                width = dollars if raw else 1
                end = self.pos
                while end < len(source) and source[end] == '{':
                    end += 1
                if end - self.pos >= width:
                    # Extra opening braces in a raw interpolation are literal text.
                    self.pos = end - width
                    pieces.append(source[segment:self.pos])
                    self.pos = end
                    inner, _ = self.scan(interpolation=width)
                    pieces.append(normalized(inner))
                    expressions.extend(inner)
                    segment = self.pos
                    continue
                self.pos = end
                continue
            if not raw and not verbatim and source[self.pos] in '\r\n':
                self.error('newline in regular string/char literal')
            self.pos += 1
        self.error('unterminated literal')

    def scan(self, interpolation=0):
        source, tokens, comments, stack = self.source, [], [], []
        pairs = {'}': '{', ')': '(', ']': '['}
        while self.pos < len(source):
            start = self.pos
            whitespace = SPACE.match(source, start)
            if whitespace:
                self.pos = whitespace.end()
                continue
            if source.startswith('//', start):
                end = source.find('\n', start)
                self.pos = len(source) if end < 0 else end
                comments.append(Token('comment', source[start:self.pos], start, self.pos))
                continue
            if source.startswith('/*', start):
                end = source.find('*/', start + 2)
                if end < 0:
                    self.error('unterminated comment')
                self.pos = end + 2
                comments.append(Token('comment', source[start:self.pos], start, self.pos))
                continue
            if source[start] == '#' and not source[source.rfind('\n', 0, start) + 1:start].strip():
                end = source.find('\n', start)
                self.pos = len(source) if end < 0 else end
                value = re.sub(r'\s+', ' ', source[start:self.pos].split('//', 1)[0]).strip()
                tokens.append(Token('directive', value, start, self.pos))
                continue
            if interpolation and not stack and source.startswith('}' * interpolation, start):
                self.pos += interpolation
                return tokens, comments
            if interpolation and not stack and source[start] == ':' and not source.startswith('::', start):
                # Format text is not C# code (e.g. {value:catch "quoted"}).
                end = source.find('}' * interpolation, start + 1)
                if end < 0:
                    self.error('unterminated interpolation format')
                tokens.append(Token('literal', source[start:end], start, end))
                self.pos = end + interpolation
                return tokens, comments
            if source[start] in '\'"@$':
                literal = self.literal()
                if literal:
                    tokens.append(literal)
                    continue
            identifier = IDENTIFIER.match(source, start)
            number = NUMBER.match(source, start) if source[start].isdigit() or source[start] == '.' else None
            operator = OPERATORS.match(source, start)
            if identifier:
                kind, self.pos = 'identifier', identifier.end()
            elif number:
                kind, self.pos = 'number', number.end()
            elif operator:
                kind, self.pos = 'symbol', operator.end()
            else:
                kind, self.pos = 'symbol', start + 1
            token = Token(kind, source[start:self.pos], start, self.pos)
            tokens.append(token)
            if interpolation:
                if token.value in ('{', '(', '['):
                    stack.append(token.value)
                elif token.value in pairs:
                    if not stack or stack.pop() != pairs[token.value]:
                        self.error('unbalanced interpolation')
        if interpolation:
            self.error('unterminated interpolation')
        return tokens, comments


def matching_pairs(tokens):
    stack, matches, branches = [], {}, []
    pairs = {'}': '{', ')': '(', ']': '['}
    for index, token in enumerate(tokens):
        if token.kind == 'directive':
            directive = token.value.split()[0]
            if directive == '#if':
                branches.append(dict(initial=stack[:], ends=[], has_else=False))
            elif directive in ('#else', '#elif'):
                if not branches:
                    raise ValueError('conditional directive without #if')
                branch = branches[-1]
                branch['ends'].append(stack)
                branch['has_else'] |= directive == '#else'
                stack = branch['initial'][:]
            elif directive == '#endif':
                if not branches:
                    raise ValueError('#endif without #if')
                branch = branches.pop()
                ends = [*branch['ends'], stack]
                if not branch['has_else']:
                    ends.append(branch['initial'])
                shapes = {tuple(tokens[group[0]].value for group in end) for end in ends}
                if len(shapes) != 1:
                    raise ValueError('conditional branches leave incompatible delimiters')
                stack = [tuple(sorted({item for end in ends for item in end[level]}))
                         for level in range(len(stack))]
            continue
        if token.kind != 'symbol':
            continue
        if token.value in ('{', '(', '['):
            stack.append((index,))
        elif token.value in pairs:
            if not stack or tokens[stack[-1][0]].value != pairs[token.value]:
                raise ValueError(f'unbalanced {token.value} at offset {token.start}')
            for opening in stack.pop():
                matches[opening] = index
    if branches:
        raise ValueError('unclosed #if')
    if stack:
        token = tokens[stack[-1][0]]
        raise ValueError(f'unclosed {token.value} at offset {token.start}')
    return matches


def code_tokens(tokens):
    for token in tokens:
        if token.kind != 'literal':
            yield token
        yield from code_tokens(token.expressions)


def has_logging(tokens):
    values = [token.value.lstrip('@') for token in tokens]
    for index, value in enumerate(values[:-1]):
        # A name alone (or a string mentioning a logger) is not a call.
        if values[index + 1] != '(':
            continue
        if value in LOG_METHODS:
            return True
        receiver = values[index - 2] if index >= 2 and values[index - 1] in ('.', '?.') else ''
        if (receiver == 'SwallowedExceptions' and value == 'Note'
                or receiver == 'InvocationJournal' and value in ('Write', 'Native')
                or receiver in ('Console', 'Debug', 'Trace') and value in (
                    'Write', 'WriteLine', 'Fail', 'Assert', 'TraceError', 'TraceWarning', 'TraceInformation')
                or receiver in ('logger', '_logger', 'Logger', 'log', '_log', 'Log') and value in (
                    'Trace', 'Debug', 'Info', 'Information', 'Warn', 'Warning', 'Error', 'Fatal', 'Critical')
                or receiver == '_activity' and value == 'Append'):
            return True
        if (value in ('Write', 'WriteLine') and index >= 4
                and values[index - 4:index] in (['Console', '.', 'Error', '.'], ['Console', '.', 'Out', '.'])):
            return True
    return False


def classify(body, exception, filters):
    if not any(token.kind != 'directive' for token in body):
        return 'empty'
    tokens = list(code_tokens([*filters, *body]))
    for index, token in enumerate(tokens):
        if token.kind == 'identifier' and token.value == 'throw':
            return 'handled'
        if (exception and token.kind == 'identifier' and token.value.lstrip('@') == exception
                and (index == 0 or tokens[index - 1].value not in ('.', '?.', '::'))):
            return 'handled'
    return 'handled' if has_logging(tokens) else 'discarding'


def scan_source(source, path, project):
    tokens, comments = Lexer(source).scan()
    pairs = matching_pairs(tokens)
    newlines = [index for index, char in enumerate(source) if char == '\n']
    line = lambda offset: bisect_right(newlines, offset) + 1
    catches, seen, errors = [], set(), []
    for index, token in enumerate(tokens):
        if token.kind != 'identifier' or token.value != 'try':
            continue
        opening = index + 1
        if opening >= len(tokens) or tokens[opening].value != '{':
            raise ValueError(f'line {line(token.start)}: try without a block')
        closing = pairs[opening]
        cursor = closing + 1
        while cursor < len(tokens) and tokens[cursor].value == 'catch':
            start = cursor
            seen.add(start)
            cursor += 1
            exception, filters = None, []
            if tokens[cursor].value == '(':
                end = pairs[cursor]
                declaration = tokens[cursor + 1:end]
                if (len(declaration) >= 2 and declaration[-1].kind == 'identifier'
                        and declaration[-2].value not in ('.', '::')):
                    exception = declaration[-1].value.lstrip('@')
                cursor = end + 1
            if tokens[cursor].value == 'when':
                cursor += 1
                if tokens[cursor].value != '(':
                    raise ValueError(f'line {line(tokens[start].start)}: when without parentheses')
                end = pairs[cursor]
                filters = tokens[cursor + 1:end]
                cursor = end + 1
            if tokens[cursor].value != '{':
                raise ValueError(f'line {line(tokens[start].start)}: catch without a block')
            end = pairs[cursor]
            body = tokens[cursor + 1:end]
            payload = [normalized(tokens[opening + 1:closing]),
                       normalized(tokens[start:cursor]), normalized(body)]
            fingerprint = hashlib.sha256(json.dumps(payload, ensure_ascii=False,
                                                    separators=(',', ':')).encode('utf-8')).hexdigest()
            catches.append(dict(fingerprint=fingerprint, path=path, line=line(tokens[start].start),
                                project=project, kind=classify(body, exception, filters), category=None,
                                start=tokens[start].start, opening=tokens[cursor].end,
                                end=tokens[end].end,
                                following=tokens[end + 1].start if end + 1 < len(tokens) else len(source)))
            cursor = end + 1
    for index, token in enumerate(tokens):
        if token.kind == 'identifier' and token.value == 'catch' and index not in seen:
            errors.append(f'{path}:{line(token.start)}: catch has no matching try')
    for comment in comments:
        if not MARKER_START.match(comment.value):
            continue
        location = f'{path}:{line(comment.start)}'
        match = MARKER.fullmatch(comment.value)
        if not match or match[1] not in CATEGORIES or not match[2].strip():
            errors.append(f'{location}: invalid swallow marker (known category and nonempty reason required)')
            continue
        candidates = [catch for catch in catches if (
            catch['start'] <= comment.start < catch['following'] and line(comment.start) == catch['line']
            or catch['opening'] <= comment.start < catch['end']
            and not source[catch['opening']:comment.start].strip())]
        if not candidates:
            errors.append(f'{location}: swallow marker must be on the catch line or first in its block')
            continue
        catch = max(candidates, key=lambda row: row['start'])
        if catch['category']:
            errors.append(f'{location}: multiple swallow markers on one catch')
        catch['category'] = match[1]
    return catches, errors


def scan(root):
    catches, errors, projects = [], [], Counter()
    for relative in SOURCE_ROOTS:
        directory = root / relative
        if not directory.is_dir():
            errors.append(f'missing source root: {relative}')
            continue
        for parent, dirs, names in os.walk(directory, onerror=lambda exc: errors.append(str(exc))):
            dirs[:] = sorted(name for name in dirs if name.lower() not in SKIP_DIRS)
            for name in sorted(names):
                if not name.lower().endswith('.cs') or name.lower().endswith(GENERATED_SUFFIXES):
                    continue
                path = Path(parent) / name
                local = path.relative_to(directory)
                if relative == SOURCE_ROOTS[0] and local.parts[0].startswith('TiaMcp.WorkerProtocol'):
                    continue
                project = 'openness-shared' if relative == SOURCE_ROOTS[1] else local.parts[0]
                try:
                    source = path.read_text(encoding='utf-8-sig')
                    header = re.match(r'\s*(?:(?://[^\n]*(?:\n|$)|/\*.*?\*/)\s*)*', source, re.S)[0]
                    if re.search(r'<auto-generated\b', header, re.I):
                        continue
                    projects[project] += 1
                    rows, issues = scan_source(source, path.relative_to(root).as_posix(), project)
                    catches.extend(rows)
                    errors.extend(issues)
                except (OSError, ValueError, IndexError) as exc:
                    errors.append(f'{path.relative_to(root).as_posix()}: {exc}')
    return catches, errors, projects


def baseline_rows(catches):
    return sorted((dict(fingerprint=row['fingerprint'], kind=row['kind'], path=row['path'], line=row['line'])
                   for row in catches if row['kind'] != 'handled' and row['category'] is None),
                  key=lambda row: (row['fingerprint'], row['path'], row['line']))


def read_baseline(path):
    data = json.loads(path.read_text(encoding='utf-8'))
    if not isinstance(data, dict) or data.get('format') != 1 or not isinstance(data.get('catches'), list):
        raise ValueError(f'{path}: unsupported baseline format')
    for row in data['catches']:
        if (not isinstance(row, dict) or not isinstance(row.get('fingerprint'), str)
                or not re.fullmatch(r'[0-9a-f]{64}', row['fingerprint'])
                or row.get('kind') not in ('empty', 'discarding')
                or not isinstance(row.get('path'), str)
                or type(row.get('line')) is not int or row['line'] < 1):
            raise ValueError(f'{path}: invalid baseline catch: {row!r}')
    return data['catches']


def difference(current, previous):
    def unmatched(rows, allowances):
        remaining, result = Counter(row['fingerprint'] for row in allowances), []
        for row in rows:
            key = row['fingerprint']
            if remaining[key]:
                remaining[key] -= 1
            else:
                result.append(row)
        return result
    return unmatched(current, previous), unmatched(previous, current)


def write_baseline(path, current, previous, allow_growth=False):
    added, _ = difference(current, previous)
    if added and not allow_growth:
        raise ValueError(f'refusing baseline growth: {len(added)} new/changed catch(es); '
                         'only reviewed initialization may use --allow-growth')
    data = dict(format=1, fingerprint='sha256 of tokenized try body, catch clause (including filter), catch body; '
                'paths/lines are informational; duplicate fingerprints retain their multiplicity', catches=current)
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n', encoding='utf-8', newline='\n')


def print_inventory(catches, projects):
    print('Project                                  files  catches  empty  discarding  handled  marked')
    for project in sorted(projects):
        rows = [row for row in catches if row['project'] == project]
        counts = Counter(row['kind'] for row in rows)
        marked = sum(row['category'] is not None for row in rows)
        print(f'{project:40} {projects[project]:5} {len(rows):8} {counts["empty"]:6} '
              f'{counts["discarding"]:11} {counts["handled"]:8} {marked:7}')
    print('Category                                  empty  discarding  handled')
    for category in ('unmarked', *CATEGORIES):
        counts = Counter(row['kind'] for row in catches if (row['category'] or 'unmarked') == category)
        print(f'{category:40} {counts["empty"]:6} {counts["discarding"]:11} {counts["handled"]:8}')
    counts = Counter(row['kind'] for row in catches)
    print(f'Total: {sum(projects.values())} files, {len(catches)} catches; '
          f'{counts["empty"]} empty, {counts["discarding"]} discarding, {counts["handled"]} handled. '
          'WorkerProtocol and generated/build files excluded.')


def check(root, baseline=None, update=False, allow_growth=False):
    catches, errors, projects = scan(root)
    print_inventory(catches, projects)
    path = baseline if baseline is not None else root / BASELINE
    current = baseline_rows(catches)
    try:
        previous = [] if update and allow_growth and not path.exists() else read_baseline(path)
        added, removed = difference(current, previous)
        for row in removed:
            print(f'[REMOVED] {row["path"]}:{row["line"]}: {row["kind"]} {row["fingerprint"]}')
        if update and not errors:
            write_baseline(path, current, previous, allow_growth)
            print(f'Wrote {len(current)} baseline catches to {path}.')
        else:
            errors.extend(f'{row["path"]}:{row["line"]}: new/changed unmarked {row["kind"]} catch '
                          f'{row["fingerprint"]}' for row in added)
        print(f'Baseline: {len(current)} current, {len(added)} added, {len(removed)} disappeared '
              '(use --update-baseline to retain only current entries).')
    except (OSError, ValueError) as exc:
        errors.append(str(exc))
    for error in errors:
        print('[FAIL] ' + error)
    print(f'Swallowed-exception check: {len(errors)} issue(s).')
    return bool(errors)


@contextmanager
def scratch_directory():
    # Keep fixtures in the worktree; mkdir's inherited permissions also work in
    # restricted Windows sessions where TemporaryDirectory's private ACL does not.
    parent = (ROOT / 'bin-build').resolve()
    path = parent / ('swallowed-self-test-' + uuid.uuid4().hex)
    path.mkdir(parents=True)
    try:
        yield path
    finally:
        if path.resolve().parent != parent:
            raise ValueError('scratch directory escaped bin-build')
        shutil.rmtree(path)


class SelfTests(unittest.TestCase):
    def rows(self, source):
        rows, errors = scan_source(source, 'sample.cs', 'sample')
        self.assertEqual([], errors)
        return rows

    def test_catch_forms_filters_and_nested_tries(self):
        rows = self.rows('''try { try { A(); } catch (Inner e) { Use(e); } }
            catch (global::System.Exception) when (Ready(F(1, (2)))) { }
            catch (Other ex) when (Ready(ex)) { return; }
            catch { return false; } finally { Done(); }''')
        self.assertEqual(['empty', 'handled', 'discarding', 'handled'], [r['kind'] for r in rows])

    def test_comments_literals_and_braces(self):
        rows = self.rows(r'''// catch { try { }
            try { var a = "catch { \\\" }"; var b = @"catch "" { }";
                var c = '}'; var d = '\''; /* catch { } */ }
            catch { /* no statements */ }''')
        self.assertEqual(['empty'], [r['kind'] for r in rows])

    def test_interpolated_and_verbatim_strings(self):
        rows = self.rows('''try { var a = $"catch {{ {F("}", new[] { 1, 2 })} }}";
                var b = $@"catch "" {F(@"""}")} {{";
                var c = @$"catch { $"nested {F("catch")}" }"; }
            catch (Exception ex) { return $"failed: {ex.Message}"; }''')
        self.assertEqual(['handled'], [r['kind'] for r in rows])

    def test_raw_strings(self):
        rows = self.rows('''try { var a = """catch { "quoted" }""";
                var b = """"
                    catch { """ }
                    """";
                var c = $$"""literal { catch } {{{F("}")}}} """; }
            catch (Exception ex) { return $$"""text {ex} {{ex.Message}}"""; }''')
        self.assertEqual(['handled'], [r['kind'] for r in rows])

    def test_preprocessor_branches(self):
        rows = self.rows('''void F() {
            #if V20
            if (A()) {
            #else
            if (B()) {
            #endif
            try { Work(); } catch { }
            }
            #if V20
            try { Old(); } catch { }
            #elif V21
            try { New(); } catch { return; }
            #endif
            }''')
        self.assertEqual(['empty', 'empty', 'discarding'], [r['kind'] for r in rows])
        self.assertEqual('empty', self.rows('try { A(); } catch {\n#if V20\n#endif\n}')[0]['kind'])

    def test_classification(self):
        cases = [('/* why */', 'empty'), ('return false;', 'discarding'),
                 (';', 'discarding'), ('throw;', 'handled'), ('throw Wrap();', 'handled'),
                 ('return ex.Message;', 'handled'), ('Use(@ex);', 'handled'),
                 ('return $"ex {other}";', 'discarding'), ('return $"{other:ex}";', 'discarding'),
                 ('return "throw ex LogError()";', 'discarding'),
                 ('return @"ex";', 'discarding'), ('return """ex""";', 'discarding'),
                 ('return other.ex;', 'discarding'), ('LogError("failed");', 'handled'),
                 ('Console.Error.WriteLine("failed");', 'handled'),
                 ('Debug.WriteLine("failed");', 'handled'), ('logger?.LogWarning("failed");', 'handled'),
                 ('SwallowedExceptions.Note("site", other);', 'handled'),
                 ('var LogError = 1;', 'discarding'), ('catalog.Find();', 'discarding'),
                 ('// ex throw LogError()\n return;', 'discarding')]
        for body, expected in cases:
            with self.subTest(body=body):
                self.assertEqual(expected, self.rows('try { A(); } catch (Exception ex) { ' + body + ' }')[0]['kind'])

    def test_markers(self):
        for category in CATEGORIES:
            for source in (f'try {{ A(); }} catch /* swallow({category}): reason */ {{ }}',
                           f'try {{ A(); }} catch\n{{\n /* swallow({category}): reason */ }}',
                           f'try {{ A(); }} catch {{ }} /* swallow({category}): reason */'):
                with self.subTest(source=source):
                    self.assertEqual(category, self.rows(source)[0]['category'])
        rows = self.rows('try { A(); } catch { try { B(); } catch { /* swallow(ui): inner */ } }')
        self.assertEqual([None, 'ui'], [row['category'] for row in rows])
        self.assertIsNone(self.rows('try { A(); } catch { var s = "/* swallow(ui): fake */"; }')[0]['category'])

    def test_invalid_markers(self):
        for comment in ('/* swallow(unknown): reason */', '/* swallow(ui):  */',
                        '/* swallow(ui) reason */', '// swallow(ui): reason\n'):
            with self.subTest(comment=comment):
                _, errors = scan_source('try { A(); } catch { ' + comment + ' }', 'x.cs', 'x')
                self.assertEqual(1, len(errors))
        for source in ('try { A(); } catch\n{ return;\n/* swallow(ui): late */ }',
                       '/* swallow(ui): orphan */ try { A(); } catch { }',
                       'try { A(); } catch /* swallow(ui): one */ { /* swallow(ui): two */ }'):
            with self.subTest(source=source):
                _, errors = scan_source(source, 'x.cs', 'x')
                self.assertEqual(1, len(errors))

    def test_fingerprints(self):
        first = self.rows('try { A("x y"); } catch (T ex) when (F(ex)) { return false; }')[0]
        same = self.rows('try\n{/* why */ A ( "x y" ) ; } catch(T ex) when(F( ex ))\n{ return /*why*/ false ; }')[0]
        self.assertEqual(first['fingerprint'], same['fingerprint'])
        for source in ('try { B("x y"); } catch (T ex) when (F(ex)) { return false; }',
                       'try { A("xy"); } catch (T ex) when (F(ex)) { return false; }',
                       'try { A("x y"); } catch (U ex) when (F(ex)) { return false; }',
                       'try { A("x y"); } catch (T ex) when (G(ex)) { return false; }',
                       'try { A("x y"); } catch (T ex) when (F(ex)) { return true; }'):
            self.assertNotEqual(first['fingerprint'], self.rows(source)[0]['fingerprint'])
        self.assertEqual(self.rows('try { F($"x {A(1)}"); } catch { }')[0]['fingerprint'],
                         self.rows('try { F($"x { A( /*why*/ 1 ) }"); } catch { }')[0]['fingerprint'])
        self.assertNotEqual(self.rows('try { F(a++); } catch { }')[0]['fingerprint'],
                            self.rows('try { F(a + +b); } catch { }')[0]['fingerprint'])
        self.assertEqual(self.rows('try { A(); } catch { }')[0]['fingerprint'],
                         self.rows('try { A(); } catch /* swallow(ui): reason */ { }')[0]['fingerprint'])

    def test_multiset_and_shrink_only_update(self):
        previous = baseline_rows(self.rows('try { A(); } catch { }'))
        moved = [dict(previous[0], path='another/project/file.cs', line=200)]
        self.assertEqual(([], []), difference(moved, previous))
        self.assertEqual((previous, []), difference(previous * 2, previous))
        self.assertEqual(([], previous), difference([], previous))
        with scratch_directory() as directory:
            path = Path(directory) / 'baseline.json'
            write_baseline(path, previous, [], allow_growth=True)
            original = path.read_bytes()
            with self.assertRaises(ValueError):
                write_baseline(path, previous * 2, previous)
            self.assertEqual(original, path.read_bytes())
            changed = baseline_rows(self.rows('try { B(); } catch { }'))
            with self.assertRaises(ValueError):
                write_baseline(path, changed, previous)
            write_baseline(path, [], previous)
            self.assertEqual([], read_baseline(path))

    def test_invalid_baseline(self):
        with scratch_directory() as directory:
            path = directory / 'baseline.json'
            for data in ([], {'format': 99, 'catches': []}, {'format': 1, 'catches': [{}]},
                         {'format': 1, 'catches': [{'fingerprint': 'invalid', 'kind': 'empty', 'path': 'x', 'line': 1}]}):
                with self.subTest(data=data):
                    path.write_text(json.dumps(data), encoding='utf-8')
                    with self.assertRaises(ValueError):
                        read_baseline(path)

    def test_scan_exclusions_and_move(self):
        with scratch_directory() as directory:
            root = Path(directory)
            for relative in SOURCE_ROOTS:
                (root / relative).mkdir(parents=True)
            source_root = root / SOURCE_ROOTS[0]
            path = source_root / 'Project' / 'sample.cs'
            path.parent.mkdir()
            path.write_text('var s = "<auto-generated>"; try { A(); } catch { }', encoding='utf-8')
            for name in ('Project/bin/bad.cs', 'Project/obj/bad.cs', 'Project/obj-v20/bad.cs',
                         'Project/Generated/bad.cs', 'Project/bad.g.cs', 'Project/bad.g.i.cs',
                         'Project/bad.Designer.cs', 'TiaMcp.WorkerProtocol.Core/bad.cs'):
                excluded = source_root / name
                excluded.parent.mkdir(parents=True, exist_ok=True)
                excluded.write_text('catch {', encoding='utf-8')
            (path.parent / 'Auto.cs').write_text('// <auto-generated />\ncatch {', encoding='utf-8')
            before, errors, _ = scan(root)
            self.assertEqual([], errors)
            self.assertEqual(1, len(before))
            target = root / SOURCE_ROOTS[2] / 'AnotherProject' / 'moved.cs'
            target.parent.mkdir()
            path.rename(target)
            after, errors, _ = scan(root)
            self.assertEqual([], errors)
            self.assertEqual(([], []), difference(baseline_rows(after), baseline_rows(before)))
            target.write_text(target.read_text(encoding='utf-8') + 'try { B(); } catch { }', encoding='utf-8')
            added, errors, _ = scan(root)
            self.assertEqual([], errors)
            self.assertEqual(1, len(difference(baseline_rows(added), baseline_rows(before))[0]))

    def test_malformed_source_fails(self):
        for source in ('try {', 'try { "unterminated', 'try { /* unterminated',
                       'try { F($"{A(1)}"); } catch {', 'catch { }'):
            with self.subTest(source=source):
                try:
                    _, errors = scan_source(source, 'x.cs', 'x')
                except ValueError:
                    continue
                self.assertTrue(errors)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT, help='repository or scratch-copy root')
    parser.add_argument('--baseline', type=Path, help='baseline path (default: <root>/' + str(BASELINE) + ')')
    parser.add_argument('--update-baseline', action='store_true', help='rewrite the baseline, refusing added fingerprints')
    parser.add_argument('--allow-growth', action='store_true', help='allow reviewed baseline initialization with --update-baseline')
    parser.add_argument('--self-test', action='store_true', help='run lexer, classification and baseline regression fixtures')
    args = parser.parse_args()
    if args.allow_growth and not args.update_baseline:
        parser.error('--allow-growth requires --update-baseline')
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(SelfTests))
        return not result.wasSuccessful()
    return check(args.root.resolve(), args.baseline, args.update_baseline, args.allow_growth)


if __name__ == '__main__':
    sys.exit(main())
