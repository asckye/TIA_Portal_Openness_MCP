using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AddInOpcUaInterface;
using AddInOpcUaInterface.Other;
using AddInOpcUaInterface.Phases;
using AddInOpcUaInterface.Phases.Phase4;
using Siemens.Engineering;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.SW.Units;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private static readonly object OpcUaModelLock = new object();
        public JsonObject GenerateOpcUaModelledInterface(string softwarePath, string interfaceName, string namespaceUri, string outputPath,
            string unitName, bool keepFolderStructure, bool keepEmptyDataBlocks, string accessLevelsJson, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(interfaceName) || interfaceName.Length > 128) throw new ArgumentException("interfaceName must contain 1..128 characters.");
            if (!Uri.TryCreate(namespaceUri, UriKind.Absolute, out _)) throw new ArgumentException("namespaceUri must be absolute, e.g. urn:company:machine.");
            if (!outputPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("outputPath must end in .xml.");
            var output = NativeFileOutput.Plan(outputPath);
            var access = JsonNode.Parse(accessLevelsJson) as JsonObject ?? throw new ArgumentException("accessLevelsJson must be an object.");
            var allowed = new HashSet<string>(new[] { "Inputs", "Outputs", "Memory", "Counters", "Timers", "GlobalDBs", "InstanceDBs", "SafetyGlobalDBs", "SafetyInstanceDBs" }, StringComparer.Ordinal);
            foreach (var item in access)
            {
                if (!allowed.Contains(item.Key) || !(item.Value is JsonValue value) || !value.TryGetValue<int>(out var level) || level < 0 || level > 4) throw new ArgumentException("Access levels: named area -> integer 0..4.");
                if (item.Key.StartsWith("Safety", StringComparison.Ordinal) && level > 1) throw new ArgumentException("Safety areas can only be excluded (0) or read-only (1).");
            }
            if (IsProjectNull()) throw new InvalidOperationException("No project is bound.");
            var plc = GetPlcSoftware(softwarePath) ?? throw new ArgumentException("PLC not found: " + softwarePath + AvailablePlcPathsSuffix());
            if (plc.GetService<OpcUaProvider>() == null) throw new NotSupportedException("PLC does not expose OpcUaProvider.");
            var unit = string.IsNullOrEmpty(unitName) ? null : plc.GetService<PlcUnitProvider>()?.UnitGroup.Units.Find(unitName);
            if (!string.IsNullOrEmpty(unitName) && unit == null) throw new ArgumentException("Software unit not found: " + unitName);
            var result = new JsonObject { ["success"] = true, ["dryRun"] = dryRun, ["softwarePath"] = softwarePath, ["unitName"] = unitName,
                ["interfaceName"] = interfaceName, ["namespaceUri"] = namespaceUri, ["outputPath"] = output.FullName, ["accessLevels"] = access.DeepClone(),
                ["source"] = "Siemens user-modelled OPC UA interface generation phases (317dfd06)",
                ["limitations"] = new JsonArray("Optimized nodes and string identifiers only.", "Nested FBs are UAObjects; their contained variables are not accessible through this generated interface.", "Output requires separate TIA import/compile validation. No live compatibility certification.") };
            if (dryRun) { result["generated"] = false; return result; }
            lock (OpcUaModelLock)
            {
                string scratch = Path.Combine(Path.GetTempPath(), "TiaMcp-OpcUa-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                try
                {
                    using (var ctx = new AddInExecutionContext())
                    {
                        ctx.InterfaceName = interfaceName; ctx.InterfaceURI = namespaceUri; ctx.FilePath = Path.Combine(scratch, "interface.xml");
                        ctx.IsSoftwareUnit = unit != null; ctx.Unit = unit!; ctx.KeepFolderStructure = keepFolderStructure; ctx.KeepEmptyDBs = keepEmptyDataBlocks; ctx.OptimizedData = true;
                        foreach (var item in access) typeof(AddInExecutionContext).GetField(item.Key + "AccessLevel")!.SetValue(ctx, item.Value!.GetValue<int>());
                        using (var template = typeof(Portal).Assembly.GetManifestResourceStream("TiaMcp.OpcUa.InterfaceTemplate.xml") ?? throw new FileNotFoundException("Embedded template missing.")) InterfaceTemplate.ImportTemplate(template);
                        ctx.NumberDefaultNodes = InterfaceTemplate.GetTotalInterfaceElements();
                        UserConstants.GetUserConstants(unit?.TagTableGroup ?? plc.TagTableGroup);
                        UserSystemDataTypes.GetUserSystemDataTypeElements(plc.TypeGroup, false);
                        var types = UserSystemDataTypes.XElementUserSystemDataTypes.Select(e => new XElement(e)).ToList();
                        if (unit != null)
                        {
                            var systemNames = UserSystemDataTypes.SystemDataTypes.ToList(); var userNames = UserSystemDataTypes.UserDataTypes.ToList();
                            UserSystemDataTypes.GetUserSystemDataTypeElements(unit.TypeGroup, true);
                            types.AddRange(UserSystemDataTypes.XElementUserSystemDataTypes.Select(e => new XElement(e)));
                            UserSystemDataTypes.SystemDataTypes.AddRange(systemNames); UserSystemDataTypes.UserDataTypes.AddRange(userNames);
                        }
                        ctx.OpcUaInterface.Root!.Add(types); ctx.NumberUserSystemDataTypes = types.Count;
                        Tags.ResetTagElements(); Tags.GetTagElements(unit?.TagTableGroup ?? plc.TagTableGroup);
                        ctx.OpcUaInterface.Root.Add(Tags.XElementTags); ctx.NumberTags = Tags.XElementTags.Count;
                        BuildDataBlockElements.ResetDatablocksElements();
                        DataBlocksGlobal.ResetDatablockElements(); DataBlocksGlobal.GetDatablockElements(unit?.BlockGroup ?? plc.BlockGroup);
                        ctx.NumberGlobalDBs = BuildDataBlockElements.XElementDataBlocks.Count;
                        DataBlocksInstance.ResetDatablockElements(); DataBlocksInstance.GetDatablockElements(unit?.BlockGroup ?? plc.BlockGroup);
                        ctx.NumberInstanceDBs = BuildDataBlockElements.XElementDataBlocks.Count - ctx.NumberGlobalDBs;
                        ctx.OpcUaInterface.Root.Add(BuildDataBlockElements.XElementDataBlocks);
                        // Reject duplicate NodeIds instead of exporting an invalid model with false success.
                        var duplicates = ctx.OpcUaInterface.Root.Elements().Where(e => e.Attribute("NodeId") != null).GroupBy(e => (string)e.Attribute("NodeId")!, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                        if (duplicates.Count > 0) throw new InvalidOperationException("Generated duplicate NodeIds: " + string.Join(", ", duplicates.Take(10)));
                        using (var stream = new FileStream(output.FullName, FileMode.CreateNew, FileAccess.Write)) ctx.OpcUaInterface.Save(stream);
                        result["generated"] = true; result["output"] = NativeFileOutput.Verify(output);
                        result["counts"] = new JsonObject { ["templateNodes"] = ctx.NumberDefaultNodes, ["typeNodes"] = ctx.NumberUserSystemDataTypes, ["tagNodes"] = ctx.NumberTags, ["globalDbNodes"] = ctx.NumberGlobalDBs, ["instanceDbNodes"] = ctx.NumberInstanceDBs };
                        result["warnings"] = new JsonArray(ctx.Warnings.Select(s => (JsonNode)s).ToArray()); result["dataComplete"] = ctx.Warnings.Count == 0;
                        result["imported"] = false;
                    }
                }
                finally { try { Directory.Delete(scratch, true); } catch { result["scratchCleanupFailed"] = scratch; } }
            }
            return result;
        }
    }
}
