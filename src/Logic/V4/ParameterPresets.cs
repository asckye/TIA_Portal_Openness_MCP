using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Logic.V4
{
    public static class ParameterPresets
    {
        public const string ConditionalSchemaComment = "Server preset applies to PLC software targets.";
        public static IReadOnlyDictionary<string, string> Empty { get; } =
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

        // Every software selector in the eight contracts has a reviewed entry, including explicit HMI exclusions.
        public static IReadOnlyDictionary<string, string> SoftwareParameters { get; } = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["AnalyzePlcCrossReferences.softwarePath"] = "PLC",
            ["BuildAndImportPlcArtifact.softwarePath"] = "PLC",
            ["CheckDownloadReadiness.softwarePath"] = "PLC",
            ["ComparePlcBlockDocuments.softwarePath"] = "PLC",
            ["CompareProjects.softwarePath"] = "PLC",
            ["CompareSoftwareToOnline.softwarePath"] = "PLC",
            ["CompileHmiDiagnostics.softwarePath"] = "HMI software; not a PLC target.",
            ["CompilePlcDiagnostics.softwarePath"] = "PLC",
            ["CompilePlcSoftware.softwarePath"] = "PLC",
            ["ConfigureMotionHardwareConnection.softwarePath"] = "PLC",
            ["ConnectOnlinePlc.softwarePath"] = "PLC",
            ["CreateLibraryMasterCopy.softwarePath"] = "sourceKind",
            ["CreatePlcBlockGroup.softwarePath"] = "PLC",
            ["CreatePlcInstanceDb.softwarePath"] = "PLC",
            ["CreatePlcTag.plc"] = "PLC",
            ["CreatePlcTagTable.plc"] = "PLC",
            ["CreatePlcTypeGroup.softwarePath"] = "PLC",
            ["CreatePlcUserConstant.plc"] = "PLC",
            ["DeleteEmptyPlcBlockGroup.softwarePath"] = "PLC",
            ["DeleteEmptyUnifiedHmiScreenGroup.softwarePath"] = "HMI software; not a PLC target.",
            ["DeleteHmiTag.softwarePath"] = "HMI software; not a PLC target.",
            ["DeletePlcBlock.softwarePath"] = "PLC",
            ["DeletePlcExternalSource.softwarePath"] = "PLC",
            ["DeletePlcTagTable.softwarePath"] = "PLC",
            ["DeletePlcType.softwarePath"] = "PLC",
            ["DeleteUnifiedHmiButtonEvent.softwarePath"] = "HMI software; not a PLC target.",
            ["DeleteUnifiedHmiDynamization.softwarePath"] = "HMI software; not a PLC target.",
            ["DescribeHmiScreen.softwarePath"] = "HMI software; not a PLC target.",
            ["DescribeHmiScreenItem.softwarePath"] = "HMI software; not a PLC target.",
            ["DescribeHmiSoftware.softwarePath"] = "HMI software; not a PLC target.",
            ["DescribeHmiTag.softwarePath"] = "HMI software; not a PLC target.",
            ["DescribeHmiTagTable.softwarePath"] = "HMI software; not a PLC target.",
            ["DescribeObject.softwarePath"] = "objectKind",
            ["DescribeObjectProperty.softwarePath"] = "objectKind",
            ["DescribePlcBlockLogic.softwarePath"] = "PLC",
            ["DescribeService.softwarePath"] = "objectKind",
            ["DisconnectOnlinePlc.softwarePath"] = "PLC",
            ["DownloadPlc.softwarePath"] = "PLC",
            ["DownloadPlcToFolder.softwarePath"] = "PLC",
            ["ExchangeCfcCharts.softwarePath"] = "PLC",
            ["ExchangeMotionCamData.softwarePath"] = "PLC",
            ["ExchangePlcAlarmTextLists.softwarePath"] = "PLC",
            ["ExchangePlcSupervisions.softwarePath"] = "PLC",
            ["ExchangeUnifiedScriptModules.softwarePath"] = "HMI software; not a PLC target.",
            ["ExchangeUnifiedTags.softwarePath"] = "HMI software; not a PLC target.",
            ["ExportAlarmClasses.softwarePath"] = "PLC",
            ["ExportAlarmInstanceTexts.softwarePath"] = "PLC",
            ["ExportAlarmTextLists.softwarePath"] = "PLC",
            ["ExportHmiConnection.softwarePath"] = "HMI software; not a PLC target.",
            ["ExportHmiProgram.softwarePath"] = "HMI software; not a PLC target.",
            ["ExportHmiScreen.softwarePath"] = "HMI software; not a PLC target.",
            ["ExportHmiTagTable.softwarePath"] = "HMI software; not a PLC target.",
            ["ExportOpcUaInterface.softwarePath"] = "PLC",
            ["ExportPlcBlock.softwarePath"] = "PLC",
            ["ExportPlcBlockDocuments.softwarePath"] = "PLC",
            ["ExportPlcBlocks.softwarePath"] = "PLC",
            ["ExportPlcBlocksDocuments.softwarePath"] = "PLC",
            ["ExportPlcProDiagInfo.softwarePath"] = "PLC",
            ["ExportPlcTagTable.softwarePath"] = "PLC",
            ["ExportPlcTagTables.softwarePath"] = "PLC",
            ["ExportPlcType.softwarePath"] = "PLC",
            ["ExportPlcTypes.softwarePath"] = "PLC",
            ["ExportPlcWatchTable.softwarePath"] = "PLC",
            ["ExportPlcWatchTablesToDirectory.softwarePath"] = "PLC",
            ["ExportSafetyPrintout.softwarePath"] = "PLC",
            ["ExportScadaData.softwarePath"] = "PLC",
            ["ExportTechnologyObject.softwarePath"] = "PLC",
            ["ExportTechnologyObjectsToDirectory.softwarePath"] = "PLC",
            ["ExportUnifiedEngineeringList.softwarePath"] = "HMI software; not a PLC target.",
            ["GenerateBlocksFromExternalSource.softwarePath"] = "PLC",
            ["GenerateOpcUaModelledInterface.softwarePath"] = "PLC",
            ["GeneratePlcLoadableFile.softwarePath"] = "PLC",
            ["GeneratePlcSourceFromBlocks.softwarePath"] = "PLC",
            ["GetClassicHmiGlobalization.softwarePath"] = "HMI software; not a PLC target.",
            ["GetClassicHmiScreenTree.softwarePath"] = "HMI software; not a PLC target.",
            ["GetHmiProgramInfo.softwarePath"] = "HMI software; not a PLC target.",
            ["GetHmiScreenSnapshot.softwarePath"] = "HMI software; not a PLC target.",
            ["GetMotionAxisConfiguration.softwarePath"] = "PLC",
            ["GetObjectIdentifier.softwarePath"] = "plcKind",
            ["GetObjectProperty.softwarePath"] = "objectKind",
            ["GetOnlineState.softwarePath"] = "PLC",
            ["GetOpcUaAccessControl.softwarePath"] = "PLC",
            ["GetPlcBlockEditCapabilities.softwarePath"] = "PLC",
            ["GetPlcBlockFingerprints.softwarePath"] = "PLC",
            ["GetPlcBlockHierarchy.softwarePath"] = "PLC",
            ["GetPlcBlockInfo.softwarePath"] = "PLC",
            ["GetPlcBlockScopes.softwarePath"] = "PLC",
            ["GetPlcChecksums.softwarePath"] = "PLC",
            ["GetPlcCrossReferences.softwarePath"] = "PLC",
            ["GetPlcObjectFingerprints.softwarePath"] = "PLC",
            ["GetPlcOpcUaConfiguration.softwarePath"] = "PLC",
            ["GetPlcTagTableConstants.softwarePath"] = "PLC",
            ["GetPlcTypeInfo.softwarePath"] = "PLC",
            ["GetPlcWatchTableCurrentValuesReadOnly.softwarePath"] = "PLC",
            ["GetSafetyBlockSignatures.softwarePath"] = "PLC",
            ["GetSoftwareInfo.softwarePath"] = "PLC",
            ["GetSoftwareTree.softwarePath"] = "PLC",
            ["GetTechnologyObjectTree.softwarePath"] = "PLC",
            ["GetUnifiedAlarmCommon.softwarePath"] = "HMI software; not a PLC target.",
            ["GetUnifiedAuditSettings.softwarePath"] = "HMI software; not a PLC target.",
            ["GetUnifiedCrossReferences.softwarePath"] = "HMI software; not a PLC target.",
            ["GetUnifiedFaceplateInstance.softwarePath"] = "HMI software; not a PLC target.",
            ["GetUnifiedGlobalScript.softwarePath"] = "HMI software; not a PLC target.",
            ["GetUnifiedGraphicSelection.softwarePath"] = "HMI software; not a PLC target.",
            ["GetUnifiedHmiButtonEvent.softwarePath"] = "HMI software; not a PLC target.",
            ["GetUnifiedHmiDynamization.softwarePath"] = "HMI software; not a PLC target.",
            ["GetUnifiedLibraryType.softwarePath"] = "HMI software; not a PLC target.",
            ["GetUnifiedObjectEvents.softwarePath"] = "HMI software; not a PLC target.",
            ["GetUnifiedObjectProperties.softwarePath"] = "HMI software; not a PLC target.",
            ["GetUnifiedRuntimeSettings.softwarePath"] = "HMI software; not a PLC target.",
            ["GetUnifiedScreenBranch.softwarePath"] = "HMI software; not a PLC target.",
            ["ImportAlarmClasses.softwarePath"] = "PLC",
            ["ImportAlarmTextLists.softwarePath"] = "PLC",
            ["ImportHmiConnection.softwarePath"] = "HMI software; not a PLC target.",
            ["ImportHmiScreen.softwarePath"] = "HMI software; not a PLC target.",
            ["ImportHmiScreensFromDirectory.softwarePath"] = "HMI software; not a PLC target.",
            ["ImportHmiTagTable.softwarePath"] = "HMI software; not a PLC target.",
            ["ImportHmiTagTablesFromDirectory.softwarePath"] = "HMI software; not a PLC target.",
            ["ImportLibraryTypeDocuments.targetSoftwarePath"] = "PLC",
            ["ImportOpcUaInterface.softwarePath"] = "PLC",
            ["ImportPlcAlarmInstanceTexts.softwarePath"] = "PLC",
            ["ImportPlcBlock.softwarePath"] = "PLC",
            ["ImportPlcBlockDocuments.softwarePath"] = "PLC",
            ["ImportPlcBlockVerified.softwarePath"] = "PLC",
            ["ImportPlcBlocksDocuments.softwarePath"] = "PLC",
            ["ImportPlcBlocksFromDirectory.softwarePath"] = "PLC",
            ["ImportPlcExternalSource.softwarePath"] = "PLC",
            ["ImportPlcProgramFromDirectory.softwarePath"] = "PLC",
            ["ImportPlcTagTable.softwarePath"] = "PLC",
            ["ImportPlcTagTablesFromDirectory.softwarePath"] = "PLC",
            ["ImportPlcType.softwarePath"] = "PLC",
            ["ImportPlcWatchTableOffline.softwarePath"] = "PLC",
            ["ImportTechnologyObject.softwarePath"] = "PLC",
            ["ImportTechnologyObjectsFromDirectory.softwarePath"] = "PLC",
            ["ImportUnifiedEngineeringList.softwarePath"] = "HMI software; not a PLC target.",
            ["ImportUnifiedOpcUaAlarms.softwarePath"] = "HMI software; not a PLC target.",
            ["InvokeObject.softwarePath"] = "objectKind",
            ["InvokeService.softwarePath"] = "objectKind",
            ["ListClassicHmiScripts.softwarePath"] = "HMI software; not a PLC target.",
            ["ListHmiConnections.softwarePath"] = "HMI software; not a PLC target.",
            ["ListHmiScreenPaths.softwarePath"] = "HMI software; not a PLC target.",
            ["ListHmiScreens.softwarePath"] = "HMI software; not a PLC target.",
            ["ListHmiTagTables.softwarePath"] = "HMI software; not a PLC target.",
            ["ListHmiTags.softwarePath"] = "HMI software; not a PLC target.",
            ["ListObjectChildren.softwarePath"] = "objectKind",
            ["ListPlcBlocks.softwarePath"] = "PLC",
            ["ListPlcExternalSources.softwarePath"] = "PLC",
            ["ListPlcForceTables.softwarePath"] = "PLC",
            ["ListPlcSoftwareUnits.softwarePath"] = "PLC",
            ["ListPlcSystemConstants.plc"] = "PLC",
            ["ListPlcSystemGroups.softwarePath"] = "PLC",
            ["ListPlcTagTables.softwarePath"] = "PLC",
            ["ListPlcTags.plc"] = "PLC",
            ["ListPlcTypes.softwarePath"] = "PLC",
            ["ListPlcUserConstants.plc"] = "PLC",
            ["ListPlcWatchTables.softwarePath"] = "PLC",
            ["ListSivarcBlockDefinitions.softwarePath"] = "PLC",
            ["ListTechnologyObjects.softwarePath"] = "PLC",
            ["ListTransferRoutes.softwarePath"] = "PLC",
            ["ListUnifiedEngineeringObjects.softwarePath"] = "HMI software; not a PLC target.",
            ["ListUnifiedGlobalScripts.softwarePath"] = "HMI software; not a PLC target.",
            ["ListUnifiedLibraryFolderEntries.softwarePath"] = "HMI software; not a PLC target.",
            ["ListUnifiedTagDefinitions.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageCfcChartProtection.softwarePath"] = "PLC",
            ["ManageClassicHmiCycle.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageClassicHmiFolder.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageClassicHmiGraphic.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageClassicHmiScreenObject.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageClassicHmiScript.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageClassicHmiTextGraphicList.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageDriveTelegrams.softwarePath"] = "driveTelegram",
            ["ManageLibraryMasterCopy.targetSoftwarePath"] = "libraryMasterCopy",
            ["ManageLibraryTypeVersion.targetSoftwarePath"] = "libraryVersion",
            ["ManageMotionAxis.softwarePath"] = "PLC",
            ["ManageOpcUaAccessControl.softwarePath"] = "PLC",
            ["ManageOpcUaInterface.softwarePath"] = "PLC",
            ["ManagePlcAlarmTextList.softwarePath"] = "PLC",
            ["ManagePlcBlockDocuments.softwarePath"] = "PLC",
            ["ManagePlcBlockProtection.softwarePath"] = "PLC",
            ["ManagePlcBlockWriteProtection.softwarePath"] = "PLC",
            ["ManagePlcDataBlockSnapshot.softwarePath"] = "PLC",
            ["ManagePlcDocuments.softwarePath"] = "PLC",
            ["ManagePlcExternalSources.softwarePath"] = "PLC",
            ["ManagePlcSafety.softwarePath"] = "PLC",
            ["ManagePlcSoftwareUnit.softwarePath"] = "PLC",
            ["ManagePlcSupervision.softwarePath"] = "PLC",
            ["ManagePlcTableEntries.softwarePath"] = "PLC",
            ["ManagePlcTagDefinition.softwarePath"] = "PLC",
            ["ManagePlcUserGroup.softwarePath"] = "PLC",
            ["ManageSivarcBlockDefinition.softwarePath"] = "PLC",
            ["ManageSivarcScreenLayout.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageTechnologyObject.softwarePath"] = "PLC",
            ["ManageTestSuiteCase.softwarePath"] = "PLC",
            ["ManageUnifiedDynamization.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageUnifiedEngineeringObject.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageUnifiedEvent.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageUnifiedHmiGroup.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageUnifiedListEntries.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageUnifiedLoggingTag.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageUnifiedObjectParts.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageUnifiedOpcUaAlarmType.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageUnifiedScreenItem.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageUnifiedScreenLayout.softwarePath"] = "HMI software; not a PLC target.",
            ["ManageWatchForceTableWebAccess.softwarePath"] = "PLC",
            ["MonitorPlcWatchTableS7.softwarePath"] = "PLC",
            ["MovePlcBlockToGroup.softwarePath"] = "PLC",
            ["PlanOnlineReadOnlyMonitoring.softwarePath"] = "PLC",
            ["PlanPlcExternalSourceImport.softwarePath"] = "PLC",
            ["ProbePlcMonitorOnlineCapabilities.softwarePath"] = "PLC",
            ["RenderPlcBlockDocument.softwarePath"] = "PLC",
            ["RepairAndReimportPlcBlock.softwarePath"] = "PLC",
            ["ResolveSivarcExpression.softwarePath"] = "PLC",
            ["ScanAccessibleDevices.softwarePath"] = "PLC",
            ["SeedProjectFromReference.plcSoftwarePath"] = "PLC",
            ["SetDeviceAddress.softwarePath"] = "processImage",
            ["SetOpcUaInterfaceEnabled.softwarePath"] = "PLC",
            ["SetPlcProgram.softwarePath"] = "PLC",
            ["SetPlcUnitObjectAccess.softwarePath"] = "PLC",
            ["SetPlcWatchTableModifyValue.softwarePath"] = "PLC",
            ["SetUnifiedGlobalScript.softwarePath"] = "HMI software; not a PLC target.",
            ["SetUnifiedLogDuration.softwarePath"] = "HMI software; not a PLC target.",
            ["SetUnifiedMultilingualProperty.softwarePath"] = "HMI software; not a PLC target.",
            ["SetUnifiedObjectProperties.softwarePath"] = "HMI software; not a PLC target.",
            ["SetUnifiedRuntimeSettings.softwarePath"] = "HMI software; not a PLC target.",
            ["ShowObjectInEditor.softwarePath"] = "plcKind",
            ["ShowWorkbenchBlock.softwarePath"] = "PLC",
            ["ShowWorkbenchLadder.softwarePath"] = "PLC",
            ["TraceTagCause.softwarePath"] = "PLC",
            ["TraceTagCauseLive.softwarePath"] = "PLC",
            ["UpgradeSivarcDefinitions.softwarePath"] = "PLC",
            ["ValidateUnifiedObject.softwarePath"] = "HMI software; not a PLC target.",
        };

        // Reviewed matches of the broader name scan that are not scalar owning-PLC presets.
        public static IReadOnlyDictionary<string, string> ReviewedSoftwareParameters { get; } = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["ApplyUnifiedHmiLayout.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["ApplyUnifiedHmiScreenDesign.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["ApplyUnifiedHmiTheme.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["BindUnifiedHmiButtonPressedTag.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["BindUnifiedHmiTagDynamization.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["BindUnifiedHmiTagDynamization.plcTag"] = "HMI tag-binding assertion, not a PLC software selector; no PLC setter.",
            ["CompareProjects.targetSoftwarePath"] = "READ comparison operand; the secondary PLC is observed, never mutated.",
            ["CompareUnifiedGraphicSelections.afterPages[].scope.softwarePath"] =
                "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["CompareUnifiedGraphicSelections.afterPages[].softwarePath"] =
                "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["CompareUnifiedGraphicSelections.beforePages[].scope.softwarePath"] =
                "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["CompareUnifiedGraphicSelections.beforePages[].softwarePath"] =
                "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["DescribeUnifiedHmiButtonEventScript.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["EnsureUnifiedHmiButtonAction.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["EnsureUnifiedHmiButtonEventHandler.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["EnsureUnifiedHmiConnection.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["EnsureUnifiedHmiConnection.plcName"] = "HMI connection peer name; selects HMI connection metadata, not a PLC mutation target.",
            ["EnsureUnifiedHmiDynamization.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["EnsureUnifiedHmiScreen.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["EnsureUnifiedHmiScreenItem.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["EnsureUnifiedHmiTag.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["EnsureUnifiedHmiTag.plcName"] = "HMI symbolic connection peer; only the HMI tag is created or changed.",
            ["EnsureUnifiedHmiTag.plcTag"] = "PLC symbol referenced by an HMI tag; not a software-owner path.",
            ["EnsureUnifiedHmiTagTable.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["GenerateSivarc.plcSoftwarePaths"] = "Explicit plural PLC inputs to HMI generation; generation mutates the selected HMI, not these PLCs.",
            ["GetUnifiedHmiTexts.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["ImportMasterCopyFromGlobalLibrary.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["ManageLibraryType.scopeSoftwarePaths"] =
                "Explicit heterogeneous library update scope, not a scalar owning-PLC selector; retained as caller-supplied scope.",
            ["ManageMotionAxis.target.plcTagPath"] = "PLC tag symbol within the already-pinned motion software; not a software-owner selector.",
            ["ManageSivarcTableRule.references.softwarePath"] =
                "Referenced rule software metadata; the selected rule/table owns the mutation, not this reference.",
            ["ManageTestSuiteCase.scope[].softwarePath"] =
                "Test-case scope reference metadata; the test-case object owns the mutation, not a PLC software object.",
            ["PrefillWorkbenchForm.fields.softwarePath"] = "Local Workbench draft form field; no engineering tool is dispatched by prefilling.",
            ["ProbeHardwareHmiConnectionOwnerCandidates.plcRootPath"] =
                "READ hardware-root probe; device/device-item identity, not a PLC software mutation selector.",
            ["ProbeHardwareHmiConnectionWhitelistedServices.plcRootPath"] = "READ hardware-root service probe; not a PLC software mutation selector.",
            ["RunHmiTemplatePlcSyncPrecheckSuite.plcXmlPath"] = "Offline exported XML input file/directory, not a live PLC software selector.",
            ["SeedProjectFromReference.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["SetUnifiedHmiButtonEventScriptCode.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["SetUnifiedHmiRuntimeState.hmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["SetUnifiedHmiRuntimeState.plcName"] = "HMI runtime connection/tag mapping name; does not choose a PLC engineering mutation target.",
            ["SynchronizeLibrary.scopeSoftwarePaths"] =
                "Explicit heterogeneous library synchronization scope, not a scalar owning-PLC selector; retained as caller-supplied scope.",
            ["ValidateAutomationContext.expectedHmiSoftwarePath"] = "HMI software or HMI comparison snapshot metadata; not a PLC software mutation selector.",
            ["ValidateAutomationContext.expectedPlcSoftwarePath"] = "READ advisory validation expectation; cannot dispatch a PLC mutation.",
            ["ValidateClassicHmiMinimalPackagePlcSync.plcSymbols"] = "Offline symbol names for HMI synchronization validation; not live software-owner paths.",
        };

        public static bool Applies(string tool, string parameter)
            => parameter == "expectedProjectFile" || SoftwareParameters.TryGetValue(tool + "." + parameter, out var kind)
                && !kind.StartsWith("HMI", StringComparison.Ordinal);

        public static bool Conditional(string tool, string parameter)
            => SoftwareParameters.TryGetValue(tool + "." + parameter, out var kind) && kind != "PLC"
                && !kind.StartsWith("HMI", StringComparison.Ordinal);

        public static bool PlcTarget(string tool, string parameter, JsonObject input)
        {
            if (!Conditional(tool, parameter)) return true;
            string Text(string name) => input[name] is JsonValue scalar && scalar.TryGetValue<string>(out var value) ? value : "";
            switch (SoftwareParameters[tool + "." + parameter])
            {
                case "objectKind": return Text("objectKind").Equals("Block", StringComparison.OrdinalIgnoreCase)
                    || Text("objectKind").Equals("Type", StringComparison.OrdinalIgnoreCase);
                case "sourceKind": return Text("sourceKind") is "block" or "type";
                case "driveTelegram": return Text("action") == "connectTechnologyObject";
                case "plcKind": return Text("kind").StartsWith("plc", StringComparison.OrdinalIgnoreCase);
                case "libraryMasterCopy": return Text("action") == "instantiate"
                    && Text("targetKind") is "block" or "blockGroup" or "tagTable" or "tag";
                case "libraryVersion": return Text("action") == "instantiate";
                case "processImage": return Text("processImageObName").Length != 0;
                default: throw new InvalidOperationException("Unreviewed software selector.");
            }
        }

        public static string Value(IReadOnlyDictionary<string, string> presets, string parameter)
            => presets[parameter == "expectedProjectFile" ? parameter : "softwarePath"];

        public static string Description(string tool, IEnumerable<string> names)
            => "Preset by the server: " + string.Join(", ", names) + ". Equal normalized values are accepted; different values are refused."
                + (names.Any(name => Conditional(tool, name)) ? " PLC software presets on mixed tools follow the target selector." : "")
                + (names.Contains("softwarePath") && tool is "InvokeObject" or "InvokeService"
                    ? " With a PLC preset, Software (including PLC software aliases), Device and DeviceItem reflection targets are refused." : "");

        public static string[] Names(string tool, JsonElement schema, IReadOnlyDictionary<string, string> presets)
            => schema.GetProperty("properties").EnumerateObject().Select(property => property.Name)
                .Where(parameter => Applies(tool, parameter) && presets.ContainsKey(parameter == "expectedProjectFile" ? parameter : "softwarePath"))
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();

        public static JsonElement Schema(string tool, JsonElement schema, IReadOnlyDictionary<string, string> presets)
        {
            var names = Names(tool, schema, presets);
            if (names.Length == 0) return schema;
            var result = JsonNode.Parse(schema.GetRawText())!.AsObject();
            foreach (string name in names.Where(name => !Conditional(tool, name)))
            {
                result["properties"]!.AsObject().Remove(name);
                if (result["required"] is JsonArray required)
                    foreach (var field in required.Where(field => (string?)field == name).ToArray()) required.Remove(field);
            }
            foreach (string name in names.Where(name => Conditional(tool, name)))
            {
                var property = result["properties"]![name]!;
                property["$comment"] = ((string?)property["$comment"] + " " + ConditionalSchemaComment).Trim();
            }
            return JsonSerializer.SerializeToElement(result);
        }

        public static Error? UnpinnedReflectionTarget(string tool, JsonObject arguments, IReadOnlyDictionary<string, string> presets)
        {
            if (!presets.ContainsKey("softwarePath")) return null;
            if (tool is "CallTool" or "PreviewToolCall" && arguments["name"] is JsonValue target && target.TryGetValue<string>(out var name))
                return UnpinnedReflectionTarget(name, arguments["arguments"] as JsonObject ?? new JsonObject(), presets);
            if (tool is "RunReadOnlyToolBatch" or "PreviewToolBatch" && arguments["operations"] is JsonArray operations)
                foreach (var operation in operations.OfType<JsonObject>())
                    if (operation["name"] is JsonValue item && item.TryGetValue<string>(out var nestedName)
                        && UnpinnedReflectionTarget(nestedName, operation["arguments"] as JsonObject ?? new JsonObject(), presets) is { } refusal)
                        return refusal;
            if (presets.TryGetValue("softwarePath", out var plcPreset) && tool is "InvokeObject" or "InvokeService"
                && arguments["objectKind"] is JsonValue kind && kind.TryGetValue<string>(out var text)
                && text.Trim().ToLowerInvariant() is "software" or "plcsoftware" or "plc_software" or "plc-software"
                    or "device" or "deviceitem" or "device_item" or "device-item")
                return new Error("objectKind cannot be reliably pinned to --preset-plc-software (" + plcPreset + "); this reflection target is refused.",
                    new PreconditionFailedDetails("server-plc-software-preset", plcPreset));
            return null;
        }

        public static Error? Refusal(string tool, JsonObject arguments, IEnumerable<string> names, IReadOnlyDictionary<string, string> presets)
        {
            if (UnpinnedReflectionTarget(tool, arguments, presets) is { } reflectionRefusal) return reflectionRefusal;
            foreach (string name in names.Where(name => PlcTarget(tool, name, arguments)))
                if (arguments.TryGetPropertyValue(name, out var supplied))
                {
                    bool same = supplied is JsonValue scalar && scalar.TryGetValue<string>(out var value)
                        && (name == "expectedProjectFile" ? PresetProjectPath.Same(Value(presets, name), value)
                            : string.Equals(Value(presets, name), value, StringComparison.Ordinal));
                    if (!same) return new Error(name + " differs from the server preset; omit it or supply the preset value.",
                        new InvalidArgumentDetails("/" + name, Array.Empty<string>()));
                }
            return null;
        }
    }
}
