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
        new("GetState","ReadState","[runtime-query-candidate] Cached attachment/project state only; IsAttached is not live connection proof. No runtime attach, project query or rebind. PID identity remains unverified.",Array.Empty<Argument>(),"RuntimeQuery"),
        new("ListPortalProcessProjects","ReadPortalProcessProjects","[runtime-query-candidate] Detached read of this release's process metadata; acquisition time is snapshot time, not process start. Separate OS start UTC is observation-only and may be unknown; no attach, launch or stable identity guarantee.",Array.Empty<Argument>(),"RuntimeQuery"),
        new("DiagnosePortalConnectReadiness","ReadPortalConnectReadiness","[runtime-query-candidate] Explicit processId metadata diagnosis without attach. Found never proves readiness or permission; those remain unknown/not-probed. No startup, repairs or project open.",new[]{new Argument("processId","integer")},"RuntimeQuery"),
        new("AddDeviceWithFallback","AddDeviceWithFallback","[bounded-device-add-candidate] V19–21 exact catalog selection only: explicit ArticleNumber+Version or full OrderNumber TypeIdentifier with empty version. Only documented CPU 1211C 6ES7211-1BE40-0XB0 or CPU 1513 6ES7513-1AM03-0AB0 with matching S7-1200/S7-1500 family accepted. No approximate/default/family fallback, insertion probing, HMI hint or retries. Complete bounded root/grouped/ungrouped inventory; collision fails closed. Default preview, exact project/hash confirmation; one native create. Unknown outcome poisons session. No save, download, configuration or online calls; native acceptance NOT RUN.",new[]{S("preferredMlfb"),S("preferredVersion"),S("deviceName"),new Argument("family","string",false,"S7-1500"),Dry(),new Argument("expectedPlanHash","string",false,"")},"DeviceAdd"),
        new("SearchHardwareCatalog","SearchHardwareCatalog","[hardware-catalog-candidate] V19–21 only. Existing explicit attached portal and open bound project required. One literal query, bounded client traversal/results with explicit truncation. Native Find has no server-side limit/cancellation. No insertion, compatibility probe, catalog repair or launch. V18 semantics gated; V14 SP1–17 API absent.",new[]{S("keyword"),new Argument("limit","integer",false,50)},"HardwareCatalog"),
        new("GetPlcWatchTables","ReadWatchTableNames","[supplementary-read-candidate] V15.1–V21 ordinary PLC root/user-group escaped watch-table paths; no force tables or values. V14 SP1 API lacks this property; typed adapters compiled; native acceptance pending.",new[]{S("softwarePath")},"SupplementaryRead"),
        new("GetTechnologyObjects","ReadTechnologyObjects","[supplementary-read-candidate] V14 SP1–V21 ordinary PLC technology metadata; root-only through V18, root/user groups V19–V21. Unavailable optional attributes reported; no live values. Typed adapters compiled; native acceptance pending.",new[]{S("softwarePath")},"SupplementaryRead"),
        new("GetSoftwareInfo","ReadSoftwareInfo","[software-read-candidate] Ordinary PLC Name, Attributes and Description with exact software identity; no live values. Windows typed rebuild/native acceptance pending.",new[]{S("softwarePath")},"SoftwareRead"),
        new("GetSoftwareTree","ReadSoftwareTree","[software-read-candidate] Ordinary PLC root/user block and type tree with exact addresses, including empty groups. System groups and software units excluded; incomplete traversal fails. Windows typed rebuild/native acceptance pending.",new[]{S("softwarePath")},"SoftwareRead"),
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
        new("Disconnect","Disconnect","[acknowledged-disconnect-candidate] End this worker session by detaching only a proven non-owning existing-process attachment. No project required; no save, project close, launch or automatic reconnect. Idle disconnect never starts a worker. Subsequent Attach requires a new explicit session. Native acceptance NOT RUN.",Array.Empty<Argument>(),"Disconnect"),
        new("GetTypes","ReadTypes","[v17-read-safe-v1] V17 type fields and case-insensitive regex. Accepts unique PLC/device/group aliases or exact address; rejects ambiguity and unused path segments.",new[]{S("softwarePath"),new Argument("regexName","string",false,"")},"Items"),
        new("GetPlcTagTables","ReadTagTableNames","[v17-read-safe-v1] V17 root-group tag-table name list. Accepts unique PLC/device/group aliases or exact address; native failures are errors, not empty success.",new[]{S("softwarePath")},"Items"),
        new("DeletePlcExternalSource","DeletePlcExternalSource","[bounded-delete-candidate] Delete one exact uniquely named ordinary offline PLC root external source. Empty groupPath only; no extension or case fallback. Preview complete inventory and session identity/hash; apply requires expectedPlanHash, confirm and expectedProjectFile. One native call, verified removal, unknown outcome poisons session. No generation, compilation, automatic save or rollback; filesystem/recovery effects and native acceptance unverified.",new[]{S("softwarePath"),S("groupPath"),S("externalSourceName"),Dry(),new Argument("expectedPlanHash","string",false,"")},"ExternalSourceDelete"),
        new("PlanPlcExternalSourceImport","PlanPlcExternalSourceImport","[external-source-plan-only] Compatibility preview for one allowlisted local ASCII SCL/AWL/DB/UDT file (4 MiB) and ordinary PLC root. This older planning contract never executes. Use ImportPlcExternalSource with its own preview and confirmation to import, then GenerateBlocksFromExternalSource and CompileAndDiagnosePlc. This call creates zero sources.",new[]{S("softwarePath"),S("groupPath"),S("filePath"),S("allowedFilePath"),Dry(),new Argument("expectedPlanHash","string",false,"")},"ExternalSourcePlan"),
        new("GetPlcExternalSources","ReadExternalSourceNames","[v17-external-source-read-v1] Root external-source names only; no source file contents, software-unit traversal or block generation. Native read failures are errors, not empty success.",new[]{S("softwarePath")},"Items"),
        new("ImportPlcExternalSource","ImportPlcExternalSource","Import one ASCII .scl/.awl/.db/.udt file into the ordinary PLC external-source root (groupPath empty). Preview then confirm the exact project and returned plan hash. Calls CreateFromFile(filename,fullPath) once, reads back the actual SourceName; pass that name to generation. Existing source names are refused. Source text is not compiled by import.",new[]{S("softwarePath"),S("groupPath"),S("filePath"),Dry(),new Argument("expectedPlanHash","string",false,"")},"ExternalSourceWorkflow"),
        new("GenerateBlocksFromExternalSource","GenerateBlocksFromExternalSource","Generate blocks/UDTs from one exact root source name. Existing blocks may be overwritten. Preview reviews source identity and block/type inventory, not source contents; confirm exact project and plan hash. V15.1+ uses GenerateBlockOption.None and reads native returned objects; V14 SP1 returns only before/after inventory observations. Generation is not a full PLC compile; next call CompileAndDiagnosePlc and check diagnostics.",new[]{S("softwarePath"),S("externalSourceName"),Dry(),new Argument("expectedPlanHash","string",false,"")},"ExternalSourceWorkflow"),
        new("ReadPlcTags","ListTags","[declaration-read-v1; ordinary PLC manual closed] Read tag declaration Name/DataType and logical address in Value; no online values. Exact case-sensitive PLC and root/user-group table paths required. Returns the existing PascalCase array; malformed worker results are errors. Software units and special ownership excluded.",new[]{S("plc"),S("table")},"Declarations"),
        new("ReadPlcUserConstants","ListUserConstants","[declaration-read-v1; ordinary PLC manual closed] Read user-constant Name/DataType and raw Value text; no expression evaluation. Exact case-sensitive PLC and root/user-group table paths required. Returns the existing PascalCase array; malformed worker results are errors. Software units and special ownership excluded.",new[]{S("plc"),S("table")},"Declarations"),
        new("ReadPlcSystemConstants","ListSystemConstants","[declaration-read-v1; ordinary PLC manual closed] Read system-constant Name/DataType and raw Value text; no hardware enumeration or online access. Exact case-sensitive PLC and root/user-group table paths required. Returns the existing PascalCase array; malformed worker results are errors. Software units and special ownership excluded.",new[]{S("plc"),S("table")},"Declarations"),
        new("ImportBlocksFromDirectory","ImportBlocksFromDirectory","[bounded-batch-import-candidate] Top directory XML only, exact ordinary PLC/group. One object per file; no overwrite, repair, inferred dependencies or rollback. Preview defaults true; apply requires complete importOrder, expectedPlanHash, confirmation and exact project. Native None; first failure stops with partial results. Windows SDK/native unverified.",new[]{S("softwarePath"),S("groupPath"),S("dir"),new Argument("regexName","string",false,""),new Argument("overwrite","boolean",false,false),Dry(),new Argument("importOrder","array",false,Array.Empty<string>()),new Argument("expectedPlanHash","string",false,""),new Argument("maxItems","integer",false,128)},"BatchImport"),
        new("ImportPlcProgramFromDirectory","ImportPlcProgramFromDirectory","[bounded-batch-import-candidate] Recursive bounded ordinary block/UDT/tag-table XML import, not a complete program restore. Exact existing target groups; one object per file; native None. Preview=true and compileAfter=false defaults differ from upstream. compileAfter, overwrite, technology and failure continuation are refused before imports. Caller order is dependency-unverified; native acceptance pending.",new[]{S("softwarePath"),S("sourceDir"),new Argument("typeGroupPath","string",false,""),new Argument("tagFolderPath","string",false,""),new Argument("technologyFolderPath","string",false,""),new Argument("blockGroupPath","string",false,""),new Argument("regexName","string",false,""),new Argument("compileAfter","boolean",false,false),new Argument("stopOnImportFailure","boolean",false,true),Dry(),new Argument("importOrder","array",false,Array.Empty<string>()),new Argument("expectedPlanHash","string",false,""),new Argument("overwrite","boolean",false,false),new Argument("maxItems","integer",false,128)},"BatchImport"),
        new("ExportBlocksAsDocuments","ExportBlocksAsDocuments","[batch-document-export-candidate] Exact V20/V21 ordinary LAD/DB groups only. Complete bounded inventory; recursive opt-in; unsupported/protected members refuse entire batch. exportPath MUST be a NEW tree under an existing parent, with path-hashed block directories. V20 resources required, V21 optional. Preview then exact project/hash confirmation. preservePath=true refused. One non-overwrite tree publication, retain partial staging and stop on failure; no replay. SDK/native acceptance pending.",new[]{S("softwarePath"),S("groupPath"),S("exportPath"),new Argument("recursive","boolean",false,false),new Argument("maxItems","integer",false,128),new Argument("preservePath","boolean",false,false),new Argument("expectedPlanHash","string",false,""),Dry()},"BatchDocumentExport"),
        new("ImportBlocksFromDocuments","ImportBlocksFromDocuments","[batch-document-import-candidate] Exact V20/V21 ordered manifest of 1..16 admitted ordinary GlobalDB document sets. Reuses single document grammar and None-only native calls; all code/resource pairs remain locked. Whole-manifest hashes, complete collision inventory and exact project/group confirmation. Stop on first uncertainty, retain succeeded/failed/not-attempted evidence, poison session; no folder guessing, overwrite, culture activation, compile, save, retry or rollback. Native acceptance NOT RUN.",new[]{S("softwarePath"),S("groupPath"),S("importPath"),new Argument("fileNamesWithoutExtension","array",true,null),new Argument("overwrite","boolean",false,false),new Argument("expectedPlanHash","string",false,""),Dry()},"BatchDocumentImport"),
        new("ImportFromDocuments","ImportFromDocuments","[document-import-candidate] Exact V20/V21, one ordinary global DB only. Complete bounded lexical admission of primitive Bool/integer scalars; rejects FC/FB/LAD, arrays, expressions, instance DBs, unknown pragmas and extra declarations. Identity comes from .s7dcl, must equal basename; code/resource bytes stay locked and hashed. V20 requires .s7res; V21 optional. Native None only (throw if exists), exact project/target/inventory/hash confirmation; no overwrite/renumber/culture activation. Every uncertain attempted outcome may have changed project, poisons session and is never replayed. Syntax/content validity and native acceptance NOT RUN.",new[]{S("softwarePath"),S("groupPath"),S("importPath"),S("fileNameWithoutExtension"),new Argument("overwrite","boolean",false,false),new Argument("expectedPlanHash","string",false,""),Dry()},"DocumentImport"),
        new("ExportAsDocuments","ExportAsDocuments","[document-export-candidate] Exact V20/V21 only; ordinary LAD/DB single block. exportPath MUST be a NEW directory under an existing parent; publishes .s7dcl plus required V20 or optional V21 .s7res together without overwrite. preservePath=true refused. Native default two-argument overload; no language override, cross-version roundtrip or full-format claim. Preview first, exact project/plan hash and confirmation for apply. Exact SDK/native acceptance pending.",new[]{S("softwarePath"),S("blockPath"),S("exportPath"),new Argument("preservePath","boolean",false,false),new Argument("expectedPlanHash","string",false,""),Dry()},"DocumentExport"),
        new("ExportPlcWatchTable","ExportPlcWatchTable","[special-export-candidate] Exact canonical ordinary PLC watch-table path; root/user groups, no force tables. Preview has zero file writes. Confirmed apply requires exact project and expectedPlanHash. ExportOptions.None; no overwrite, snapshots or retry. V16–21 manual-backed method source; V15.1 is signature-verified but preview only pending semantics.",new[]{S("softwarePath"),S("watchTableName"),S("exportPath"),new Argument("expectedPlanHash","string",false,""),Dry()},"SpecialExport"),
        new("ExportTechnologyObject","ExportTechnologyObject","[special-export-candidate] Exact canonical TO path; root-only before V19, user groups from V19. Preview first; apply requires exact project, confirmation and expectedPlanHash, consistent supported TO and scoped offline target. Native XML unchanged, ExportOptions.None, no snapshots. V16–20 manual-backed source; other releases signature-verified but preview only until export semantics close.",new[]{S("softwarePath"),S("toName"),S("exportPath"),new Argument("expectedPlanHash","string",false,""),Dry()},"SpecialExport"),
        new("ExportBlocks","ExportBlocks","[bounded-batch-xml-v1] Exact ordinary standard PLC/group scope; complete bounded inventory, recursive opt-in. Preview first; execution requires inventory hash, confirmation and exact project. Original XML numbers/symbols unchanged; no overwrite or retry; partial outcomes retained. Native unverified.",new[]{S("softwarePath"),S("groupPath"),S("exportPath"),new Argument("recursive","boolean",false,false),new Argument("maxItems","integer",false,128),new Argument("expectedInventoryHash","string",false,""),Dry()},"BatchExport"),
        new("ExportTypes","ExportTypes","[bounded-batch-xml-v1] Exact ordinary standard PLC/group scope; complete bounded inventory, recursive opt-in. Preview first; execution requires inventory hash, confirmation and exact project. Original XML numbers/symbols unchanged; no overwrite or retry; partial outcomes retained. Native unverified.",new[]{S("softwarePath"),S("groupPath"),S("exportPath"),new Argument("recursive","boolean",false,false),new Argument("maxItems","integer",false,128),new Argument("expectedInventoryHash","string",false,""),Dry()},"BatchExport"),
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
    internal static bool Available(Definition definition, string releaseKey)
    {
        var major = TiaMcp.Versioning.TiaVersionCatalog.Get(releaseKey).MajorVersion;
        switch (definition.ResponseMember)
        {
            case "HardwareCatalog": case "DeviceAdd": return major >= 19;
            case "SpecialExport": return major >= 16;
            case "DocumentExport": case "BatchDocumentExport": case "DocumentImport": case "BatchDocumentImport": return major >= 20;
        }
        return definition.Name != "GetPlcWatchTables" || releaseKey != "14sp1";
    }
    internal static IList<McpServerTool> Create(IFoundationWorker worker, string releaseKey) => Definitions.Where(d => Available(d, releaseKey)).Select(d => (McpServerTool)new FoundationTool(d, worker)).ToArray();
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
        foreach(var p in definition.Arguments) { var schema=new JsonObject { ["type"]=p.Type, ["description"]="Exact foundation argument: "+p.Name }; if(p.Type=="array") schema["items"]=new JsonObject { ["type"]="string" }; if(!p.Required) schema["default"]=JsonSerializer.SerializeToNode(p.Default); properties[p.Name]=schema; }
        var schemaRoot=new JsonObject { ["type"]="object", ["properties"]=properties, ["additionalProperties"]=false, ["required"]=new JsonArray(definition.Arguments.Where(p=>p.Required).Select(p=>(JsonNode?)JsonValue.Create(p.Name)).ToArray()) };
        tool=new Tool { Name=definition.Name, Description="[PLC foundation; native unverified] "+definition.Description, InputSchema=JsonSerializer.SerializeToElement(schemaRoot) };
    }
    public override Tool ProtocolTool=>tool;
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,CancellationToken cancellationToken=default)
        => InvokeCoreAsync(request, cancellationToken);
    internal ValueTask<CallToolResult> InvokeV4Async(RequestContext<CallToolRequestParams> request, string release, string id, CancellationToken cancellationToken)
        => InvokeCoreAsync(request, cancellationToken, release, id);
    private async ValueTask<CallToolResult> InvokeCoreAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken, string? release = null, string? id = null)
    {
        bool dispatched = false;
        var values = new JsonObject();
        JsonNode? raw = null;
        try
        {
            if(request.Params?.Arguments != null) foreach(var pair in request.Params.Arguments)
            {
                var spec=definition.Arguments.SingleOrDefault(p=>p.Name==pair.Key) ?? throw new ArgumentException("Unknown/case-mismatched argument: "+pair.Key);
                var value=pair.Value;
                bool valid=spec.Type=="string" ? value.ValueKind==JsonValueKind.String : spec.Type=="boolean" ? value.ValueKind is JsonValueKind.True or JsonValueKind.False : spec.Type=="array" ? value.ValueKind==JsonValueKind.Array && value.GetArrayLength()<=256 && value.EnumerateArray().All(x=>x.ValueKind==JsonValueKind.String) : value.ValueKind==JsonValueKind.Number && value.TryGetInt32(out _);
                if(!valid) throw new ArgumentException("Invalid argument type: "+pair.Key);
                values[pair.Key]=JsonNode.Parse(value.GetRawText());
            }
            foreach(var p in definition.Arguments) if(!values.ContainsKey(p.Name)) { if(p.Required) throw new ArgumentException("Missing argument: "+p.Name); values[p.Name]=JsonSerializer.SerializeToNode(p.Default); }
            if(values["dryRun"] is JsonValue dry && !dry.GetValue<bool>() && (values["confirm"]?.GetValue<bool>()!=true || string.IsNullOrWhiteSpace(values["expectedProjectFile"]?.GetValue<string>())))
                throw new ArgumentException("Execution requires confirm=true and the exact expectedProjectFile; preview is the default.");
            if(definition.Name=="DiagnosePortalConnectReadiness" && values["processId"]!.GetValue<int>()<=0) throw new ArgumentException("Select an explicit positive processId.");
            if(definition.ResponseMember=="HardwareCatalog") HardwareCatalogContract.ValidateArguments(values);
            if(definition.ResponseMember=="Declarations") DeclarationReadContract.ValidateArguments(values);
            if(definition.ResponseMember=="BatchExport" && !values["dryRun"]!.GetValue<bool>() && string.IsNullOrWhiteSpace(values["expectedInventoryHash"]!.GetValue<string>())) throw new ArgumentException("Execution requires expectedInventoryHash from a fresh preview.");
            if(definition.ResponseMember is "SpecialExport" or "DocumentExport" or "BatchDocumentExport" or "DocumentImport" or "BatchDocumentImport" && !values["dryRun"]!.GetValue<bool>() && string.IsNullOrWhiteSpace(values["expectedPlanHash"]!.GetValue<string>())) throw new ArgumentException("Apply requires expectedPlanHash from a reviewed preview.");
            if(definition.ResponseMember is "DocumentImport" or "BatchDocumentImport" && values["overwrite"]!.GetValue<bool>()) throw new ArgumentException("Document import supports native None only, no overwrite.");
            if(definition.ResponseMember=="BatchDocumentImport")
            {
                var names=values["fileNamesWithoutExtension"]!.AsArray().Select(x=>x!.GetValue<string>()).ToArray();
                if(names.Length<1||names.Length>16||names.Any(x=>string.IsNullOrWhiteSpace(x)||x.Length>128)||names.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=names.Length)throw new ArgumentException("Explicit ordered unique manifest of 1..16 basenames required.");
            }
            if(definition.ResponseMember=="BatchImport")
            {
                if(values["overwrite"]!.GetValue<bool>() || (values["compileAfter"]?.GetValue<bool>() ?? false) || !(values["stopOnImportFailure"]?.GetValue<bool>() ?? true) || !string.IsNullOrEmpty(values["technologyFolderPath"]?.GetValue<string>())) throw new ArgumentException("Batch import supports no overwrite, compileAfter, technology or failure continuation.");
                if(!values["dryRun"]!.GetValue<bool>() && (values["importOrder"]!.AsArray().Count==0 || string.IsNullOrWhiteSpace(values["expectedPlanHash"]!.GetValue<string>()))) throw new ArgumentException("Apply requires importOrder and expectedPlanHash from reviewed preview.");
            }
            if(definition.ResponseMember=="DeviceAdd" && !values["dryRun"]!.GetValue<bool>() && string.IsNullOrWhiteSpace(values["expectedPlanHash"]!.GetValue<string>())) throw new ArgumentException("Creation requires a reviewed expectedPlanHash.");
            if(definition.ResponseMember=="ExternalSourceDelete" && !values["dryRun"]!.GetValue<bool>() && string.IsNullOrWhiteSpace(values["expectedPlanHash"]!.GetValue<string>())) throw new ArgumentException("Delete requires a reviewed expectedPlanHash.");
            if(definition.ResponseMember=="ExternalSourcePlan" && !values["dryRun"]!.GetValue<bool>()) throw new ArgumentException("External-source native apply is blocked; this route is planning only.");
            if(definition.ResponseMember=="ExternalSourceWorkflow" && !values["dryRun"]!.GetValue<bool>() && string.IsNullOrWhiteSpace(values["expectedPlanHash"]!.GetValue<string>())) throw new ArgumentException("External-source execution requires expectedPlanHash from this tool's preview.");
            cancellationToken.ThrowIfCancellationRequested();
            dispatched = true;
            var result=await worker.Call(definition.Operation,values,cancellationToken);
            raw = result?.DeepClone();
            if(definition.ResponseMember is "Connection" or "Projects" or "Bind" or "ProjectMutation" or "ProjectTree") result=V17ProjectEnvelope.Wrap(definition.Name,definition.ResponseMember,result,values["dryRun"]?.GetValue<bool>() ?? false);
            else if(definition.ResponseMember=="HardwareCatalog") result=HardwareCatalogContract.Validate(result,values);
            else if(definition.ResponseMember=="Disconnect") result=DisconnectContract.Validate(result);
            else if(definition.ResponseMember=="DeviceAdd") result=DeviceAddContract.Validate(result,values);
            else if(definition.ResponseMember=="ExternalSourceDelete") result=ExternalSourceDeleteContract.Validate(result,values);
            else if(definition.ResponseMember=="ExternalSourcePlan") result=ExternalSourcePlanContract.ValidateRequest(values,result);
            else if(definition.ResponseMember=="ExternalSourceWorkflow") result=ExternalSourceWorkflowContract.Validate(definition.Operation,values,result);
            else if(definition.ResponseMember=="BatchDocumentExport") result=BatchDocumentExportContract.Validate(result,values);
            else if(definition.ResponseMember=="BatchDocumentImport") result=BatchDocumentImportContract.Validate(result,values);
            else if(definition.ResponseMember=="DocumentImport") result=DocumentImportContract.Validate(result,values);
            else if(definition.ResponseMember=="DocumentExport") result=DocumentExportContract.Validate(result,values["dryRun"]!.GetValue<bool>());
            else if(definition.ResponseMember=="SpecialExport") result=SpecialExportContract.Validate(result,values["dryRun"]!.GetValue<bool>());
            else if(definition.ResponseMember=="BatchImport") result=BatchImportContract.Validate(result,values["dryRun"]!.GetValue<bool>());
            else if(definition.ResponseMember=="BatchExport") result=BatchExportContract.Validate(result,values["dryRun"]!.GetValue<bool>());
            else if(definition.ResponseMember=="Compile") result=V17CompileEnvelope.Wrap(definition.Name,result,values["dryRun"]!.GetValue<bool>());
            else if(definition.ResponseMember is "Mutation" or "ExportFile") result=V17MutationEnvelope.Wrap(definition.Name,definition.ResponseMember,result,values["dryRun"]!.GetValue<bool>());
            else if(definition.ResponseMember=="RuntimeQuery") result=RuntimeQueryContract.Validate(definition.Name,result,values["processId"]?.GetValue<int>());
            else if(definition.ResponseMember=="Declarations") result=DeclarationReadContract.Validate(definition.Name,values["table"]!.GetValue<string>(),result);
            else if(definition.ResponseMember=="SupplementaryRead") result=SupplementaryReadContract.Wrap(definition.Name,result,McpJsonUtilities.DefaultOptions);
            else if(definition.ResponseMember=="SoftwareRead") result=SoftwareReadContract.Wrap(definition.Name,result,McpJsonUtilities.DefaultOptions);
            else if(definition.ResponseMember!=null) result=V17ReadEnvelope.Wrap(definition.Name,definition.ResponseMember,result);
            if (release != null) return FoundationV4Result.Worker(release, definition, id!, values, raw, result);
            return new CallToolResult { Content=new List<ContentBlock>{new TextContentBlock { Text=result?.ToJsonString() ?? "null" }} };
        }
        catch (Exception ex) when (release != null)
        { return FoundationV4Result.Failure(release, FoundationV4Tool.Name(definition.Name), id!, dispatched, FoundationV4Result.IsMutation(definition, values), raw, ex); }
        catch(OperationCanceledException) { throw; }
        catch(McpException) { throw; }
        catch(Exception ex) when(definition.ResponseMember!=null)
        { throw new McpException(ex.Message,ex,ex is ArgumentException || ex is WorkerOperationException { Code:-32602 } ? McpErrorCode.InvalidParams : McpErrorCode.InternalError); }
        catch(Exception ex) { return new CallToolResult { IsError=true,Content=new List<ContentBlock>{new TextContentBlock { Text=ex.Message }} }; }
    }
}
