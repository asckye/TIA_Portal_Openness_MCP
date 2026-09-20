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

namespace TiaMcpServer.Siemens
{
    // Partial: opcua. Extracted from Portal.cs (god-file split); behavior unchanged.
    public partial class Portal
    {
        #region opcua

        /// <summary>Returns the ServerInterfaceGroup node (OpcUaProvider.CommunicationGroup.ServerInterfaceGroup, typed since 2.7.35).</summary>
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
            var data = new JsonObject { ["softwarePath"] = softwarePath, ["timestamp"] = DateTime.Now.ToString("O") };

            if (IsProjectNull())
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "No project open.", Data = data };

            var plc = GetPlcSoftware(softwarePath);
            if (plc == null)
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = $"PLC software not found: '{softwarePath}'.", Data = data };

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
                _logger?.LogError(ex, "GetOpcUaConfig failed for {SoftwarePath}", softwarePath);
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

        // 2.7.48: delete (or read) one OPC UA server interface / SIMATIC interface / reference namespace - there was no way to remove
        // an interface (the 2.7.45 ImportOpcUaInterface bug left an empty server interface behind that made the whole PLC fail to
        // compile: "The OPC UA server interface ... is empty or does not contain unique nodes").
        public ResponseMessage ManageOpcUaInterface(string softwarePath, string interfaceName, string action = "read", string interfaceType = "ServerInterface", bool dryRun = true)
            => RunHmiStepTool("ManageOpcUaInterface", meta => {
                if (action != "read" && action != "delete") throw new ArgumentException("action must be read/delete.");
                if (string.IsNullOrWhiteSpace(interfaceName)) throw new ArgumentException("Exact interfaceName required (GetOpcUaConfig lists them).");
                bool writing = action == "delete" && !dryRun;
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
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
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

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

                TrySetProperty(item, "Enabled", enabled);
                return new ResponseMessage
                {
                    Message = $"{interfaceType} '{interfaceName}' {(enabled ? "enabled" : "disabled")}. Download to PLC to apply.",
                    Meta = new JsonObject { ["softwarePath"] = softwarePath, ["interfaceName"] = interfaceName, ["enabled"] = enabled }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SetOpcUaInterfaceEnabled failed");
                return new ResponseMessage { Message = $"Error: {ex.Message}" };
            }
        }

        public ResponseMessage ExportOpcUaInterface(string softwarePath, string interfaceName, string exportPath, string interfaceType = "ServerInterface")
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

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
                TryInvokeMethodByName(item, "Export", new FileInfo(exportPath));
                return new ResponseMessage
                {
                    Message = $"{interfaceType} '{interfaceName}' exported to '{exportPath}'.",
                    Meta = new JsonObject { ["exportPath"] = exportPath }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ExportOpcUaInterface failed");
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        public ResponseMessage ImportOpcUaInterface(string softwarePath, string importPath, string interfaceType = "ServerInterface")
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

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
                // 2.7.46 real project: a missing file still answered "created and imported" and left an EMPTY server interface behind,
                // because the reflective Import swallowed its exception - the file is checked first and Import errors are propagated
                // (a freshly created interface is removed again when its import fails).
                var fi = new FileInfo(importPath);
                if (!fi.Exists) return new ResponseMessage { Message = $"Import file not found: {importPath}", Meta = new JsonObject { ["success"] = false } };
                var interfaceName = Path.GetFileNameWithoutExtension(importPath);
                var existing = FindByName(collection, interfaceName);

                object? target = existing; bool createdNow = false;
                if (target == null)
                {
                    target = TryInvokeMethodByName(collection, "Create", interfaceName);
                    if (target == null) return new ResponseMessage { Message = $"Could not create {interfaceType} '{interfaceName}'.", Meta = new JsonObject { ["success"] = false } };
                    createdNow = true;
                }
                var import = target.GetType().GetMethod("Import", new[] { typeof(FileInfo) });
                if (import == null) return new ResponseMessage { Message = $"Import(FileInfo) is not exposed by {target.GetType().Name}.", Meta = new JsonObject { ["success"] = false } };
                try { import.Invoke(target, new object[] { fi }); }
                catch (TargetInvocationException tie)
                {
                    if (createdNow) { try { target.GetType().GetMethod("Delete", Type.EmptyTypes)?.Invoke(target, null); } catch { } }
                    return new ResponseMessage { Message = $"Import failed: {(tie.InnerException ?? tie).Message}" + (createdNow ? $" (the new {interfaceType} '{interfaceName}' was removed again)" : ""), Meta = new JsonObject { ["success"] = false } };
                }
                return new ResponseMessage { Message = createdNow
                    ? $"{interfaceType} '{interfaceName}' created and imported from '{importPath}'."
                    : $"Existing {interfaceType} '{interfaceName}' updated from '{importPath}'.", Meta = new JsonObject { ["success"] = true, ["created"] = createdNow } };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ImportOpcUaInterface failed");
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
    }
}
