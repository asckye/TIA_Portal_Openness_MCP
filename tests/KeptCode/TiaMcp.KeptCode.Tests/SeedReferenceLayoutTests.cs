using System;
using System.IO;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class SeedReferenceLayoutTests
{
    [Fact]
    public void Manifest_block_directory_must_resolve_inside_the_reference_directory()
    {
        string repo = Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string fixture = Path.Combine(repo, "bin-build", "seed-layout-" + Guid.NewGuid().ToString("N"));
        string root = Directory.CreateDirectory(Path.Combine(fixture, "reference")).FullName;
        const string filename = "MCPVerify_FC_LAD.xml";
        try
        {
            string manifest = Path.Combine(root, "manifest.json");
            string donors = Directory.CreateDirectory(Path.Combine(fixture, "donors")).FullName;
            File.Copy(Path.Combine(repo, "templates", "mcp-full-e2e-verify", "plc", "blocks", filename), Path.Combine(donors, filename));
            File.WriteAllText(manifest, """{"plcBlocksDir":"../donors"}""");
            var refusal = Assert.Throws<AdapterPreconditionException>(() => SeedReferenceLayout.Read(root, "21", null));
            Assert.Equal(SeedReferenceLayout.Condition, refusal.Condition);
            Assert.Equal("referenceDir", refusal.ParamName);

            Directory.Move(donors, Path.Combine(root, "donors"));
            File.WriteAllText(manifest, """{"plcBlocksDir":"donors"}""");
            var plan = SeedReferenceLayout.Read(root + Path.DirectorySeparatorChar, "21", null);
            Assert.Collection(plan.PlannedFiles,
                file => Assert.Equal("manifest.json", (string?)file!["name"]),
                file =>
                {
                    Assert.Equal("donors/" + filename, (string?)file!["name"]);
                    Assert.Equal("plc/blocks/" + filename, (string?)file!["destination"]);
                });
            File.AppendAllText(Path.Combine(root, "donors", filename), "\n");
            var changed = Assert.Throws<AdapterPreconditionException>(() => plan.Stage(Path.Combine(fixture, "stage"), new JsonObject()));
            Assert.Equal("seed-input-changed", changed.Condition);
        }
        finally { Directory.Delete(fixture, true); }
    }
}
