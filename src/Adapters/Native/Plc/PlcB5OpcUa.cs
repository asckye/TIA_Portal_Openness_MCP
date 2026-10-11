using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcpServer.ModelContextProtocol;
using EngineeringGroupOperations = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;
using EngineeringScalarProperties = TiaMcp.Adapters.Native.Plc.PlcB5Scalar;
using NativeResultState = TiaMcp.Adapters.Native.Plc.PlcB5Result;
using NativeFileOutput = TiaMcp.Adapters.Native.Plc.PlcSourceFileOutput;
using ResponseMeta = TiaMcp.Adapters.Native.Plc.PlcB5Meta;
using PortalException = TiaMcp.Adapters.Contracts.PlcSoftwareException;
#if PLC_OPC_UA
using Siemens.Engineering.SW.OpcUa;
#endif

namespace TiaMcp.Adapters.Native.Plc
{
    internal sealed partial class PlcB5Adapter
    {
#if PLC_OPC_UA
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

        public PlcB5Reply GetOpcUaConfig(string softwarePath)
        {
            // envelope: legacy-roundtrip-data-stamp
            var data = new PlcB5Object { ["softwarePath"] = softwarePath, ["timestamp"] = DateTime.Now.ToString("O") };

            if (_session.IsProjectNull())
                return new PlcB5Reply { Ok = false, Message = "No project open.", Data = data, Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };

            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null)
                return new PlcB5Reply { Ok = false, Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Data = data, Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };

