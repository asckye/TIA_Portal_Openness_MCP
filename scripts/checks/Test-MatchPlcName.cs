#!/usr/bin/env dotnet
// Verify Guard.MatchPlcName against the built engine, or compile just that production member with -SourceOnly.
// Usage: dotnet run scripts/checks/Test-MatchPlcName.cs -- -SourceOnly
#:property PublishAot=false
#:property NuGetAudit=false

using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;

var options = args.ToList();
var sourceOnly = RemoveFlag(options, "-SourceOnly", "--source-only");
var exe = Take(options, "-Exe", "--exe");
var dotnet = Take(options, "-Dotnet", "--dotnet") ?? "dotnet";
if (options.Count != 0) throw new ArgumentException("Unexpected argument: " + options[0]);
var root = FindRoot(Directory.GetCurrentDirectory());
if (!sourceOnly)
{
    if (string.IsNullOrWhiteSpace(exe)) exe = Path.Combine(root, "runtime", "v21", "worker", "TiaMcp.Engine.V21.exe");
    var harness = Path.Combine(root, "tests", "Engine", "TiaMcpServer.HttpTests", "TiaMcpServer.HttpTests.csproj");
    return await Run(dotnet, new[] { "run", "--project", harness, "-c", "Release", "--no-restore", "--", Path.GetFullPath(exe), "test-match-plc-name" }, root);
}

var sourceFile = Path.Combine(root, "src", "Logic", "Siemens", "Guard.cs");
var sourceText = await File.ReadAllTextAsync(sourceFile);
var methodSource = ExtractMember(sourceText, "MatchPlcName");
var fixtureText = "using System; using System.Collections.Generic; using System.Linq; namespace TiaMcpServer.Siemens { internal static class Guard {\n" + methodSource + "\n} }";
var temporary = NewTempDirectory("tia-match-plc-name-");
try
{
    var assemblyPath = Path.Combine(temporary, "Fixture.dll");
    await CompileFixture(fixtureText, assemblyPath, Array.Empty<string>(), dotnet);
    var assembly = Assembly.Load(await File.ReadAllBytesAsync(assemblyPath));
    var type = assembly.GetType("TiaMcpServer.Siemens.Guard", true)!;
    var match = type.GetMethod("MatchPlcName", BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException("MatchPlcName");
    var passed = 0;
    var failed = 0;
    void Check(string description, string[] available, string token, string? expected)
    {
        var actual = match.Invoke(null, new object[] { available, token }) as string;
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            passed++;
            Console.WriteLine($"PASS {description,-22} '{token}' -> {(actual ?? "<null>")}");
        }
        else
        {
            failed++;
            Console.WriteLine($"FAIL {description,-22} '{token}' -> got '{actual}' expected '{expected}'");
        }
    }

    Check("single exact", new[] { "PLC_1" }, "PLC_1", "PLC_1");
    Check("single case", new[] { "PLC_1" }, "plc_1", "PLC_1");
    Check("single trim", new[] { "PLC_1" }, " PLC_1 ", "PLC_1");
    Check("single wrong name", new[] { "PLC_1" }, "garbage", null);
    Check("single substr", new[] { "PLC_1" }, "plc", null);
    Check("single omitted", new[] { "PLC_1" }, "", "PLC_1");
    var multi = new[] { "MainPLC", "SafetyPLC", "PLC_1" };
    Check("multi exact", multi, "SafetyPLC", "SafetyPLC");
    Check("multi case", multi, "safetyplc", "SafetyPLC");
    Check("multi trim", multi, " SafetyPLC ", "SafetyPLC");
    Check("multi substr", multi, "Safety", null);
    Check("multi ambiguous", multi, "PLC", null);
    Check("multi not-found", multi, "NoSuch", null);
    Check("multi empty", multi, "", null);
    var two = new[] { "PLC_1", "PLC_2" };
    Check("two exact", two, "PLC_2", "PLC_2");
    Check("two digit", two, "1", null);
    Check("two ambiguous", two, "PLC", null);
    Check("empty list", Array.Empty<string>(), "PLC_1", null);
    Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
    return failed == 0 ? 0 : 1;
}
finally { Directory.Delete(temporary, true); }

static string ExtractMember(string source, string name)
{
    var matches = Regex.Matches(source, @"\b" + Regex.Escape(name) + @"\s*\(");
    foreach (Match match in matches)
    {
        var openParen = source.IndexOf('(', match.Index);
        var closeParen = FindMatching(source, openParen, '(', ')');
        var body = FindMethodBody(source, closeParen + 1);
        if (body < 0) continue;
        var closeBrace = FindMatching(source, body, '{', '}');
        var declarationStart = source.LastIndexOf('\n', match.Index);
        declarationStart = declarationStart < 0 ? 0 : declarationStart + 1;
        return source[declarationStart..(closeBrace + 1)];
    }
    throw new InvalidDataException("Production member not found: " + name);
}

