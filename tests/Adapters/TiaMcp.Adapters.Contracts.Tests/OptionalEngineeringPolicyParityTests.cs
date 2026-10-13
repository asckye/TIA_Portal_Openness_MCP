using System.Text.RegularExpressions;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class OptionalEngineeringPolicyParityTests
{
    [Theory]
    [InlineData("Startdrive")]
    [InlineData("Sivarc")]
    [InlineData("Dcc")]
    public void Host_and_worker_validation_have_identical_control_flow_and_constants(string family)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "CLAUDE.md"))) root = root.Parent;
        Assert.NotNull(root);
        string host = File.ReadAllText(Path.Combine(root.FullName, "src", "Logic", "Siemens", family + "Logic.cs"));
        string worker = File.ReadAllText(Path.Combine(root.FullName, "src", "Adapters", "Native", "Optional", family + "Policy.cs"));
        host = host.Replace("JsonNode.Parse", "OptionalInputs.Parse", StringComparison.Ordinal)
            .Replace("JsonValue.Create", "HmiValues.Value", StringComparison.Ordinal)
            .Replace("JsonNode", "object", StringComparison.Ordinal).Replace("JsonObject", "HmiObject", StringComparison.Ordinal)
            .Replace("JsonArray", "HmiArray", StringComparison.Ordinal).Replace("JsonValue", "object", StringComparison.Ordinal)
            .Replace(".GetValue<", ".OptionalValue<", StringComparison.Ordinal).Replace(".TryGetValue<", ".TryOptionalValue<", StringComparison.Ordinal)
            .Replace(".AsArray()", ".Array()", StringComparison.Ordinal);
        Assert.Equal(Tokens(Body(host, family + "Logic")), Tokens(Body(worker, family + "Policy")));
    }

    private static string Body(string source, string name)
        => source[source.IndexOf('{', source.IndexOf("class " + name, StringComparison.Ordinal))..];

    private static string[] Tokens(string source)
        => Regex.Matches(source, """@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|//[^\r\n]*|/\*[\s\S]*?\*/|\s+|\w+|\S""")
            .Select(token => token.Value).Where(token => !string.IsNullOrWhiteSpace(token)
                && !token.StartsWith("//", StringComparison.Ordinal) && !token.StartsWith("/*", StringComparison.Ordinal)).ToArray();
}
