using System;
using System.Collections.Generic;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class SivarcTools
    {
        private readonly SivarcService _sivarc;

        public SivarcTools(SivarcService sivarc) => _sivarc = sivarc;

        [McpServerTool(Name="GetSivarcRuleTree"), Description("[L2][HMI][READ] Typed SiVArc rule hierarchy of one family: category screens / tags / advancedTags / alarms / copies / textLists (Sivarc.ScreenRules ... TextlistRules). Without tablePath: the folder subtree from folderPath (user folders and rule tables with isDefault, counts and the connected library type version). With tablePath (Folder/Table): the table's top-level rules paged as records plus nested rule groups up to maxDepth (200 rules per group). Rule rows are typed per family (condition, operator, enabled, layout field, loop count, tag table, folder structure, program block / library references). Answers NotSupported without the SiVArc option. Never generates. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult GetSivarcRuleTreeV4(
            [Description("category: screens | tags | advancedTags | alarms | copies | textLists.")] string category,
            string folderPath="",
            string tablePath="",
            [Description("includeRules: true also returns the rules of each table.")] bool includeRules=true,
            int maxDepth=4,
            int offset=0,
            int limit=200)
            => LibraryToolContract.Run("GetSivarcRuleTree", false, true, () =>
            {
                LibraryToolContract.Check(() => { SivarcLogic.ValidateTreeRequest(category, folderPath, tablePath, maxDepth, offset, limit); });
                return ReadSivarcRuleTree(category, folderPath, tablePath, includeRules, maxDepth, offset, limit);
            });

        internal ResponseMessage ReadSivarcRuleTree(
            string category,
            string folderPath="",
            string tablePath="",
            bool includeRules=true,
            int maxDepth=4,
            int offset=0,
            int limit=200)
        => _sivarc.ReadSivarcRuleTree(category,folderPath,tablePath,includeRules,maxDepth,offset,limit);
        [McpServerTool(Name="ManageSivarcRuleContainer"), Description("[L2][HMI][WRITE] SiVArc rule folders and rule tables of one family: kind folder (*RuleFolderComposition.Create / Delete, empty folders only) or table (*RuleTableComposition.Create(name), createFromType with typePath + typeVersion of a *RuleTableType in the project or an open global library (libraryName), Delete of non-default tables). path is Folder/Sub/Name relative to the family's system folder; read returns the typed row. delete needs confirmDelete=true when dryRun=false. Default preview; real edits need the SiVArc licence; never generates or saves. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageSivarcRuleContainerV4(
            string category,
            [Description("kind: folder | table.")] string kind,
            [Description("path: exact path of the object ('Folder/Name').")] string path,
            [Description("action: the operation to perform - read | create | createFromType | delete.")] string action="read",
            string libraryName="",
            string typePath="",
            [Description("typeVersion: version string of the rule table type ('' = default).")] string typeVersion="",
            bool confirmDelete=false,
            bool dryRun=true)
            => LibraryToolContract.Run("ManageSivarcRuleContainer", !dryRun && !(action == "read"), true, () =>
            {
                LibraryToolContract.Check(() => { SivarcLogic.ValidateContainerRequest(category, kind, path, action, typePath, typeVersion, confirmDelete, dryRun); });
                return ManageSivarcRuleContainer(category, kind, path, action, libraryName, typePath, typeVersion, confirmDelete, dryRun);
            });

        internal ResponseMessage ManageSivarcRuleContainer(
            string category,
            string kind,
            string path,
            string action="read",
            string libraryName="",
            string typePath="",
            string typeVersion="",
            bool confirmDelete=false,
            bool dryRun=true)
        => _sivarc.ManageSivarcRuleContainer(category,kind,path,action,libraryName,typePath,typeVersion,confirmDelete,dryRun);
        [McpServerTool(Name="ManageSivarcTableRule"), Description("[L2][HMI][WRITE] One SiVArc rule or rule group (kind rule / group) inside a rule table (typed; the older path-based ManageSivarcRule supports arbitrary sub-paths): rulePath is Group/Sub/Name relative to tablePath. Actions read, create (Create(name)), createFromMasterCopy (CreateFrom(MasterCopy, CreateOptions Replace|Rename) with masterCopyPath in the project or an open global library; rulePath is then the target group path, empty = the table), update, delete (confirmDelete when real). properties holds typed scalars (Name, Comment, Condition, ConditionOperator None|And|Equal|..., Enabled, screens: LayoutField / LoopCount, tags: TagGroupHierarchy / TagTable, copies: FolderStructure). references assigns library objects or PLC blocks: {ProgramBlock:{kind:plcBlock,softwarePath,path} | {kind:masterCopy|libraryType|masterCopyFolder|typeFolder,path,libraryName}, LibraryScreen, ScreenObjectLibraryItem, TagLibraryItem, AlarmLibraryItem, TextlistLibraryItem, LibraryObject; null is passed through, but TIA refuses it for ProgramBlock ('may not be null', 2.7.39 real project) - assign another block instead}. deviceSelection {PLC_1:true, HMI_RT_1:false} writes the PLC / HMI device columns (SetAttributes), deviceNames reads them. Screen rules also report GetLayoutFields(). Default preview; never generates or saves. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageSivarcTableRuleV4(
            string category,
            string tablePath,
            [Description("rulePath: 'Table/Group/Rule' path of the rule.")] string rulePath="",
            [Description("kind: rule | group.")] string kind="rule",
            [Description("action: the operation to perform - read | create | createFromMasterCopy | update | delete.")] string action="read",
            AttributeMap<Scalar> properties = null!,
            [Description("references: JSON object of library references for the rule (see the tool description).")] Dictionary<string, SivarcReference?> references = null!,
            [Description("deviceSelection: JSON object selecting PLC / HMI devices for the rule.")] Dictionary<string, bool> deviceSelection = null!,
            [Description("deviceNames: JSON array of device names.")] string[] deviceNames = null!,
            string libraryName="",
            string masterCopyPath="",
            [Description("createOption: Replace | Rename.")] string createOption="Replace",
            bool confirmDelete=false,
            bool dryRun=true)
            => LibraryToolContract.Run("ManageSivarcTableRule", !dryRun && !(action == "read"), true, () =>
            {
                string propertiesJson = V4Json.Serialize(properties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>()));
                string referencesJson = V4Json.Serialize(references ?? new Dictionary<string, SivarcReference?>());
                string deviceSelectionJson = V4Json.Serialize(deviceSelection ?? new Dictionary<string, bool>());
                string deviceNamesJson = V4Json.Serialize(deviceNames ?? Array.Empty<string>());
                LibraryToolContract.Check(() => { SivarcLogic.ValidateRuleRequest(category, tablePath, rulePath, kind, action, propertiesJson, referencesJson, deviceSelectionJson, masterCopyPath, createOption, confirmDelete, dryRun); SivarcLogic.ParseNames(deviceNamesJson, "deviceNames"); });
                return ManageSivarcTableRule(category, tablePath, rulePath, kind, action, propertiesJson, referencesJson, deviceSelectionJson, deviceNamesJson, libraryName, masterCopyPath, createOption, confirmDelete, dryRun);
            });

        internal ResponseMessage ManageSivarcTableRule(
            string category,
            string tablePath,
            string rulePath="",
            string kind="rule",
            string action="read",
            string propertiesJson="{}",
            string referencesJson="{}",
            string deviceSelectionJson="{}",
            string deviceNamesJson="[]",
            string libraryName="",
            string masterCopyPath="",
            string createOption="Replace",
            bool confirmDelete=false,
            bool dryRun=true)
        => _sivarc.ManageSivarcTableRule(category,tablePath,rulePath,kind,action,propertiesJson,referencesJson,deviceSelectionJson,deviceNamesJson,libraryName,masterCopyPath,createOption,confirmDelete,dryRun);
        [McpServerTool(Name="ListSivarcBlockDefinitions"), Description("[L2][PLC-Software][READ] SiVArc data of one code block (blockPath Folder/Block under the PLC block group) through the SivarcDataProvider block service: tag definitions (name / value / comment), text definitions (name / expression / comment / multilingual text) and the V21 tag member settings (UseCommonConfiguration, CommonParameters, BlockParameters with acquisition cycle / mode). Answers NotSupported without the SiVArc option. Read-only. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ListSivarcBlockDefinitionsV4(
            string softwarePath,
            string blockPath,
            [Description("includeBlockParameters: true also returns block parameters.")] bool includeBlockParameters=true)
            => LibraryToolContract.Run("ListSivarcBlockDefinitions", false, true, () =>
            {
                return ReadSivarcBlockDefinitions(softwarePath, blockPath, includeBlockParameters);
            });

        internal ResponseMessage ReadSivarcBlockDefinitions(
            string softwarePath,
            string blockPath,
            bool includeBlockParameters=true)
        => _sivarc.ReadSivarcBlockDefinitions(softwarePath,blockPath,includeBlockParameters);
        [McpServerTool(Name="ManageSivarcBlockDefinition"), Description("[L2][PLC-Software][WRITE] One SiVArc definition of a code block: kind tagDefinition (Name / Value / Comment; create / update / delete), textDefinition (Name / Expression / Comment plus texts {de-DE: text} per project language; create / update / delete), tagMemberSettings (UseCommonConfiguration; update), commonParameters or blockParameter=name (AcquisitionCycle, AcquisitionMode None|CyclicContinuous|CyclicInOperation|OnDemand, Comment; update; V21). delete needs confirmDelete when real. Default preview; real edits need the SiVArc licence; never saves. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageSivarcBlockDefinitionV4(
            string softwarePath,
            string blockPath,
            [Description("kind: tagDefinition | textDefinition | tagMemberSettings | commonParameters | blockParameter.")] string kind,
            string name="",
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            AttributeMap<Scalar> properties = null!,
            [Description("texts: JSON object language tag -> text.")] AttributeMap<string> texts = null!,
            bool confirmDelete=false,
            bool dryRun=true)
            => LibraryToolContract.Run("ManageSivarcBlockDefinition", !dryRun && !(action == "read"), true, () =>
            {
                string propertiesJson = V4Json.Serialize(properties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>()));
                string textsJson = V4Json.Serialize(texts ?? new AttributeMap<string>(new Dictionary<string, string>()));
                LibraryToolContract.Check(() => { SivarcLogic.ValidateDefinitionRequest(blockPath, kind, name, action, propertiesJson, textsJson, confirmDelete, dryRun); });
                return ManageSivarcBlockDefinition(softwarePath, blockPath, kind, name, action, propertiesJson, textsJson, confirmDelete, dryRun);
            });

        internal ResponseMessage ManageSivarcBlockDefinition(
            string softwarePath,
            string blockPath,
            string kind,
            string name="",
            string action="read",
            string propertiesJson="{}",
            string textsJson="{}",
            bool confirmDelete=false,
            bool dryRun=true)
        => _sivarc.ManageSivarcBlockDefinition(softwarePath,blockPath,kind,name,action,propertiesJson,textsJson,confirmDelete,dryRun);
        [McpServerTool(Name="ResolveSivarcExpression"), Description("[L2][HMI][READ] Resolve a SiVArc expression (Block.DB.SymbolicName & HmiApplication.Type & LibraryObject.FolderPath ...) for every instance of one code block in the compiled PLC call structure: Sivarc.GetExpressionResolver(codeBlock, hmiDeviceItem, libraryItem).Resolve(expression). devicePath / itemPath name the HMI device item, libraryItemKind masterCopy | libraryType with libraryItemPath (project library or open global library via libraryName). Rows: instanceName, callPath, result (paged by maxResults). Requires a compiled PLC and the SiVArc option; read-only. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ResolveSivarcExpressionV4(
            string softwarePath,
            string blockPath,
            string[] devicePath,
            string[] itemPath,
            [Description("libraryItemKind: masterCopy | libraryType.")] string libraryItemKind,
            [Description("libraryItemPath: 'Folder/Name' of the library item the expression refers to.")] string libraryItemPath,
            [Description("expression: the SiVArc expression to resolve.")] string expression,
            string libraryName="",
            [Description("maxResults: cap on results returned.")] int maxResults=500)
            => LibraryToolContract.Run("ResolveSivarcExpression", false, true, () =>
            {
                string devicePathJson = V4Json.Serialize(devicePath);
                string itemPathJson = V4Json.Serialize(itemPath);
                LibraryToolContract.Check(() => { SivarcLogic.ValidateExpressionRequest(blockPath, devicePathJson, itemPathJson, libraryItemKind, libraryItemPath, expression, maxResults); });
                return ResolveSivarcExpression(softwarePath, blockPath, devicePathJson, itemPathJson, libraryItemKind, libraryItemPath, expression, libraryName, maxResults);
            });

        internal ResponseMessage ResolveSivarcExpression(
            string softwarePath,
            string blockPath,
            string devicePathJson,
            string itemPathJson,
            string libraryItemKind,
            string libraryItemPath,
            string expression,
            string libraryName="",
            int maxResults=500)
        => _sivarc.ResolveSivarcExpression(softwarePath,blockPath,devicePathJson,itemPathJson,libraryItemKind,libraryItemPath,expression,libraryName,maxResults);
        [McpServerTool(Name="ManageSivarcScreenLayout"), Description("[L2][HMI][WRITE] SiVArc layout fields of one screen (classic WinCC Screen or Unified HmiScreen) through the V21 LayoutData screen service: action export writes the YML file (must not exist; verified by size and SHA-256), import applies a YML file and returns state / numberOfLayouts. screenName is an exact name or an absolute /Group/Screen path. Import defaults to preview; NotSupported on V20 or without the SiVArc option; never saves. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageSivarcScreenLayoutV4(
            string softwarePath,
            [Description("screenName: exact screen name.")] string screenName,
            [Description("action: the operation to perform - export | import.")] string action,
            [Description("filePath: absolute YML (.yml) layout file; new output or existing import file.")] string filePath,
            bool dryRun=true)
            => LibraryToolContract.Run("ManageSivarcScreenLayout", action == "export" || !dryRun, true, () =>
            {
                LibraryToolContract.Check(() => { SivarcLogic.ValidateLayoutRequest(screenName, action, filePath, dryRun); });
                return ManageSivarcScreenLayout(softwarePath, screenName, action, filePath, dryRun);
            });

        internal ResponseMessage ManageSivarcScreenLayout(
            string softwarePath,
            string screenName,
            string action,
            string filePath,
            bool dryRun=true)
        => _sivarc.ManageSivarcScreenLayout(softwarePath,screenName,action,filePath,dryRun);
        [McpServerTool(Name="UpgradeSivarcDefinitions"), Description("[L2][PLC-Software][WRITE] Upgrade the SiVArc definitions of one PLC (SivarcDefinitionsUpgrader.Upgrade on the PlcSoftware: legacy SIVARCCOND / SIVARCTEXT functions become tag / text definitions in the block plug-ins). Returns warningCount and the recursive feedback messages. Default preview; NotSupported without the SiVArc option; never saves. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult UpgradeSivarcDefinitionsV4(string softwarePath, bool dryRun=true)
            => LibraryToolContract.Run("UpgradeSivarcDefinitions", !dryRun, true, () =>
            {
                return UpgradeSivarcDefinitions(softwarePath, dryRun);
            });

        internal ResponseMessage UpgradeSivarcDefinitions(string softwarePath, bool dryRun=true)
        => _sivarc.UpgradeSivarcDefinitions(softwarePath,dryRun);
        [McpServerTool(Name="GenerateSivarc"), Description("[L2][HMI][WRITE] Native SiVArc generation (typed Sivarc.Generate) for one exact HMI device plus optional additionalHmiDeviceNames (multi-device overload) and explicit PLC paths. generationOptions are official GenerationOptions flags joined with | (AllTags|FullGeneration; None = project settings). Default preview; can create/replace HMI objects according to rules. plcSoftwarePaths names the PLC software; the native call is tried with the software names and then with the owning device names (SiVArc only knows PLCs connected to the HMI device - the 2.7.39 real project without an HMI connection answered 'PLC device not found' for both). Returns the typed SivarcGenerationResult with recursive feedback messages. No automatic save/compile/download. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult GenerateSivarcV4(
            [Description("hmiDeviceName: exact HMI device name to generate for.")] string hmiDeviceName,
            [Description("plcSoftwarePaths: JSON array of PLC software paths included in the generation.")] string[] plcSoftwarePaths,
            [Description("generationOptions: SiVArc generation option name ('' = default).")] string generationOptions,
            bool dryRun=true,
            [Description("additionalHmiDeviceNames: JSON array of further HMI device names.")] string[] additionalHmiDeviceNames = null!)
            => LibraryToolContract.Run("GenerateSivarc", !dryRun, true, () =>
            {
                string plcSoftwarePathsJson = V4Json.Serialize(plcSoftwarePaths);
                string additionalHmiDeviceNamesJson = V4Json.Serialize(additionalHmiDeviceNames ?? Array.Empty<string>());
                LibraryToolContract.Check(() => { SivarcLogic.ParseNames(plcSoftwarePathsJson, "plcSoftwarePaths"); SivarcLogic.ParseNames(additionalHmiDeviceNamesJson, "additionalHmiDeviceNames"); SivarcLogic.ParseGenerationOptions(generationOptions); });
                return GenerateSiVArc(hmiDeviceName, plcSoftwarePathsJson, generationOptions, dryRun, additionalHmiDeviceNamesJson);
            });

        internal ResponseMessage GenerateSiVArc(
            string hmiDeviceName,
            string plcSoftwarePathsJson,
            string generationOptions,
            bool dryRun=true,
            string additionalHmiDeviceNamesJson="[]")
        => _sivarc.GenerateSiVArc(hmiDeviceName,plcSoftwarePathsJson,generationOptions,dryRun,additionalHmiDeviceNamesJson);
    }
}