            try
            {
                var provider = plc.GetService<OpcUaProvider>();
                if (provider == null)
                    return new PlcB5Reply { Ok = false, Message = "OpcUaProvider not available for this PLC.", Data = data, Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false)) };

                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null)
                    return new PlcB5Reply { Ok = false, Message = "ServerInterfaceGroup not accessible.", Data = data, Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false)) };

                data["serverInterfaces"] = CollectOpcUaItems(PlcB5Scalar.TryProperty(sig, "ServerInterfaces"));
                data["simaticInterfaces"] = CollectOpcUaItems(PlcB5Scalar.TryProperty(sig, "SimaticInterfaces"));
                data["referenceNamespaces"] = CollectOpcUaItems(PlcB5Scalar.TryProperty(sig, "ReferenceNamespaces"));

                return new PlcB5Reply { Ok = true, Message = $"OPC UA config read for '{softwarePath}'.", Data = data };
            }
            catch (Exception ex)
            {
                return new PlcB5Reply { Ok = false, Message = $"Error: {ex.Message}", Data = data };
            }
        }

        private static PlcB5Array CollectOpcUaItems(object? collection)
        {
            var arr = new PlcB5Array();
            if (collection is not IEnumerable items || collection is string) return arr;
            foreach (var item in items)
            {
                if (item == null) continue;
                var obj = new PlcB5Object();
                foreach (var prop in new[] { "Name", "Comment", "Author", "Enabled", "UseStringNodeIds", "GenerateNodes", "GeneratedInterfaceName" })
                {
                    var val = PlcB5Scalar.TryProperty(item, prop);
                    if (val != null) obj[prop] = PlcB5Scalar.Value(val.ToString());
                }
                arr.Add(obj);
            }
            return arr;
        }

        // Read or delete one OPC UA server interface / SIMATIC interface / reference namespace.
        // Native observation: an empty interface blocks PLC compilation with
        // "The OPC UA server interface ... is empty or does not contain unique nodes".
        // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
        public PlcB5Reply ManageOpcUaInterface(string softwarePath, string interfaceName, string action = "read", string interfaceType = "ServerInterface", bool dryRun = true)
            => RunTool("ManageOpcUaInterface", meta => {
                meta["mayHaveChanged"] = false;
                if (action != "read" && action != "delete") throw new ArgumentException("action must be read/delete.");
                if (string.IsNullOrWhiteSpace(interfaceName)) throw new ArgumentException("Exact interfaceName required (GetPlcOpcUaConfiguration lists them).");
                bool writing = action == "delete" && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var sig = GetOpcUaServerInterfaceGroup(plc) ?? throw new NotSupportedException("ServerInterfaceGroup not accessible on this PLC.");
                string collectionProp = interfaceType switch { "SimaticInterface" => "SimaticInterfaces", "ReferenceNamespace" => "ReferenceNamespaces", "ServerInterface" => "ServerInterfaces", _ => throw new ArgumentException("interfaceType must be ServerInterface/SimaticInterface/ReferenceNamespace.") };
                var collection = PlcB5Scalar.TryProperty(sig, collectionProp) ?? throw new NotSupportedException(collectionProp + " not accessible.");
                var item = FindByName(collection, interfaceName) ?? throw new PortalException("NotFound", interfaceType + " '" + interfaceName + "' not found in '" + softwarePath + "'.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["interfaceType"] = interfaceType; meta["interfaceName"] = interfaceName;
                meta["before"] = EngineeringScalarProperties.Read(item);
                if (action == "read") return "OPC UA interface read; no changes.";
                if (!writing) return "OPC UA interface deletion preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                EngineeringGroupOperations.Call(item, "Delete", Type.EmptyTypes);
                if (FindByName(PlcB5Scalar.TryProperty(sig, collectionProp), interfaceName) != null) throw new InvalidOperationException("Interface still present after Delete.");
                meta["verifiedAbsent"] = true;
                return "OPC UA interface deleted and absence verified; compile afterwards, no save/download.";
            });

        public PlcB5Reply SetOpcUaInterfaceEnabled(string softwarePath, string interfaceName, bool enabled, string interfaceType = "ServerInterface")
        {
            if (_session.IsProjectNull()) return new PlcB5Reply { Message = "No project open.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };
            var plc = _session.ResolvePlc(softwarePath, true);
            if (plc == null) return new PlcB5Reply { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };

            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new PlcB5Reply { Message = "ServerInterfaceGroup not accessible.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false)) };

                string collectionProp = interfaceType switch
                {
                    "SimaticInterface" => "SimaticInterfaces",
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = PlcB5Scalar.TryProperty(sig, collectionProp);
                var item = FindByName(collection, interfaceName);
                if (item == null)
                    return new PlcB5Reply { Message = $"{interfaceType} '{interfaceName}' not found in '{softwarePath}'.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };

                PlcB5Scalar.TrySetProperty(item, "Enabled", enabled);
                return new PlcB5Reply
                {
                    Message = $"{interfaceType} '{interfaceName}' {(enabled ? "enabled" : "disabled")}. Download to PLC to apply.",
                    Meta = new PlcB5Object { ["softwarePath"] = softwarePath, ["interfaceName"] = interfaceName, ["enabled"] = enabled }
                };
            }
            catch (Exception ex)
            {
                return new PlcB5Reply { Message = $"Error: {ex.Message}" };
            }
        }

        public PlcB5Reply ExportOpcUaInterface(string softwarePath, string interfaceName, string exportPath, string interfaceType = "ServerInterface")
        {
            if (_session.IsProjectNull()) return new PlcB5Reply { Message = "No project open.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };
            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null) return new PlcB5Reply { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };

            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new PlcB5Reply { Message = "ServerInterfaceGroup not accessible.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false)) };

                string collectionProp = interfaceType switch
                {
                    "SimaticInterface" => "SimaticInterfaces",
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = PlcB5Scalar.TryProperty(sig, collectionProp);
                var item = FindByName(collection, interfaceName);
                if (item == null)
                    return new PlcB5Reply { Message = $"{interfaceType} '{interfaceName}' not found.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                PlcB5Scalar.TryInvokeMethodByName(item, "Export", new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(exportPath)));
                return new PlcB5Reply
                {
                    Message = $"{interfaceType} '{interfaceName}' exported to '{exportPath}'.",
                    Meta = new PlcB5Object { ["exportPath"] = exportPath }
                };
            }
            catch (Exception ex)
            {
                _session.RecordExportFailure(ex);
                return new PlcB5Reply { Message = $"Export failed: {ex.Message}" };
            }
        }

        public PlcB5Reply ImportOpcUaInterface(string softwarePath, string importPath, string interfaceType = "ServerInterface")
        {
            if (_session.IsProjectNull()) return new PlcB5Reply { Message = "No project open.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };
            var plc = _session.ResolvePlc(softwarePath, true);
            if (plc == null) return new PlcB5Reply { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };

            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new PlcB5Reply { Message = "ServerInterfaceGroup not accessible.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false)) };

                string collectionProp = interfaceType switch
                {
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = PlcB5Scalar.TryProperty(sig, collectionProp);
                if (collection == null) return new PlcB5Reply { Message = $"{collectionProp} collection not accessible.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false)) };

                // ServerInterfaceComposition.Create(name) then Import(file)
                // OR find existing and call Import
                // Check the file before creation, propagate Import failures and attempt to remove a newly created interface on failure.
                // Native observation: importing a missing file left an empty server interface when the exception was discarded.
                // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
                var fi = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath));
                if (!fi.Exists) return new PlcB5Reply { Message = $"Import file not found: {importPath}", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };
                var interfaceName = Path.GetFileNameWithoutExtension(importPath);
                var existing = FindByName(collection, interfaceName);

                object? target = existing; bool createdNow = false;
                if (target == null)
                {
                    target = PlcB5Scalar.TryInvokeMethodByName(collection, "Create", interfaceName);
                    if (target == null) return new PlcB5Reply { Message = $"Could not create {interfaceType} '{interfaceName}'.", Meta = ResponseMeta.Unstamped(false) };
                    createdNow = true;
                }
                var import = target.GetType().GetMethod("Import", new[] { typeof(FileInfo) });
                if (import == null) return new PlcB5Reply { Message = $"Import(FileInfo) is not exposed by {target.GetType().Name}.", Meta = ResponseMeta.Unstamped(false) };
                try { import.Invoke(target, new object[] { fi }); }
                catch (TargetInvocationException tie)
                {
                    if (createdNow) { try { target.GetType().GetMethod("Delete", Type.EmptyTypes)?.Invoke(target, null); } catch { /* swallow(cleanup): Failure to delete a newly created interface must not replace the original import failure. */ } }
                    return new PlcB5Reply { Message = $"Import failed: {(tie.InnerException ?? tie).Message}" + (createdNow ? $" (the new {interfaceType} '{interfaceName}' was removed again)" : ""), Meta = ResponseMeta.Unstamped(false) };
                }
                return new PlcB5Reply { Message = createdNow
                    ? $"{interfaceType} '{interfaceName}' created and imported from '{importPath}'."
                    : $"Existing {interfaceType} '{interfaceName}' updated from '{importPath}'.", Meta = ResponseMeta.Unstamped(true, ("created", createdNow)) };
            }
            catch (Exception ex)
            {
                return new PlcB5Reply { Message = $"Import failed: {ex.Message}" };
            }
        }

        private static object? FindByName(object? collection, string name)
        {
            if (collection is not IEnumerable items || collection is string) return null;
            foreach (var item in items)
            {
                if (item == null) continue;
                if (string.Equals(PlcB5Scalar.TryProperty(item, "Name")?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return null;
        }

        #endregion

        private object RequireOpcUaAccessControl(PlcSoftware plc)
        {
            var group = GetOpcUaServerInterfaceGroup(plc) ?? throw new NotSupportedException("OPC UA ServerInterfaceGroup unavailable for this PLC.");
            if (group.GetType().GetProperty("AccessControl") == null) throw new NotSupportedException("ServerInterfaceGroup.AccessControl is not exposed by this TIA Portal version (requires V21 or newer).");
            return EngineeringGroupOperations.Get(group, "AccessControl");
        }
        private static PlcB5Object ReadRole(object role)
        {
            var row = EngineeringScalarProperties.Read(role);
            row["namespacePermissions"] = new PlcB5Array(EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(role, "NamespacePermissions")).Select(p => (object)EngineeringScalarProperties.Read(p)).ToArray());
            return row;
        }
        // Typed NamespaceAccessRestriction row (V21 only; the type does not exist in the V20 PublicAPI).
        private static PlcB5Object RestrictionRow(object restriction)
        {
#if !PLC_B5_V21
            return EngineeringScalarProperties.Read(restriction);
#else
            if (restriction is global::Siemens.Engineering.SW.OpcUa.AccessControl.NamespaceAccessRestriction r)
                return new PlcB5Object { ["namespaceIndex"] = r.NamespaceIndex, ["namespaceUri"] = r.NamespaceUri, ["applyRestrictionsToBrowse"] = r.ApplyRestrictionsToBrowse, ["sessionRequired"] = r.SessionRequired, ["signingRequired"] = r.SigningRequired, ["encryptionRequired"] = r.EncryptionRequired, ["restrictionClass"] = r.GetType().Name };
            return EngineeringScalarProperties.Read(restriction);
#endif
        }
        private static object ExactByNamespaceUri(object collection, string namespaceUri)
        {
            RequireExactName(namespaceUri, "namespaceUri");
            var matches = EngineeringGroupOperations.Items(collection).Where(x => string.Equals(x.GetType().GetProperty("NamespaceUri")?.GetValue(x)?.ToString(), namespaceUri, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Exact NamespaceUri matched " + matches.Length + " entries.");
            return matches[0];
        }
        public PlcB5Reply ReadOpcUaAccessControl(string softwarePath, string section = "roles", int offset = 0, int limit = 100)
            => RunTool("GetOpcUaAccessControl", meta => {
                meta["mayHaveChanged"] = false;
                RequireOneOf(section, new[] { "roles", "restrictions" }, "section");
                ValidatePagination(offset, limit);
                var control = RequireOpcUaAccessControl(_session.ExactPlcForEngineering(softwarePath, false));
                var roles = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(control, "RoleMappings")).ToArray();
                var restrictions = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(control, "NamespaceAccessRestrictions")).ToArray();
                meta["roleCount"] = roles.Length; meta["restrictionCount"] = restrictions.Length; meta["section"] = section;
                var all = section == "roles" ? roles : restrictions;
                var rows = all.Skip(offset).Take(limit).Select(x => section == "roles" ? (object)ReadRole(x) : RestrictionRow(x)).ToArray();
                meta["records"] = new PlcB5Array(rows);
                foreach (var pair in PageMeta(all.Length, offset, limit, rows.Length)) meta[pair.Key] = pair.Value?.DeepClone();
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                return "OPC UA server access control (" + section + ") read; no user secrets exposed, nothing changed.";
            });

        public PlcB5Reply ManageOpcUaAccessControl(string softwarePath, string action, string roleName = "", string definedInNamespace = "", string projectRole = "",
            string namespaceUri = "", string permission = "", bool enabled = false, string propertiesJson = "{}", bool confirmChange = false, bool dryRun = true)
            => RunTool("ManageOpcUaAccessControl", meta => {
                meta["mayHaveChanged"] = false;
                RequireOneOf(action, OpcUaActions, "action");
                RequireConfirmation(confirmChange, "confirmChange", dryRun);
                using var exclusive = dryRun ? null : _session.AcquireHmiEditAccess();
                var control = RequireOpcUaAccessControl(_session.ExactPlcForEngineering(softwarePath, !dryRun));
                var roleMappings = EngineeringGroupOperations.Get(control, "RoleMappings");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                object? role = null;
                if (action != "setRestriction")
                {
                    RequireExactName(roleName, "roleName"); meta["roleName"] = roleName;
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
                        if (!standard) RequireExactName(definedInNamespace, "definedInNamespace");
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
                        if (role == null) throw new PortalException("NotFound", "Role not found: " + roleName);
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
                        if (role == null) throw new PortalException("NotFound", "Role not found: " + roleName);
                        RequireExactName(projectRole, "projectRole");
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
                        if (role == null) throw new PortalException("NotFound", "Role not found: " + roleName);
                        RequireOneOf(permission, OpcUaPermissionNames, "permission");
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
                        var changes = ObjectInput(propertiesJson) ?? throw new ArgumentException("propertiesJson must be an object.");
                        if (changes.Count == 0) throw new ArgumentException("propertiesJson must contain at least one property.");
                        var prepared = EngineeringScalarProperties.Prepare(restriction.GetType(), changes);
                        meta["before"] = RestrictionRow(restriction); meta["requestedProperties"] = changes;
                        if (dryRun) return "Namespace restriction preview; properties validated, nothing changed.";
                        EngineeringScalarProperties.Apply(restriction, prepared, meta); meta["apiCallSuccess"] = true;
                        meta["after"] = RestrictionRow(restriction);
                        return "OPC UA namespace restriction updated with readback; project not saved, compiled or downloaded.";
                    }
                }
            });

#endif
    }
}
