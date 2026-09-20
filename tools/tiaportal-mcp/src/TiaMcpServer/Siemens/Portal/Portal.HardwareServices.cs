using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.Systemdiagnostics.Settings;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.WatchAndForceTables;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // Types are resolved from the assembly that carries HardwareObject, so V20 (no CommunicationConnections) degrades to NotSupported.
        // V20 HardwareObject lacks GetService; Device/DeviceItem implement IEngineeringServiceProvider on both versions.
        private static IEngineeringServiceProvider ServiceProvider(HardwareObject owner)
            => owner as IEngineeringServiceProvider ?? throw new NotSupportedException(owner.GetType().FullName + " is not a service provider.");
        private static Type RequireHardwareApiType(string typeName)
            => typeof(HardwareObject).Assembly.GetType(typeName) ?? typeof(PlcSoftware).Assembly.GetType(typeName)
               ?? throw new NotSupportedException(typeName + " is not exposed by the connected TIA Portal Openness version (requires V21 or newer).");
        // 2.7.46: the HW connection composition is NOT a service - it is the Connections property of the V21 feature service
        // Siemens.Engineering.HW.Features.CommunicationManagement on the CPU device item (real project: GetService<ConnectionComposition>
        // answered "Official service unavailable in installed API" on 1515F-2 PN V2.9 although the API has the type).
        private static object RequireConnectionComposition(HardwareObject owner)
        {
            var management = OfficialServiceAccess.Require(owner, "Siemens.Engineering.HW.Features.CommunicationManagement", typeof(HardwareObject).Assembly.GetName().Name!);
            return management.GetType().GetProperty("Connections")?.GetValue(management)
                ?? throw new NotSupportedException("CommunicationManagement.Connections is null on the selected hardware object.");
        }
        private static string? LinkName(object connection, string property)
        {
            var link = connection.GetType().GetProperty(property)?.GetValue(connection);
            return link == null ? null : link.GetType().GetProperty("Name")?.GetValue(link)?.ToString() ?? link.GetType().Name;
        }
        private static JsonObject ReadConnection(object connection)
        {
            var row = EngineeringScalarProperties.Read(connection);
            foreach (var link in new[] { "LocalTarget", "PartnerTarget", "LocalInterface", "PartnerInterface" }) row[char.ToLowerInvariant(link[0]) + link.Substring(1) + "Name"] = LinkName(connection, link);
            return row;
        }
        private static Node ExactInterfaceNode(HardwareObject interfaceItem, string nodeName, string parameter)
        {
            var network = ServiceProvider(interfaceItem).GetService<NetworkInterface>() ?? throw new InvalidOperationException(parameter + " must identify a DeviceItem that exposes NetworkInterface.");
            var nodes = EngineeringGroupOperations.Items(network.Nodes).Cast<Node>().ToArray();
            if (string.IsNullOrEmpty(nodeName))
            {
                if (nodes.Length != 1) throw new ArgumentException(parameter + " interface has " + nodes.Length + " nodes; give the exact node name: " + string.Join(", ", nodes.Select(n => n.Name)));
                return nodes[0];
            }
            return EngineeringGroupOperations.Find(network.Nodes, nodeName) as Node ?? throw new InvalidOperationException("Node not found on interface: " + nodeName);
        }
        private static object[] ConnectionsNamed(object composition, string name)
            => EngineeringGroupOperations.Items(composition).Where(c => string.Equals(c.GetType().GetProperty("LocalConnectionName")?.GetValue(c)?.ToString(), name, StringComparison.Ordinal)).ToArray();

        public ResponseMessage ReadCommunicationConnections(string devicePathJson, string itemPathJson = "[]", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadCommunicationConnections", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var owner = ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["owner"] = EngineeringScalarProperties.Read(owner);
                var composition = RequireConnectionComposition(owner);
                var all = EngineeringGroupOperations.Items(composition).ToArray();
                var rows = all.Skip(offset).Take(limit).Select(c => (JsonNode)ReadConnection(c)).ToArray();
                meta["records"] = new JsonArray(rows);
                foreach (var pair in HardwareServicesLogic.PageMeta(all.Length, offset, limit, rows.Length)) meta[pair.Key] = pair.Value?.DeepClone();
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "Scalar properties of Siemens.Engineering.HW.CommunicationConnections.* plus link names; complex members excluded.";
                return "Communication connections of the exact hardware object read; no modification.";
            });

        public ResponseMessage ManageCommunicationConnection(string devicePathJson, string itemPathJson, string action, string connectionType = "", string connectionName = "",
            string localInterfaceItemPathJson = "[]", string localNodeName = "", string partnerDevicePathJson = "[]", string partnerItemPathJson = "[]",
            string partnerInterfaceItemPathJson = "[]", string partnerNodeName = "", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageCommunicationConnection", meta => {
                HardwareServicesLogic.RequireOneOf(action, new[] { "create", "delete" }, "action");
                if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var owner = ExactEngineeringHardware(devicePathJson, itemPathJson);
                var composition = RequireConnectionComposition(owner);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["owner"] = EngineeringScalarProperties.Read(owner);
                int before = EngineeringGroupOperations.Items(composition).Count(); meta["countBefore"] = before;
                if (action == "delete")
                {
                    HardwareServicesLogic.RequireExactName(connectionName, "connectionName");
                    var matches = ConnectionsNamed(composition, connectionName);
                    if (matches.Length != 1) throw new InvalidOperationException("Exact LocalConnectionName matched " + matches.Length + " connections; refusing.");
                    if (matches[0].GetType().GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Connection.Delete is unavailable.");
                    meta["before"] = ReadConnection(matches[0]);
                    if (dryRun) return "Connection delete preview; nothing changed.";
                    meta["mayHaveChanged"] = true;
                    EngineeringGroupOperations.Call(matches[0], "Delete", Type.EmptyTypes);
                    if (ConnectionsNamed(composition, connectionName).Length != 0) throw new InvalidOperationException("Connection remains after Delete.");
                    meta["apiCallSuccess"] = true; meta["verifiedAbsent"] = true; meta["countAfter"] = EngineeringGroupOperations.Items(composition).Count();
                    return "Connection deleted and verified absent; project not saved, compiled or downloaded.";
                }
                var kind = RequireHardwareApiType(HardwareServicesLogic.ConnectionTypeName(connectionType));
                var create = composition.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3
                        && m.GetParameters()[0].ParameterType == typeof(Node) && m.GetParameters()[1].ParameterType == typeof(DeviceItem) && m.GetParameters()[2].ParameterType == typeof(Node))
                    ?? throw new NotSupportedException("ConnectionComposition.Create<T>(Node, DeviceItem, Node) is unavailable.");
                var localInterface = ExactEngineeringHardware(devicePathJson, localInterfaceItemPathJson);
                var localNode = ExactInterfaceNode(localInterface, localNodeName, "localInterfaceItemPathJson");
                var partnerTarget = ExactEngineeringHardware(partnerDevicePathJson, partnerItemPathJson) as DeviceItem ?? throw new ArgumentException("partnerItemPathJson must identify a DeviceItem (nonempty path).");
                var partnerInterface = ExactEngineeringHardware(partnerDevicePathJson, partnerInterfaceItemPathJson);
                var partnerNode = ExactInterfaceNode(partnerInterface, partnerNodeName, "partnerInterfaceItemPathJson");
                if (!string.IsNullOrEmpty(connectionName) && ConnectionsNamed(composition, HardwareServicesLogic.RequireExactName(connectionName, "connectionName")).Length != 0)
                    throw new InvalidOperationException("A connection with this LocalConnectionName already exists on the owner.");
                meta["request"] = new JsonObject { ["connectionType"] = kind.FullName, ["localNode"] = localNode.Name, ["localSubnet"] = localNode.ConnectedSubnet?.Name,
                    ["partnerTarget"] = partnerTarget.Name, ["partnerNode"] = partnerNode.Name, ["partnerSubnet"] = partnerNode.ConnectedSubnet?.Name, ["connectionName"] = connectionName };
                if (dryRun) return "Connection create preview; objects and native signature resolved, nothing changed.";
                meta["mayHaveChanged"] = true;
                object created;
                try { created = create.MakeGenericMethod(kind).Invoke(composition, new object[] { localNode, partnerTarget, partnerNode }) ?? throw new InvalidOperationException("Native Create returned null."); }
                catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
                meta["apiCallSuccess"] = true;
                if (!string.IsNullOrEmpty(connectionName))
                {
                    var nameProperty = created.GetType().GetProperty("LocalConnectionName");
                    if (nameProperty?.SetMethod?.IsPublic != true) throw new InvalidOperationException("Connection created but LocalConnectionName is not writable; it keeps the native default name.");
                    nameProperty.SetValue(created, connectionName);
                    if (nameProperty.GetValue(created)?.ToString() != connectionName) throw new InvalidOperationException("Connection created but LocalConnectionName readback differs.");
                }
                meta["after"] = ReadConnection(created);
                int after = EngineeringGroupOperations.Items(composition).Count(); meta["countAfter"] = after;
                if (after != before + 1) throw new InvalidOperationException("Native Create returned but the connection count did not increase by one.");
                return "Connection created and read back; project not saved, compiled or downloaded.";
            });

        // 2.7.35: typed rows (WatchTableAccessRule.WatchTable / ForceTableAccessRule.ForceTable carry the table; Access is writable natively).
        private static JsonObject ReadAccessRule(object rule, string tableProperty)
        {
            var row = EngineeringScalarProperties.Read(rule);
            row["tableName"] = rule switch { WatchTableAccessRule w => w.WatchTable?.Name, ForceTableAccessRule f => f.ForceTable?.Name, _ => LinkName(rule, tableProperty) };
            row["ruleClass"] = rule.GetType().Name;
            return row;
        }
        public ResponseMessage ManageWatchForceTableWebAccess(string devicePathJson, string itemPathJson, string action = "read", string softwarePath = "",
            string tableKind = "watch", string tablePath = "", string access = "Read", bool confirmChange = false, bool dryRun = true)
            => RunHmiStepTool("ManageWatchForceTableWebAccess", meta => {
                HardwareServicesLogic.RequireOneOf(action, new[] { "read", "assign", "unassign" }, "action");
                HardwareServicesLogic.RequireOneOf(tableKind, new[] { "watch", "force" }, "tableKind");
                bool writing = action != "read" && !dryRun;
                if (action != "read") HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);
                using var exclusive = writing ? AcquireHmiEditAccess() : null;
                var item = ExactEngineeringHardware(devicePathJson, itemPathJson) as DeviceItem ?? throw new ArgumentException("Exact CPU DeviceItem path required.");
                var manager = item.GetService<WatchAndForceTableAccessManager>() ?? throw new NotSupportedException("WatchAndForceTableAccessManager unavailable on this DeviceItem.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["watchTableRules"] = new JsonArray(manager.WatchtableAccessRules.Select(r => (JsonNode)ReadAccessRule(r, "WatchTable")).ToArray());
                meta["forceTableRules"] = new JsonArray(manager.ForcetableAccessRules.Select(r => (JsonNode)ReadAccessRule(r, "ForceTable")).ToArray());
                meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                if (action == "read") return "Web-server watch/force table access rules read; no change.";
                var requested = (WatchAndForceTableAccess)Enum.Parse(typeof(WatchAndForceTableAccess), HardwareServicesLogic.RequireOneOf(access, HardwareServicesLogic.TableAccessValues, "access"));
                var plc = ExactPlcForEngineering(softwarePath, writing);
                var parts = EngineeringGroupOperations.Parts(tablePath);
                var group = EngineeringGroupOperations.Group(plc.WatchAndForceTableGroup, string.Join("/", parts.Take(parts.Length - 1)));
                var table = EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(group, tableKind == "watch" ? "WatchTables" : "ForceTables"), parts.Last())
                    ?? throw new PortalException(PortalErrorCode.NotFound, "Exact " + tableKind + " table not found: " + tablePath);
                meta["tablePath"] = tablePath; meta["requestedAccess"] = requested.ToString();
                WatchTableAccessRuleComposition watchRules = manager.WatchtableAccessRules; ForceTableAccessRuleComposition forceRules = manager.ForcetableAccessRules;
                object? Existing() => tableKind == "watch" ? (object?)watchRules.Find((PlcWatchTable)table) : forceRules.Find((PlcForceTable)table);
                object Create() => tableKind == "watch" ? (object)watchRules.Create((PlcWatchTable)table, requested) : forceRules.Create((PlcForceTable)table, requested);
                var existing = Existing();
                meta["before"] = existing == null ? null : ReadAccessRule(existing, tableKind == "watch" ? "WatchTable" : "ForceTable");
                if (action == "unassign")
                {
                    if (existing == null) throw new PortalException(PortalErrorCode.NotFound, "No access rule exists for this table.");
                    if (dryRun) return "Unassign preview; nothing changed.";
                    meta["mayHaveChanged"] = true;
                    if (existing is WatchTableAccessRule watchRule) watchRule.Delete(); else if (existing is ForceTableAccessRule forceRule) forceRule.Delete(); else EngineeringGroupOperations.Call(existing, "Delete", Type.EmptyTypes);
                    if (Existing() != null) throw new InvalidOperationException("Access rule remains after Delete.");
                    meta["verifiedAbsent"] = true;
                    return "Table access rule removed and verified; project not saved, compiled or downloaded.";
                }
                var accessProperty = existing?.GetType().GetProperty("Access");
                if (existing != null && Equals(accessProperty?.GetValue(existing), requested)) { meta["alreadyAssigned"] = true; return "Access rule already matches; nothing to change."; }
                if (existing != null && accessProperty?.SetMethod?.IsPublic != true) throw new NotSupportedException("Existing rule Access is read-only; unassign first, then assign.");
                if (dryRun) return "Assign preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                if (existing is WatchTableAccessRule existingWatch) existingWatch.Access = requested;
                else if (existing is ForceTableAccessRule existingForce) existingForce.Access = requested;
                else if (existing != null) accessProperty!.SetValue(existing, requested);
                else Create();
                var after = Existing() ?? throw new InvalidOperationException("Rule missing after assign.");
                if (!Equals(after.GetType().GetProperty("Access")?.GetValue(after), requested)) throw new InvalidOperationException("Access readback differs from requested value.");
                meta["after"] = ReadAccessRule(after, tableKind == "watch" ? "WatchTable" : "ForceTable");
                return "Table access rule assigned and read back; project not saved, compiled or downloaded.";
            });

        public ResponseMessage ExchangeSystemDiagnosticsSettings(string action, string filePath, string devicePathJson = "[]", string itemPathJson = "[]", bool confirmImport = false, bool dryRun = true)
            => RunHmiStepTool("ExchangeSystemDiagnosticsSettings", meta => {
                HardwareServicesLogic.RequireOneOf(action, new[] { "export", "import" }, "action");
                // 2.7.46 real project: TIA answers "Filename suffix must be .dat" for anything else.
                if (!(filePath ?? "").EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("filePath must end in .dat (SystemdiagnosticsSettingsDataProvider format).");
                bool writing = !dryRun;
                if (action == "import") HardwareServicesLogic.RequireConfirmation(confirmImport, "confirmImport", dryRun);
                using var exclusive = writing && action == "import" ? AcquireHmiEditAccess() : null;
                IEngineeringServiceProvider owner = devicePathJson == "[]" ? (IEngineeringServiceProvider)_project! : ServiceProvider(ExactEngineeringHardware(devicePathJson, itemPathJson));
                var provider = owner.GetService<SystemdiagnosticsSettingsDataProvider>() ?? throw new NotSupportedException("SystemdiagnosticsSettingsDataProvider unavailable on the selected owner.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["ownerType"] = owner.GetType().FullName;
                if (action == "export")
                {
                    var file = NativeFileOutput.Plan(filePath); meta["plannedFile"] = file.FullName;
                    if (dryRun) return "System diagnostics settings export preview; no file written.";
                    meta["mayHaveWrittenFiles"] = true;
                    var result = provider.Export(file);
                    OfficialServiceAccess.AttachResult(meta, result);
                    meta["file"] = NativeFileOutput.Verify(file);
                    if (result?.State.ToString() == "Error") throw new PortalException(PortalErrorCode.ExportFailed, "Native export reported Error state.");
                    return "System diagnostics settings exported to a new file and hashed; content semantics not verified.";
                }
                var source = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath"); meta["sourceFile"] = NativeFileOutput.Verify(source);
                if (dryRun) return "System diagnostics settings import preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                var imported = provider.Import(source);
                OfficialServiceAccess.AttachResult(meta, imported);
                if (imported?.State.ToString() == "Error") throw new PortalException(PortalErrorCode.ImportFailed, "Native import reported Error state.");
                return "System diagnostics settings imported (native state attached); project not saved, compiled or downloaded.";
            });

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
        // 2.7.35: typed NamespaceAccessRestriction row (V21 only; the type does not exist in the V20 PublicAPI).
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
            => RunHmiStepTool("ReadOpcUaAccessControl", meta => {
                HardwareServicesLogic.RequireOneOf(section, new[] { "roles", "restrictions" }, "section");
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var control = RequireOpcUaAccessControl(ExactPlcForEngineering(softwarePath, false));
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
            => RunHmiStepTool("ManageOpcUaAccessControl", meta => {
                HardwareServicesLogic.RequireOneOf(action, HardwareServicesLogic.OpcUaActions, "action");
                HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);
                using var exclusive = dryRun ? null : AcquireHmiEditAccess();
                var control = RequireOpcUaAccessControl(ExactPlcForEngineering(softwarePath, !dryRun));
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

        public ResponseMessage ImportDeviceAml(string filePath, string logFilePath, string importOption = "RetainTiaDevice", bool confirmImport = false, bool dryRun = true)
            => RunHmiStepTool("ImportDeviceAml", meta => {
                var option = (CaxImportOptions)Enum.Parse(typeof(CaxImportOptions), HardwareServicesLogic.RequireOneOf(importOption, HardwareServicesLogic.CaxImportOptions, "importOption"));
                HardwareServicesLogic.RequireConfirmation(confirmImport, "confirmImport", dryRun);
                var source = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath");
                var log = NativeFileOutput.Plan(logFilePath);
                using var exclusive = dryRun ? null : AcquireHmiEditAccess();
                var cax = _project!.GetService<CaxProvider>() ?? throw new NotSupportedException("CaxProvider unavailable for this project.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["importOption"] = option.ToString();
                meta["sourceFile"] = NativeFileOutput.Verify(source); meta["plannedLogFile"] = log.FullName;
                meta["deviceCountBefore"] = EnumerateAllDevices().Count();
                if (dryRun) return "CAx/AutomationML import preview; source hashed, nothing imported.";
                meta["mayHaveChanged"] = true; meta["mayHaveWrittenFiles"] = true;
                bool ok = cax.Import(source, log, option);
                meta["apiCallSuccess"] = true; meta["nativeResult"] = ok; meta["deviceCountAfter"] = EnumerateAllDevices().Count();
                log.Refresh();
                if (log.Exists && log.Length > 0) meta["logFile"] = NativeFileOutput.Verify(log); else { meta["logFile"] = null; meta["logFileMissing"] = true; }
                if (!ok) throw new PortalException(PortalErrorCode.ImportFailed, "CaxProvider.Import returned false; inspect the native log file.");
                return "CAx/AutomationML import returned true (native log hashed); imported content not semantically verified. Project not saved, compiled or downloaded.";
            });

        public ResponseMessage ReadHardwareFeatures(string devicePathJson, string itemPathJson = "[]", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadHardwareFeatures", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var owner = ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["owner"] = EngineeringScalarProperties.Read(owner);
                var advertised = ServiceProvider(owner).GetServiceInfos().Select(i => i.Type.FullName ?? i.Type.Name).ToArray();
                meta["advertisedServices"] = new JsonArray(advertised.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
                var getService = typeof(IEngineeringServiceProvider).GetMethod("GetService")!;
                var rows = new System.Collections.Generic.List<JsonObject>();
                foreach (var feature in HardwareServicesLogic.FeatureCatalog)
                {
                    var typeName = HardwareServicesLogic.FeatureTypeName(feature);
                    var type = typeof(HardwareObject).Assembly.GetType(typeName) ?? typeof(PlcSoftware).Assembly.GetType(typeName);
                    var row = new JsonObject { ["feature"] = feature, ["type"] = typeName, ["typeAvailableInApi"] = type != null, ["present"] = false };
                    if (type == null || !typeof(IEngineeringService).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface) { row["skipped"] = type == null ? "type absent on this version" : "not a concrete service type"; rows.Add(row); continue; }
                    object? service;
                    try { service = getService.MakeGenericMethod(type).Invoke(owner, null); }
                    catch (TargetInvocationException ex) { row["probeError"] = (ex.InnerException ?? ex).GetBaseException().Message; rows.Add(row); continue; }
                    row["present"] = service != null;
                    if (service != null) row["values"] = HardwareServicesLogic.FeatureValuesAllowed(feature) ? EngineeringScalarProperties.Read(service) : new JsonObject { ["withheld"] = "credential-related feature; values not dumped" };
                    rows.Add(row);
                }
                var page = rows.Skip(offset).Take(limit).Select(r => (JsonNode)r).ToArray();
                meta["records"] = new JsonArray(page);
                meta["presentCount"] = rows.Count(r => r["present"]!.GetValue<bool>());
                foreach (var pair in HardwareServicesLogic.PageMeta(rows.Count, offset, limit, page.Length)) meta[pair.Key] = pair.Value?.DeepClone();
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "Catalog of Siemens.Engineering.HW.Features.* from the V21 XML docs; presence via GetService<T>, scalar values only. Nothing written.";
                return "Hardware feature services probed on the exact hardware object; read-only.";
            });
    }
}
