using System.Xml.Linq;
using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class WorkbenchFontDeliveryTests
{
    [Theory]
    [InlineData("MapleMono-CN-Regular.ttf")]
    [InlineData("MapleMono-CN-SemiBold.ttf")]
    [InlineData("LICENSE.txt")]
    public void Private_font_payload_is_neither_required_nor_delivered(string name)
    {
        string root = Repository.FindRoot();
        string source = "src/Studio/Gui/Fonts/" + name;
        Assert.False(File.Exists(Path.Combine(root, source)));
        var rules = DeliveryRules.Load(root);
        string runtime = "runtime/studio/Fonts/" + name;
        Assert.False(rules.Delivered(runtime));
        Assert.DoesNotContain(runtime, DeliveryRules.Strings(rules.Data["requiredFiles"]));
        Assert.Contains("runtime/studio/Fonts/", DeliveryRules.Strings(rules.Data["exclude"]!["prefixes"]));
    }

    [Fact]
    public void Project_and_delivery_have_no_private_font_payload_or_licence()
    {
        string root = Repository.FindRoot();
        var project = XDocument.Load(Path.Combine(root, "src/Studio/Gui/TiaOpenness.Gui.csproj"));
        foreach (string item in new[] { "Content", "Resource", "EmbeddedResource" })
            Assert.DoesNotContain(project.Descendants(item), element => ((string?)element.Attribute("Include"))?.Contains("Fonts/", StringComparison.Ordinal) == true);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "src/Studio/Gui"), "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf"));
        Assert.False(File.Exists(Path.Combine(root, "docs/licenses/MapleMono-CN-OFL.txt")));
        var rules = DeliveryRules.Load(root);
        Assert.False(rules.Delivered("docs/licenses/MapleMono-CN-OFL.txt"));
        Assert.DoesNotContain("docs/licenses/MapleMono-CN-OFL.txt", DeliveryRules.Strings(rules.Data["include"]!["files"]));
        Assert.Empty(DeliveryRules.Strings(rules.Data["requiredFiles"]).Where(path => path.StartsWith("runtime/studio/Fonts/", StringComparison.Ordinal)));
        foreach (string name in new[] { "JetBrainsMono-Regular.ttf", "JetBrainsMono-Medium.ttf", "JetBrainsMono-OFL.txt", "NotoSansSC-Regular.otf", "NotoSansSC-Bold.otf", "NotoSansSC-OFL.txt" })
            Assert.False(rules.Delivered("runtime/studio/Fonts/" + name));
    }
}
