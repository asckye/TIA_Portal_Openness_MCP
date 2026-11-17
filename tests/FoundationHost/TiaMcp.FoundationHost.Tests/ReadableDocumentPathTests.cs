using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters;
using TiaMcp.FoundationHost;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class ReadableDocumentPathTests
{
    private const string Group = "组 一/二 组";
    private const string Block = "中文块 A";

    private sealed class PreviewWorker(Func<string, JsonObject, JsonNode> reply) : IFoundationWorker
    {
        internal int Calls;
        internal JsonNode? Payload;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++;
            Payload = reply(operation, arguments);
            return Task.FromResult<JsonNode?>(Payload.DeepClone());
        }
        public void Dispose() { }
    }

    [Theory]
    [InlineData("20", "ExportPlcBlockDocuments")]
    [InlineData("21", "ExportPlcBlockDocuments")]
    [InlineData("20", "ImportPlcBlockDocuments")]
    [InlineData("21", "ImportPlcBlockDocuments")]
    [InlineData("20", "ImportPlcBlocksDocuments")]
    [InlineData("21", "ImportPlcBlocksDocuments")]
    public async Task Saved_document_paths_preview_with_readable_worker_identities(string release, string toolName)
    {
        string repository = TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        byte[] resource = File.ReadAllBytes(Path.Combine(repository, "reference", "tool-examples", "languages", "FB_LadStart.s7res"));
        string root = Path.Combine(repository, "bin-build", "refactor", "round1",
            "document-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int native = 0;
        string Saved(string path) => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        var arguments = new JsonObject { ["softwarePath"] = "devices/PLC", ["dryRun"] = true };
        if (toolName == "ExportPlcBlockDocuments")
        {
            arguments["blockPath"] = Saved(Group + "/" + Block);
            arguments["exportPath"] = Path.Combine(root, "export");
        }
        else
        {
            arguments["groupPath"] = Saved(Group);
            arguments["importPath"] = Path.Combine(root, "input");
            if (toolName == "ImportPlcBlockDocuments") arguments["fileNameWithoutExtension"] = "Demo";
            else arguments["fileNamesWithoutExtension"] = new JsonArray("Demo", "Second");
        }

        PlcDocumentImportContext Context(JsonObject request, string name) => new() {
            Request = new PlcDocumentImportRequest {
                Release = release, Project = Path.Combine(root, "fixture.ap" + release), Software = "devices/PLC",
                Group = (string)request["groupPath"]!, Directory = (string)request["importPath"]!, Name = name,
                ProcessId = 1, TargetIdentity = "same-project-plc-group" },
            Scan = () => new[] { Path.Combine((string)request["importPath"]!, name + ".s7dcl"),
                Path.Combine((string)request["importPath"]!, name + ".s7res") },
            Open = path => new MemoryStream(path.EndsWith(".s7dcl", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes("DATA_BLOCK " + name + " VAR Ready:Bool; END_VAR END_DATA_BLOCK") : resource, false),
            Inventory = () => Array.Empty<string>(), Collision = _ => false, Exists = _ => false,
            Recheck = () => native++, Import = () => { native++; throw new InvalidOperationException("Preview must not import."); }
        };

        using var worker = new PreviewWorker((operation, request) => {
            if (toolName == "ExportPlcBlockDocuments")
            {
                Assert.Equal("ExportAsDocuments", operation);
                var plan = PlcDocumentExportPolicy.Run(release, Path.Combine(root, "fixture.ap" + release), "devices/PLC",
                    (string)request["blockPath"]!, (string)request["exportPath"]!, "LAD", true, true, "", () => native++,
                    (_, _) => { native++; throw new InvalidOperationException("Preview must not export."); });
                Assert.Equal(Group + "/" + Block, plan.BlockPath);
                return JsonSerializer.SerializeToNode(plan)!;
            }
            if (toolName == "ImportPlcBlockDocuments")
            {
                Assert.Equal("ImportFromDocuments", operation);
                var context = Context(request, "Demo");
                var plan = context.Run();
                Assert.Equal(Group, context.Request.Group);
                Assert.Equal(Group, plan.GroupPath);
                return JsonSerializer.SerializeToNode(plan)!;
            }
            Assert.Equal("ImportBlocksFromDocuments", operation);
            var contexts = new[] { Context(request, "Demo"), Context(request, "Second") };
            var batch = PlcBatchDocumentImportPolicy.Run(contexts, true, "", false, "");
            Assert.All(batch.Items, item => Assert.Equal(Group, item.Preview.GroupPath));
            return JsonSerializer.SerializeToNode(batch)!;
        });
        try
        {
            var tool = FoundationTools.Create(worker, release).Single(item => item.ProtocolTool.Name == toolName);
            var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
            var request = new RequestContext<CallToolRequestParams>(server) { Params = new() {
                Name = toolName, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
            var result = await tool.InvokeAsync(request);
            Assert.True(result.IsError != true, result.StructuredContent?.ToJsonString());
            Assert.Equal(1, worker.Calls);
            Assert.Equal(0, native);
            Assert.Empty(Directory.GetFileSystemEntries(root));
            var wrong = (JsonObject)arguments.DeepClone();
            if (toolName == "ExportPlcBlockDocuments")
            {
                wrong["blockPath"] = "Other/" + Block;
                Assert.Throws<InvalidDataException>(() => DocumentExportContract.ValidateRequest(wrong, worker.Payload));
            }
            else
            {
                wrong["groupPath"] = "Other";
                if (toolName == "ImportPlcBlockDocuments")
                    Assert.Throws<InvalidDataException>(() => DocumentImportContract.Validate(worker.Payload, wrong));
                else Assert.Throws<InvalidDataException>(() => BatchDocumentImportContract.Validate(worker.Payload, wrong));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
