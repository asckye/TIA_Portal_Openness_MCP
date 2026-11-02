using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Models.Errors;
using TiaOpenness.Contracts.Models.Policies;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Rpc;
using TiaMcp.WorkerChannel;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class PortalProcessConnectionTests
{
    private static PortalProcessInfo[] Processes() =>
    [
        new() { ProcessId = 123, ProjectPath = @"D:\项目\Line.ap21", Release = "21.0", UiMode = "WithUserInterface" },
        new() { ProcessId = 456, ProjectPath = null, Release = "21.0", UiMode = "WithoutUserInterface" },
    ];

    [Fact]
    public void Explicit_PID_selects_exactly_and_never_probes_other_processes()
    {
        var processes = Processes();
        Assert.Same(processes[1], PortalProcessSelection.Select(processes, 456, "21.0", _ => throw new Exception("Unexpected OS probe")));
    }

    [Theory]
    [InlineData(false, "has exited")]
    [InlineData(true, "another release")]
    public void Missing_PID_refuses_with_the_reason_instead_of_selecting_another(bool running, string reason)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PortalProcessSelection.Select(Processes(), 789, "21.0", pid =>
        { Assert.Equal(789, pid); return running; }));
        Assert.Contains(reason, error.Message);
        Assert.Contains("789", error.Message);
    }

    [Fact]
    public void Omitted_PID_keeps_zero_one_and_several_process_behaviour()
    {
        var processes = Processes();
        Assert.Null(PortalProcessSelection.Select([], null, "21.0", _ => throw new Exception()));
        Assert.Same(processes[0], PortalProcessSelection.Select([processes[0]], null, "21.0", _ => throw new Exception()));
        var error = Assert.Throws<PortalProcessSelectionRequiredException>(() => PortalProcessSelection.Select(processes, null, "21.0", _ => throw new Exception()));
        Assert.Same(processes, error.Processes);
        Assert.Contains(processes[0].ProjectPath, error.Message);
        Assert.Contains("PID 456: no project", error.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("1.5")]
    [InlineData("\"123\"")]
    [InlineData("true")]
    [InlineData("[]")]
    public void Bridge_validates_PID_before_resolving_or_entering_the_backend(string json)
    {
        using var dispatcher = new RpcDispatcher(() => throw new Exception("Backend must not resolve"), null);
        // Use real JSON types, including numbers beyond Int32 and non-integral numbers.
        var response = dispatcher.Handle(new() { Id = "2", Method = RpcMethods.SessionConnect,
            Params = BridgeJson.Deserialize<RpcRequest>("{\"params\":{\"processId\":" + json + "}}").Params });
        Assert.Equal(RpcErrorCodes.InvalidParams, response.Error.Code);
        Assert.Contains("processId", response.Error.Message);
        Assert.False(dispatcher.BackendEntered);
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"processId\":null}", null)]
    [InlineData("{\"processId\":456,\"attachToRunning\":false}", 456)]
    public void Bridge_forwards_the_optional_PID_once_and_preserves_other_arguments(string json, int? expected)
    {
        var factory = new Factory();
        using var dispatcher = new RpcDispatcher(() => factory, null);
        var response = dispatcher.Handle(Request(json));
        Assert.Null(response.Error);
        Assert.True(BridgeJson.Deserialize<SessionState>(response.Result.Value).Connected);
        var call = Assert.Single(factory.Proxy.Calls);
        Assert.Equal(expected, call[3]);
        Assert.Equal(true, call[0]);
        Assert.Equal(expected.HasValue ? false : true, call[1]);
        Assert.Null(call[2]);
    }

    [Theory]
    [InlineData("Openness firewall denied access")]
    [InlineData("TIA Portal PID 456 has exited")]
    [InlineData("PID 456 belongs to another release")]
    public void Bridge_preserves_refusal_without_retry_or_start(string reason)
    {
        var factory = new Factory();
        factory.Proxy.Failure = new InvalidOperationException(reason);
        using var dispatcher = new RpcDispatcher(() => factory, null);
        var response = dispatcher.Handle(Request("{\"processId\":456}"));
        Assert.Equal(RpcErrorCodes.InternalError, response.Error.Code);
        Assert.Equal(reason, response.Error.Message);
        Assert.Single(factory.Proxy.Calls);
    }

    [Fact]
    public void Bridge_reports_all_candidate_fields_as_structured_error_data()
    {
        var factory = new Factory();
        factory.Proxy.Failure = new PortalProcessSelectionRequiredException(Processes());
        using var dispatcher = new RpcDispatcher(() => factory, null);
        var response = dispatcher.Handle(Request("{}"));
        Assert.Equal(RpcErrorCodes.ProcessSelectionRequired, response.Error.Code);
        var rows = BridgeJson.Deserialize<List<PortalProcessInfo>>(response.Error.Data.Value);
        Assert.Equal(BridgeJson.Serialize(Processes()), BridgeJson.Serialize(rows));
        Assert.Single(factory.Proxy.Calls);
    }

    [Fact]
    public void Process_selection_channel_reply_is_a_known_refusal_and_the_selected_retry_advances_binding_once()
    {
        var factory = new Factory();
        factory.Proxy.Failure = new PortalProcessSelectionRequiredException(Processes());
        using var dispatcher = new BridgeChannelDispatcher(() => factory);
        var refused = dispatcher.Handle(new ChannelRequest(1, RpcMethods.SessionConnect, "{}", (_, _) => { }));
        Assert.Equal(ChannelOutcome.RejectedBeforeNative, refused.Failure.Outcome);
        Assert.Equal(RpcErrorCodes.ProcessSelectionRequired, BridgeJson.Deserialize<RpcError>(refused.Failure.RpcErrorJson).Code);
        Assert.Equal(0, dispatcher.Observe().Epoch);
        Assert.False(dispatcher.Observe().Bound);
        factory.Proxy.Failure = null;
        var connected = dispatcher.Handle(new ChannelRequest(2, RpcMethods.SessionConnect, "{\"processId\":456}", (_, _) => { }));
        Assert.Null(connected.Failure);
        Assert.Equal(1, dispatcher.Observe().Epoch);
        Assert.True(dispatcher.Observe().Bound);
        Assert.Equal(456, factory.Proxy.Calls[1][3]);
        Assert.Equal(2, factory.Proxy.Calls.Count);
    }

    [Fact]
    public void Process_snapshot_round_trips_and_captures_its_bridge_golden_when_requested()
    {
        var process = Processes()[0];
        var wire = BridgeJson.Serialize(process);
        Assert.Equal(wire, BridgeJson.Serialize(BridgeJson.Deserialize<PortalProcessInfo>(wire)));
        if (System.Environment.GetEnvironmentVariable("TIA_MCP_UPDATE_GOLDEN") != "1") return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "tests/Studio/TiaOpenness.Core.Tests/BridgeJsonGolden.json");
        var source = File.ReadAllText(path);
        var golden = JsonNode.Parse(source).AsObject();
        if (golden.ContainsKey(nameof(PortalProcessInfo)))
        {
            Assert.Equal(wire, BridgeJson.Serialize(BridgeJson.Deserialize<PortalProcessInfo>(golden[nameof(PortalProcessInfo)].ToJsonString())));
            return;
        }
        int end = source.LastIndexOf('}');
        var updated = source[..end].TrimEnd() + ",\n  \"PortalProcessInfo\": " + wire + "\n}\n";
        File.WriteAllText(path, updated.Replace("\r\n", "\n"), new UTF8Encoding(false));
    }

    private static RpcRequest Request(string json) => new() { Id = "connect", Method = RpcMethods.SessionConnect,
        Params = BridgeJson.Deserialize<RpcRequest>("{\"params\":" + json + "}").Params };

    private sealed class Factory : ITiaSessionFactory
    {
        private readonly ITiaSession _session = DispatchProxy.Create<ITiaSession, SessionProxy>();
        public SessionProxy Proxy => (SessionProxy)_session;
        public SessionMode Mode => SessionMode.Openness;
        public void Configure(string version) { }
        public ITiaSession Create() => _session;
    }

    public class SessionProxy : DispatchProxy
    {
        public List<object[]> Calls { get; } = [];
        public Exception Failure { get; set; }
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(IDisposable.Dispose)) return null;
            Assert.Equal(nameof(ITiaSession.Connect), method.Name);
            Calls.Add(args);
            if (Failure != null) throw Failure;
            return new SessionState { Connected = true, OpennessVersion = "21.0" };
        }
    }
}
