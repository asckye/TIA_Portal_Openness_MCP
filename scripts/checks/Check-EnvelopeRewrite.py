"""E4: accept only closed, expression-preserving C# envelope rewrites against Git.

Outside a matched template every character must be unchanged (including comments
and whitespace). Inside, only inter-token whitespace is ignored; literal spelling
and expression tokens are exact. This is deliberately not a general C# refactorer.
Builder implementations/tests are reviewed with E2, separately from call-site E4.
"""
import argparse
from collections import Counter
from dataclasses import dataclass
import importlib.util
from pathlib import Path
import re
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('envelope_rewrite_lexer',
    Path(__file__).with_name('Check-SwallowedExceptions.py'))
lexer = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = lexer
spec.loader.exec_module(lexer)

# No wildcard replacement, caller ToString(), casts, clock substitution, collection
# argument or implicit-clock overload is allowed. JsonNode tuple values keep the
# initializer's implicit conversions; the compiler rejects IEnumerable arguments.
TEMPLATES = ('Basic/explicit-clock', 'Unstamped/verdict', 'Unstamped/fields')
CLOCKS = {'DateTime.Now', 'DateTime.UtcNow', 'ResponseClock.Now', 'ResponseClock.UtcNow'}
MARKER = re.compile(r'// envelope: legacy-[a-z0-9]+(?:-[a-z0-9]+)*')


def tokens(source):
    code, comments = lexer.Lexer(source).scan()
    return sorted([*code, *comments], key=lambda token: token.start)


def spellings(source, items):
    # Raw interpolated literals matter too: changing an interpolation is not whitespace.
    return tuple(source[token.start:token.end] for token in items)


def constant(source):
    items = tokens(source)
    if len(items) == 2 and items[0].value in ('+', '-') and items[1].kind == 'number':
        return True
    return len(items) == 1 and (items[0].kind == 'number' or items[0].value in ('true', 'false', 'null')
        or items[0].kind == 'literal' and not items[0].expressions)


def annotation_free(source):
    cuts = []
    for token in tokens(source):
        if token.kind != 'comment' or not MARKER.fullmatch(token.value):
            continue
        start = source.rfind('\n', 0, token.start) + 1
        end = source.find('\n', token.end)
        end = len(source) if end < 0 else end + 1
        if source[start:token.start].strip() or source[token.end:end].strip():
            continue
        cuts.append((start, end))
    clean, previous, removed = '', 0, []
    for start, end in cuts:
        clean += source[previous:start]
        removed.append((len(clean), end - start))
        previous = end
    return clean + source[previous:], removed


@dataclass(frozen=True)
class Rewrite:
    start: int
    end: int
    template: str
    replacement: str


