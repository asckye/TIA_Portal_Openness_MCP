using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

// Only the upstream project's dependency string arrays are consumed. Reject
// unfamiliar array syntax instead of silently omitting a runtime dependency.
internal static class ProjectDependencies
{
    internal static string[] Read(string source)
    {
        var dependencies = new List<string>();
        var table = "";
        var index = 0;
        while (index < source.Length)
        {
            var end = source.IndexOf('\n', index);
            if (end < 0) end = source.Length;
            var line = source[index..end].Trim();
            if (line.StartsWith('[')) table = line.Split('#', 2)[0].Trim();
            var match = Regex.Match(line, @"\A(dependencies|opcua|web)\s*=\s*");
            if (match.Success && (table == "[project]" && match.Groups[1].Value == "dependencies" || table == "[project.optional-dependencies]" && match.Groups[1].Value is "opcua" or "web"))
            {
                var opening = index + source[index..end].IndexOf('=') + 1;
                var result = ReadArray(source, opening);
                dependencies.AddRange(result.Rows);
                index = result.End;
            }
            else index = end;
            if (index < source.Length && source[index] == '\n') index++;
        }
        return dependencies.Where(d => !d.StartsWith("plc-", StringComparison.Ordinal)).ToArray();
    }
    private static (List<string> Rows, int End) ReadArray(string source, int cursor)
    {
        var rows = new List<string>();
        void Space()
        {
            while (cursor < source.Length)
            {
                if (char.IsWhiteSpace(source[cursor])) cursor++;
                else if (source[cursor] == '#') { while (cursor < source.Length && source[cursor] != '\n') cursor++; }
                else break;
            }
        }
        Space();
        if (cursor >= source.Length || source[cursor++] != '[') throw new ReleaseException("Expected TOML dependency string array");
        Space();
        while (cursor < source.Length && source[cursor] != ']')
        {
            var quote = source[cursor++];
            if (quote is not ('\'' or '"') || cursor + 1 < source.Length && source[cursor] == quote && source[cursor + 1] == quote) throw new ReleaseException("Unsupported TOML dependency string syntax");
            var text = new StringBuilder();
            while (cursor < source.Length && source[cursor] != quote)
            {
                var c = source[cursor++];
                if (c is '\r' or '\n') throw new ReleaseException("Unterminated TOML dependency string");
                if (c == '\\' && quote == '"')
                {
                    if (cursor >= source.Length) throw new ReleaseException("Unterminated TOML escape");
                    var escape = source[cursor++];
                    if (escape is 'u' or 'U')
                    {
                        var width = escape == 'u' ? 4 : 8;
                        if (cursor + width > source.Length || !Regex.IsMatch(source.Substring(cursor, width), "\\A[0-9a-fA-F]+\\z")) throw new ReleaseException("Invalid TOML Unicode escape");
                        text.Append(char.ConvertFromUtf32(Convert.ToInt32(source.Substring(cursor, width), 16))); cursor += width;
                    }
                    else text.Append(escape switch { 'b' => '\b', 't' => '\t', 'n' => '\n', 'f' => '\f', 'r' => '\r', '"' => '"', '\\' => '\\', _ => throw new ReleaseException("Invalid TOML escape") });
                }
                else text.Append(c);
            }
            if (cursor >= source.Length) throw new ReleaseException("Unterminated TOML dependency string");
            cursor++; rows.Add(text.ToString()); Space();
            if (cursor < source.Length && source[cursor] == ',') { cursor++; Space(); }
            else if (cursor >= source.Length || source[cursor] != ']') throw new ReleaseException("Expected comma in TOML dependency array");
        }
        if (cursor >= source.Length) throw new ReleaseException("Unterminated TOML dependency array");
        return (rows, ++cursor);
    }
}
