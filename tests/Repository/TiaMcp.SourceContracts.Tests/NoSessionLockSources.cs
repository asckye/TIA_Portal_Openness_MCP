using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class NoSessionLockSources
{
    [Fact]
    public void Session_reset_required_is_never_produced()
    {
        string root = Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), "src");
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        {
            if (Path.GetFileName(file) == "V4Json.cs") continue;
            string source = Repository.ReadSource(file);
            Assert.DoesNotContain("new SessionResetRequiredDetails", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HostBehavior.SessionReset(", source, StringComparison.Ordinal);
        }
    }
}
