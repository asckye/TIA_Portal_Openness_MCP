using System.Text;
using System.Text.Json;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.WorkbenchControl.Tests;

public sealed class ProtocolTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    internal static WorkbenchControlRequest Request(WorkbenchControlOperation operation = WorkbenchControlOperation.ReadState,
        WorkbenchControlArguments? arguments = null) => new()
    {
        RequestId = Id, DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(2), Operation = operation,
        Origin = new() { ReleaseKey = "19", HostProcessId = Environment.ProcessId, McpSession = "0123456789abcdef", ClientName = "test" },
        Arguments = arguments ?? new WorkbenchReadStateArguments()
    };
    private static string Json(WorkbenchControlRequest request) => JsonSerializer.Serialize(request, WorkbenchControlProtocol.Json);
    private static MemoryStream Frame(string json)
    {
        byte[] data = Encoding.UTF8.GetBytes(json);
        var stream = new MemoryStream(); stream.Write(BitConverter.GetBytes(data.Length)); stream.Write(data); stream.Position = 0;
        return stream;
    }
    private static async Task Reject(string json)
    {
        using var stream = Frame(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Read<WorkbenchControlRequest>(stream, default));
    }
    public static IEnumerable<object[]> Operations()
    {
        yield return new object[] { WorkbenchControlOperation.DisplayPage, new WorkbenchDisplayPageArguments { Page = WorkbenchPage.Blocks } };
        yield return new object[] { WorkbenchControlOperation.DisplayBlock, new WorkbenchDisplayBlockArguments { SoftwarePath = "PLC_1", BlockPath = "Motion/FB_Axis" } };
        yield return new object[] { WorkbenchControlOperation.DisplayCall, new WorkbenchDisplayCallArguments { RequestId = Id } };
        yield return new object[] { WorkbenchControlOperation.DisplayLadder, new WorkbenchDisplayLadderArguments { SoftwarePath = "PLC_1", BlockPath = "Main" } };
        yield return new object[] { WorkbenchControlOperation.DisplayAtlas, new WorkbenchDisplayAtlasArguments() };
        yield return new object[] { WorkbenchControlOperation.ReadState, new WorkbenchReadStateArguments() };
        yield return new object[] { WorkbenchControlOperation.ReadSelection, new WorkbenchReadSelectionArguments { Offset = 2, Limit = 256 } };
        yield return new object[] { WorkbenchControlOperation.PrefillForm, new WorkbenchPrefillArguments { Form = WorkbenchPrefillForm.BlockFilter, Mode = WorkbenchPrefillMode.Set, Fields = new WorkbenchBlockFilterFields { Filter = "电机" } } };
    }
    [Theory, MemberData(nameof(Operations))]
    public async Task EachOperationRoundTripsWithItsOwnArguments(object operation, object arguments)
    {
        var request = Request((WorkbenchControlOperation)operation, (WorkbenchControlArguments)arguments);
        using var stream = new MemoryStream();
        await WorkbenchControlFrames.Write(stream, request, default);
        var bytes = stream.ToArray();
        Assert.Equal(bytes.Length - 4, bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24);
        stream.Position = 0;
        var copy = await WorkbenchControlFrames.Read<WorkbenchControlRequest>(stream, default);
        Assert.Equal(Json(request), Json(copy));
        Assert.Equal(arguments.GetType(), copy.Arguments.GetType());
    }
    [Theory]
    [InlineData("root")]
    [InlineData("origin")]
    [InlineData("arguments")]
    [InlineData("fields")]
    public async Task UnknownAndDuplicateFieldsAreRejectedAtEveryDepth(string level)
    {
        var json = Json(Request(WorkbenchControlOperation.PrefillForm, new WorkbenchPrefillArguments
        { Form = WorkbenchPrefillForm.BlockFilter, Fields = new WorkbenchBlockFilterFields { Filter = "x" } }));
        var insertion = level switch { "root" => "{", "origin" => "\"origin\":{", "arguments" => "\"arguments\":{", _ => "\"fields\":{" };
        int index = json.IndexOf(insertion, StringComparison.Ordinal) + insertion.Length;
        await Reject(json.Insert(index, "\"unexpected\":true,"));
        string duplicate = level switch { "root" => "\"version\":1,", "origin" => "\"host\":\"foundation\",", "arguments" => "\"form\":\"blockFilter\",", _ => "\"filter\":\"y\"," };
        await Reject(json.Insert(index, duplicate));
    }
    [Theory]
    [InlineData("protocol")]
    [InlineData("version")]
    [InlineData("requestId")]
    [InlineData("deadlineUtc")]
    [InlineData("origin")]
    [InlineData("operation")]
    [InlineData("arguments")]
    public async Task MissingEnvelopeFieldsAreRejected(string field)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Json(Request()))!.AsObject(); node.Remove(field);
        await Reject(node.ToJsonString());
    }
    [Theory]
    [InlineData("approve")]
    [InlineData("approval.switch")]
    [InlineData("service.start")]
    [InlineData("project.save")]
    [InlineData("DISPLAY.PAGE")]
    [InlineData("8")]
    public async Task OperationIsClosedAndStringOnly(string value)
        => await Reject(Json(Request()).Replace("\"read.state\"", value == "8" ? "8" : JsonSerializer.Serialize(value)));
    [Theory]
    [InlineData("mcp")]
    [InlineData("settings")]
    [InlineData("approvals")]
    [InlineData("Blocks")]
    public async Task PagesExcludeAdministrativeSurfaces(string page)
        => await Reject(Json(Request(WorkbenchControlOperation.DisplayPage, new WorkbenchDisplayPageArguments())).Replace("\"overview\"", JsonSerializer.Serialize(page)));
    [Theory]
    [InlineData("connectProject")]
    [InlineData("importBlocks")]
    [InlineData("exportBlocks")]
    [InlineData("generationWizard")]
    public async Task FuturePrefillFormsAreNotInV1(string form)
        => await Reject(Json(Request(WorkbenchControlOperation.PrefillForm, new WorkbenchPrefillArguments())).Replace("\"inspectionRules\"", JsonSerializer.Serialize(form)));
    [Fact]
    public async Task ArgumentTypesCannotBeMixedAndNestedRequiredFieldsCannotBeOmitted()
    {
        await Reject(Json(Request()).Replace("\"arguments\":{}", "\"arguments\":{\"page\":\"blocks\"}"));
        await Reject(Json(Request(WorkbenchControlOperation.DisplayBlock, new WorkbenchDisplayBlockArguments { SoftwarePath = "p", BlockPath = "b" })).Replace("\"blockPath\":\"b\"", "\"blockPath\":null"));
        await Reject(Json(Request()).Replace("\"hostProcessId\":" + Environment.ProcessId + ",", ""));
        await Reject(Json(Request(WorkbenchControlOperation.DisplayCall, new WorkbenchDisplayCallArguments { RequestId = Id })).Replace("\"arguments\":{\"requestId\":\"" + Id + "\"}", "\"arguments\":{}"));
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Write(stream, Request(WorkbenchControlOperation.DisplayPage), default));
        Assert.Equal(0, stream.Length);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(262145)]
    [InlineData(int.MaxValue)]
    public async Task InvalidLengthsAreRejectedBeforeReadingABody(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Read<WorkbenchControlRequest>(stream, default));
    }
    [Fact]
    public async Task OversizeWritesLeaveTheStreamUntouched()
    {
        var response = new WorkbenchControlResponse { RequestId = Id, Data = new() { Result = new string('x', WorkbenchControlFrames.MaximumBytes) }, Workbench = new() { Version = "4.1.0" } };
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Write(stream, response, default));
        Assert.Equal(0, stream.Length);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{broken")]
    public async Task NonObjectsAndMalformedJsonAreRejected(string json) => await Reject(json);
    [Fact]
    public async Task TruncationAndCancellationAreBounded()
    {
        using var shortHeader = new MemoryStream(new byte[3]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => WorkbenchControlFrames.Read<WorkbenchControlRequest>(shortHeader, default));
        using var shortBody = new MemoryStream(new byte[] { 2, 0, 0, 0, 123 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => WorkbenchControlFrames.Read<WorkbenchControlRequest>(shortBody, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var stream = Frame(Json(Request()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkbenchControlFrames.Read<WorkbenchControlRequest>(stream, cancelled.Token));
    }
    [Fact]
    public async Task VersionNegotiationRefusesWithoutDowngradeAndListsContracts()
    {
        var request = Request(); Assert.Null(WorkbenchControlProtocol.Negotiate(request, "4.1.0"));
        request.Version = 2;
        using var wire = Frame(Json(request));
        var decoded = await WorkbenchControlFrames.Read<WorkbenchControlRequest>(wire, default);
        var refusal = WorkbenchControlProtocol.Negotiate(decoded, "4.1.0")!;
        Assert.Equal(WorkbenchControlStatus.Refused, refusal.Status);
        Assert.Equal(WorkbenchControlError.UnsupportedCapability, refusal.Refusal!.Code);
        Assert.Equal(new[] { 1 }, refusal.Workbench.Contracts); Assert.Equal(1, refusal.Version);
        Assert.Equal(2, request.Version);
        using var response = new MemoryStream(); await WorkbenchControlFrames.Write(response, refusal, default);
        response.Position = 0; await WorkbenchControlFrames.ReadResponse(response, Id, default);
    }
    [Fact]
    public async Task ResponseCorrelationAndClosedResponseFieldsAreChecked()
    {
        var response = new WorkbenchControlResponse { RequestId = Id, Data = new() { Page = WorkbenchPage.Calls }, Workbench = new() { Version = "4.1.0" } };
        string json = JsonSerializer.Serialize(response, WorkbenchControlProtocol.Json);
        using var mismatch = Frame(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.ReadResponse(mismatch, new string('a', 32), default));
        foreach (var invalid in new[] { json.Replace("\"done\"", "\"approved\""), json.Replace("\"data\":{", "\"data\":{\"unexpected\":1,"),
            json.Replace("\"page\":\"calls\"", "\"page\":\"calls\",\"page\":\"audit\""), json.Replace("\"version\":\"4.1.0\"", "\"version\":\"4.1.0\",\"version\":\"4.1.0\"") })
        {
            using var stream = Frame(invalid);
            await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Read<WorkbenchControlResponse>(stream, default));
        }
    }
    [Theory]
    [InlineData("RESOURCE_UNAVAILABLE")]
    [InlineData("ACCESS_DENIED")]
    [InlineData("UNSUPPORTED_CAPABILITY")]
    [InlineData("PRECONDITION_FAILED")]
    [InlineData("IDENTITY_MISMATCH")]
    [InlineData("NOT_FOUND")]
    [InlineData("TARGET_AMBIGUOUS")]
    [InlineData("TIMEOUT")]
    [InlineData("INTERNAL_ERROR")]
    [InlineData("INVALID_ARGUMENT")]
    public async Task EveryDesignedErrorCodeRoundTrips(string code)
    {
        string json = "{\"protocol\":\"tiamcp.workbench-control\",\"version\":1,\"requestId\":\"" + Id + "\",\"status\":\"refused\",\"data\":null,\"refusal\":{\"code\":\"" + code + "\",\"condition\":\"test\",\"target\":\"pipe-owner\",\"candidates\":[]},\"workbench\":{\"version\":\"4.1.0\",\"contracts\":[1]}}";
        using var stream = Frame(json); var response = await WorkbenchControlFrames.Read<WorkbenchControlResponse>(stream, default);
        Assert.Contains(code, JsonSerializer.Serialize(response, WorkbenchControlProtocol.Json));
        using var invalid = Frame(json.Replace(code, "CONFIRMATION_REQUIRED"));
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Read<WorkbenchControlResponse>(invalid, default));
    }
    [Fact]
    public async Task NotificationIsAClosedSnapshotAndCannotBeReadAsAResponse()
    {
        var notification = new WorkbenchControlNotification { State = new() { Page = WorkbenchPage.Blocks, Busy = false,
            Prefill = new() { Form = WorkbenchPrefillForm.BlockFilter, Fields = new WorkbenchBlockFilterFields { Filter = "Main" }, Origin = Request().Origin } } };
        using var stream = new MemoryStream(); await WorkbenchControlFrames.Write(stream, notification, default);
        stream.Position = 0; var copy = await WorkbenchControlFrames.Read<WorkbenchControlNotification>(stream, default);
        Assert.Equal(WorkbenchPage.Blocks, copy.State.Page);
        stream.Position = 0; await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Read<WorkbenchControlResponse>(stream, default));
        using var unknown = Frame(JsonSerializer.Serialize(notification, WorkbenchControlProtocol.Json).Replace("stateChanged", "approval"));
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Read<WorkbenchControlNotification>(unknown, default));
    }
    [Fact]
    public void DeadlinePoliciesAreIndependentOfApprovalAndNativeSessionLocks()
    {
        var now = DateTimeOffset.UtcNow; var request = Request();
        request.DeadlineUtc = now; Assert.Equal(WorkbenchControlError.Timeout, request.CheckDeadline(now));
        request.DeadlineUtc = now.AddSeconds(3); Assert.Equal(WorkbenchControlError.InvalidArgument, request.CheckDeadline(now));
        request.Operation = WorkbenchControlOperation.DisplayPage;
        Assert.Null(request.CheckDeadline(now)); request.DeadlineUtc = now.AddSeconds(6);
        Assert.Equal(WorkbenchControlError.InvalidArgument, request.CheckDeadline(now));
    }
    [Fact]
    public async Task ChannelsRejectEachOthersFramesAndApprovalGoldenBytesStayUnchanged()
    {
        var approval = new ApprovalDecision { RequestId = Id, PlanHash = new string('a', 64), ArgumentDigest = new string('b', 64), Decision = "granted" };
        using var approvalWire = new MemoryStream(); await ApprovalFrames.Write(approvalWire, approval, default);
        string expected = "{\"Version\":1,\"RequestId\":\"" + Id + "\",\"PlanHash\":\"" + new string('a', 64) + "\",\"ArgumentDigest\":\"" + new string('b', 64) + "\",\"Decision\":\"granted\"}";
        Assert.Equal(Frame(expected).ToArray(), approvalWire.ToArray());
        approvalWire.Position = 0; await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Read<WorkbenchControlRequest>(approvalWire, default));
        using var controlWire = new MemoryStream(); await WorkbenchControlFrames.Write(controlWire, Request(), default);
        controlWire.Position = 0; await Assert.ThrowsAsync<InvalidDataException>(() => ApprovalFrames.Read<PendingApproval>(controlWire, default));
        controlWire.Position = 0; await Assert.ThrowsAsync<InvalidDataException>(() => ApprovalFrames.Read<ApprovalDecision>(controlWire, default));
    }
    [Fact]
    public void NamesAreSeparatedAndIncludeSidDataRootAndProtocolDomain()
    {
        string scope = Path.GetFullPath("approval.settings"); const string sid = "S-1-5-21-1-2-3-1000";
        Assert.NotEqual(ApprovalPipe.Name(scope, sid), WorkbenchControlPipe.Name(scope, sid));
        Assert.Equal(WorkbenchControlPipe.Name(scope, sid), WorkbenchControlPipe.Name(scope.ToUpperInvariant(), sid));
        Assert.NotEqual(WorkbenchControlPipe.Name(scope, sid), WorkbenchControlPipe.Name(scope + ".other", sid));
        Assert.NotEqual(WorkbenchControlPipe.Name(scope, sid), WorkbenchControlPipe.Name(scope, sid + "1"));
        Assert.StartsWith("TiaMcp.Workbench.v1.", WorkbenchControlPipe.Name(scope, sid));
    }
    [Fact]
    public async Task PendingApprovalRequestKeepsItsGoldenLengthPrefixAndJson()
    {
        var request = new PendingApproval { RequestId = Id, Host = "foundation", ReleaseKey = "19", Tool = "SaveProject",
            PlanHash = new string('a', 64), ArgumentDigest = new string('b', 64), Deadline = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Operations = new[] { new ApprovalAction { Tool = "SaveProject", Action = "apply", Target = "project" } } };
        string expected = "{\"Version\":1,\"Kind\":\"request\",\"Outcome\":null,\"RequestId\":\"" + Id + "\",\"Host\":\"foundation\",\"ReleaseKey\":\"19\",\"Tool\":\"SaveProject\",\"PlanHash\":\"" + new string('a', 64)
            + "\",\"ArgumentDigest\":\"" + new string('b', 64) + "\",\"ProjectIdentity\":\"{}\",\"ParametersJson\":\"{}\",\"Operations\":[{\"Tool\":\"SaveProject\",\"Action\":\"apply\",\"Target\":\"project\"}],\"TimeoutSeconds\":120,\"Deadline\":\"2030-01-01T00:00:00+00:00\"}";
        using var wire = new MemoryStream(); await ApprovalFrames.Write(wire, request, default);
        Assert.Equal(Frame(expected).ToArray(), wire.ToArray());
        wire.Position = 0; await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Read<WorkbenchControlRequest>(wire, default));
    }
    [Fact]
    public void OperationAndPageMembersMatchTheV1WhitelistExactly()
    {
        Assert.Equal(new[] { "DisplayPage", "DisplayBlock", "DisplayCall", "DisplayLadder", "DisplayAtlas", "ReadState", "ReadSelection", "PrefillForm" }, Enum.GetNames<WorkbenchControlOperation>());
        Assert.Equal(new[] { "Overview", "Blocks", "VersionControl", "Calls", "Audit", "Environment", "Log" }, Enum.GetNames<WorkbenchPage>());
        Assert.Equal(new[] { "InspectionRules", "BlockFilter", "BlockSelection" }, Enum.GetNames<WorkbenchPrefillForm>());
    }
    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 0)]
    [InlineData(0, 257)]
    public async Task SelectionPagingRejectsInvalidRanges(int offset, int limit)
        => await Reject(Json(Request(WorkbenchControlOperation.ReadSelection, new WorkbenchReadSelectionArguments { Offset = offset, Limit = limit })));
    [Theory]
    [InlineData("inspectionRules", "{\"namePattern\":\".*\"}")]
    [InlineData("blockFilter", "{\"filter\":\"Motor\"}")]
    [InlineData("blockSelection", "{\"softwarePath\":\"PLC_1\",\"blockPaths\":[\"Main\"]}")]
    public async Task PrefillSetAndClearAreTypedAndBounded(string form, string fields)
    {
        string envelope = Json(Request());
        string json = envelope.Replace("\"read.state\"", "\"prefill.form\"").Replace("\"arguments\":{}", "\"arguments\":{\"form\":\"" + form + "\",\"mode\":\"set\",\"fields\":" + fields + "}");
        using var stream = Frame(json); var request = await WorkbenchControlFrames.Read<WorkbenchControlRequest>(stream, default);
        using var wire = new MemoryStream(); await WorkbenchControlFrames.Write(wire, request, default);
        await Reject(json.Replace("\"set\"", "\"clear\""));
        await Reject(json.Replace(fields, "{}"));
        await Reject(json.Replace(fields, "{\"overwrite\":true}"));
        using var clear = Frame(json.Replace("\"set\"", "\"clear\"").Replace(fields, "{}"));
        await WorkbenchControlFrames.Read<WorkbenchControlRequest>(clear, default);
    }
    [Fact]
    public async Task RenderArtifactsAreBoundToTheRequestAndKind()
    {
        var args = new WorkbenchDisplayLadderArguments { SoftwarePath = "PLC_1", BlockPath = "Main", RenderRequestId = Id,
            Artifact = new() { RequestId = Id, Path = "D:/reports/Main.html", Sha256 = new string('a', 64), Kind = WorkbenchRenderKind.Ladder } };
        using var wire = new MemoryStream(); await WorkbenchControlFrames.Write(wire, Request(WorkbenchControlOperation.DisplayLadder, args), default);
        args.Artifact.RequestId = new string('b', 32);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Write(wire, Request(WorkbenchControlOperation.DisplayLadder, args), default));
        args.Artifact.RequestId = Id; args.Artifact.Kind = WorkbenchRenderKind.Atlas;
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Write(wire, Request(WorkbenchControlOperation.DisplayLadder, args), default));
    }
    [Fact]
    public async Task InvalidUtf8IsAFormatRefusal()
    {
        using var stream = new MemoryStream(new byte[] { 3, 0, 0, 0, 34, 255, 34 });
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Read<WorkbenchControlRequest>(stream, default));
    }
    [Fact]
    public async Task ExactBudgetIsAcceptedAndOneMoreByteIsRejected()
    {
        var response = new WorkbenchControlResponse { RequestId = Id, Data = new() { Result = "" }, Workbench = new() { Version = "4.1.0" } };
        int overhead = JsonSerializer.SerializeToUtf8Bytes(response, WorkbenchControlProtocol.Json).Length;
        response.Data.Result = new string('x', WorkbenchControlFrames.MaximumBytes - overhead);
        using var stream = new MemoryStream(); await WorkbenchControlFrames.Write(stream, response, default);
        Assert.Equal(WorkbenchControlFrames.MaximumBytes + 4, stream.Length);
        stream.Position = 0; var copy = await WorkbenchControlFrames.ReadResponse(stream, Id, default);
        Assert.Equal(response.Data.Result, copy.Data!.Result);
        response.Data.Result += "x";
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkbenchControlFrames.Write(stream, response, default));
        Assert.Equal(WorkbenchControlFrames.MaximumBytes + 4, stream.Length);
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => base.ReadAsync(buffer, offset, Math.Min(count, 1), token);
    }
    [Fact]
    public async Task HeaderAndBodyCanArriveOneByteAtATime()
    {
        string json = Json(Request()); using var encoded = Frame(json); using var fragmented = new FragmentedStream(encoded.ToArray());
        Assert.Equal(json, Json(await WorkbenchControlFrames.Read<WorkbenchControlRequest>(fragmented, default)));
    }
}