static int FindMethodBody(string source, int start)
{
    for (var i = start; i < source.Length; i++)
    {
        if (SkipTrivia(source, ref i)) continue;
        if (source[i] == '{') return i;
        if (source[i] == ';') return -1;
        if (source[i] == '=' && i + 1 < source.Length && source[i + 1] == '>')
        {
            while (i < source.Length && source[i] != ';') i++;
            return -1;
        }
    }
    return -1;
}

static int FindMatching(string source, int start, char open, char close)
{
    var depth = 0;
    for (var i = start; i < source.Length; i++)
    {
        if (SkipTrivia(source, ref i)) continue;
        if (source[i] == open) depth++;
        else if (source[i] == close && --depth == 0) return i;
    }
    throw new InvalidDataException("Unbalanced production source around " + start);
}

static bool SkipTrivia(string source, ref int index)
{
    if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
    {
        while (index < source.Length && source[index] != '\n') index++;
        return true;
    }
    if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
    {
        index += 2;
        while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/')) index++;
        index++;
        return true;
    }
    var quote = source[index] == '"' || source[index] == '\'' ? source[index] : '\0';
    var verbatim = quote == '"' && index > 0 && source[index - 1] == '@';
    if (quote == '\0') return false;
    for (index++; index < source.Length; index++)
    {
        if (!verbatim && source[index] == '\\') { index++; continue; }
        if (source[index] == quote)
        {
            if (verbatim && index + 1 < source.Length && source[index + 1] == quote) { index++; continue; }
            return true;
        }
    }
    return true;
}

static async Task CompileFixture(string source, string outputPath, IReadOnlyList<string> extraReferences, string dotnet)
{
    var sdkText = await Capture(dotnet, new[] { "--list-sdks" });
    var sdkLine = sdkText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("10.", StringComparison.Ordinal)).LastOrDefault()
        ?? throw new InvalidOperationException(".NET 10 SDK is required.");
    var bracket = sdkLine.IndexOf('[');
    var version = sdkLine[..sdkLine.IndexOf(' ')];
    var sdkRoot = sdkLine[(bracket + 1)..^1];
    var csc = Path.Combine(sdkRoot, version, "Roslyn", "bincore", "csc.dll");
    var packRoot = Path.Combine(sdkRoot, "..", "packs", "Microsoft.NETCore.App.Ref");
    var referenceDirectory = Directory.GetDirectories(Path.GetFullPath(packRoot)).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Select(path => Path.Combine(path, "ref", "net10.0")).First(Directory.Exists);
    var sourcePath = Path.ChangeExtension(outputPath, ".cs");
    await File.WriteAllTextAsync(sourcePath, source, new UTF8Encoding(false));
    var responsePath = Path.Combine(Path.GetDirectoryName(outputPath)!, "compile.rsp");
    var compilerArgs = new List<string> { "/nologo", "/target:library", "/langversion:latest", "/nullable:enable", "/nostdlib+", "/out:" + Quote(outputPath), Quote(sourcePath) };
    compilerArgs.AddRange(Directory.GetFiles(referenceDirectory, "*.dll").Select(file => "/reference:" + Quote(file)));
    compilerArgs.AddRange(extraReferences.Select(file => "/reference:" + Quote(file)));
    await File.WriteAllLinesAsync(responsePath, compilerArgs, new UTF8Encoding(false));
    var result = await Run(dotnet, new[] { csc, "@" + responsePath }, Path.GetDirectoryName(outputPath)!);
    if (result != 0) throw new InvalidOperationException("Production source fixture compilation failed: " + result);
}

static async Task<int> Run(string program, IReadOnlyList<string> arguments, string? workingDirectory = null)
{
    var start = new System.Diagnostics.ProcessStartInfo(program) { WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(), UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start " + program);
    await process.WaitForExitAsync();
    return process.ExitCode;
}

static async Task<string> Capture(string program, IReadOnlyList<string> arguments)
{
    var start = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return await output + await error;
}

static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
static string NewTempDirectory(string prefix)
{
    var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var path = Path.GetFullPath(Path.Combine(root, prefix + Guid.NewGuid().ToString("N")));
    if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Test directory escaped TEMP.");
    Directory.CreateDirectory(path);
    return path;
}
static bool RemoveFlag(List<string> values, params string[] names)
{
    var found = false;
    foreach (var name in names) found |= values.RemoveAll(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase)) > 0;
    return found;
}
static string? Take(List<string> values, params string[] names)
{
    foreach (var name in names)
    {
        var index = values.FindIndex(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) continue;
        if (index + 1 >= values.Count) throw new ArgumentException("Missing value for " + name);
        var result = values[index + 1];
        values.RemoveAt(index + 1);
        values.RemoveAt(index);
        return result;
    }
    return null;
}
static string FindRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "TiaPortalOpenness.Offline.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("Repository root not found.");
}
