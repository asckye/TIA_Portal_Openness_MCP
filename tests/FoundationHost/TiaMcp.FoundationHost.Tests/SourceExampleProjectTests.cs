using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class SourceExampleProjectTests
{
    [Fact]
    public void Source_files_hold_no_test_run_project_name()
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string source = Path.Combine(root, "src");
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(source, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj")).ToArray();
        Assert.NotEmpty(files);
        var offenders = files.Where(file => File.ReadAllText(file).Contains("AutomaticDipCoatingMachine", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file));
        Assert.Empty(offenders);
    }
}
