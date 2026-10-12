using Xunit;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class DocumentationLinksTests
{
    [Fact]
    public void AnchorsRespectUnicodeDuplicateHeadingsExplicitIdsAndFences()
    {
        var directory = Path.Combine(Repository.FindRoot(), "bin-build/link-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fixture.md");
        try
        {
            File.WriteAllText(path, "# 中文与 `API`\n## Same\n## Same\n<a id=\"retained\"></a>\n```text\n## Not a heading\n```\n");
            foreach (var anchor in new[] { "中文与-api", "Same".ToLowerInvariant(), "same-1", "retained", "%E4%B8%AD%E6%96%87%E4%B8%8E-api" })
                Assert.Null(DocumentationLinks.AnchorError(path, "#" + anchor));
            Assert.NotNull(DocumentationLinks.AnchorError(path, "#not-a-heading"));
            Assert.NotNull(DocumentationLinks.AnchorError(path, "#same-2"));
            Assert.Null(DocumentationLinks.AnchorError(path, "https://example.invalid/#remote"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
