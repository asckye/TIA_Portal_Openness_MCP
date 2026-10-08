using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.WorkerChannel;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer { static partial void WaitToolTask(Task task, ref bool waited); }
}

public sealed class ProgressTests
{
    private sealed class OwnerStream : MemoryStream
    {
        internal readonly List<int> Owners = new();
        public override void Write(byte[] bytes, int offset, int count) { Owners.Add(Environment.CurrentManagedThreadId); base.Write(bytes, offset, count); }
    }
    private sealed class Services : IServiceProvider { public object? GetService(Type type) => null; }
    private static async Task Operation(IMcpServer server, RequestContext<CallToolRequestParams> context)
    {
        await Task.Run(async () => {
            if (context.Params?.ProgressToken != null)
                for (int i = 1; i <= 3; i++) {
                    await Task.Delay(10).ConfigureAwait(false);
                    await server.SendNotificationAsync("notifications/progress", new { progress = i, total = 3, message = "fixture", progressToken = context.Params.ProgressToken });
                }
        });
    }
    [Theory, InlineData(false), InlineData(true)]
    public void Pool_thread_notifications_are_emitted_only_by_the_channel_owner(bool enabled)
    {
        int owner = Environment.CurrentManagedThreadId;
        using var input = new MemoryStream(); using var output = new OwnerStream();
        var identity = new ChannelIdentity("21", new string('a', 64), new string('b', 64), 123, new string('c', 64));
        var server = new ChannelServer(input, output, identity, () => new(0, false), request => {
            using var shim = new WorkerProgressShim(request, enabled, new Services());
            var method = typeof(ProgressTests).GetMethod(nameof(Operation), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var values = new object?[2]; shim.Bind(method, values);
            var task = (Task)method.Invoke(null, values)!;
            Assert.True(WorkerProgressShim.Wait(task));
            return ChannelResponse.Success("{}");
        }, ChannelProfile.Engine);
        server.WriteHello();
        server.Handle(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"engine.invoke\",\"params\":{},\"bindingEpoch\":0}"));
        var frames = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
        Assert.Equal(enabled ? 5 : 2, frames.Length);
        Assert.All(output.Owners, id => Assert.Equal(owner, id));
        if (enabled) {
            var progress = frames.Skip(1).Take(3).Select(f => f.GetProperty("params")).ToArray();
            Assert.Equal(new[] { 33, 66, 100 }, progress.Select(p => p.GetProperty("percent").GetInt32()));
            Assert.All(progress, p => Assert.False(p.GetProperty("payload").TryGetProperty("progressToken", out _)));
        }
    }
    [Fact]
    public void Spill_rejects_truncation_and_path_injection()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "spill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string descriptor = WorkerReplySpill.Write(root, "{\"data\":\"complete\"}");
        string file = Directory.GetFiles(root).Single(); File.WriteAllText(file, "{}");
        Assert.Throws<IOException>(() => WorkerReplySpill.Read(root, descriptor, out _));
        Assert.True(File.Exists(file));
        Assert.Throws<IOException>(() => WorkerReplySpill.Read(root, "{\"workerReplySpill\":{\"id\":\"../outside\",\"byteLength\":0,\"sha256\":\"\"}}", out _));
        File.Delete(file); Directory.Delete(root);
    }
}
