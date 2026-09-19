using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.Dcc;
using Siemens.Engineering.MC.Drives.Dcc.DccExceptions;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.DccLogic;

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-② (2.7.39): typed SINAMICS DCC option package (V21 Siemens.Engineering.DCC.dll, V20 inside Siemens.Engineering.dll).
    // DriveControlChartContainer is a service of the DriveObject (null when the drive does not support DCC); charts nest through
    // Subcharts, blocks come from DCB libraries (DcbLibrary / DcbBlockType), pins connect pin-to-pin or to chart interfaces and can be
    // published as drive parameters. Every DCC failure is a DccException (: EngineeringTargetInvocationException) - the 39 concrete
    // classes are classified in DccFailure so a licence, import or naming problem is named, not buried in the stack.
    public partial class Portal
    {
        private static readonly Type[] KnownDccExceptions =
        {
            typeof(DccException), typeof(DccBlockInvalidNameException), typeof(DccBlockNameAlreadyUsedException), typeof(DccBlockTypeNotFoundException), typeof(DccChartIsProtectedException),
            typeof(DccCommentTooLongException), typeof(DccConnectionAlreadyExistsException), typeof(DccConnectionPartnerMismatchException), typeof(DccExportException), typeof(DccIllegalAssignmentException),
            typeof(DccImportException), typeof(DccImportBlockCreationException), typeof(DccImportBlockTypeNotFoundException), typeof(DccImportChartCreationException), typeof(DccImportChartWithSameNameAlreadyAvailableException),
            typeof(DccImportDcbTypeDifferentVersionAlreadyUsedException), typeof(DccImportFileAlreadyInUseException), typeof(DccImportLibraryIsMissingException), typeof(DccInvalidSheetNumberException), typeof(DccInvalidValueException),
            typeof(DccLibraryImportAlreadyAvailableException), typeof(DccLibraryImportCorruptedStudioLibraryException), typeof(DccLibraryImportGenericPinLimitExceededException), typeof(DccLibraryImportIntegrityBrokenException),
            typeof(DccLibraryImportOverallPinLimitExceededException), typeof(DccLibraryImportStandardLibraryAlreadyAvailableException), typeof(DccLibraryImportUnsupportedLibraryTypeException), typeof(DccLicenseUnavailableException),
            typeof(DccNameAlreadyUsedException), typeof(DccNameInvalidException), typeof(DccNestingTooDeepException), typeof(DccNoMoreFreeParameterNumberInDriveObjectException), typeof(DccParameterMergingException),
            typeof(DccParameterNumberAlreadyExistsException), typeof(DccParameterNumberOutOfRangeException), typeof(DccPublishedStructAlarmIdException), typeof(DccTextTooLongException), typeof(DccTooManyChartPartitionsException), typeof(DccTooManyChartsException)
        };
        // Runs a DCC tool body; a DccException is recorded typed (class, family, licence flag) before it propagates to RunHmiStepTool.
        private static string DccStep(JsonObject meta, Func<string> body)
        {
            try { return body(); }
            catch (DccException ex)
            {
                meta["dccException"] = new JsonObject
                {
                    ["type"] = ex.GetType().Name, ["message"] = ex.Message, ["known"] = KnownDccExceptions.Contains(ex.GetType()),
                    ["family"] = ex is DccImportException ? "import" : ex is DccLicenseUnavailableException ? "licence" : ex is DccExportException ? "export" : "chart",
                    ["licenceMissing"] = ex is DccLicenseUnavailableException
                };
                throw;
            }
        }

        // ---- resolution ----------------------------------------------------------------------------------------------------------
        private static DriveControlChartContainer ExactDccContainer(DriveObject drive)
            => drive.GetService<DriveControlChartContainer>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "DriveControlChartContainer not provided by this drive object (device without DCC support, or SINAMICS DCC not installed).");
        private static DriveControlChart ExactDccChart(DriveControlChartContainer container, string[] path)
        {
            DriveControlChart chart = container.Charts.Find(path[0]) ?? throw new PortalException(PortalErrorCode.NotFound, "DCC chart not found: " + path[0]);
            foreach (var name in path.Skip(1)) chart = chart.Subcharts.Find(name) ?? throw new PortalException(PortalErrorCode.NotFound, "DCC subchart not found: " + name + " (under " + chart.Name + ")");
            return chart;
        }
        private static DccBlock ExactDccBlock(DriveControlChart chart, string name) => chart.Blocks.Find(name) ?? throw new PortalException(PortalErrorCode.NotFound, "DCC block not found in chart " + chart.Name + ": " + name);
        private static DccPin ExactDccPin(DccBlock block, string name) => block.Pins.Find(name) ?? throw new PortalException(PortalErrorCode.NotFound, "DCC pin not found on block " + block.Name + ": " + name);
        private static DccChartInterface ExactDccInterface(DriveControlChart chart, string name)
            => EngineeringGroupOperations.Items(chart.ChartInterfaces).Cast<DccChartInterface>().FirstOrDefault(i => i.Name == name) ?? throw new PortalException(PortalErrorCode.NotFound, "DCC chart interface not found in chart " + chart.Name + ": " + name);

        // ---- rows ----------------------------------------------------------------------------------------------------------------
        private static JsonObject? DccContainerSummary(DriveObject drive)
        {
            var container = drive.GetService<DriveControlChartContainer>(); if (container == null) return null;
            var row = new JsonObject { ["available"] = true };
            Safe(row, "chartCount", () => container.Charts.Count); Safe(row, "charts", () => new JsonArray(EngineeringGroupOperations.Items(container.Charts).Cast<DriveControlChart>().Take(100).Select(c => (JsonNode)c.Name).ToArray()));
            Safe(row, "dcbLibraries", () => new JsonArray(EngineeringGroupOperations.Items(container.DcbLibraries).Cast<DcbLibrary>().Select(l => (JsonNode)DcbLibraryRow(l, false)).ToArray()));
            return row;
        }
        private static JsonObject DcbLibraryRow(DcbLibrary l, bool types)
        {
            var row = new JsonObject { ["libraryName"] = l.LibraryName };
            Safe(row, "version", () => l.Version?.ToString()); Safe(row, "blockTypeCount", () => l.BlockTypes.Count);
            if (types) Safe(row, "blockTypes", () => new JsonArray(EngineeringGroupOperations.Items(l.BlockTypes).Cast<DcbBlockType>().Take(500).Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Description }).ToArray()));
            return row;
        }
        private static JsonObject StatementRow(Statement s, string statementClass)
        {
            var row = new JsonObject { ["name"] = s.Name, ["statementClass"] = statementClass };
            Safe(row, "comment", () => s.Comment); Safe(row, "positionX", () => s.PositionX); Safe(row, "positionY", () => s.PositionY); Safe(row, "partition", () => s.Partition?.Name);
            return row;
        }
        private static JsonObject DccParameterRow(DccParameter p)
        {
            var row = new JsonObject();
            Safe(row, "number", () => p.Number); Safe(row, "arrayIndex", () => p.ArrayIndex); Safe(row, "parameterText", () => p.ParameterText); Safe(row, "isSink", () => p.IsSink); Safe(row, "isSignal", () => p.IsSignal);
            return row;
        }
        private static JsonObject DccEndpointRow(IEngineeringObject? o) => o switch
        {
            DccPin pin => new JsonObject { ["kind"] = "pin", ["name"] = pin.Name, ["block"] = (pin.Parent as DccBlock)?.Name },
            DccChartInterface i => new JsonObject { ["kind"] = "chartInterface", ["name"] = i.Name },
            DccParameter p => new JsonObject { ["kind"] = "parameter", ["number"] = p.Number, ["arrayIndex"] = p.ArrayIndex },
            null => new JsonObject { ["kind"] = "none" },
            _ => new JsonObject { ["kind"] = o.GetType().Name }
        };
        private static JsonObject ConnectionRow(DccConnection c)
        {
            var row = new JsonObject();
            Safe(row, "sink", () => DccEndpointRow(c.Sink)); Safe(row, "source", () => DccEndpointRow(c.Source));
            return row;
        }
        // DccPin and DccChartInterface share the official IOVariable (: IDccObject) contract: name / comment / value / unit / direction / flags / connections.
        private static JsonObject IoVariableRow(IOVariable v, bool connections)
        {
            IDccObject dccObject = v;
            var row = new JsonObject { ["name"] = dccObject.Name };
            Safe(row, "comment", () => dccObject.Comment); Safe(row, "value", () => EngineeringScalarProperties.Json(v.Value)); Safe(row, "unit", () => v.Unit); Safe(row, "isInput", () => v.IsInput);
            Safe(row, "invisible", () => v.Invisible); Safe(row, "forTest", () => v.ForTest);
            if (connections) Safe(row, "connections", () => new JsonArray(EngineeringGroupOperations.Items(v.Connections).Cast<DccConnection>().Select(c => (JsonNode)ConnectionRow(c)).ToArray()));
            else Safe(row, "connectionCount", () => v.Connections.Count);
            return row;
        }
        private static JsonObject PinRow(DccPin p, bool connections)
        {
            var row = IoVariableRow(p, connections);
            Safe(row, "isPublished", () => p.IsPublished); Safe(row, "parameter", () => p.Parameter == null ? null : DccParameterRow(p.Parameter));
            return row;
        }
        private static JsonObject BlockRow(DccBlock b, bool pins)
        {
            var row = StatementRow(b, "DccBlock");
            Safe(row, "genericInputsNumber", () => b.GenericInputsNumber); Safe(row, "pinCount", () => b.Pins.Count);
            if (pins) Safe(row, "pins", () => new JsonArray(EngineeringGroupOperations.Items(b.Pins).Cast<DccPin>().Take(500).Select(p => (JsonNode)PinRow(p, true)).ToArray()));
            return row;
        }
        private static JsonObject DccInterfaceRow(DccChartInterface i)
        {
            var row = IoVariableRow(i, true);
            Safe(row, "pin", () => DccEndpointRow(i.Pin));
            return row;
        }
        private static JsonObject ChartRow(DriveControlChart c, int depth, int maxDepth, bool blocks, bool pins)
        {
            var row = StatementRow(c, "DriveControlChart");
            Safe(row, "horizontalSheets", () => c.HorizontalSheets); Safe(row, "verticalSheets", () => c.VerticalSheets); Safe(row, "sheetWidth", () => c.SheetWidth); Safe(row, "sheetHeight", () => c.SheetHeight);
            Safe(row, "blockCount", () => c.Blocks.Count); Safe(row, "subchartCount", () => c.Subcharts.Count); Safe(row, "interfaceCount", () => c.ChartInterfaces.Count);
            Safe(row, "partitions", () => new JsonArray(EngineeringGroupOperations.Items(c.Partitions).Cast<DccChartPartition>().Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["comment"] = p.Comment }).ToArray()));
            Safe(row, "chartInterfaces", () => new JsonArray(EngineeringGroupOperations.Items(c.ChartInterfaces).Cast<DccChartInterface>().Take(200).Select(i => (JsonNode)DccInterfaceRow(i)).ToArray()));
            if (blocks) Safe(row, "blocks", () => new JsonArray(EngineeringGroupOperations.Items(c.Blocks).Cast<DccBlock>().Take(500).Select(b => (JsonNode)BlockRow(b, pins)).ToArray()));
            if (depth < maxDepth) Safe(row, "subcharts", () => new JsonArray(EngineeringGroupOperations.Items(c.Subcharts).Cast<DriveControlChart>().Take(200).Select(s => (JsonNode)ChartRow(s, depth + 1, maxDepth, blocks, pins)).ToArray()));
            return row;
        }
        private static JsonArray SequenceRows(IEnumerable<Statement> statements) => new JsonArray(statements.Take(1000).Select((s, i) => (JsonNode)new JsonObject { ["index"] = i, ["name"] = s.Name, ["statementClass"] = s is DriveControlChart ? "DriveControlChart" : s is DccBlock ? "DccBlock" : s.GetType().Name }).ToArray());
        // propertiesJson.Partition names a partition of the owning chart; the other writable scalars go through EngineeringScalarProperties.
        private static void ApplyDccProperties(object target, JsonObject properties, DriveControlChart? owner, JsonObject meta)
        {
            var scalars = new JsonObject(); foreach (var pair in properties) if (pair.Key != "Partition") scalars[pair.Key] = pair.Value?.DeepClone();
            if (properties.ContainsKey("Partition"))
            {
                var name = properties["Partition"]?.GetValue<string>() ?? "";
                var partition = owner?.Partitions.Find(name) ?? throw new PortalException(PortalErrorCode.NotFound, "Partition not found in the owning chart: " + name);
                ((Statement)target).Partition = partition; meta["partitionApplied"] = partition.Name;
            }
            if (scalars.Count > 0) EngineeringScalarProperties.Apply(target, EngineeringScalarProperties.Prepare(target.GetType(), scalars), meta);
        }

        // ---- tools ---------------------------------------------------------------------------------------------------------------
        public ResponseMessage ReadDccCharts(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string chartPath = "", int maxDepth = 3, bool includeBlocks = true, bool includePins = false, bool includeLibraries = true)
            => RunHmiStepTool("ReadDccCharts", meta => DccStep(meta, () =>
            {
                if (maxDepth < 1 || maxDepth > 8) throw new ArgumentException("maxDepth 1..8 required.");
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                var container = ExactDccContainer(drive);
                meta["driveObject"] = StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label;
                if (includeLibraries) Safe(meta, "dcbLibraries", () => new JsonArray(EngineeringGroupOperations.Items(container.DcbLibraries).Cast<DcbLibrary>().Select(l => (JsonNode)DcbLibraryRow(l, true)).ToArray()));
                if (string.IsNullOrEmpty(chartPath))
                {
                    var charts = EngineeringGroupOperations.Items(container.Charts).Cast<DriveControlChart>().ToArray();
                    meta["records"] = new JsonArray(charts.Select(c => (JsonNode)ChartRow(c, 1, maxDepth, includeBlocks, includePins)).ToArray()); meta["expectedCount"] = charts.Length; meta["actualCount"] = charts.Length;
                    Safe(meta, "chartSequence", () => SequenceRows(container.Charts.GetChartSequence()));
                    return "DCC chart container read (charts with subcharts, partitions, interfaces" + (includeBlocks ? ", blocks" : "") + (includePins ? ", pins" : "") + ", DCB libraries, execution sequence); no modification.";
                }
                var chart = ExactDccChart(container, Logic.ChartParts(chartPath));
                meta["chart"] = ChartRow(chart, 1, maxDepth, includeBlocks, includePins);
                Safe(meta, "runSequence", () => SequenceRows(chart.GetRunSequence()));
                return "DCC chart read (attributes, partitions, interfaces, blocks / subcharts to maxDepth, run sequence); no modification.";
            }));

        // Typed retrofit of the 2.7.x reflective tool (same leading signature): chartName is now a chart path (Root/Sub), empty on create = auto name.
        public ResponseMessage ManageDccChart(string devicePathJson, string itemPathJson, ushort driveObjectNumber, string chartName, string action, string filePath = "", string importOptions = "", string propertiesJson = "{}", bool dryRun = true, int driveObjectIndex = -1, bool confirmDelete = false, int sequenceIndex = -1)
            => RunHmiStepTool("ManageDccChart", meta => DccStep(meta, () =>
            {
                var r = Logic.ValidateChartRequest(chartName, action, filePath, importOptions, propertiesJson, confirmDelete, dryRun);
                if (sequenceIndex >= 0 && action != "update") throw new ArgumentException("sequenceIndex (MoveInRuntimeSequence) applies to action update only.");
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                var container = ExactDccContainer(drive);
                meta["driveObject"] = StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                DriveControlChart? parent = r.Path.Length > 1 ? ExactDccChart(container, r.Path.Take(r.Path.Length - 1).ToArray()) : null;
                DriveControlChartComposition charts = parent?.Subcharts ?? container.Charts;
                DriveControlChart? chart = r.Path.Length == 0 ? null : (r.Action == "create" ? charts.Find(r.Path.Last()) : ExactDccChart(container, r.Path));
                if (action == "create" && chart != null) throw new PortalException(PortalErrorCode.InvalidState, "DCC chart already exists: " + chartName);
                if (chart != null) meta["before"] = ChartRow(chart, 1, 1, false, false);
                switch (action)
                {
                    case "read": return "DCC chart read; use ReadDccCharts for the nested tree. No modification.";
                    case "readSequence": meta["sequence"] = chart == null ? SequenceRows(container.Charts.GetChartSequence()) : SequenceRows(chart.GetRunSequence()); return chart == null ? "DCC charts in execution order (GetChartSequence); no modification." : "Run sequence of the chart (GetRunSequence); no modification.";
                }
                FileInfo? file = null;
                if (action == "export" || action == "exportAll") { file = NativeFileOutput.Plan(filePath); meta["file"] = file.FullName; }
                if (action == "import") { file = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath"); meta["file"] = file.FullName; meta["importOptions"] = r.ImportOption; }
                if (!r.Writes && !r.WritesFiles) return "DCC chart " + action + " preview; nothing changed" + (action == "delete" ? " (delete removes the chart with all subcharts and blocks)" : "") + ".";
                meta["mayHaveChanged"] = r.Writes; meta["mayHaveWrittenFiles"] = r.WritesFiles;
                switch (action)
                {
                    case "create":
                        meta["nativeSignature"] = r.AutoName ? "DriveControlChartComposition.Create()" : "DriveControlChartComposition.Create(string)";
                        chart = r.AutoName ? charts.Create() : charts.Create(r.Path.Last());
                        if (r.Properties.Count > 0) ApplyDccProperties(chart, r.Properties, parent, meta);
                        meta["createdName"] = chart.Name; break;
                    case "update":
                        if (r.Properties.Count > 0) ApplyDccProperties(chart!, r.Properties, parent, meta);
                        if (sequenceIndex >= 0) { meta["nativeSignature"] = "Statement.MoveInRuntimeSequence(uint)"; chart!.MoveInRuntimeSequence((uint)sequenceIndex); }
                        break;
                    case "delete": meta["nativeSignature"] = "DriveControlChart.Delete()"; chart!.Delete(); break;
                    case "export": meta["nativeSignature"] = "DriveControlChart.Export(string)"; chart!.Export(file!.FullName); meta["output"] = NativeFileOutput.Verify(file); break;
                    case "exportAll": meta["nativeSignature"] = "DriveControlChartComposition.Export(string)"; container.Charts.Export(file!.FullName); meta["output"] = NativeFileOutput.Verify(file); break;
                    case "import":
                        {
                            meta["nativeSignature"] = "DriveControlChartComposition.Import(string, DccImportOptions)";
                            DccImportResultData result = charts.Import(file!.FullName, (DccImportOptions)Enum.Parse(typeof(DccImportOptions), r.ImportOption));
                            var remapped = new JsonObject(); Safe(meta, "remappedParameterNumbers", () => { foreach (var pair in result.RemappedParameterNumbers) remapped[pair.Key.ToString()] = EngineeringScalarProperties.Json(pair.Value); return remapped; });
                            break;
                        }
                    case "optimizeSequence": meta["nativeSignature"] = "DriveControlChart.OptimizeRunSequence()"; chart!.OptimizeRunSequence(); Safe(meta, "runSequence", () => SequenceRows(chart.GetRunSequence())); break;
                    case "showEditor": meta["nativeSignature"] = "DriveControlChart.ShowDccEditor()"; chart!.ShowDccEditor(); meta["mayHaveChanged"] = false; break;
                }
                var name = action == "create" ? chart!.Name : r.Path.Length > 0 ? r.Path.Last() : "";
                if (name.Length > 0)
                {
                    var after = (parent?.Subcharts ?? container.Charts).Find(name);
                    if (action == "delete") meta["verifiedAbsent"] = after == null; else if (after != null) meta["after"] = ChartRow(after, 1, 1, false, false);
                    if (action == "delete" ? after != null : action != "import" && action != "exportAll" && after == null) throw new InvalidOperationException("Chart presence not as expected after " + action + ".");
                }
                else if (action == "import") meta["after"] = new JsonArray(EngineeringGroupOperations.Items(container.Charts).Cast<DriveControlChart>().Select(c => (JsonNode)c.Name).ToArray());
                return "DCC chart " + action + " executed and read back; no save, download or online drive command.";
            }));

        public ResponseMessage ManageDccBlock(string devicePathJson, string itemPathJson, string chartPath, string blockName = "", string action = "read", string blockType = "", string libraryName = "", string propertiesJson = "{}", ushort driveObjectNumber = 0, int driveObjectIndex = -1, bool confirmDelete = false, int sequenceIndex = -1, bool dryRun = true)
            => RunHmiStepTool("ManageDccBlock", meta => DccStep(meta, () =>
            {
                var r = Logic.ValidateBlockRequest(chartPath, blockName, action, blockType, libraryName, propertiesJson, confirmDelete, dryRun);
                if (sequenceIndex >= 0 && action != "update") throw new ArgumentException("sequenceIndex (MoveInRuntimeSequence) applies to action update only.");
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                var chart = ExactDccChart(ExactDccContainer(drive), r.ChartPath);
                meta["chart"] = chartPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "read")
                {
                    if (string.IsNullOrEmpty(blockName)) { var all = EngineeringGroupOperations.Items(chart.Blocks).Cast<DccBlock>().ToArray(); meta["records"] = new JsonArray(all.Select(b => (JsonNode)BlockRow(b, false)).ToArray()); meta["expectedCount"] = all.Length; return "DCC blocks of the chart read; give blockName for one block with pins."; }
                    meta["block"] = BlockRow(ExactDccBlock(chart, blockName), true); return "DCC block read with pins, published parameters and connections; no modification.";
                }
                DccBlock? block = action == "create" ? (string.IsNullOrEmpty(blockName) ? null : chart.Blocks.Find(blockName)) : ExactDccBlock(chart, blockName);
                if (action == "create" && block != null) throw new PortalException(PortalErrorCode.InvalidState, "DCC block already exists in the chart: " + blockName);
                if (block != null) meta["before"] = BlockRow(block, false);
                if (!r.Writes) return "DCC block " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "create":
                        meta["nativeSignature"] = string.IsNullOrEmpty(blockName) ? "DccBlockComposition.Create(string blockType)" : string.IsNullOrEmpty(r.LibraryName) ? "DccBlockComposition.Create(string name, string blockType)" : "DccBlockComposition.Create(string name, string blockType, string libraryName)";
                        block = string.IsNullOrEmpty(blockName) ? chart.Blocks.Create(r.BlockType) : string.IsNullOrEmpty(r.LibraryName) ? chart.Blocks.Create(blockName, r.BlockType) : chart.Blocks.Create(blockName, r.BlockType, r.LibraryName);
                        if (r.Properties.Count > 0) ApplyDccProperties(block, r.Properties, chart, meta);
                        meta["createdName"] = block.Name; break;
                    case "update":
                        if (r.Properties.Count > 0) ApplyDccProperties(block!, r.Properties, chart, meta);
                        if (sequenceIndex >= 0) { meta["nativeSignature"] = "Statement.MoveInRuntimeSequence(uint)"; block!.MoveInRuntimeSequence((uint)sequenceIndex); }
                        break;
                    case "delete": meta["nativeSignature"] = "DccBlock.Delete()"; block!.Delete(); break;
                    case "setAsPredecessor": meta["nativeSignature"] = "DccBlock.SetAsPredecessor()"; block!.SetAsPredecessor(); break;
                }
                var name = action == "create" ? block!.Name : (r.Properties["Name"]?.GetValue<string>() ?? blockName);
                var after = chart.Blocks.Find(name);
                if (action == "delete") { meta["verifiedAbsent"] = after == null; if (after != null) throw new InvalidOperationException("Block remains after Delete()."); }
                else { if (after == null) throw new InvalidOperationException("Block not found after " + action + "."); meta["after"] = BlockRow(after, action == "create"); }
                if (action == "update" && sequenceIndex >= 0) Safe(meta, "runSequence", () => SequenceRows(chart.GetRunSequence()));
                return "DCC block " + action + " executed and read back; no save, download or online drive command.";
            }));

        public ResponseMessage ManageDccPin(string devicePathJson, string itemPathJson, string chartPath, string blockName, string pinName, string action = "read", string propertiesJson = "{}", string partnerJson = "{}", bool setAsSignal = false, int parameterNumber = -1, int arrayIndex = -1, ushort driveObjectNumber = 0, int driveObjectIndex = -1, bool dryRun = true)
            => RunHmiStepTool("ManageDccPin", meta => DccStep(meta, () =>
            {
                var r = Logic.ValidatePinRequest(chartPath, blockName, pinName, action, propertiesJson, partnerJson, setAsSignal, parameterNumber, arrayIndex, dryRun);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                var chart = ExactDccChart(ExactDccContainer(drive), r.ChartPath);
                var block = ExactDccBlock(chart, blockName); var pin = ExactDccPin(block, pinName);
                meta["chart"] = chartPath; meta["block"] = blockName; meta["pin"] = pinName; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["before"] = PinRow(pin, true);
                if (action == "read") return "DCC pin read (value / unit / flags, published parameter, connections with sink and source); no modification.";
                DccPin? partnerPin = null; DccChartInterface? partnerInterface = null;
                if (action == "connect") { if (r.PartnerInterface.Length > 0) partnerInterface = ExactDccInterface(chart, r.PartnerInterface); else partnerPin = ExactDccPin(ExactDccBlock(chart, r.PartnerBlock), r.PartnerPin); }
                DccConnection? connection = null;
                if (action == "disconnect")
                {
                    connection = EngineeringGroupOperations.Items(pin.Connections).Cast<DccConnection>().FirstOrDefault(c => Matches(c.Sink, r) || Matches(c.Source, r))
                        ?? throw new PortalException(PortalErrorCode.NotFound, "No connection of pin " + pinName + " to the given partner.");
                    meta["connection"] = ConnectionRow(connection);
                }
                if (action == "updateParameter" && pin.Parameter == null) throw new PortalException(PortalErrorCode.InvalidState, "Pin " + pinName + " is not published (no DccParameter).");
                if (!r.Writes) return "DCC pin " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "update": EngineeringScalarProperties.Apply(pin, EngineeringScalarProperties.Prepare(typeof(DccPin), r.Properties), meta); break;
                    case "connect":
                        meta["nativeSignature"] = partnerInterface != null ? "DccPin.Connect(DccChartInterface)" : "DccPin.Connect(DccPin)";
                        meta["connection"] = ConnectionRow(partnerInterface != null ? pin.Connect(partnerInterface) : pin.Connect(partnerPin!)); break;
                    case "disconnect": meta["nativeSignature"] = "DccConnection.Delete()"; connection!.Delete(); break;
                    case "publish":
                        meta["nativeSignature"] = r.HasArrayIndex ? "DccPin.Publish(uint, uint, bool)" : r.HasParameterNumber ? "DccPin.Publish(bool, uint)" : "DccPin.Publish(bool)";
                        DccParameter published = r.HasArrayIndex ? pin.Publish(r.ParameterNumber, r.ArrayIndex, setAsSignal) : r.HasParameterNumber ? pin.Publish(setAsSignal, r.ParameterNumber) : pin.Publish(setAsSignal);
                        meta["published"] = DccParameterRow(published); break;
                    case "unpublish": meta["nativeSignature"] = "DccPin.Unpublish()"; pin.Unpublish(); break;
                    case "updateParameter": EngineeringScalarProperties.Apply(pin.Parameter!, EngineeringScalarProperties.Prepare(typeof(DccParameter), r.Properties), meta); break;
                }
                var fresh = ExactDccPin(ExactDccBlock(chart, blockName), pinName);
                meta["after"] = PinRow(fresh, true);
                if (action == "publish") meta["publishedVerified"] = fresh.IsPublished; if (action == "unpublish") meta["unpublishedVerified"] = !fresh.IsPublished;
                return "DCC pin " + action + " executed and read back; no save, download or online drive command.";
            }));
        private static bool Matches(IEngineeringObject? endpoint, Logic.PinRequest r) => endpoint switch
        {
            DccChartInterface i => r.PartnerInterface.Length > 0 && i.Name == r.PartnerInterface,
            DccPin p => r.PartnerInterface.Length == 0 && p.Name == r.PartnerPin && (p.Parent as DccBlock)?.Name == r.PartnerBlock,
            _ => false
        };

        public ResponseMessage ManageDccChartInterface(string devicePathJson, string itemPathJson, string chartPath, string interfaceName = "", string action = "read", string sourceBlock = "", string sourcePin = "", string propertiesJson = "{}", ushort driveObjectNumber = 0, int driveObjectIndex = -1, bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageDccChartInterface", meta => DccStep(meta, () =>
            {
                var r = Logic.ValidateInterfaceRequest(chartPath, interfaceName, action, sourceBlock, sourcePin, propertiesJson, confirmDelete, dryRun);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                var chart = ExactDccChart(ExactDccContainer(drive), r.ChartPath);
                meta["chart"] = chartPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "read")
                {
                    if (string.IsNullOrEmpty(interfaceName)) { var all = EngineeringGroupOperations.Items(chart.ChartInterfaces).Cast<DccChartInterface>().ToArray(); meta["records"] = new JsonArray(all.Select(i => (JsonNode)DccInterfaceRow(i)).ToArray()); meta["expectedCount"] = all.Length; return "DCC chart interfaces read; no modification."; }
                    meta["interface"] = DccInterfaceRow(ExactDccInterface(chart, interfaceName)); return "DCC chart interface read; no modification.";
                }
                DccChartInterface? iface = action == "create" ? null : ExactDccInterface(chart, interfaceName);
                DccPin? source = action == "create" ? ExactDccPin(ExactDccBlock(chart, r.SourceBlock), r.SourcePin) : null;
                if (iface != null) meta["before"] = DccInterfaceRow(iface); if (source != null) meta["sourcePin"] = PinRow(source, false);
                if (!r.Writes) return "DCC chart interface " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                string name = interfaceName;
                switch (action)
                {
                    case "create": meta["nativeSignature"] = "DccChartInterfaceComposition.Create(DccPin)"; iface = chart.ChartInterfaces.Create(source!); name = iface.Name; meta["createdName"] = name; break;
                    case "update": EngineeringScalarProperties.Apply(iface!, EngineeringScalarProperties.Prepare(typeof(DccChartInterface), r.Properties), meta); break;
                    case "delete": meta["nativeSignature"] = "DccChartInterface.Delete()"; iface!.Delete(); break;
                }
                var after = EngineeringGroupOperations.Items(chart.ChartInterfaces).Cast<DccChartInterface>().FirstOrDefault(i => i.Name == name);
                if (action == "delete") { meta["verifiedAbsent"] = after == null; if (after != null) throw new InvalidOperationException("Chart interface remains after Delete()."); }
                else { if (after == null) throw new InvalidOperationException("Chart interface not found after " + action + "."); meta["after"] = DccInterfaceRow(after); }
                return "DCC chart interface " + action + " executed and read back; no save, download or online drive command.";
            }));

        public ResponseMessage ManageDccChartPartition(string devicePathJson, string itemPathJson, string chartPath, string partitionName = "", string action = "read", string propertiesJson = "{}", ushort driveObjectNumber = 0, int driveObjectIndex = -1, bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageDccChartPartition", meta => DccStep(meta, () =>
            {
                var r = Logic.ValidatePartitionRequest(chartPath, partitionName, action, propertiesJson, confirmDelete, dryRun);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                var chart = ExactDccChart(ExactDccContainer(drive), r.ChartPath);
                meta["chart"] = chartPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                JsonArray Rows() => new JsonArray(EngineeringGroupOperations.Items(chart.Partitions).Cast<DccChartPartition>().Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["comment"] = p.Comment }).ToArray());
                meta["before"] = Rows();
                if (action == "read") return "DCC chart partitions read; no modification.";
                DccChartPartition? partition = chart.Partitions.Find(partitionName);
                if (action == "create" && partition != null) throw new PortalException(PortalErrorCode.InvalidState, "Partition already exists: " + partitionName);
                if (action != "create" && partition == null) throw new PortalException(PortalErrorCode.NotFound, "Partition not found: " + partitionName);
                if (!r.Writes) return "DCC chart partition " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true; string name = partitionName;
                switch (action)
                {
                    case "create": meta["nativeSignature"] = "DccChartPartitionComposition.Create(string)"; partition = chart.Partitions.Create(partitionName); break;
                    case "update": EngineeringScalarProperties.Apply(partition!, EngineeringScalarProperties.Prepare(typeof(DccChartPartition), r.Properties), meta); name = r.Properties["Name"]?.GetValue<string>() ?? partitionName; break;
                    case "delete": meta["nativeSignature"] = "DccChartPartition.Delete()"; partition!.Delete(); break;
                }
                var after = chart.Partitions.Find(name);
                if (action == "delete") { meta["verifiedAbsent"] = after == null; if (after != null) throw new InvalidOperationException("Partition remains after Delete()."); }
                else if (after == null) throw new InvalidOperationException("Partition not found after " + action + ".");
                meta["after"] = Rows();
                return "DCC chart partition " + action + " executed and read back; no save, download or online drive command.";
            }));

        public ResponseMessage ManageDcbLibraries(string action = "read", string devicePathJson = "[]", string itemPathJson = "[]", ushort driveObjectNumber = 0, int driveObjectIndex = -1, string filePath = "", bool dryRun = true)
            => RunHmiStepTool("ManageDcbLibraries", meta => DccStep(meta, () =>
            {
                bool write = Logic.ValidateLibraryRequest(action, filePath, dryRun);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "read")
                {
                    var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                    var container = ExactDccContainer(drive);
                    var libraries = EngineeringGroupOperations.Items(container.DcbLibraries).Cast<DcbLibrary>().ToArray();
                    meta["driveObject"] = StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label;
                    meta["records"] = new JsonArray(libraries.Select(l => (JsonNode)DcbLibraryRow(l, true)).ToArray()); meta["expectedCount"] = libraries.Length;
                    return "DCB libraries used by the drive object's charts read with their block types; no modification.";
                }
                using var access = write ? AcquireHmiEditAccess() : null;
                var file = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath"); meta["file"] = file.FullName;
#if TIA_V20
                throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "DcbLibraryImporter.ImportDcbLibrary is a V21 addition; absent on V20.");
#else
                var importer = (_project!.ProjectLibrary as IEngineeringServiceProvider)?.GetService<DcbLibraryImporter>() ?? _project.GetService<DcbLibraryImporter>()
                    ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "DcbLibraryImporter unavailable (SINAMICS DCC not installed).");
                if (!write) return "DCB extension library import preview; nothing imported into the project library.";
                meta["mayHaveChanged"] = true; meta["nativeSignature"] = "DcbLibraryImporter.ImportDcbLibrary(string)";
                importer.ImportDcbLibrary(file.FullName);
                return "DCB extension library imported into the project library (DcbLibraryImporter); no save.";
#endif
            }));

        // Generic property-path reader kept from 2.7.x for arbitrary DCC sub-paths (typed drive resolution since 2.7.39).
        public ResponseMessage ReadDccObject(string devicePathJson, string itemPathJson, ushort driveObjectNumber, string objectPathJson = "[]", int offset = 0, int limit = 100, int driveObjectIndex = -1)
            => RunHmiStepTool("ReadDccObject", meta => DccStep(meta, () =>
            {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("Invalid pagination.");
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                var root = ExactDccContainer(drive);
                var target = EngineeringObjectAddress.Resolve(root, objectPathJson);
                var items = target is System.Collections.IEnumerable && target is not string ? EngineeringGroupOperations.Items(target).ToArray() : new[] { target };
                var rows = items.Skip(offset).Take(limit).Select(x => (JsonNode)EngineeringObjectAddress.Read(x)).ToArray();
                meta["records"] = new JsonArray(rows); meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Length; meta["nextOffset"] = offset + rows.Length < items.Length ? offset + rows.Length : (int?)null; meta["truncated"] = offset + rows.Length < items.Length; meta["dataComplete"] = false;
                return "Offline DCC objects (charts/blocks/pins/libraries) read at exact property path; scalar scope and live pagination only.";
            }));
    }
}
