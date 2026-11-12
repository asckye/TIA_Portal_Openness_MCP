using System.Text.RegularExpressions;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class SimpleNameLoadSources
{
    [Fact]
    public void Native_assembly_loads_use_assembly_identities()
    {
        string root = TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string[] files = Directory.GetFiles(Path.Combine(root, "src/Adapters/Native"), "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            Assert.False(Regex.IsMatch(File.ReadAllText(file),
                @"\bAssembly\s*\.\s*Load\s*\(\s*(?:@?""|assembly(?:Name)?\s*\))"), file);
        }
    }
}
