using System;
using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    // 2.7.58: descriptions for the parameters that recur across the roster. 1488 of 2204 parameters carried no
    // [Description] (213 tools) - most of them the same forty names: softwarePath, devicePathJson, dryRun, offset,
    // limit, confirmDelete ... A parameter without a description is invisible to the schema hints, the preflight and
    // the derived examples, and the model has to guess its meaning. This vocabulary fills the gap generically; a
    // hand-written [Description] on the parameter always wins (SpecsOf uses the vocabulary only when none exists),
    // and the build statistics still count the source-level gap so it keeps shrinking. Zero dependencies.
    public static class ParameterVocabulary
    {
        private static readonly Dictionary<string, string> Texts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dryRun"] = "dryRun: true (default) previews and changes nothing; false executes (a tool with a confirm* flag also needs that flag).",
            ["confirmDelete"] = "confirmDelete: must be true together with dryRun=false to delete; keep false until the user agreed to that deletion.",
            ["confirmChange"] = "confirmChange: must be true together with dryRun=false to apply the change; keep false until the user agreed.",
            ["confirmWrite"] = "confirmWrite: must be true together with dryRun=false to write; keep false until the user agreed.",
            ["confirmRun"] = "confirmRun: must be true together with dryRun=false to execute; keep false until the user agreed.",
            ["confirmUpload"] = "confirmUpload: must be true together with dryRun=false to upload into the project; keep false until the user agreed.",
            ["confirmInstanceChange"] = "confirmInstanceChange: must be true together with dryRun=false to change the PLCSIM Advanced instance.",
            ["softwarePath"] = "softwarePath: the PLC or HMI software path exactly as GetProjectTree shows it (e.g. 'PLC_1', 'HMI_RT_1'); the software name, not the station name.",
            ["targetSoftwarePath"] = "targetSoftwarePath: the target PLC software path from GetProjectTree (e.g. 'PLC_1').",
            ["hmiSoftwarePath"] = "hmiSoftwarePath: the HMI software path from GetProjectTree (e.g. 'HMI_RT_1').",
            ["expectedProject"] = "expectedProject: the name of the project this call must run in (GetProject); the call is refused when another project is bound.",
            ["expectedToken"] = "expectedToken: the token a preceding dryRun=true call returned; passing it proves the same change is being applied.",
            ["devicePathJson"] = "devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. [\"PLC_1\"] (the array itself or its JSON text).",
            ["itemPathJson"] = "itemPathJson: JSON array of exact device-item names below the station, e.g. [\"PROFINET interface_1\"]; [] means the station itself (or its CPU where the tool says so).",
            ["destinationDevicePathJson"] = "destinationDevicePathJson: JSON array naming the destination station, like devicePathJson.",
            ["destinationItemPathJson"] = "destinationItemPathJson: JSON array of exact device-item names at the destination, like itemPathJson.",
            ["partnerDevicePathJson"] = "partnerDevicePathJson: JSON array naming the partner station, like devicePathJson.",
            ["partnerItemPathJson"] = "partnerItemPathJson: JSON array of exact device-item names on the partner, like itemPathJson.",
            ["localInterfaceItemPathJson"] = "localInterfaceItemPathJson: JSON array path to the local interface item, e.g. [\"PLC_1\",\"PROFINET interface_1\"].",
            ["partnerInterfaceItemPathJson"] = "partnerInterfaceItemPathJson: JSON array path to the partner's interface item.",
            ["objectPathJson"] = "objectPathJson: JSON array path of the object inside the software, e.g. [\"Group\",\"Name\"].",
            ["groupPathJson"] = "groupPathJson: JSON array path of the group, e.g. [\"Folder\",\"Subfolder\"]; [] = the root.",
            ["propertiesJson"] = "propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names).",
            ["attributesJson"] = "attributesJson: JSON object attribute name -> value to write.",
            ["promptAnswersJson"] = "promptAnswersJson: JSON object of explicit answers to TIA prompts by prompt type name.",
            ["filePath"] = "filePath: full path of the file on the TIA machine (engine host), e.g. 'C:\\Temp\\file.xml'.",
            ["importPath"] = "importPath: full path of the file (or folder where the tool says so) on the TIA machine to import from.",
            ["exportPath"] = "exportPath: full path on the TIA machine to write to; the response reports what was written.",
            ["outputPath"] = "outputPath: full file path on the TIA machine to write.",
            ["directory"] = "directory: folder on the TIA machine.",
            ["destinationDirectory"] = "destinationDirectory: folder on the TIA machine that receives the output.",
            ["folderPath"] = "folderPath: group / folder path inside the software ('' = root).",
            ["groupPath"] = "groupPath: group path inside the software, e.g. 'Group/Subgroup' ('' = root).",
            ["targetGroupPath"] = "targetGroupPath: group path that receives the result ('' = root).",
            ["blockPath"] = "blockPath: 'Group/Subgroup/Name' of the block as GetSoftwareTree shows it; a root block is its bare name.",
            ["typePath"] = "typePath: 'Group/Name' of the PLC data type as GetSoftwareTree shows it.",
            ["objectPath"] = "objectPath: path of the object inside the software ('Group/Name').",
            ["tablePath"] = "tablePath: 'Group/Table' path of the table as the read tools show it.",
            ["chartPath"] = "chartPath: 'Folder/Chart' path of the chart.",
            ["screenPath"] = "screenPath: screen path inside the HMI, e.g. '/Group/Main'.",
            ["masterCopyPath"] = "masterCopyPath: 'Folder/Name' of the master copy in the library.",
            ["libraryName"] = "libraryName: name of the global library ('' = the project library).",
            ["targetLibraryName"] = "targetLibraryName: name of the target global library ('' = the project library).",
            ["name"] = "name: exact name of the object (case-sensitive).",
            ["newName"] = "newName: the new name (rename actions).",
            ["itemName"] = "itemName: exact name of the item.",
            ["unitName"] = "unitName: software unit name when the object lives in a unit ('' = the PLC program itself).",
            ["unitKind"] = "unitKind: which unit collection unitName refers to - unit or safety (read tools also accept all).",
            ["password"] = "password: password when the object is protected; never logged.",
            ["culture"] = "culture: language tag such as 'en-US' or 'zh-CN'.",
            ["offset"] = "offset: index of the first item to return (paging), 0 = beginning.",
            ["limit"] = "limit: maximum number of items to return (paging).",
            ["pageSize"] = "pageSize: items per page.",
            ["cursor"] = "cursor: continuation token from the previous page ('' = first page).",
            ["maxDepth"] = "maxDepth: how deep to walk the tree.",
            ["maxItems"] = "maxItems: cap on items returned.",
            ["budgetMs"] = "budgetMs: time budget in milliseconds for the walk.",
            ["version"] = "version: catalog / firmware version string, e.g. 'V2.9'.",
            ["family"] = "family: device family hint, e.g. 'S7-1200', 'S7-1500', 'WinCCUnifiedPC'.",
            ["pgPcInterface"] = "pgPcInterface: PG/PC interface name substring, e.g. 'PLCSIM' (ReadTransferRoutes / ScanAccessibleDevices list them).",
            ["subnetName"] = "subnetName: subnet name, e.g. 'PN/IE_1'.",
            ["driveObjectNumber"] = "driveObjectNumber: drive object number (DO) inside the drive unit.",
            ["driveObjectIndex"] = "driveObjectIndex: 0-based index of the drive object inside the drive unit.",
            ["sensorIndex"] = "sensorIndex: 0-based encoder / sensor index.",
            ["propertyName"] = "propertyName: exact name of the property.",
            ["includeIdentical"] = "includeIdentical: true also lists objects that compare equal.",
            ["action"] = "action: the operation to perform - the tool description lists the accepted values (case-sensitive); 'read' is the safe one where offered.",
            ["kind"] = "kind: the object kind - the tool description lists the accepted values (case-sensitive).",
            ["objectKind"] = "objectKind: the object kind - the tool description lists the accepted values.",
            ["category"] = "category: the category - the tool description lists the accepted values.",
            ["mode"] = "mode: the mode - the tool description lists the accepted values.",
            ["itemType"] = "itemType: the item type - the tool description lists the accepted values.",
            ["eventType"] = "eventType: HmiButtonEventType value, e.g. Down (press), Up (release), Tapped.",
            ["connectionType"] = "connectionType: the connection type - the tool description lists the accepted values.",
            ["importOption"] = "importOption: import option name (Override replaces objects of the same name; the tool description lists the values).",
            ["importOptions"] = "importOptions: import option name (Override replaces objects of the same name; the tool description lists the values).",
            ["copyMode"] = "copyMode: how to copy - the tool description lists the accepted values.",
            ["generateOption"] = "generateOption: generate option name - the tool description lists the accepted values.",
            ["targetKind"] = "targetKind: the target kind - the tool description lists the accepted values.",
        };

        /// <summary>A description for a parameter name the roster uses everywhere, or null when the name is not in the vocabulary.</summary>
        public static string? Describe(string name)
            => name != null && Texts.TryGetValue(name, out var text) ? text : null;

        public static IReadOnlyCollection<string> Names => Texts.Keys;
    }
}