def initializers(source):
    items = tokens(source)
    raw = spellings(source, items)
    for i in range(len(items) - 2):
        if raw[i:i + 2] != ('new', 'JsonObject'):
            continue
        j = i + 2
        if raw[j:j + 2] == ('(', ')'):
            j += 2
        if raw[j] != '{':
            continue
        j += 1
        fields = []
        while j + 4 < len(items) and raw[j] == '[' and raw[j + 2:j + 4] == (']', '='):
            key = raw[j + 1]
            if items[j + 1].kind != 'literal' or not re.fullmatch(r'"[A-Za-z][A-Za-z0-9]*"', key):
                break
            start = j + 4
            j = start
            stack = []
            while j < len(items):
                value = raw[j]
                if not stack and value in (',', '}'):
                    break
                if value in ('(', '[', '{'):
                    stack.append(value)
                elif value in (')', ']', '}'):
                    if not stack:
                        break
                    stack.pop()
                j += 1
            if j == start or j == len(items):
                break
            fields.append((key, source[items[start].start:items[j - 1].end]))
            if raw[j] == ',':
                j += 1
            else:
                break
        if not fields or j >= len(items) or raw[j] != '}':
            continue
        if any(item.kind in ('comment', 'directive') for item in items[i:j + 1]):
            continue  # Comments within an initializer need an explicitly reviewed template.
        keys = [key for key, _ in fields]
        # Repeated keys can make argument evaluation observe different node ownership.
        if len(set(keys)) != len(keys):
            continue
        if keys[:2] == ['"timestamp"', '"success"']:
            clock = fields[0][1]
            if ''.join(spellings(clock, tokens(clock))) not in CLOCKS:
                continue
            template, method, arguments, tail = TEMPLATES[0], 'Basic', [clock, fields[1][1]], fields[2:]
        elif keys[0] == '"success"' and '"timestamp"' not in keys:
            template = TEMPLATES[2] if len(fields) > 1 else TEMPLATES[1]
            method, arguments, tail = 'Unstamped', [fields[0][1]], fields[1:]
        else:
            continue
        # Append adopts nodes after all arguments have been evaluated. With at most
        # one nonconstant tail value, no later expression can observe earlier node
        # adoption (e.g. child.Parent). Broader shapes need a new reviewed template.
        if sum(not constant(value) for _, value in tail) > 1:
            continue
        arguments += [f'({key}, {value})' for key, value in tail]
        yield Rewrite(items[i].start, items[j].end, template,
                      f'ResponseMeta.{method}(' + ', '.join(arguments) + ')')


def compare(old, new):
    """Return (template counts, first mismatch offset, nearest template)."""
    old, _ = annotation_free(old)
    new, removed = annotation_free(new)
    candidates = list(initializers(old))
    new_tokens = tokens(new)
    by_start = {token.start: i for i, token in enumerate(new_tokens)}
    new_raw = spellings(new, new_tokens)
    counts = Counter()
    before = after = 0

    def failure(position):
        nearest = min(candidates, key=lambda item: abs(item.start - before)).template if candidates else 'none (see ' + ', '.join(TEMPLATES) + ')'
        original = position + sum(length for start, length in removed if start <= position)
        return counts, original, nearest

    for candidate in candidates:
        if candidate.start < before:
            continue  # A nested value must remain verbatim inside its outer template.
        prefix = old[before:candidate.start]
        if not new.startswith(prefix, after):
            common = next((i for i, (a, b) in enumerate(zip(prefix, new[after:])) if a != b),
                          min(len(prefix), len(new) - after))
            return failure(after + common)
        after += len(prefix)
        before = candidate.start
        original = old[candidate.start:candidate.end]
        if new.startswith(original, after):
            after += len(original)
        else:
            expected = spellings(candidate.replacement, tokens(candidate.replacement))
            index = by_start.get(after)
            if index is None or new_raw[index:index + len(expected)] != expected:
                return failure(after)
            after = new_tokens[index + len(expected) - 1].end
            counts[candidate.template] += 1
        before = candidate.end
    suffix = old[before:]
    if suffix != new[after:]:
        common = next((i for i, (a, b) in enumerate(zip(suffix, new[after:])) if a != b),
                      min(len(suffix), len(new) - after))
        return failure(after + common)
    return counts, None, None


