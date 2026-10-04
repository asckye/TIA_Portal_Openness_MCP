using Siemens.Engineering.HW.Utilities;
using Siemens.Engineering.SW.Tags;
using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using Microsoft.Extensions.Logging;
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
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Security;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.Systemdiagnostics.Settings;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.WatchAndForceTables;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class HardwareServicesService
    {
        private readonly IEngineeringSession _session;

        public HardwareServicesService(IEngineeringSession session) => _session = session;
        private static Type RequireHardwareApiType(string typeName)
            => typeof(HardwareObject).Assembly.GetType(typeName) ?? typeof(PlcSoftware).Assembly.GetType(typeName)
               ?? throw new NotSupportedException(typeName + " is not exposed by the connected TIA Portal Openness version (requires V21 or newer).");
        // The HW connection composition is NOT a service - it is the Connections property of the V21 feature service
        // Siemens.Engineering.HW.Features.CommunicationManagement on the CPU device item (real project: GetService<ConnectionComposition>
        // answered "Official service unavailable in installed API" on 1515F-2 PN V2.9 although the API has the type;
        // TIA Portal V21, 2026-09-20, docs/reference/real-machine-ledger.md).
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
        private Node ExactInterfaceNode(HardwareObject interfaceItem, string nodeName, string parameter)
        {
            var network = _session.ServiceProvider(interfaceItem).GetService<NetworkInterface>() ?? throw new InvalidOperationException(parameter + " must identify a DeviceItem that exposes NetworkInterface.");
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
            => _session.RunHmiStepTool("ReadCommunicationConnections", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var owner = _session.ExactEngineeringHardware(devicePathJson, itemPathJson);
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
            => _session.RunHmiStepTool("ManageCommunicationConnection", meta => {
                HardwareServicesLogic.RequireOneOf(action, new[] { "create", "delete" }, "action");
                if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var owner = _session.ExactEngineeringHardware(devicePathJson, itemPathJson);
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
                var localInterface = _session.ExactEngineeringHardware(devicePathJson, localInterfaceItemPathJson);
                var localNode = ExactInterfaceNode(localInterface, localNodeName, "localInterfaceItemPathJson");
                var partnerTarget = _session.ExactEngineeringHardware(partnerDevicePathJson, partnerItemPathJson) as DeviceItem ?? throw new ArgumentException("partnerItemPathJson must identify a DeviceItem (nonempty path).");
                var partnerInterface = _session.ExactEngineeringHardware(partnerDevicePathJson, partnerInterfaceItemPathJson);
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

        // Typed rows (WatchTableAccessRule.WatchTable / ForceTableAccessRule.ForceTable carry the table; Access is writable natively).
        private static JsonObject ReadAccessRule(object rule, string tableProperty)
        {
            var row = EngineeringScalarProperties.Read(rule);
            row["tableName"] = rule switch { WatchTableAccessRule w => w.WatchTable?.Name, ForceTableAccessRule f => f.ForceTable?.Name, _ => LinkName(rule, tableProperty) };
            row["ruleClass"] = rule.GetType().Name;
            return row;
        }
        public ResponseMessage ManageWatchForceTableWebAccess(string devicePathJson, string itemPathJson, string action = "read", string softwarePath = "",
            string tableKind = "watch", string tablePath = "", string access = "Read", bool confirmChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageWatchForceTableWebAccess", meta => {
                HardwareServicesLogic.RequireOneOf(action, new[] { "read", "assign", "unassign" }, "action");
                HardwareServicesLogic.RequireOneOf(tableKind, new[] { "watch", "force" }, "tableKind");
                bool writing = action != "read" && !dryRun;
                if (action != "read") HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);
                using var exclusive = writing ? _session.AcquireHmiEditAccess() : null;
                var item = _session.ExactEngineeringHardware(devicePathJson, itemPathJson) as DeviceItem ?? throw new ArgumentException("Exact CPU DeviceItem path required.");
                var manager = item.GetService<WatchAndForceTableAccessManager>() ?? throw new NotSupportedException("WatchAndForceTableAccessManager unavailable on this DeviceItem.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["watchTableRules"] = new JsonArray(manager.WatchtableAccessRules.Select(r => (JsonNode)ReadAccessRule(r, "WatchTable")).ToArray());
                meta["forceTableRules"] = new JsonArray(manager.ForcetableAccessRules.Select(r => (JsonNode)ReadAccessRule(r, "ForceTable")).ToArray());
                meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                if (action == "read") return "Web-server watch/force table access rules read; no change.";
                var requested = (WatchAndForceTableAccess)Enum.Parse(typeof(WatchAndForceTableAccess), HardwareServicesLogic.RequireOneOf(access, HardwareServicesLogic.TableAccessValues, "access"));
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
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
            => _session.RunHmiStepTool("ExchangeSystemDiagnosticsSettings", meta => {
                HardwareServicesLogic.RequireOneOf(action, new[] { "export", "import" }, "action");
                // TIA Portal V21, 2026-09-20 (docs/reference/real-machine-ledger.md): TIA answers "Filename suffix must be .dat" for anything else.
                if (!(filePath ?? "").EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("filePath must end in .dat (SystemdiagnosticsSettingsDataProvider format).");
                bool writing = !dryRun;
                if (action == "import") HardwareServicesLogic.RequireConfirmation(confirmImport, "confirmImport", dryRun);
                using var exclusive = writing && action == "import" ? _session.AcquireHmiEditAccess() : null;
                IEngineeringServiceProvider owner = devicePathJson == "[]" ? (IEngineeringServiceProvider)_session.CurrentProject! : _session.ServiceProvider(_session.ExactEngineeringHardware(devicePathJson, itemPathJson));
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

        public ResponseMessage ReadHardwareFeatures(string devicePathJson, string itemPathJson = "[]", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("ReadHardwareFeatures", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var owner = _session.ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["owner"] = EngineeringScalarProperties.Read(owner);
                var advertised = _session.ServiceProvider(owner).GetServiceInfos().Select(i => i.Type.FullName ?? i.Type.Name).ToArray();
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
        // ---- device service objects (web applications, telecontrol data points, dynamic certificate management) ---------------
#if !TIA_V20
        private static JsonObject TelecontrolRow(TelecontrolDataPoint point)
        {
            var row = new JsonObject { ["name"] = point.Name, ["dataPointType"] = point.DataPointType, ["pointClass"] = point.GetType().Name };
            switch (point)
            {
                case TelecontrolDnp3DataPoint dnp3: row["dataPointIndex"] = dnp3.DataPointIndex; row["masterFunction"] = dnp3.MasterFunction; break;
                case TelecontrolIecDataPoint iec: row["dataPointIndex"] = iec.DataPointIndex; row["masterFunction"] = iec.MasterFunction; break;
                case TelecontrolWdcDataPoint wdc: row["dataPointIndex"] = wdc.DataPointIndex; break;
            }
            return row;
        }
#endif
        private static JsonObject CertificateServiceRow(CertificateSupportedService service) => new JsonObject
        {
#if TIA_V20
            ["id"] = service.Id, ["serviceType"] = service.ServiceType.ToString(), ["serviceGroupName"] = service.ServiceGroupName   // V20: UInt16 Id, no ApplicationUri / Guid
#else
            ["id"] = service.Id, ["serviceType"] = service.ServiceType.ToString(), ["serviceGroupName"] = service.ServiceGroupName, ["applicationUri"] = service.ApplicationUri, ["guid"] = service.Guid.ToString()
#endif
        };
        private static JsonObject CertificateConfigurationRow(CertificateManagementConfiguration configuration)
        {
            var row = new JsonObject();
            try { row["usage"] = configuration.Usage.ToString(); } catch (Exception ex) { row["usageError"] = ex.GetBaseException().Message; }
            try { row["certificateExpirationEventActivated"] = configuration.CertificateExpirationEventActivated; } catch (Exception ex) { row["certificateExpirationEventActivatedError"] = ex.GetBaseException().Message; }
            try { row["remainingCertificateLifetime"] = configuration.RemainingCertificateLifetime; } catch (Exception ex) { row["remainingCertificateLifetimeError"] = ex.GetBaseException().Message; }
            try { row["services"] = new JsonArray(EngineeringGroupOperations.Items(configuration.CertificateSupportedServices).Cast<CertificateSupportedService>().Select(s => (JsonNode)CertificateServiceRow(s)).ToArray()); }
            catch (Exception ex) { row["servicesError"] = ex.GetBaseException().Message; }
            return row;
        }

        public ResponseMessage ManageDeviceServiceObjects(string devicePathJson, string itemPathJson, string family, string action = "read", string name = "", string propertiesJson = "{}", string filePath = "", bool confirmChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageDeviceServiceObjects", meta => {
                var properties = HardwareNetworkLogic.ParseObject(propertiesJson, "propertiesJson");
                DeviceServiceObjectRules.ValidateServiceObjectRequest(family, action, name, properties, filePath, confirmChange, dryRun);
                bool write = action != "read" && !dryRun;
                using var access = write ? _session.AcquireHmiEditAccess() : null;
                var owner = _session.ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["family"] = family; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["ownerPath"] = _session.HardwareOwnerPath(owner);
                if (properties.Count > 0) meta["requestedProperties"] = properties.DeepClone();
                switch (family)
                {
                    case "webApplications":
#if TIA_V20
                        throw new NotSupportedException("DefaultWebPagesFeature.WebApplicationConfigurations exists in the V21 PublicAPI only.");
#else
                    {
                        DefaultWebPagesFeature feature = _session.RequireHardwareService<DefaultWebPagesFeature>(owner, "itemPathJson");
                        JsonArray Rows() => new JsonArray(EngineeringGroupOperations.Items(feature.WebApplicationConfigurations).Cast<WebApplicationConfiguration>().Select(w => (JsonNode)new JsonObject { ["name"] = w.Name, ["applicationType"] = w.ApplicationType.ToString(), ["isDefault"] = w.IsDefault }).ToArray());
                        meta["records"] = Rows();
                        if (action == "read") { meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "Web application configurations read (DefaultWebPagesFeature); no modification."; }
                        WebApplicationConfiguration target = feature.WebApplicationConfigurations.Find(name) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact web application not found: " + name);
                        meta["before"] = new JsonObject { ["name"] = target.Name, ["isDefault"] = target.IsDefault };
                        if (!write) return "setDefault preview; nothing changed.";
                        meta["mayHaveChanged"] = true; target.IsDefault = true;
                        if (!target.IsDefault) throw new InvalidOperationException("IsDefault readback differs.");
                        meta["after"] = Rows(); meta["apiCallSuccess"] = true; return "Default web application set and read back; no save.";
                    }
#endif
#if TIA_V20
                    case "telecontrolDataPoints":
                    {
                        // V20: TelecontrolManagement exposes ExportDataPoints / ImportDataPoints only (typed data point rows are V21).
                        TelecontrolManagement management = _session.RequireHardwareService<TelecontrolManagement>(owner, "itemPathJson");
                        if (action != "export" && action != "import") throw new NotSupportedException("Telecontrol data point objects (TelecontrolDataPoint*) exist in the V21 PublicAPI only; V20 offers export / import.");
                        var file = new FileInfo(filePath); meta["filePath"] = file.FullName;
                        if (action == "export" && file.Exists) throw new IOException("Export refuses to overwrite an existing file.");
                        if (action == "import" && !file.Exists) throw new FileNotFoundException("Data point file not found.", file.FullName);
                        if (!write) return action + " preview; nothing " + (action == "export" ? "written" : "imported") + ".";
                        meta["mayHaveChanged"] = true;
                        if (action == "export") { management.ExportDataPoints(file); file.Refresh(); meta["fileBytes"] = file.Exists ? file.Length : 0; } else management.ImportDataPoints(file);
                        meta["apiCallSuccess"] = true; return "Telecontrol data points " + action + " completed; no save.";
                    }
#else
                    case "telecontrolDataPoints":
                    {
                        TelecontrolManagement management = _session.RequireHardwareService<TelecontrolManagement>(owner, "itemPathJson");
                        TelecontrolDataPointComposition points = management.TelecontrolDataPoints;
                        JsonArray Rows() => new JsonArray(EngineeringGroupOperations.Items(points).Cast<TelecontrolDataPoint>().Select(p => (JsonNode)TelecontrolRow(p)).ToArray());
                        if (action == "read") { meta["records"] = Rows(); meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "Telecontrol data points read (TelecontrolManagement); no modification."; }
                        if (action == "export" || action == "import")
                        {
                            var file = new FileInfo(filePath); meta["filePath"] = file.FullName;
                            if (action == "export" && file.Exists) throw new IOException("Export refuses to overwrite an existing file.");
                            if (action == "import" && !file.Exists) throw new FileNotFoundException("Data point file not found.", file.FullName);
                            if (!write) return action + " preview; nothing " + (action == "export" ? "written" : "imported") + ".";
                            meta["mayHaveChanged"] = true;
                            if (action == "export") { management.ExportDataPoints(file); file.Refresh(); meta["fileBytes"] = file.Exists ? file.Length : 0; }
                            else { int before = EngineeringGroupOperations.Items(points).Count(); management.ImportDataPoints(file); meta["countBefore"] = before; meta["countAfter"] = EngineeringGroupOperations.Items(points).Count(); }
                            meta["apiCallSuccess"] = true; return "Telecontrol data points " + action + " completed; no save.";
                        }
                        var point = EngineeringGroupOperations.Items(points).Cast<TelecontrolDataPoint>().FirstOrDefault(p => p.Name == name) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact telecontrol data point not found: " + name);
                        meta["before"] = TelecontrolRow(point);
                        var prepared = EngineeringScalarProperties.Prepare(point.GetType(), properties);
                        if (!write) return action + " preview; nothing changed.";
                        meta["mayHaveChanged"] = true;
                        if (action == "delete") { point.Delete(); if (EngineeringGroupOperations.Items(points).Cast<TelecontrolDataPoint>().Any(p => p.Name == name)) throw new InvalidOperationException("Delete returned but the data point is still listed."); meta["verifiedAbsent"] = true; return "Telecontrol data point deleted and verified absent; no save."; }
                        EngineeringScalarProperties.Apply(point, prepared, meta); meta["after"] = TelecontrolRow(point); meta["apiCallSuccess"] = true;
                        return "Telecontrol data point updated and read back; no save.";
                    }
#endif
                    default:
                    {
                        CertificateManagementConfiguration configuration = _session.RequireHardwareService<CertificateManagementConfiguration>(owner, "itemPathJson");
                        meta["before"] = CertificateConfigurationRow(configuration);
                        if (action == "read") { meta["apiCallSuccess"] = true; meta["dataComplete"] = true; meta["scope"] = "CertificateManagementConfiguration (Usage TIAPortal/Runtime, CertificateExpirationEventActivated, RemainingCertificateLifetime %) and its CertificateSupportedServices (Id, ServiceType, ServiceGroupName, ApplicationUri, Guid). PLC families V3.0+."; return "Dynamic certificate configuration read; no modification."; }
                        CertificateSupportedServiceComposition services = configuration.CertificateSupportedServices;
                        CertificateSupportedService? service = null;
#if TIA_V20
                        if (action == "createService" && services.Find(ushort.Parse(name)) != null) throw new InvalidOperationException("A certificate-supported service with Id " + name + " already exists.");
                        if (action == "setServiceGroupName" || action == "deleteService")
                        {
                            service = (ushort.TryParse(name, out var id) ? services.Find(id) : null)
                                ?? throw new PortalException(PortalErrorCode.NotFound, "No certificate-supported service with Id " + name + " (V20: UInt16 Id, no Guid lookup).");
#else
                        if (action == "createService" && services.Find(uint.Parse(name)) != null) throw new InvalidOperationException("A certificate-supported service with Id " + name + " already exists.");
                        if (action == "setServiceGroupName" || action == "deleteService")
                        {
                            service = (uint.TryParse(name, out var id) ? services.Find(id) : null) ?? (Guid.TryParse(name, out var guid) ? services.Find(guid) : null)
                                ?? throw new PortalException(PortalErrorCode.NotFound, "No certificate-supported service with Id or Guid " + name + ".");
#endif
                            meta["service"] = CertificateServiceRow(service);
                        }
                        var preparedConfiguration = action == "update" ? EngineeringScalarProperties.Prepare(typeof(CertificateManagementConfiguration), properties) : null;
                        if (!write) return action + " preview; nothing changed (Runtime usage is refused while web server and OPC UA server are deactivated; the expiration event needs Runtime usage).";
                        meta["mayHaveChanged"] = true;
                        switch (action)
                        {
                            case "update": EngineeringScalarProperties.Apply(configuration, preparedConfiguration!, meta); break;
                            case "setServiceGroupName":
                                var groupName = properties["ServiceGroupName"]!.ToString(); service!.ServiceGroupName = groupName;
                                if (service.ServiceGroupName != groupName) throw new InvalidOperationException("ServiceGroupName readback differs."); break;
                            case "createService":
                            {
                                // CertificateSupportedServiceComposition.Create(id, serviceGroupName, serviceType): name = new Id, propertiesJson carries the other two.
                                int before = EngineeringGroupOperations.Items(services).Count();
#if TIA_V20
                                var created = services.Create(ushort.Parse(name), properties["ServiceGroupName"]!.ToString(), (CertificateSupportedServiceName)Enum.Parse(typeof(CertificateSupportedServiceName), properties["ServiceType"]!.ToString()));
#else
                                var created = services.Create(uint.Parse(name), properties["ServiceGroupName"]!.ToString(), (CertificateSupportedServiceName)Enum.Parse(typeof(CertificateSupportedServiceName), properties["ServiceType"]!.ToString()));
#endif
                                meta["created"] = CertificateServiceRow(created);
                                if (EngineeringGroupOperations.Items(services).Count() != before + 1) throw new InvalidOperationException("Create returned but the service count did not grow by one."); break;
                            }
                            case "deleteService":
                                var deletedId = service!.Id; service.Delete();
                                if (services.Find(deletedId) != null) throw new InvalidOperationException("Delete returned but the service is still found by Id."); meta["verifiedAbsent"] = true; break;
                        }
                        meta["after"] = CertificateConfigurationRow(configuration); meta["apiCallSuccess"] = true;
                        return "Dynamic certificate configuration " + action + " executed and read back; no save/compile/download.";
                    }
                }
            });








        // Enable/disable remote PUT/GET access. NOTE: hardware-config change — a hardware DownloadToPlc is
        // required for it to take effect on the live CPU.
        public JsonObject SetPutGetAccess(string devicePath, bool enable)
        {
            if (_session.IsProjectNull()) return new JsonObject { ["ok"] = false, ["message"] = "No project open." };
            var device = _session.GetDevice(devicePath);
            if (device == null) return new JsonObject { ["ok"] = false, ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            var (item, attrName) = _session.FindPutGetAttribute(device);
            if (item == null || attrName == null)
                return new JsonObject
                {
                    ["ok"] = false,
                    ["device"] = device.Name,
                    ["message"] = "PUT/GET access cannot be set via Openness on this CPU/firmware (not an exposed attribute; " +
                                  "confirmed e.g. on S7-1200 1211C V4.6). Set it manually in TIA: " +
                                  "CPU > Protection & Security > Connection mechanisms > 'Permit access with PUT/GET communication', then download hardware."
                };

            object? before = null; try { before = item.GetAttribute(attrName); } catch { /* swallow(probe-optional): Optional PUT/GET readback must not suppress the write attempt or its result. */ }
            try { item.SetAttribute(attrName, enable); }
            catch (Exception ex)
            {
                return new JsonObject { ["ok"] = false, ["device"] = device.Name, ["attributeName"] = attrName, ["message"] = $"SetAttribute failed: {ex.Message}" };
            }
            object? after = null; try { after = item.GetAttribute(attrName); } catch { /* swallow(probe-optional): Optional PUT/GET readback must not suppress the write attempt or its result. */ }

            return new JsonObject
            {
                ["ok"] = _session.AttrValueIsEnabled(after) == enable,
                ["device"] = device.Name,
                ["deviceItem"] = item.Name,
                ["attributeName"] = attrName,
                ["before"] = before?.ToString() ?? string.Empty,
                ["after"] = after?.ToString() ?? string.Empty,
                ["note"] = "Hardware-config change — run DownloadToPlc (hardware) for it to take effect on the CPU."
            };
        }

        // Native observation: TIA V21, original date not recorded; see docs/reference/real-machine-ledger.md.
        // TIA refuses the first hardware download of an S7-1500 FW 2.9 CPU whose access level is above
        // "Full access" without a full-access password, or whose confidential PLC configuration data has no password. The
        // CPU protection API controls both. Official pages "Access level setting" and "Managing PLC Master Secret in PLCs": both live as
        // HW features on the CPU DeviceItem (PlcAccessLevelProvider / PlcMasterSecretConfigurator). Passwords go in as SecureString and
        // are never echoed; the readable state (access level enum, MasterSecretConfiguration enum) is read back after every change.
        public ResponseMessage ManagePlcProtection(string devicePathJson, string itemPathJson = "[]", string action = "read", string accessLevel = "",
            string password = "", string newPassword = "", bool confirmChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcProtection", meta => {
                var (act, level) = PlcProtectionLogic.Validate(action, accessLevel, password, newPassword);
                var cpu = ResolveCpuItem(devicePathJson, itemPathJson, meta);
                meta["action"] = act; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                var accessProvider = cpu.GetService<PlcAccessLevelProvider>();
                var secretProvider = cpu.GetService<PlcMasterSecretConfigurator>();
                var accessControl = cpu.GetService<PlcAccessControlConfigurationProvider>();
                meta["before"] = ReadProtectionState(accessProvider, secretProvider, accessControl);
                if (act == "read") return "PLC protection read (access level, master secret state, access control); no modification.";

                if (PlcProtectionLogic.LevelActions.Contains(act) && accessProvider == null)
                    throw new NotSupportedException("PlcAccessLevelProvider is not available on this device item (not an S7-1200/1500 CPU?).");
                if (PlcProtectionLogic.MasterSecretActions.Contains(act) && secretProvider == null)
                    throw new NotSupportedException("PlcMasterSecretConfigurator is not available on this device item (needs an S7-1500 FW >= 2.9 / S7-1200 FW >= 4.5 CPU).");
                if (act == "setAccessPassword" || act == "resetAccessPassword")
                {
                    var current = accessProvider!.PlcProtectionAccessLevel.ToString();
                    var warning = PlcProtectionLogic.PasswordLevelWarning(current, level);
                    if (warning != null) meta["warning"] = warning;
                }
                if (act == "setAccessLevel" && PlcProtectionLogic.NeedsFullAccessPassword(level))
                    meta["note"] = "TIA's hardware compile requires the FullAccess password once the level is " + level + " - set it with action setAccessPassword accessLevel FullAccess.";
                meta["plan"] = DescribeProtectionPlan(act, level);
                if (dryRun) return "Preview: " + meta["plan"] + " Set dryRun=false and confirmChange=true to execute.";
                HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);

                using var access = _session.AcquireHmiEditAccess();
                meta["mayHaveChanged"] = true;
                using var secure = string.IsNullOrEmpty(password) ? null : ProjectSecurityLogic.Secure(password);
                using var secureNew = string.IsNullOrEmpty(newPassword) ? null : ProjectSecurityLogic.Secure(newPassword);
                switch (act)
                {
                    case "setAccessLevel":
                        accessProvider!.PlcProtectionAccessLevel = (PlcProtectionAccessLevel)Enum.Parse(typeof(PlcProtectionAccessLevel), level);
                        break;
                    case "setAccessPassword":
                        accessProvider!.SetPassword((PlcProtectionAccessLevel)Enum.Parse(typeof(PlcProtectionAccessLevel), level), secure!);
                        break;
                    case "resetAccessPassword":
                        accessProvider!.ResetPassword((PlcProtectionAccessLevel)Enum.Parse(typeof(PlcProtectionAccessLevel), level));
                        break;
                    case "protectMasterSecret":
                        secretProvider!.Protect(secure!);
                        break;
                    case "changeMasterSecret":
                        secretProvider!.ChangePassword(secure!, secureNew!);
                        break;
                    case "unprotectMasterSecret":
                        if (secure != null) secretProvider!.Unprotect(secure); else secretProvider!.Unprotect();
                        break;
                    case "resetMasterSecret":
                        secretProvider!.Reset();
                        break;
                    case "protectAllConfiguration":
#if TIA_V20
                        throw new NotSupportedException("ProtectAllPlcConfiguration / ProtectAllPlcConfigurationWithPassword exist in the V21 PublicAPI only.");
#else
                        if (secure != null) secretProvider!.ProtectAllPlcConfigurationWithPassword(secure); else secretProvider!.ProtectAllPlcConfiguration();
                        break;
#endif
                    case "unprotectAllConfiguration":
#if TIA_V20
                        throw new NotSupportedException("UnprotectAllPlcConfiguration exists in the V21 PublicAPI only.");
#else
                        secretProvider!.UnprotectAllPlcConfiguration();
                        break;
#endif
                }
                meta["apiCallSuccess"] = true;
                var after = ReadProtectionState(accessProvider, secretProvider, accessControl);
                meta["after"] = after;
                if (act == "setAccessLevel")
                {
                    var readback = after["accessLevel"]?.ToString() ?? "";
                    meta["readbackVerified"] = string.Equals(readback, level, StringComparison.Ordinal);
                    if (!meta["readbackVerified"]!.GetValue<bool>()) throw new InvalidOperationException("Access level readback is '" + readback + "', not '" + level + "'.");
                }
                else if (PlcProtectionLogic.MasterSecretActions.Contains(act) && act != "changeMasterSecret")
                {
                    var state = after["masterSecret"]?.ToString() ?? "";
                    var expected = PlcProtectionLogic.ExpectedMasterSecretStates(act, !string.IsNullOrEmpty(password));
                    meta["expectedMasterSecret"] = string.Join("|", expected);
                    meta["readbackVerified"] = expected.Contains(state, StringComparer.Ordinal);
                    if (!expected.Contains(state, StringComparer.Ordinal)) throw new InvalidOperationException("MasterSecretConfiguration read back as '" + state + "', expected " + string.Join(" / ", expected) + ".");
                }
                else meta["readbackVerified"] = "password actions have no readable state; TIA raised no exception";
                return "PLC protection " + act + " executed on '" + cpu.Name + "'; state read back in meta.after. Hardware not compiled, project not saved.";
            });

        private DeviceItem ResolveCpuItem(string devicePathJson, string itemPathJson, JsonObject meta)
        {
            var owner = _session.ExactEngineeringHardware(devicePathJson, itemPathJson);
            if (owner is DeviceItem given && given.Classification == DeviceItemClassifications.CPU) { meta["cpu"] = given.Name; return given; }
            // An empty item path (or the device / rail) resolves to the CPU item of the station, the same object the TIA UI edits.
            var cpu = FindCpuItem(owner) ?? throw new InvalidOperationException("No CPU device item found under '" + owner.Name + "'; give the CPU item path (e.g. [\"导轨_0\",\"PLC_1\"]).");
            meta["cpu"] = cpu.Name;
            return cpu;
        }

        private static DeviceItem? FindCpuItem(HardwareObject owner)
        {
            foreach (var item in owner.DeviceItems)
            {
                if (item.Classification == DeviceItemClassifications.CPU) return item;
                var nested = FindCpuItem(item);
                if (nested != null) return nested;
            }
            return null;
        }

        private static JsonObject ReadProtectionState(PlcAccessLevelProvider? access, PlcMasterSecretConfigurator? secret, PlcAccessControlConfigurationProvider? control)
        {
            var state = new JsonObject();
            state["accessLevel"] = access == null ? null : access.PlcProtectionAccessLevel.ToString();
            state["accessLevelProviderPresent"] = access != null;
            state["masterSecret"] = secret == null ? null : secret.MasterSecretConfiguration.ToString();
            state["masterSecretConfiguratorPresent"] = secret != null;
            if (control != null)
            {
                try { state["accessControl"] = control.PlcAccessControlConfiguration.ToString(); } catch (Exception ex) { state["accessControlError"] = ex.Message; }
                try { state["umcServerAddress"] = control.UmcServerAddress; } catch (Exception) { /* swallow(probe-optional): The UMC server address is absent when access control is not configured. */ /* not configured */ }
            }
            state["accessControlProviderPresent"] = control != null;
            state["meaning"] = "masterSecret: None = 'Protect confidential PLC configuration data' unchecked; WithoutPassword = checked without a password (TIA's hardware compile refuses the download); WithPassword / WithPasswordAllDataProtection = password configured.";
            return state;
        }

        private static string DescribeProtectionPlan(string action, string level) => action switch
        {
            "setAccessLevel" => "set PlcAccessLevelProvider.PlcProtectionAccessLevel = " + level + ".",
            "setAccessPassword" => "PlcAccessLevelProvider.SetPassword(" + level + ", <password>).",
            "resetAccessPassword" => "PlcAccessLevelProvider.ResetPassword(" + level + ").",
            "protectMasterSecret" => "PlcMasterSecretConfigurator.Protect(<password>) - configures the password for confidential PLC configuration data.",
            "changeMasterSecret" => "PlcMasterSecretConfigurator.ChangePassword(<password>, <newPassword>).",
            "unprotectMasterSecret" => "PlcMasterSecretConfigurator.Unprotect(<password when configured>) - unchecks the protection.",
            "resetMasterSecret" => "PlcMasterSecretConfigurator.Reset() - removes the master secret; certificates encrypted with it are lost.",
            "protectAllConfiguration" => "PlcMasterSecretConfigurator.ProtectAllPlcConfiguration[WithPassword] - 'protect all PLC configuration data'.",
            "unprotectAllConfiguration" => "PlcMasterSecretConfigurator.UnprotectAllPlcConfiguration().",
            _ => "read only."
        };

        // Hardware compilation runs before a download; CompileSoftware only compiles the program.
        // ICompilable on the Device (or a given item) is what the TIA UI's "Compile > Hardware" does.
        public ResponseMessage CompileDevice(string devicePathJson, string itemPathJson = "[]")
            => _session.RunHmiStepTool("CompileDevice", meta => {
                var owner = _session.ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["target"] = owner.Name; meta["targetType"] = owner.GetType().Name;
                var compilable = _session.ServiceProvider(owner).GetService<ICompilable>()
                    ?? throw new NotSupportedException("ICompilable is not available on '" + owner.Name + "'.");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                CompilerResult result = compilable.Compile();
                meta["compileElapsedMs"] = watch.ElapsedMilliseconds;
                meta["apiCallSuccess"] = true;
                var collected = CompilerDiagnostics.CollectCompilerMessages(result.Messages);
                foreach (var kv in collected.Summary(result.State.ToString(), result.ErrorCount, result.WarningCount)) meta[kv.Key] = kv.Value?.DeepClone();
                meta["errors"] = new JsonArray(collected.Errors.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
                meta["warnings"] = new JsonArray(collected.Warnings.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray());
                meta["success"] = result.State != CompilerResultState.Error;
                meta["operationSuccess"] = result.State != CompilerResultState.Error;
                return "Hardware compile of '" + owner.Name + "' finished: " + result.State + " (errors " + result.ErrorCount + ", warnings " + result.WarningCount + "). Project not saved.";
            });

        public ResponseMessage ManageHardwareUtilities(string action = "list", string typeIdentifier = "", string devicePathJson = "[]", string itemPathJson = "[]", string filePath = "", string password = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManageHardwareUtilities", meta => {
                HardwareUtilityRules.ValidateHardwareUtilityRequest(action, typeIdentifier, devicePathJson, filePath, password, dryRun);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                if (action == "list")
                {
                    meta["records"] = new JsonArray(EngineeringGroupOperations.Items(_session.CurrentProject!.HwUtilities).Cast<HardwareUtility>().Select(u => (JsonNode)new JsonObject { ["identifier"] = u.Identifier, ["utilityClass"] = u.GetType().Name }).ToArray());
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "Hardware utilities listed (Project.HwUtilities); no modification.";
                }
                if (action == "findModuleTypes" || action == "findContainerTypes" || action == "normalizeTypeIdentifier")
                {
                    ModuleInformationProvider provider = _session.RequireHardwareUtility<ModuleInformationProvider>(HardwareUtilityRules.ModuleInformationProviderId);
                    meta["typeIdentifier"] = typeIdentifier;
                    switch (action)
                    {
                        case "findModuleTypes": meta["moduleTypes"] = new JsonArray(provider.FindModuleTypes(typeIdentifier).Select(x => (JsonNode)x).ToArray()); break;
                        case "findContainerTypes": meta["containerTypes"] = new JsonArray(provider.FindContainerTypes(typeIdentifier).Select(x => (JsonNode)x).ToArray()); break;
                        default: meta["normalizedTypeIdentifier"] = provider.GetTypeIdentifierNormalized(typeIdentifier); break;
                    }
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "ModuleInformationProvider." + action + " answered; no modification.";
                }
                var file = new FileInfo(filePath); if (file.Exists) throw new IOException("Export refuses to overwrite an existing file: " + file.FullName);
                var owner = _session.ExactEngineeringHardware(devicePathJson, itemPathJson); meta["ownerPath"] = _session.HardwareOwnerPath(owner); meta["filePath"] = file.FullName;
                if (dryRun) return action + " preview; nothing written (" + (action == "exportOpcUa" ? "OpcUaExportProvider.Export(DeviceItem, FileInfo) writes the PLC data as OPC UA XML" : "CardReaderPscProvider.Export(Device, FileInfo[, SecureString]) creates a .psc card image; f-activated devices refuse on V18 and below, encryption needs CPU V40.0+") + ").";
                meta["mayHaveChanged"] = true;
                if (action == "exportOpcUa")
                {
                    var item = owner as DeviceItem ?? throw new ArgumentException("exportOpcUa needs the PLC DeviceItem (non-empty itemPathJson).");
                    OpcUaExportProvider provider = _session.RequireHardwareUtility<OpcUaExportProvider>(HardwareUtilityRules.OpcUaExportProviderId);
                    provider.Export(item, file);
                }
                else
                {
                    var device = owner as Device ?? throw new ArgumentException("exportCardReaderPsc needs the Device (empty itemPathJson).");
                    CardReaderPscProvider provider = _session.RequireHardwareUtility<CardReaderPscProvider>(HardwareUtilityRules.CardReaderPscProviderId);
                    if (string.IsNullOrEmpty(password)) provider.Export(device, file);
                    else using (var secure = PlcBlockServicesLogic.ToSecureString(password)) provider.Export(device, file, secure);
                }
                file.Refresh(); if (!file.Exists || file.Length == 0) throw new InvalidOperationException("Export returned but no file was written.");
                meta["fileBytes"] = file.Length; meta["apiCallSuccess"] = true;
                return action + " completed; file written. No save.";
            });

        public JsonObject GetPutGetAccess(string devicePath) => _session.GetPutGetAccess(devicePath);
    }
}
