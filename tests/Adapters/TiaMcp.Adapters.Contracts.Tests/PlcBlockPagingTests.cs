using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts.Paging;
using Xunit;

public sealed class PlcBlockPagingTests
{
    private sealed class PagingFacade
    {
        public PlcBlockPage ReadBlockPage(string softwarePath, string regexName = "", int limit = 20, string cursor = "")
            => new PlcBlockPage { Items = new[] { new PlcBlockDetails { Name = "B😀", Path = "Group/B😀" } }, Total = 1 };
    }

    [Fact]
    public void Dedicated_worker_module_preserves_arguments_and_host_values()
    {
        var module = new TiaMcp.PlcWorker.WorkerOperationModule("plc-paging", typeof(PagingFacade), new[] { "ReadBlockPage" });
        var registered = TiaMcp.PlcWorker.WorkerOperationModule.Register(new[] { module });
        var method = registered[TiaMcp.PlcWorker.WorkerOperations.BlockPage];
        Assert.True(TiaMcp.PlcWorker.WorkerOperations.IsReadOnly(TiaMcp.PlcWorker.WorkerOperations.BlockPage));
        var values = TiaMcp.PlcWorker.WorkerJson.Arguments(method.GetParameters(),
            TiaMcp.PlcWorker.WorkerJson.ParseArguments("{\"softwarePath\":\"PLC\"}"));
        Assert.Equal(new object?[] { "PLC", "", 20, "" }, values);
        var result = method.Invoke(new PagingFacade(), values);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(result)),
            System.Text.Json.Nodes.JsonNode.Parse(TiaMcp.PlcWorker.WorkerJson.Serialize(result))));
    }

    [Fact]
    public void All_130_group_qualified_paths_are_returned_once_and_final_page_completes()
    {
        var paths = Enumerable.Range(0, 130).Select(i => "G%2F1/Sub/B" + i.ToString("D3")).ToArray();
        var found = new List<string>();
        string cursor = "";
        do
        {
            var page = PlcBlockPaging.Plan(paths, "session/project/plc/filter", 20, cursor);
            Assert.Equal(130, page.Total);
            found.AddRange(paths.Skip(page.Offset).Take(20));
            cursor = page.NextCursor ?? "";
        } while (cursor.Length != 0);
        Assert.Equal(paths, found);
    }

    [Fact]
    public void Changed_inventory_query_project_or_session_rejects_the_continuation()
    {
        var paths = new[] { "Group/First", "Group/Second" };
        string cursor = PlcBlockPaging.Plan(paths, "scope", 1, "").NextCursor!;
        Assert.Throws<ArgumentException>(() => PlcBlockPaging.Plan(paths, "other-scope", 1, cursor));
        Assert.Throws<ArgumentException>(() => PlcBlockPaging.Plan(new[] { "New", "Other" }, "scope", 1, cursor));
        Assert.Throws<ArgumentException>(() => PlcBlockPaging.Plan(paths, "scope", 1, "malformed"));
        Assert.Null(PlcBlockPaging.Plan(Array.Empty<string>(), "scope", 20, "").NextCursor);
        Assert.Null(PlcBlockPaging.Plan(paths, "scope", 1, cursor).NextCursor);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(101)]
    public void Invalid_limits_are_refused(int limit)
        => Assert.Throws<ArgumentException>(() => PlcBlockPaging.Plan(Array.Empty<string>(), "scope", limit, ""));
}
