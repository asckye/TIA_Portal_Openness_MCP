using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
using AddInOpcUaInterface;
using AddInOpcUaInterface.Other;
using AddInOpcUaInterface.Phases;
using AddInOpcUaInterface.Phases.Phase4;
using Siemens.Engineering.SW.Units;
using Siemens.Engineering.HW.Systemdiagnostics.Settings;
using Siemens.Engineering.SW.WatchAndForceTables;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class OpcUaService
    {
        private readonly IEngineeringSession _session;

        public OpcUaService(IEngineeringSession session) => _session = session;

        #region opcua

        /// <summary>Returns the ServerInterfaceGroup node (OpcUaProvider.CommunicationGroup.ServerInterfaceGroup).</summary>
        private static object? GetOpcUaServerInterfaceGroup(PlcSoftware plc)
        {
            var provider = plc.GetService<OpcUaProvider>();
            if (provider == null) return null;
            OpcUaCommunicationGroup? commGroup = provider.CommunicationGroup;
            if (commGroup == null) return null;
            ServerInterfaceGroup? group = commGroup.ServerInterfaceGroup;
            return group;
        }

        public ModelContextProtocol.ResponseJsonReport GetOpcUaConfig(string softwarePath)
        {
            // envelope: legacy-roundtrip-data-stamp
            var data = new JsonObject { ["softwarePath"] = softwarePath, ["timestamp"] = DateTime.Now.ToString("O") };

            if (_session.IsProjectNull())
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "No project open.", Data = data };

            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null)
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Data = data };

            try
            {
                var provider = plc.GetService<OpcUaProvider>();
                if (provider == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "OpcUaProvider not available for this PLC.", Data = data };

                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "ServerInterfaceGroup not accessible.", Data = data };

                data["serverInterfaces"] = CollectOpcUaItems(TryGetPropertyValue(sig, "ServerInterfaces"));
                data["simaticInterfaces"] = CollectOpcUaItems(TryGetPropertyValue(sig, "SimaticInterfaces"));
                data["referenceNamespaces"] = CollectOpcUaItems(TryGetPropertyValue(sig, "ReferenceNamespaces"));

                return new ModelContextProtocol.ResponseJsonReport { Ok = true, Message = $"OPC UA config read for '{softwarePath}'.", Data = data };
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "GetOpcUaConfig failed for {SoftwarePath}", softwarePath);
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = $"Error: {ex.Message}", Data = data };
            }
        }

        private static JsonArray CollectOpcUaItems(object? collection)
        {
            var arr = new JsonArray();
            if (collection is not IEnumerable items || collection is string) return arr;
            foreach (var item in items)
            {
                if (item == null) continue;
                var obj = new JsonObject();
                foreach (var prop in new[] { "Name", "Comment", "Author", "Enabled", "UseStringNodeIds", "GenerateNodes", "GeneratedInterfaceName" })
                {
                    var val = TryGetPropertyValue(item, prop);
                    if (val != null) obj[prop] = JsonValue.Create(val.ToString());
                }
                arr.Add(obj);
            }
            return arr;
        }

        // Read or delete one OPC UA server interface / SIMATIC interface / reference namespace.
        // Native observation: an empty interface blocks PLC compilation with
        // "The OPC UA server interface ... is empty or does not contain unique nodes".
        // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
        public ResponseMessage ManageOpcUaInterface(string softwarePath, string interfaceName, string action = "read", string interfaceType = "ServerInterface", bool dryRun = true)
            => _session.RunHmiStepTool("ManageOpcUaInterface", meta => {
                if (action != "read" && action != "delete") throw new ArgumentException("action must be read/delete.");
                if (string.IsNullOrWhiteSpace(interfaceName)) throw new ArgumentException("Exact interfaceName required (GetOpcUaConfig lists them).");
                bool writing = action == "delete" && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var sig = GetOpcUaServerInterfaceGroup(plc) ?? throw new NotSupportedException("ServerInterfaceGroup not accessible on this PLC.");
                string collectionProp = interfaceType switch { "SimaticInterface" => "SimaticInterfaces", "ReferenceNamespace" => "ReferenceNamespaces", "ServerInterface" => "ServerInterfaces", _ => throw new ArgumentException("interfaceType must be ServerInterface/SimaticInterface/ReferenceNamespace.") };
                var collection = TryGetPropertyValue(sig, collectionProp) ?? throw new NotSupportedException(collectionProp + " not accessible.");
                var item = FindByName(collection, interfaceName) ?? throw new PortalException(PortalErrorCode.NotFound, interfaceType + " '" + interfaceName + "' not found in '" + softwarePath + "'.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["interfaceType"] = interfaceType; meta["interfaceName"] = interfaceName;
                meta["before"] = EngineeringScalarProperties.Read(item);
                if (action == "read") return "OPC UA interface read; no changes.";
                if (!writing) return "OPC UA interface deletion preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                EngineeringGroupOperations.Call(item, "Delete", Type.EmptyTypes);
                if (FindByName(TryGetPropertyValue(sig, collectionProp), interfaceName) != null) throw new InvalidOperationException("Interface still present after Delete.");
                meta["verifiedAbsent"] = true;
                return "OPC UA interface deleted and absence verified; compile afterwards, no save/download.";
            });

        public ResponseMessage SetOpcUaInterfaceEnabled(string softwarePath, string interfaceName, bool enabled, string interfaceType = "ServerInterface")
        {
            if (_session.IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = _session.ResolvePlc(softwarePath, PlcAccess.Write);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() };

            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new ResponseMessage { Message = "ServerInterfaceGroup not accessible." };

                string collectionProp = interfaceType switch
                {
                    "SimaticInterface" => "SimaticInterfaces",
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = TryGetPropertyValue(sig, collectionProp);
                var item = FindByName(collection, interfaceName);
                if (item == null)
                    return new ResponseMessage { Message = $"{interfaceType} '{interfaceName}' not found in '{softwarePath}'." };

                _session.TrySetProperty(item, "Enabled", enabled);
                return new ResponseMessage
                {
                    Message = $"{interfaceType} '{interfaceName}' {(enabled ? "enabled" : "disabled")}. Download to PLC to apply.",
                    Meta = new JsonObject { ["softwarePath"] = softwarePath, ["interfaceName"] = interfaceName, ["enabled"] = enabled }
                };
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "SetOpcUaInterfaceEnabled failed");
                return new ResponseMessage { Message = $"Error: {ex.Message}" };
            }
        }

        public ResponseMessage ExportOpcUaInterface(string softwarePath, string interfaceName, string exportPath, string interfaceType = "ServerInterface")
        {
            if (_session.IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() };

            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new ResponseMessage { Message = "ServerInterfaceGroup not accessible." };

                string collectionProp = interfaceType switch
                {
                    "SimaticInterface" => "SimaticInterfaces",
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = TryGetPropertyValue(sig, collectionProp);
                var item = FindByName(collection, interfaceName);
                if (item == null)
                    return new ResponseMessage { Message = $"{interfaceType} '{interfaceName}' not found." };

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                _session.TryInvokeMethodByName(item, "Export", new FileInfo(exportPath));
                return new ResponseMessage
                {
                    Message = $"{interfaceType} '{interfaceName}' exported to '{exportPath}'.",
                    Meta = new JsonObject { ["exportPath"] = exportPath }
                };
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "ExportOpcUaInterface failed");
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        public ResponseMessage ImportOpcUaInterface(string softwarePath, string importPath, string interfaceType = "ServerInterface")
        {
            if (_session.IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = _session.ResolvePlc(softwarePath, PlcAccess.Write);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() };

            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new ResponseMessage { Message = "ServerInterfaceGroup not accessible." };

                string collectionProp = interfaceType switch
                {
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = TryGetPropertyValue(sig, collectionProp);
                if (collection == null) return new ResponseMessage { Message = $"{collectionProp} collection not accessible." };

                // ServerInterfaceComposition.Create(name) then Import(file)
                // OR find existing and call Import
                // Check the file before creation, propagate Import failures and attempt to remove a newly created interface on failure.
                // Native observation: importing a missing file left an empty server interface when the exception was discarded.
                // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
                var fi = new FileInfo(importPath);
                if (!fi.Exists) return new ResponseMessage { Message = $"Import file not found: {importPath}", Meta = ResponseMeta.Unstamped(false) };
                var interfaceName = Path.GetFileNameWithoutExtension(importPath);
                var existing = FindByName(collection, interfaceName);

                object? target = existing; bool createdNow = false;
                if (target == null)
                {
                    target = _session.TryInvokeMethodByName(collection, "Create", interfaceName);
                    if (target == null) return new ResponseMessage { Message = $"Could not create {interfaceType} '{interfaceName}'.", Meta = ResponseMeta.Unstamped(false) };
                    createdNow = true;
                }
                var import = target.GetType().GetMethod("Import", new[] { typeof(FileInfo) });
                if (import == null) return new ResponseMessage { Message = $"Import(FileInfo) is not exposed by {target.GetType().Name}.", Meta = ResponseMeta.Unstamped(false) };
                try { import.Invoke(target, new object[] { fi }); }
                catch (TargetInvocationException tie)
                {
                    if (createdNow) { try { target.GetType().GetMethod("Delete", Type.EmptyTypes)?.Invoke(target, null); } catch { /* swallow(cleanup): Failure to delete a newly created interface must not replace the original import failure. */ } }
                    return new ResponseMessage { Message = $"Import failed: {(tie.InnerException ?? tie).Message}" + (createdNow ? $" (the new {interfaceType} '{interfaceName}' was removed again)" : ""), Meta = ResponseMeta.Unstamped(false) };
                }
                return new ResponseMessage { Message = createdNow
                    ? $"{interfaceType} '{interfaceName}' created and imported from '{importPath}'."
                    : $"Existing {interfaceType} '{interfaceName}' updated from '{importPath}'.", Meta = ResponseMeta.Unstamped(true, ("created", createdNow)) };
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "ImportOpcUaInterface failed");
                return new ResponseMessage { Message = $"Import failed: {ex.Message}" };
            }
        }

        private static object? FindByName(object? collection, string name)
        {
            if (collection is not IEnumerable items || collection is string) return null;
            foreach (var item in items)
            {
                if (item == null) continue;
                if (string.Equals(TryGetPropertyValue(item, "Name")?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return null;
        }

        #endregion

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
            if (_session.IsProjectNull()) throw new InvalidOperationException("No project is bound.");
            var plc = _session.GetPlcSoftware(softwarePath) ?? throw new ArgumentException("PLC not found: " + softwarePath + _session.AvailablePlcPathsSuffix());
            if (plc.GetService<OpcUaProvider>() == null) throw new NotSupportedException("PLC does not expose OpcUaProvider.");
            var unit = string.IsNullOrEmpty(unitName) ? null : plc.GetService<PlcUnitProvider>()?.UnitGroup.Units.Find(unitName);
            if (!string.IsNullOrEmpty(unitName) && unit == null) throw new ArgumentException("Software unit not found: " + unitName);
            // envelope: legacy-multiple-dynamic-fields
            var result = new JsonObject { ["success"] = true, ["dryRun"] = dryRun, ["softwarePath"] = softwarePath, ["unitName"] = unitName,
                ["interfaceName"] = interfaceName, ["namespaceUri"] = namespaceUri, ["outputPath"] = output.FullName, ["accessLevels"] = access.DeepClone(),
                ["source"] = "Siemens user-modelled OPC UA interface generation phases (317dfd06)",
                ["limitations"] = new JsonArray("Optimized nodes and string identifiers only.", "Nested FBs are UAObjects; their contained variables are not accessible through this generated interface.", "Output requires separate TIA import/compile validation. No live compatibility certification.") };
            if (dryRun) { result["generated"] = false; return result; }
            lock (OpcUaModelLock)
            {
                string scratch = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "TiaMcp-OpcUa-" + Guid.NewGuid().ToString("N"));
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
                finally { try { Directory.Delete(scratch, true); } catch { /* swallow(cleanup): Scratch deletion failure is reported without replacing the generated result or original failure. */ result["scratchCleanupFailed"] = scratch; } }
            }
            return result;
        }

        private object RequireOpcUaAccessControl(PlcSoftware plc)
        {
            var group = GetOpcUaServerInterfaceGroup(plc) ?? throw new NotSupportedException("OPC UA ServerInterfaceGroup unavailable for this PLC.");
            if (group.GetType().GetProperty("AccessControl") == null) throw new NotSupportedException("ServerInterfaceGroup.AccessControl is not exposed by this TIA Portal version (requires V21 or newer).");
            return EngineeringGroupOperations.Get(group, "AccessControl");
        }
        private static JsonObject ReadRole(object role)
        {
            var row = EngineeringScalarProperties.Read(role);
            row["namespacePermissions"] = new JsonArray(EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(role, "NamespacePermissions")).Select(p => (JsonNode)EngineeringScalarProperties.Read(p)).ToArray());
            return row;
        }
        // Typed NamespaceAccessRestriction row (V21 only; the type does not exist in the V20 PublicAPI).
        private static JsonObject RestrictionRow(object restriction)
        {
#if TIA_V20
            return EngineeringScalarProperties.Read(restriction);
#else
            if (restriction is global::Siemens.Engineering.SW.OpcUa.AccessControl.NamespaceAccessRestriction r)
                return new JsonObject { ["namespaceIndex"] = r.NamespaceIndex, ["namespaceUri"] = r.NamespaceUri, ["applyRestrictionsToBrowse"] = r.ApplyRestrictionsToBrowse, ["sessionRequired"] = r.SessionRequired, ["signingRequired"] = r.SigningRequired, ["encryptionRequired"] = r.EncryptionRequired, ["restrictionClass"] = r.GetType().Name };
            return EngineeringScalarProperties.Read(restriction);
#endif
        }
        private static object ExactByNamespaceUri(object collection, string namespaceUri)
        {
            HardwareServicesLogic.RequireExactName(namespaceUri, "namespaceUri");
            var matches = EngineeringGroupOperations.Items(collection).Where(x => string.Equals(x.GetType().GetProperty("NamespaceUri")?.GetValue(x)?.ToString(), namespaceUri, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Exact NamespaceUri matched " + matches.Length + " entries.");
            return matches[0];
        }
        public ResponseMessage ReadOpcUaAccessControl(string softwarePath, string section = "roles", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("ReadOpcUaAccessControl", meta => {
                HardwareServicesLogic.RequireOneOf(section, new[] { "roles", "restrictions" }, "section");
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var control = RequireOpcUaAccessControl(_session.ExactPlcForEngineering(softwarePath, false));
                var roles = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(control, "RoleMappings")).ToArray();
                var restrictions = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(control, "NamespaceAccessRestrictions")).ToArray();
                meta["roleCount"] = roles.Length; meta["restrictionCount"] = restrictions.Length; meta["section"] = section;
                var all = section == "roles" ? roles : restrictions;
                var rows = all.Skip(offset).Take(limit).Select(x => section == "roles" ? (JsonNode)ReadRole(x) : RestrictionRow(x)).ToArray();
                meta["records"] = new JsonArray(rows);
                foreach (var pair in HardwareServicesLogic.PageMeta(all.Length, offset, limit, rows.Length)) meta[pair.Key] = pair.Value?.DeepClone();
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                return "OPC UA server access control (" + section + ") read; no user secrets exposed, nothing changed.";
            });

        public ResponseMessage ManageOpcUaAccessControl(string softwarePath, string action, string roleName = "", string definedInNamespace = "", string projectRole = "",
            string namespaceUri = "", string permission = "", bool enabled = false, string propertiesJson = "{}", bool confirmChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageOpcUaAccessControl", meta => {
                HardwareServicesLogic.RequireOneOf(action, HardwareServicesLogic.OpcUaActions, "action");
                HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);
                using var exclusive = dryRun ? null : _session.AcquireHmiEditAccess();
                var control = RequireOpcUaAccessControl(_session.ExactPlcForEngineering(softwarePath, !dryRun));
                var roleMappings = EngineeringGroupOperations.Get(control, "RoleMappings");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                object? role = null;
                if (action != "setRestriction")
                {
                    HardwareServicesLogic.RequireExactName(roleName, "roleName"); meta["roleName"] = roleName;
                    role = EngineeringGroupOperations.Find(roleMappings, roleName);
                    if (role != null) meta["before"] = ReadRole(role);
                }
                switch (action)
                {
                    case "createRole":
                    case "addStandardRole":
                    {
                        if (role != null) throw new InvalidOperationException("Role already exists: " + roleName);
                        bool standard = action == "addStandardRole";
                        if (!standard) HardwareServicesLogic.RequireExactName(definedInNamespace, "definedInNamespace");
                        var signature = standard ? new[] { typeof(string) } : new[] { typeof(string), typeof(string) };
                        var method = standard ? "AddStandardOpcUaRole" : "Create";
                        if (roleMappings.GetType().GetMethod(method, signature) == null) throw new NotSupportedException("RoleMappingComposition." + method + " is unavailable.");
                        if (dryRun) return "Role creation preview; nothing changed.";
                        int before = EngineeringGroupOperations.Items(roleMappings).Count();
                        meta["mayHaveChanged"] = true;
                        var result = standard ? EngineeringGroupOperations.Call(roleMappings, method, signature, roleName) : EngineeringGroupOperations.Call(roleMappings, method, signature, roleName, definedInNamespace);
                        meta["apiCallSuccess"] = true;
                        if (EngineeringGroupOperations.Items(roleMappings).Count() != before + 1) throw new InvalidOperationException("Native call returned but role count did not increase by one.");
                        var created = EngineeringGroupOperations.Find(roleMappings, roleName) ?? (result is IEngineeringObject ? result : null) ?? throw new InvalidOperationException("Role count increased but the exact role name was not found.");
                        meta["after"] = ReadRole(created);
                        return "OPC UA role added and read back; project not saved, compiled or downloaded.";
                    }
                    case "deleteRole":
                    {
                        if (role == null) throw new PortalException(PortalErrorCode.NotFound, "Role not found: " + roleName);
                        if (role.GetType().GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("RoleMapping.Delete is unavailable.");
                        if (dryRun) return "Role delete preview; nothing changed.";
                        meta["mayHaveChanged"] = true;
                        EngineeringGroupOperations.Call(role, "Delete", Type.EmptyTypes); meta["apiCallSuccess"] = true;
                        if (EngineeringGroupOperations.Find(roleMappings, roleName) != null) throw new InvalidOperationException("Role remains after Delete.");
                        meta["verifiedAbsent"] = true;
                        return "OPC UA role deleted and verified absent; project not saved, compiled or downloaded.";
                    }
                    case "setProjectRole":
                    {
                        if (role == null) throw new PortalException(PortalErrorCode.NotFound, "Role not found: " + roleName);
                        HardwareServicesLogic.RequireExactName(projectRole, "projectRole");
                        if (role.GetType().GetMethod("SetProjectRole", new[] { typeof(string) }) == null) throw new NotSupportedException("RoleMapping.SetProjectRole is unavailable.");
                        if (dryRun) return "Project role mapping preview; nothing changed.";
                        meta["mayHaveChanged"] = true;
                        EngineeringGroupOperations.Call(role, "SetProjectRole", new[] { typeof(string) }, projectRole); meta["apiCallSuccess"] = true;
                        if (EngineeringGroupOperations.Get(role, "ProjectRole").ToString() != projectRole) throw new InvalidOperationException("ProjectRole readback differs.");
                        meta["after"] = ReadRole(role);
                        return "OPC UA role mapped to project role and read back; project not saved, compiled or downloaded.";
                    }
                    case "setPermission":
                    {
                        if (role == null) throw new PortalException(PortalErrorCode.NotFound, "Role not found: " + roleName);
                        HardwareServicesLogic.RequireOneOf(permission, HardwareServicesLogic.OpcUaPermissionNames, "permission");
                        var target = ExactByNamespaceUri(EngineeringGroupOperations.Get(role, "NamespacePermissions"), namespaceUri);
                        var flag = target.GetType().GetProperty(permission);
                        if (flag?.PropertyType != typeof(bool) || target.GetType().GetMethod("SetPermission", new[] { typeof(string), typeof(bool) }) == null)
                            throw new NotSupportedException("NamespacePermission." + permission + " / SetPermission(string,bool) unavailable; cannot verify readback.");
                        meta["permissionBefore"] = EngineeringScalarProperties.Read(target); meta["permission"] = permission; meta["enabled"] = enabled;
                        if (dryRun) return "Permission change preview; nothing changed.";
                        meta["mayHaveChanged"] = true;
                        EngineeringGroupOperations.Call(target, "SetPermission", new[] { typeof(string), typeof(bool) }, permission, enabled); meta["apiCallSuccess"] = true;
                        if (!Equals(flag.GetValue(target), enabled)) throw new InvalidOperationException("Permission readback differs from requested value.");
                        meta["permissionAfter"] = EngineeringScalarProperties.Read(target);
                        return "OPC UA namespace permission changed and read back; project not saved, compiled or downloaded.";
                    }
                    default:
                    {
                        var restriction = ExactByNamespaceUri(EngineeringGroupOperations.Get(control, "NamespaceAccessRestrictions"), namespaceUri);
                        var changes = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                        if (changes.Count == 0) throw new ArgumentException("propertiesJson must contain at least one property.");
                        var prepared = EngineeringScalarProperties.Prepare(restriction.GetType(), changes);
                        meta["before"] = RestrictionRow(restriction); meta["requestedProperties"] = changes.DeepClone();
                        if (dryRun) return "Namespace restriction preview; properties validated, nothing changed.";
                        EngineeringScalarProperties.Apply(restriction, prepared, meta); meta["apiCallSuccess"] = true;
                        meta["after"] = RestrictionRow(restriction);
                        return "OPC UA namespace restriction updated with readback; project not saved, compiled or downloaded.";
                    }
                }
            });
    }
}
