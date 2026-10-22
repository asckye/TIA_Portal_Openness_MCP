using System.Security.Cryptography;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class WorkbenchFontDeliveryTests
{
    [Theory]
    [InlineData("MapleMono-CN-Regular.ttf", "e6338c3dca9a3bed3fb81f43c8bb8feca3b6ad54829f5a3229527d030a321dbd")]
    [InlineData("MapleMono-CN-SemiBold.ttf", "320c87dc4134f0bc88ad878255af1f1957c9e7898e76638efc9a3cdf3868574e")]
    [InlineData("LICENSE.txt", "eb2d28d2e565a0757e3d64e34ebb452e75a0cad87c0ab3faf4e08ba7596de902")]
    public void Maple_font_payload_is_required_by_source_and_delivery_gates_with_original_bytes(string name, string hash)
    {
        string root = Repository.FindRoot();
        string source = "src/Studio/Gui/Fonts/" + name;
        var policy = SourceRoots.Load(root);
        foreach (string consumer in new[] { "repository", "bundle", "package" }) Assert.Contains(source, policy.RequiredPaths(consumer));
        var rules = DeliveryRules.Load(root);
        string runtime = "runtime/studio/Fonts/" + name;
        Assert.True(rules.Delivered(runtime));
        Assert.Contains(runtime, DeliveryRules.Strings(rules.Data["requiredFiles"]));
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, source)))));
        var provenance = File.ReadAllText(Path.Combine(root, "src/Studio/Gui/Fonts/SOURCES.txt"));
        Assert.Contains(hash + "  " + name, provenance);
        Assert.Contains("cb1e79b2c23dff772ae351784ef2b84454a61b3920e9b20bd5db4bf207e4472d", provenance);
    }

    [Fact]
    public void Font_files_are_output_and_publish_content_and_never_wpf_resources()
    {
        string root = Repository.FindRoot();
        var project = XDocument.Load(Path.Combine(root, "src/Studio/Gui/TiaOpenness.Gui.csproj"));
        var fonts = project.Descendants("Content").Single(item => ((string?)item.Attribute("Include"))?.Contains("Fonts/MapleMono-CN-Regular.ttf", StringComparison.Ordinal) == true);
        Assert.Equal(new[] { "Fonts/MapleMono-CN-Regular.ttf", "Fonts/MapleMono-CN-SemiBold.ttf", "Fonts/LICENSE.txt" }, ((string)fonts.Attribute("Include")!).Split(';'));
        Assert.Equal("PreserveNewest", (string?)fonts.Attribute("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)fonts.Attribute("CopyToPublishDirectory"));
        Assert.Null(fonts.Parent!.Attribute("Condition"));
        Assert.DoesNotContain(project.Descendants("Resource"), item => ((string?)item.Attribute("Include"))?.Contains("Fonts", StringComparison.Ordinal) == true);
        Assert.Equal(new[] { "MapleMono-CN-Regular.ttf", "MapleMono-CN-SemiBold.ttf" }, Directory.EnumerateFiles(Path.Combine(root, "src/Studio/Gui/Fonts"), "*.ttf").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(File.ReadAllBytes(Path.Combine(root, "src/Studio/Gui/Fonts/LICENSE.txt")), File.ReadAllBytes(Path.Combine(root, "docs/licenses/MapleMono-CN-OFL.txt")));
        Assert.True(DeliveryRules.Load(root).Delivered("docs/licenses/MapleMono-CN-OFL.txt"));
        foreach (string name in new[] { "JetBrainsMono-Regular.ttf", "JetBrainsMono-Medium.ttf", "JetBrainsMono-OFL.txt", "NotoSansSC-Regular.otf", "NotoSansSC-Bold.otf", "NotoSansSC-OFL.txt" })
            Assert.False(DeliveryRules.Load(root).Delivered("runtime/studio/Fonts/" + name));
    }
}
