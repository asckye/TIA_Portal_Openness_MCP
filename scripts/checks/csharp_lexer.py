"""C# lexical source primitives shared by the remaining Python source checks."""
from dataclasses import dataclass
import json
import re

SKIP_DIRS = {'bin', 'obj', 'bin-v20', 'obj-v20', 'generated', '__pycache__'}
GENERATED_SUFFIXES = ('.g.cs', '.g.i.cs', '.generated.cs', '.designer.cs')
IDENTIFIER = re.compile(r'@?[^\W\d]\w*', re.UNICODE)
NUMBER = re.compile(r'(?:0[xX][\da-fA-F_]+|0[bB][01_]+|(?:\d[\d_]*(?:\.\d[\d_]*)?|'
                    r'\.\d[\d_]*)(?:[eE][+-]?[\d_]+)?)[uUlLfFdDmM]*')
SPACE = re.compile(r'\s+')
OPERATORS = re.compile(r'>>>=|>>>|<<=|>>=|\?\?=|=>|\?\.|\?\?|::|\+\+|--|&&|\|\||'
                       r'==|!=|<=|>=|<<|>>|\+=|-=|\*=|/=|%=|&=|\|=|\^=|\.\.')


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


