using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.LegacyHost;

internal sealed record Argument(string Name, string Type = "string", bool Required = true, object? Default = null);
internal sealed record Definition(string Name, string Operation, string Description, Argument[] Arguments, string? ResponseMember = null);

internal static class FoundationTools
{
    private static Argument S(string name) => new(name);
    private static Argument Dry() => new("dryRun", "boolean", false, true);
    internal static readonly Definition[] Definitions = {
        new("GetBlockInfo","ReadBlockInfo","[v17-object-read-safe-v1; manual reconciliation pending] Read exact group-qualified block details; no fallback or write.",new[]{S("softwarePath"),S("blockPath")},"BlockInfo"),
        new("GetTypeInfo","ReadTypeInfo","[v17-object-read-safe-v1; manual reconciliation pending] Read exact group-qualified PLC type details; no fallback or write.",new[]{S("softwarePath"),S("typePath")},"TypeInfo"),
        new("Connect", "Attach", "[v17-project-safe-v1] Explicit existing process ID required; no launch or process ownership. Repeated attach refused.", new[]{new Argument("processId","integer")},"Connection"),
        new("GetProject", "ListProjects", "[v17-project-safe-v1] List projects and API-supported local sessions with exact file identities and attributes.", Array.Empty<Argument>(),"Projects"),
        new("AttachToOpenProject", "BindProject", "[v17-project-safe-v1] Bind an existing project/session only when both projectName and exact expectedProjectFile match; borrowed targets cannot be closed.",new[]{S("projectName"),S("expectedProjectFile")},"Bind"),
        new("OpenProject","OpenProject","[v17-project-safe-v1] Preview opening an exact-version .ap file or API-supported .als local session. No replacement, upgrade, server-open or commit.",new[]{S("path"),Dry()},"ProjectMutation"),
        new("CreateProject","CreateProject","[v17-project-safe-v1] Preview creation in an existing parent directory; no project/session replacement.",new[]{S("directoryPath"),S("projectName"),Dry()},"ProjectMutation"),
        new("SaveProject","SaveProject","[v17-project-safe-v1] Explicitly save the exact bound project/local session locally; no server commit. Preview by default.",new[]{Dry()},"ProjectMutation"),
        new("CloseProject","CloseProject","[v17-project-safe-v1] Close only an unmodified project/local session opened by this worker. No implicit save, discard, commit or TIA termination.",new[]{Dry()},"ProjectMutation"),
        new("GetProjectTree","ReadProjectTree","[v17-project-safe-v1] Actual device groups, devices, nested items and software, including ungrouped devices and exact software paths.",Array.Empty<Argument>(),"ProjectTree"),
        new("GetBlocks","ReadBlocks","[v17-read-safe-v1] V17 block fields and case-insensitive regex. Accepts unique PLC/device/group aliases or exact address; rejects ambiguity and unused path segments.",new[]{S("softwarePath"),new Argument("regexName","string",false,"")},"Items"),
        new("GetBlocksWithHierarchy","ReadBlockHierarchy","[v17-read-safe-v1] Actual nested block groups, including empty groups. Accepts unique PLC/device/group aliases or exact address; ambiguity is an error.",new[]{S("softwarePath")},"Root"),
        new("GetTypes","ReadTypes","[v17-read-safe-v1] V17 type fields and case-insensitive regex. Accepts unique PLC/device/group aliases or exact address; rejects ambiguity and unused path segments.",new[]{S("softwarePath"),new Argument("regexName","string",false,"")},"Items"),
        new("GetPlcTagTables","ReadTagTableNames","[v17-read-safe-v1] V17 root-group tag-table name list. Accepts unique PLC/device/group aliases or exact address; native failures are errors, not empty success.",new[]{S("softwarePath")},"Items"),
        new("GetPlcExternalSources","ReadExternalSourceNames","[v17-external-source-read-v1] Root external-source names only; no source file contents, software-unit traversal or block generation. Native read failures are errors, not empty success.",new[]{S("softwarePath")},"Items"),
        new("ReadPlcTags","ListTags","[declaration-read-v1; ordinary PLC manual closed] Read tag declaration Name/DataType and logical address in Value; no online values. Exact case-sensitive PLC and root/user-group table paths required. Returns the existing PascalCase array; malformed worker results are errors. Software units and special ownership excluded.",new[]{S("plc"),S("table")},"Declarations"),
        new("ReadPlcUserConstants","ListUserConstants","[declaration-read-v1; ordinary PLC manual closed] Read user-constant Name/DataType and raw Value text; no expression evaluation. Exact case-sensitive PLC and root/user-group table paths required. Returns the existing PascalCase array; malformed worker results are errors. Software units and special ownership excluded.",new[]{S("plc"),S("table")},"Declarations"),
        new("ReadPlcSystemConstants","ListSystemConstants","[declaration-read-v1; ordinary PLC manual closed] Read system-constant Name/DataType and raw Value text; no hardware enumeration or online access. Exact case-sensitive PLC and root/user-group table paths required. Returns the existing PascalCase array; malformed worker results are errors. Software units and special ownership excluded.",new[]{S("plc"),S("table")},"Declarations"),
        new("ExportBlock","ExportBlock","[v17-exchange-safe-v1] Export consistent native block XML into an existing directory; optional existing subdirectories preserve groups. Never overwrites. V14 SP1 SCL XML contains interfaces only, not a full program backup. Preview by default.",new[]{S("softwarePath"),S("blockPath"),S("exportPath"),new Argument("preservePath","boolean",false,false),Dry()},"Mutation"),
        new("ExportType","ExportType","[v17-exchange-safe-v1] Export consistent native type XML into an existing directory. Never overwrites. Preview by default.",new[]{S("softwarePath"),S("exportPath"),S("typePath"),new Argument("preservePath","boolean",false,false),Dry()},"Mutation"),
        new("ExportPlcTagTable","ExportTagTable","[v17-exchange-safe-v1] Export one root-group tag table to a new XML file. Preview by default.",new[]{S("softwarePath"),S("tagTableName"),S("exportPath"),Dry()},"ExportFile"),
        new("ImportBlock","ImportBlocks","[v17-exchange-safe-v1] Native XML import into exact group; V14 SCL interface-only XML cannot restore or replace full programs; empty group means root. No version rewrite or fallback. Preview by default; overwrite defaults false.",new[]{S("softwarePath"),S("groupPath"),S("importPath"),new Argument("overwrite","boolean",false,false),Dry()},"Mutation"),
        new("ImportType","ImportTypes","[v17-exchange-safe-v1] Native type XML import into exact group. No version rewrite. Preview and no overwrite by default.",new[]{S("softwarePath"),S("groupPath"),S("importPath"),new Argument("overwrite","boolean",false,false),Dry()},"Mutation"),
        new("ImportPlcTagTable","ImportTagTables","[v17-exchange-safe-v1] Native table XML import into exact folder. No fallback. Preview and no overwrite by default.",new[]{S("softwarePath"),S("folderPath"),S("importPath"),new Argument("overwrite","boolean",false,false),Dry()},"Mutation"),
        new("CreatePlcTagTable","CreateTagTable","Create one PLC tag table; collisions refused.",new[]{S("plc"),S("group"),S("name"),Dry()}),
        new("CreatePlcTag","CreateTag","Create one tag declaration; no online write.",new[]{S("plc"),S("table"),S("name"),S("dataType"),S("address"),Dry()}),
        new("CreatePlcUserConstant","CreateUserConstant","Create one user constant.",new[]{S("plc"),S("table"),S("name"),S("dataType"),S("value"),Dry()}),
        new("CompileSoftware","CompileSoftware","[v17-compile-safe-v1] Preview by default. Actual PLC compilation returns native counts and nested messages; no download. Nonempty safety password requires supplied V17+ SafetyAdministration API and service; never ignored.",new[]{S("softwarePath"),new Argument("password","string",false,""),Dry()},"Compile"),
        new("CompileAndDiagnosePlc","CompileSoftware","[v17-compile-safe-v1] Actual counts plus error/warning/info diagnostics, preserving summary lines in raw messages. Preview by default; exact project confirmation required for execution.",new[]{S("softwarePath"),new Argument("password","string",false,""),Dry()},"Compile")
    };
    internal static IList<McpServerTool> Create(IFoundationWorker worker) => Definitions.Select(d => (McpServerTool)new FoundationTool(d,worker)).ToArray();
}