def git(*args):
    result = subprocess.run(['git', *args], cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        raise ValueError(result.stderr.decode('utf-8', errors='replace').strip())
    return result.stdout


def changed_paths(base, paths):
    changed = git('diff', '--name-only', '-z', base, '--', *paths)
    untracked = git('ls-files', '--others', '--exclude-standard', '-z', '--', *paths)
    return sorted({name.decode('utf-8') for name in (changed + untracked).split(b'\0')
                   if name.lower().endswith(b'.cs')})


def check(base, paths):
    # Resolve once, and never interpret a user-provided revision as a Git option.
    base = git('rev-parse', '--verify', '--end-of-options', base + '^{commit}').decode().strip()
    counts = Counter()
    errors = 0
    names = changed_paths(base, paths)
    for name in names:
        path = ROOT / name
        try:
            old = git('show', base + ':' + name).decode('utf-8')
            new = path.read_bytes().decode('utf-8')
            found, offset, nearest = compare(old, new)
            if offset is not None:
                line = new.count('\n', 0, offset) + 1
                errors += 1
                print(f'FAIL {name}:{line}: unknown rewrite; nearest template: {nearest}')
                continue
            counts.update(found)
            print(f'PASS {name}: ' + (', '.join(f'{key}={value}' for key, value in sorted(found.items())) or 'annotation only'))
        except (OSError, UnicodeError, ValueError) as error:
            errors += 1
            print(f'FAIL {name}:1: {error}; nearest template: none (file must exist and be valid UTF-8 C#)')
    print(f'COMPLETE: {len(names) - errors} files passed; {errors} failed; {sum(counts.values())} template rewrites')
    return int(bool(errors))


class SelfTests(unittest.TestCase):
    BASIC = 'new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["count"] = res.Members?.Count() ?? 0 }'
    BUILT = 'ResponseMeta.Basic(DateTime.Now, true, ("count", res.Members?.Count() ?? 0))'

    def assertRewrite(self, old, new, accepted=True):
        result = compare('prefix;\n' + old + ';\nsuffix;', 'prefix;\n' + new + ';\nsuffix;')
        self.assertEqual(accepted, result[1] is None, result)
        return result

    def test_basic(self):
        self.assertEqual({'Basic/explicit-clock': 1}, self.assertRewrite(self.BASIC, self.BUILT)[0])

    def test_unstamped(self):
        for suffix, tail in (('', ''), (', ["usage"] = usage', ', ("usage", usage)'),
                             (', ["n"] = 9007199254740993L, ["nil"] = null', ', ("n", 9007199254740993L), ("nil", null)')):
            self.assertRewrite('new JsonObject { ["success"] = false' + suffix + ' }',
                               'ResponseMeta.Unstamped(false' + tail + ')')

    def test_nested_values_and_strings(self):
        for value in ('new JsonObject { ["x"] = "a,b}\\\"" }', '$"value {F(1, 2)}"'):
            old = 'new JsonObject { ["success"] = ok, ["value"] = ' + value + ' }'
            self.assertRewrite(old, next(initializers(old)).replacement)

    def test_whitespace_inside_template_only(self):
        self.assertRewrite(self.BASIC, self.BUILT.replace(',', ',\n ').replace('DateTime.Now', 'DateTime . Now'))
        self.assertRewrite(self.BASIC + '\nSame();', self.BUILT + '\n Same();', False)

    def test_wrong_order(self):
        self.assertRewrite(self.BASIC, 'ResponseMeta.Basic(true, DateTime.Now, ("count", res.Members?.Count() ?? 0))', False)
        old = 'new JsonObject { ["success"] = true, ["a"] = 1, ["b"] = 2 }'
        self.assertRewrite(old, 'ResponseMeta.Unstamped(true, ("b", 2), ("a", 1))', False)

    def test_moved_timestamp(self):
        self.assertRewrite(self.BASIC, self.BUILT.replace('DateTime.Now, ', ''), False)
        self.assertRewrite(self.BASIC, 'var stamp = DateTime.Now; ' + self.BUILT.replace('DateTime.Now', 'stamp'), False)
        self.assertRewrite(self.BASIC, self.BUILT.replace('DateTime.Now', 'ResponseClock.Now'), False)

    def test_dropped_key_and_changed_value_type(self):
        self.assertRewrite(self.BASIC, 'ResponseMeta.Basic(DateTime.Now, true)', False)
        for value in ('0L', '"0"', '(long)0'):
            self.assertRewrite(self.BASIC, self.BUILT.replace('?? 0', '?? ' + value), False)

    def test_extra_statement_in_same_hunk(self):
        self.assertRewrite(self.BASIC, self.BUILT + '; DoNativeWork()', False)
        self.assertRewrite(self.BASIC, 'DoNativeWork(); ' + self.BUILT, False)

    def test_collection_and_stringification_rejected(self):
        for replacement in ('ResponseMeta.Basic(DateTime.Now, true, values)',
                            self.BUILT.replace('res.Members?.Count() ?? 0', 'res.ToString()'),
                            self.BUILT.replace('res.Members?.Count() ?? 0', 'res.Members')):
            self.assertRewrite(self.BASIC, replacement, False)

    def test_annotations_are_standalone_comments(self):
        self.assertRewrite(self.BASIC, '// envelope: legacy-single-verdict\n' + self.BUILT)
        self.assertRewrite('Same();\n', '// envelope: legacy-single-verdict\nSame();\n')
        self.assertRewrite('Same();\n', '// envelope: legacy-single-verdict\nChanged();\n', False)
        self.assertRewrite(self.BASIC, '// envelope: legacy-x extra text\n' + self.BUILT, False)
        self.assertRewrite('var s = """\n// envelope: legacy-x\n""";', 'var s = """\n""";', False)

    def test_comments_not_silently_removed(self):
        self.assertRewrite(self.BASIC.replace('DateTime.Now', '/* clock */ DateTime.Now'), self.BUILT, False)
        self.assertRewrite(self.BASIC, self.BUILT.replace('DateTime.Now', '/* clock */ DateTime.Now'), False)

    def test_duplicate_keys_and_unknown_shapes(self):
        for old in ('new JsonObject { ["timestamp"] = DateTime.Now }',
                    'new JsonObject { ["success"] = true, ["timestamp"] = DateTime.Now }',
                    'new JsonObject { ["success"] = false, ["success"] = true }'):
            self.assertRewrite(old, self.BUILT, False)

    def test_node_adoption_cannot_move_past_an_observer(self):
        old = 'new JsonObject { ["success"] = true, ["child"] = child, ["owned"] = child.Parent != null }'
        self.assertRewrite(old, 'ResponseMeta.Unstamped(true, ("child", child), ("owned", child.Parent != null))', False)

    def test_all_explicit_clocks_and_empty_tail(self):
        for clock in sorted(CLOCKS):
            self.assertRewrite('new JsonObject() { ["timestamp"] = ' + clock + ', ["success"] = ok, }',
                               'ResponseMeta.Basic(' + clock + ', ok)')

    def test_several_rewrites_and_unchanged_candidate(self):
        self.assertRewrite(self.BASIC + ';\n' + self.BASIC + ';\n' + self.BASIC,
                           self.BUILT + ';\n' + self.BASIC + ';\n' + self.BUILT)

    def test_interpolation_change_and_unrelated_edit(self):
        old = 'new JsonObject { ["success"] = true, ["value"] = $"{Count()}" }'
        self.assertRewrite(old, 'ResponseMeta.Unstamped(true, ("value", $"{Other()}"))', False)
        self.assertRewrite(self.BASIC + '; return value', self.BUILT + '; return other', False)

    def test_error_location_counts_annotation_lines(self):
        old = 'Same();\n' + self.BASIC
        new = '// envelope: legacy-x\nSame();\n' + self.BUILT + '; Bad();'
        _, offset, nearest = compare(old, new)
        self.assertEqual(3, new.count('\n', 0, offset) + 1)
        self.assertEqual('Basic/explicit-clock', nearest)

    def test_unchanged_is_byte_exact(self):
        self.assertRewrite('Same(); // comment', 'Same(); // changed', False)
        self.assertRewrite('Same();', ' Same();', False)
        self.assertRewrite(self.BASIC, self.BASIC)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', help='Git commit before the call-site rewrite')
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('paths', nargs='*', help='Git pathspecs for call sites; default: every changed C# file')
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(SelfTests))
        return int(not result.wasSuccessful())
    if not args.base:
        parser.error('--base or --self-test is required')
    try:
        return check(args.base, args.paths)
    except (OSError, ValueError) as error:
        print('FAIL ' + str(error))
        return 1


if __name__ == '__main__':
    sys.exit(main())
