using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.LegacyHost;

// Only this boundary is registered. The existing host implementations retain their
// worker protocol and bounded parsers; legacy names are not callable aliases.
internal sealed class FoundationV4Tool : McpServerTool
{
    internal static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AddDeviceWithFallback"] = "CreateHardwareDevice", ["AttachToOpenProject"] = "AttachOpenProject",
        ["Bootstrap"] = "InitializeEnvironment", ["Connect"] = "ConnectPortal", ["Disconnect"] = "DisconnectPortal",
        ["DiagnosePortalConnectReadiness"] = "GetPortalConnectionReadiness", ["GetState"] = "GetSessionState",
        ["GetProject"] = "GetProjectInfo", ["CompileSoftware"] = "CompilePlcSoftware", ["CompileAndDiagnosePlc"] = "CompilePlcDiagnostics",
        ["GetBlocks"] = "ListPlcBlocks", ["GetBlockInfo"] = "GetPlcBlockInfo", ["GetBlocksWithHierarchy"] = "GetPlcBlockHierarchy",
        ["GetTypes"] = "ListPlcTypes", ["GetTypeInfo"] = "GetPlcTypeInfo", ["GetPlcTagTables"] = "ListPlcTagTables",
        ["GetPlcWatchTables"] = "ListPlcWatchTables", ["GetPlcExternalSources"] = "ListPlcExternalSources",
        ["GetTechnologyObjects"] = "ListTechnologyObjects", ["ReadPlcTags"] = "ListPlcTags",
        ["ReadPlcUserConstants"] = "ListPlcUserConstants", ["ReadPlcSystemConstants"] = "ListPlcSystemConstants",
        ["ExportBlock"] = "ExportPlcBlock", ["ExportBlocks"] = "ExportPlcBlocks", ["ExportType"] = "ExportPlcType",
        ["ExportTypes"] = "ExportPlcTypes", ["ImportBlock"] = "ImportPlcBlock", ["ImportType"] = "ImportPlcType",
        ["ImportBlocksFromDirectory"] = "ImportPlcBlocksFromDirectory",
        ["BuildPlcUdtXml"] = "BuildPlcUdt", ["BuildPlcTagTableXml"] = "BuildPlcTagTable",
        ["BuildPlcGlobalDbXml"] = "BuildPlcGlobalDb", ["BuildStructuredTextXml"] = "BuildStructuredText",
        ["BuildFlgNetCallXml"] = "BuildFlgNetCall", ["ComposePlcFcBlockXml"] = "BuildPlcFcBlock",
        ["ComposePlcFbBlockXml"] = "BuildPlcFbBlock", ["ComposePlcLadFcBlockXml"] = "BuildPlcLadFcBlock",
        ["BuildPlcSymbolManifestFromXmlPath"] = "BuildPlcSymbolManifestFromPath",
        ["ExportAsDocuments"] = "ExportPlcBlockDocuments", ["ExportBlocksAsDocuments"] = "ExportPlcBlocksDocuments",
        ["ImportFromDocuments"] = "ImportPlcBlockDocuments", ["ImportBlocksFromDocuments"] = "ImportPlcBlocksDocuments"
    };
    internal static string Name(string source) => Names.TryGetValue(source, out var name) ? name : source;
    internal static string Guidance(string text)
    {
        foreach (var pair in Names.OrderByDescending(p => p.Key.Length))
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\b" + pair.Key + @"\b", pair.Value);
        return text.Replace("Returns the existing PascalCase array", "Returns declarations in V4 data.items");
    }

    private readonly McpServerTool inner;
    private readonly string release;
    private readonly Tool tool;
    private readonly bool deviceCandidate;
    private readonly bool importCandidate;
    private string? parameter;
    private Func<JsonElement, (string? Json, Error? Error)>? convert;

    internal FoundationV4Tool(McpServerTool inner, string release) : this(inner, release, null) { }

    internal FoundationV4Tool(McpServerTool inner, string release, Func<string, BehaviorPolicy>? policyForTest)
    {
        this.inner = inner; this.release = release;
        var source = inner.ProtocolTool;
        deviceCandidate = source.Name == "AddDeviceWithFallback" && release == "19"
            && (policyForTest?.Invoke("P6-DEVICE") ?? BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, release, "P6-DEVICE")) == BehaviorPolicy.SafeV4;
        importCandidate = PlcImportContract.Entries.Contains(Name(source.Name), StringComparer.Ordinal)
            && (policyForTest?.Invoke("P6-IMPORT") ?? BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, release, "P6-IMPORT")) == BehaviorPolicy.SafeV4;
        var schema = JsonNode.Parse(source.InputSchema.GetRawText())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        JsonElement? typedSchema = null;
        void Construction<T>(string key) where T : ConstructionSpec
        {
            parameter = key;
            var contract = new ConstructionInput<T>(ConstructionProfile.Foundation);
            typedSchema = contract.Schema;
            convert = value => { var input = contract.Read(value, key); return input.Error != null
                ? (null, input.Error) : (ConstructionAdapter.ToBuilderJson(input.Value!), null); };
        }
        switch (source.Name)
        {
            case "BuildPlcUdtXml": Construction<UdtSpec>("udt"); break;
            case "BuildPlcTagTableXml": Construction<PlcTagTableSpec>("tagTable"); break;
            case "BuildPlcGlobalDbXml": Construction<GlobalDbSpec>("globalDb"); break;
            case "BuildStructuredTextXml": Construction<StructuredTextSpec>("structuredText"); break;
            case "BuildFlgNetCallXml": Construction<FlgNetCallSpec>("flgNet"); break;
            case "ComposePlcFcBlockXml": Construction<FcBlockSpec>("fcBlock"); break;
            case "ComposePlcFbBlockXml": Construction<FbBlockSpec>("fbBlock"); break;
            case "ComposePlcLadFcBlockXml": Construction<LadFcBlockSpec>("ladFcBlock"); break;
            case "PlanArtifactImportOrder":
                parameter = "artifacts";
                var contract = DomainValidation.Contract<Artifact[]>();
                typedSchema = contract.Schema;
                convert = value => { var input = contract.Read(value, "artifacts"); return input.Error != null
                    ? (null, input.Error) : (V4Json.Serialize(input.Value), null); };
                break;
        }
        if (parameter != null)
        {
            properties.Remove(parameter + "Json");
            properties[parameter] = JsonNode.Parse(typedSchema!.Value.GetRawText());
            var required = schema["required"]!.AsArray();
            for (int i = 0; i < required.Count; i++)
                if (required[i]!.GetValue<string>() == parameter + "Json") required[i] = parameter;
        }
        string description = Guidance(source.Description ?? "");
        if (parameter != null) description = description.Replace(parameter + "Json", parameter)
            + " Supply a typed object/array with exact camelCase fields, never a JSON string. Existing Foundation budgets and output-release limits apply.";
        if (parameter == "artifacts") description = "Offline dependency-first import planning. artifacts is a typed array of {id, target?, priority?, dependencies?}; 1..256 unique IDs, no missing or cyclic dependencies. No worker or file operations.";
        if (deviceCandidate)
        {
            schema = JsonNode.Parse(new DeviceCreationContract().InputSchema.GetRawText())!.AsObject();
            schema["properties"]!["family"]!["enum"] = new JsonArray("S7-1200", "S7-1500");
            description = "[PLC foundation][WRITE] Exact catalog device creation, restricted to CPU 1211C 6ES7211-1BE40-0XB0 and CPU 1513 6ES7513-1AM03-0AB0. Default preview; apply needs confirm=true, expectedPlanHash and expectedProjectFile. One Create, no fallback, save, compile or download. Test/accepted behaviorPolicy=safe-v4.";
        }
        if (importCandidate)
        {
            schema = JsonNode.Parse(PlcImportContract.Schema(Name(source.Name), release).GetRawText())!.AsObject();
            description = "[PLC foundation][WRITE] Same-release PLC import candidate. Preview returns original file hashes, exact target groups, complete inventory, overwrite capability and plan. Apply requires confirmation, expectedPlanHash and expectedProjectFile. Directory importOrder is explicit. One import per item with content readback; stop on first failure, unknown requires session reset. No source rewrite, compile, save, retry or rollback. Test/accepted behaviorPolicy=safe-v4.";
        }
        tool = new Tool { Name = Name(source.Name), Description = description
            + (inner is FoundationTool { IsNative: true } && !deviceCandidate && !importCandidate ? " Native behaviorPolicy=current; V4 native acceptance is pending." : ""),
            InputSchema = JsonSerializer.SerializeToElement(schema), OutputSchema = FoundationV4Result.Schema };
    }

    public override Tool ProtocolTool => tool;
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        string id = Meta.Correlate(null);
        var args = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        try
        {
            var validation = new InputContract<ToolArguments>(new InputSchema(tool.InputSchema), new InputBudget())
                .Read(JsonSerializer.SerializeToElement(args), "arguments");
            if (validation.Error != null && importCandidate) return FoundationV4Result.ImportCandidate(PlcImportSession.Result(release, tool.Name, id, null, validation.Error, Outcome.RejectedBeforeOperation, Execution.NotStarted));
            if (validation.Error != null) return deviceCandidate
                ? FoundationV4Result.DeviceCandidate(DeviceCreationSession.Result(release, tool.Name, id, null, validation.Error, Outcome.RejectedBeforeOperation, Execution.NotStarted))
                : FoundationV4Result.Reject(release, tool.Name, id, validation.Error, inner is FoundationTool { IsNative: true });
            if (deviceCandidate) return await ((FoundationTool)inner).InvokeDeviceCandidateAsync(args, release, id, cancellationToken);
            if (importCandidate) return await ((FoundationTool)inner).InvokeImportCandidateAsync(args, release, tool.Name, id, cancellationToken);
            var adapted = new Dictionary<string, JsonElement>(args, StringComparer.Ordinal);
            if (parameter != null)
            {
                var input = convert!(args[parameter]);
                if (input.Error != null) return FoundationV4Result.Reject(release, tool.Name, id, input.Error);
                adapted.Remove(parameter);
                adapted[parameter + "Json"] = JsonSerializer.SerializeToElement(input.Json);
            }
            var original = request.Params;
            request.Params = new CallToolRequestParams { Name = inner.ProtocolTool.Name, Arguments = adapted };
            try
            {
                if (inner is FoundationTool foundation) return await foundation.InvokeV4Async(request, release, id, cancellationToken);
                var result = await inner.InvokeAsync(request, cancellationToken);
                var body = JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text);
                return FoundationV4Result.Host(release, tool.Name, id, body, parameter != null && parameter != "artifacts", result.IsError == true, args);
            }
            finally { request.Params = original; }
        }
        catch (OperationCanceledException) /* swallow(privacy): cancellation is represented by the stable V4 code, without exception text */
        { return FoundationV4Result.Reject(release, tool.Name, id, new Error("Request cancelled before completion.", new CancelledDetails("host"))); }
        catch (McpException ex) when (ex.ErrorCode == McpErrorCode.InvalidParams)
        { return FoundationV4Result.Reject(release, tool.Name, id, FoundationV4Result.Invalid("arguments")); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
        { return FoundationV4Result.Reject(release, tool.Name, id, FoundationV4Result.Invalid("arguments")); }
        catch (Exception) /* swallow(privacy): return a value-free V4 failure; implementation details are not protocol data */
        { return FoundationV4Result.HostFailure(release, tool.Name, id); }
    }
}