internal sealed class FoundationTool : McpServerTool
{
    private readonly Definition definition;
    private readonly IFoundationWorker worker;
    private readonly Tool tool;
    internal FoundationTool(Definition definition, IFoundationWorker worker)
    {
        if(definition.Arguments.Any(a=>a.Name=="dryRun")) definition=definition with { Arguments=definition.Arguments.Concat(new[]{new Argument("confirm","boolean",false,false),new Argument("expectedProjectFile","string",false,"")}).ToArray() };
        this.definition=definition; this.worker=worker;
        var properties=new JsonObject();
        foreach(var p in definition.Arguments) { var schema=new JsonObject { ["type"]=p.Type, ["description"]="Exact foundation argument: "+p.Name }; if(!p.Required) schema["default"]=JsonSerializer.SerializeToNode(p.Default); properties[p.Name]=schema; }
        var schemaRoot=new JsonObject { ["type"]="object", ["properties"]=properties, ["additionalProperties"]=false, ["required"]=new JsonArray(definition.Arguments.Where(p=>p.Required).Select(p=>(JsonNode?)JsonValue.Create(p.Name)).ToArray()) };
        tool=new Tool { Name=definition.Name, Description="[Source preview; native unverified] "+definition.Description, InputSchema=JsonSerializer.SerializeToElement(schemaRoot) };
    }
    public override Tool ProtocolTool=>tool;
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,CancellationToken cancellationToken=default)
    {
        try
        {
            var values=new JsonObject();
            if(request.Params?.Arguments != null) foreach(var pair in request.Params.Arguments)
            {
                var spec=definition.Arguments.SingleOrDefault(p=>p.Name==pair.Key) ?? throw new ArgumentException("Unknown/case-mismatched argument: "+pair.Key);
                var value=pair.Value;
                bool valid=spec.Type=="string" ? value.ValueKind==JsonValueKind.String : spec.Type=="boolean" ? value.ValueKind is JsonValueKind.True or JsonValueKind.False : value.ValueKind==JsonValueKind.Number && value.TryGetInt32(out _);
                if(!valid) throw new ArgumentException("Invalid argument type: "+pair.Key);
                values[pair.Key]=JsonNode.Parse(value.GetRawText());
            }
            foreach(var p in definition.Arguments) if(!values.ContainsKey(p.Name)) { if(p.Required) throw new ArgumentException("Missing argument: "+p.Name); values[p.Name]=JsonSerializer.SerializeToNode(p.Default); }
            if(values["dryRun"] is JsonValue dry && !dry.GetValue<bool>() && (values["confirm"]?.GetValue<bool>()!=true || string.IsNullOrWhiteSpace(values["expectedProjectFile"]?.GetValue<string>())))
                throw new ArgumentException("Execution requires confirm=true and the exact expectedProjectFile; preview is the default.");
            if(definition.ResponseMember=="Declarations") DeclarationReadContract.ValidateArguments(values);
            cancellationToken.ThrowIfCancellationRequested();
            var result=await worker.Call(definition.Operation,values,cancellationToken);
            if(definition.ResponseMember is "Connection" or "Projects" or "Bind" or "ProjectMutation" or "ProjectTree") result=V17ProjectEnvelope.Wrap(definition.Name,definition.ResponseMember,result,values["dryRun"]?.GetValue<bool>() ?? false);
            else if(definition.ResponseMember=="Compile") result=V17CompileEnvelope.Wrap(definition.Name,result,values["dryRun"]!.GetValue<bool>());
            else if(definition.ResponseMember is "Mutation" or "ExportFile") result=V17MutationEnvelope.Wrap(definition.Name,definition.ResponseMember,result,values["dryRun"]!.GetValue<bool>());
            else if(definition.ResponseMember=="Declarations") result=DeclarationReadContract.Validate(definition.Name,values["table"]!.GetValue<string>(),result);
            else if(definition.ResponseMember!=null) result=V17ReadEnvelope.Wrap(definition.Name,definition.ResponseMember,result);
            return new CallToolResult { Content=new List<ContentBlock>{new TextContentBlock { Text=result?.ToJsonString() ?? "null" }} };
        }
        catch(OperationCanceledException) { throw; }
        catch(McpException) { throw; }
        catch(Exception ex) when(definition.ResponseMember!=null)
        { throw new McpException(ex.Message,ex,ex is ArgumentException || ex is WorkerOperationException { Code:-32602 } ? McpErrorCode.InvalidParams : McpErrorCode.InternalError); }
        catch(Exception ex) { return new CallToolResult { IsError=true,Content=new List<ContentBlock>{new TextContentBlock { Text=ex.Message }} }; }
    }
}
