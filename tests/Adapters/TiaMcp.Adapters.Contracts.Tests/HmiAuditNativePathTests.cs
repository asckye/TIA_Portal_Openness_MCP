using TiaMcp.Tests.Shared;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class HmiAuditNativePathTests
{
    [Fact]
    public void Classic_listings_use_decoded_folder_prefixes_for_scripts_and_library_types()
    {
        string source = Read("src/Adapters/Native/Hmi/MotionProDiagClassicHmiAdapter.cs");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(source,
            System.Text.RegularExpressions.Regex.Escape("Walk(root, parts.Length == 0 ? \"\" : string.Join(\"/\", parts) + \"/\", 0);")).Count);
        Assert.Contains("ClassicScriptFolder(hmi, parts)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("folderPath.TrimEnd('/')", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Graphics_use_the_project_on_both_sides_of_the_V21_API_move()
    {
        var source = Read("src/Adapters/Native/Hmi/HmiSession.cs");
        var method = source[source.IndexOf("ClassicGraphics()", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("internal object ExactOpenEngineeringLibrary", StringComparison.Ordinal)];
        Assert.Contains("var project = session.CurrentProject", method);
        Assert.Contains("#if HMI_GRAPHICS_PROVIDER", method);
        Assert.Contains("project.GetService<Siemens.Engineering.Hmi.Globalization.GraphicsProvider>()", method);
        Assert.Contains("#else\n            return project.Graphics;", method);
        Assert.DoesNotContain("hmi.GetService", method);
        var graphics = Read("src/Adapters/Native/Hmi/ClassicHmiFoldersAdapter.cs");
        graphics = graphics[graphics.IndexOf("public HmiMessage ManageClassicHmiGraphic", StringComparison.Ordinal)..];
        Assert.Contains("_session.ClassicGraphics()", graphics);
        Assert.DoesNotContain("_session.ExactClassicHmi", graphics);
    }

    [Fact]
    public void Failed_connection_setup_returns_before_the_shortcut_can_create_tags()
    {
        var source = Read("src/Adapters/Native/Hmi/UnifiedHmiAdapter.cs");
        int ensure = source.IndexOf("var connDesc = EnsureUnifiedHmiConnection", StringComparison.Ordinal);
        int tags = source.IndexOf("// Ensure tags in HMI tag table", ensure, StringComparison.Ordinal);
        var connectionStep = source[ensure..tags];
        Assert.Contains("catch (Exception ex)", connectionStep);
        Assert.Contains("return new HmiMessage", connectionStep);
        Assert.DoesNotContain("Tags.Create", connectionStep);
        var connectionMethod = source[source.IndexOf("public HmiDescribe EnsureUnifiedHmiConnection", StringComparison.Ordinal)..];
        connectionMethod = connectionMethod[..connectionMethod.IndexOf("public HmiMessage EnsureUnifiedHmiScreenItem", StringComparison.Ordinal)];
        Assert.Contains("HmiExchangePolicy.RequireConnection", connectionMethod);
        Assert.DoesNotContain("InvokeCreate(", connectionMethod);
        Assert.DoesNotContain(".SetValue(", connectionMethod);
        Assert.DoesNotContain("TrySetProperty(", connectionMethod);
    }

    private static string Read(string relative)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "CLAUDE.md")))
            root = root.Parent ?? throw new InvalidOperationException("Repository root not found.");
        return SourceFamily.Read(Path.Combine(root.FullName, relative)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
