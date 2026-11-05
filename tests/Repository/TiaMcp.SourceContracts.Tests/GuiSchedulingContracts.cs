using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class GuiSchedulingContracts
{
    [Fact]
    public void EveryGuiInputMethodUsesTheSerialReservedLane()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        string directory = Path.Combine(root, "tests/Studio/TiaOpenness.Gui.Tests");
        var declaration = new Regex(@"^    (?<access>public|private|internal) (?:static )?(?:async )?[^\n=;]+? (?<name>\w+)\([^\n]*", RegexOptions.Multiline);
        var input = new Regex(@"PressSpace\(|\.Focus\(|Keyboard\.Focus|IsKeyboardFocused|Mouse\.");
        var checkedMethods = new List<string>();
        foreach (string path in Directory.EnumerateFiles(directory, "*Tests.cs"))
        {
            string source = File.ReadAllText(path);
            var declarations = declaration.Matches(source).Cast<Match>().ToArray();
            var methods = declarations.Select((match, index) => new
            {
                Name = match.Groups["name"].Value,
                Public = match.Groups["access"].Value == "public",
                Body = source[match.Index..(index + 1 < declarations.Length ? declarations[index + 1].Index : source.Length)]
            }).ToArray();
            var sensitive = methods.Where(method => input.IsMatch(method.Body))
                .Select(method => method.Name).ToHashSet(StringComparer.Ordinal);
            // Include test entry points which reach mouse APIs through a local helper.
            for (int pass = 0; pass < methods.Length; pass++)
                sensitive.UnionWith(methods.Where(method => sensitive.Any(callee =>
                    Regex.IsMatch(method.Body, @"\b" + Regex.Escape(callee) + @"\s*\("))).Select(method => method.Name).ToArray());
            foreach (var method in methods.Where(method => method.Public && sensitive.Contains(method.Name)))
            {
                string cls = "TiaOpenness.Gui.Tests." + Path.GetFileNameWithoutExtension(path);
                checkedMethods.Add(cls + "." + method.Name);
                Assert.True(TimeSensitiveTests.Contains(cls, method.Name), cls + "." + method.Name);
                string key = "T:" + cls + "." + method.Name;
                Assert.Equal(0, TimeSensitiveTests.SensitivePartition(key));
                Assert.Equal(3, TestShard.Assign(cls, key, key, 4,
                    new Dictionary<string, int>(StringComparer.Ordinal), reserveSensitive: true));
            }
        }
        Assert.NotEmpty(checkedMethods);
        Assert.Contains(checkedMethods, name => name.Contains(".WpfContextTests.", StringComparison.Ordinal));
        Assert.Contains("TiaOpenness.Gui.Tests.WorkbenchControlTests.Discrete_Wpf_input_marks_human_activity_for_1500ms", checkedMethods);
    }
}
