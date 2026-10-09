using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.Utilities;
using Siemens.Engineering.SW;
#if PLC_HARDWARE_DIAGNOSTICS
using Siemens.Engineering.HW.Systemdiagnostics.Settings;
#endif
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using static TiaMcp.Adapters.Hardware.HardwareAddressPrimitives;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private T HardwareRequireUtility<T>(string identifier) where T : HardwareUtility
        {
            HardwareUtilityComposition utilities = project!.HwUtilities;
            var utility = utilities.Find(identifier) as T ?? HardwareGroupOperations.Items(utilities).OfType<T>().FirstOrDefault()
                ?? throw new NotSupportedException(typeof(T).Name + " is not among Project.HwUtilities (" + string.Join(", ", HardwareGroupOperations.Items(utilities).Cast<HardwareUtility>().Select(u => u.Identifier)) + ").");
            return utility;
        }
        private static SecureString HardwareSecureString(string value)
        { var result = new SecureString(); foreach (char c in value) result.AppendChar(c); result.MakeReadOnly(); return result; }
        private static Type HardwareRequireHardwareApiType(string typeName)
            => typeof(HardwareObject).Assembly.GetType(typeName) ?? typeof(PlcSoftware).Assembly.GetType(typeName)
               ?? throw new NotSupportedException(typeName + " is not exposed by the connected TIA Portal Openness version (requires V21 or newer).");

        private static object HardwareRequireConnectionComposition(HardwareObject owner)
        {
            var management = HardwareOfficialService.Require(owner, "Siemens.Engineering.HW.Features.CommunicationManagement", typeof(HardwareObject).Assembly.GetName().Name!);
            return management.GetType().GetProperty("Connections")?.GetValue(management)
                ?? throw new NotSupportedException("CommunicationManagement.Connections is null on the selected hardware object.");
        }

        private static string? HardwareLinkName(object connection, string property)
        {
            var link = connection.GetType().GetProperty(property)?.GetValue(connection);
            return link == null ? null : link.GetType().GetProperty("Name")?.GetValue(link)?.ToString() ?? link.GetType().Name;
        }

        private static Dictionary<string, object?> HardwareReadConnection(object connection)
        {
            var row = HardwareScalarEvidence.Read(connection);
            foreach (var link in new[] { "LocalTarget", "PartnerTarget", "LocalInterface", "PartnerInterface" }) row[char.ToLowerInvariant(link[0]) + link.Substring(1) + "Name"] = HardwareLinkName(connection, link);
            return row;
        }

        private Node HardwareExactInterfaceNode(HardwareObject interfaceItem, string nodeName, string parameter)
        {
            var network = HardwareServiceProvider(interfaceItem).GetService<NetworkInterface>() ?? throw new InvalidOperationException(parameter + " must identify a DeviceItem that exposes NetworkInterface.");
            var nodes = HardwareGroupOperations.Items(network.Nodes).Cast<Node>().ToArray();
            if (string.IsNullOrEmpty(nodeName))
            {
                if (nodes.Length != 1) throw new ArgumentException(parameter + " interface has " + nodes.Length + " nodes; give the exact node name: " + string.Join(", ", nodes.Select(n => n.Name)));
                return nodes[0];
            }
            return HardwareGroupOperations.Find(network.Nodes, nodeName) as Node ?? throw new InvalidOperationException("Node not found on interface: " + nodeName);
        }

        private static object[] HardwareConnectionsNamed(object composition, string name)
            => HardwareGroupOperations.Items(composition).Where(c => string.Equals(c.GetType().GetProperty("LocalConnectionName")?.GetValue(c)?.ToString(), name, StringComparison.Ordinal)).ToArray();

#if PLC_HARDWARE_LINKED_TAGS
        public HardwareAddressingReply HardwareReadCommunicationConnections(string[] devicePathJson, string[] itemPathJson = null!, int offset = 0, int limit = 100)
            => RunHardwareAddressStep("ListCommunicationConnections", meta => {
                HardwareServicesPolicy.ValidatePagination(offset, limit);
                var owner = ExactHardware(devicePathJson, itemPathJson);
                meta["owner"] = HardwareScalarEvidence.Read(owner);
                var composition = HardwareRequireConnectionComposition(owner);
                var all = HardwareGroupOperations.Items(composition).ToArray();
                var rows = all.Skip(offset).Take(limit).Select(c => (object)HardwareReadConnection(c)).ToArray();
                meta["records"] = new List<object?>(rows);
                foreach (var pair in HardwareServicesPolicy.PageMeta(all.Length, offset, limit, rows.Length)) meta[pair.Key] = pair.Value;
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "Scalar properties of Siemens.Engineering.HW.CommunicationConnections.* plus link names; complex members excluded.";
                return "Communication connections of the exact hardware object read; no modification.";
            });
#else
        public HardwareAddressingReply HardwareReadCommunicationConnections(string[] devicePathJson, string[] itemPathJson = null!, int offset = 0, int limit = 100) => throw new NotSupportedException("CommunicationConnections requires V21.");
#endif

#if PLC_HARDWARE_LINKED_TAGS
        public HardwareAddressingReply HardwareManageCommunicationConnection(string[] devicePathJson, string[] itemPathJson, string action, string connectionType = "", string connectionName = "",
            string[] localInterfaceItemPathJson = null!, string localNodeName = "", string[] partnerDevicePathJson = null!, string[] partnerItemPathJson = null!,
            string[] partnerInterfaceItemPathJson = null!, string partnerNodeName = "", bool confirmDelete = false, bool dryRun = true)
            => RunHardwareAddressStep("ManageCommunicationConnection", meta => {
                HardwareServicesPolicy.RequireOneOf(action, new[] { "create", "delete" }, "action");
                if (action == "delete") HardwareServicesPolicy.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : HardwareEditAccess();
                var owner = ExactHardware(devicePathJson, itemPathJson);
                var composition = HardwareRequireConnectionComposition(owner);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["owner"] = HardwareScalarEvidence.Read(owner);
                int before = HardwareGroupOperations.Items(composition).Count(); meta["countBefore"] = before;
                if (action == "delete")
                {
                    HardwareServicesPolicy.RequireExactName(connectionName, "connectionName");
                    var matches = HardwareConnectionsNamed(composition, connectionName);
                    if (matches.Length != 1) throw new InvalidOperationException("Exact LocalConnectionName matched " + matches.Length + " connections; refusing.");
                    if (matches[0].GetType().GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Connection.Delete is unavailable.");
                    meta["before"] = HardwareReadConnection(matches[0]);
                    if (dryRun) return "Connection delete preview; nothing changed.";
                    meta["mayHaveChanged"] = true;
                    HardwareGroupOperations.Call(matches[0], "Delete", Type.EmptyTypes);
                    if (HardwareConnectionsNamed(composition, connectionName).Length != 0) throw new InvalidOperationException("Connection remains after Delete.");
                    meta["apiCallSuccess"] = true; meta["verifiedAbsent"] = true; meta["countAfter"] = HardwareGroupOperations.Items(composition).Count();
                    return "Connection deleted and verified absent; project not saved, compiled or downloaded.";
                }
                var kind = HardwareRequireHardwareApiType(HardwareServicesPolicy.ConnectionTypeName(connectionType));
                var create = composition.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3
                        && m.GetParameters()[0].ParameterType == typeof(Node) && m.GetParameters()[1].ParameterType == typeof(DeviceItem) && m.GetParameters()[2].ParameterType == typeof(Node))
                    ?? throw new NotSupportedException("ConnectionComposition.Create<T>(Node, DeviceItem, Node) is unavailable.");
                var localInterface = ExactHardware(devicePathJson, localInterfaceItemPathJson);
                var localNode = HardwareExactInterfaceNode(localInterface, localNodeName, "localInterfaceItemPathJson");
                var partnerTarget = ExactHardware(partnerDevicePathJson, partnerItemPathJson) as DeviceItem ?? throw new ArgumentException("partnerItemPathJson must identify a DeviceItem (nonempty path).");
                var partnerInterface = ExactHardware(partnerDevicePathJson, partnerInterfaceItemPathJson);
                var partnerNode = HardwareExactInterfaceNode(partnerInterface, partnerNodeName, "partnerInterfaceItemPathJson");
                if (!string.IsNullOrEmpty(connectionName) && HardwareConnectionsNamed(composition, HardwareServicesPolicy.RequireExactName(connectionName, "connectionName")).Length != 0)
                    throw new InvalidOperationException("A connection with this LocalConnectionName already exists on the owner.");
                meta["request"] = new Dictionary<string, object?> { ["connectionType"] = kind.FullName, ["localNode"] = localNode.Name, ["localSubnet"] = localNode.ConnectedSubnet?.Name,
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
                meta["after"] = HardwareReadConnection(created);
                int after = HardwareGroupOperations.Items(composition).Count(); meta["countAfter"] = after;
                if (after != before + 1) throw new InvalidOperationException("Native Create returned but the connection count did not increase by one.");
                return "Connection created and read back; project not saved, compiled or downloaded.";
            });
#else
        public HardwareAddressingReply HardwareManageCommunicationConnection(string[] devicePathJson, string[] itemPathJson, string action, string connectionType = "", string connectionName = "",
            string[] localInterfaceItemPathJson = null!, string localNodeName = "", string[] partnerDevicePathJson = null!, string[] partnerItemPathJson = null!,
            string[] partnerInterfaceItemPathJson = null!, string partnerNodeName = "", bool confirmDelete = false, bool dryRun = true) => throw new NotSupportedException("CommunicationConnections requires V21.");
#endif

#if PLC_HARDWARE_DIAGNOSTICS
        public HardwareAddressingReply HardwareExchangeSystemDiagnosticsSettings(string action, string filePath, string[] devicePathJson = null!, string[] itemPathJson = null!, bool confirmImport = false, bool dryRun = true)
            => RunHardwareAddressStep("ExchangeSystemDiagnosticsSettings", meta => {
                HardwareServicesPolicy.RequireOneOf(action, new[] { "export", "import" }, "action");
                // TIA Portal V21, 2026-09-20 (docs/reference/real-machine-ledger.md): TIA answers "Filename suffix must be .dat" for anything else.
                if (!(filePath ?? "").EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("filePath must end in .dat (SystemdiagnosticsSettingsDataProvider format).");
                bool writing = !dryRun;
                if (action == "import") HardwareServicesPolicy.RequireConfirmation(confirmImport, "confirmImport", dryRun);
                using var exclusive = writing && action == "import" ? HardwareEditAccess() : null;
                IEngineeringServiceProvider owner = devicePathJson.Length == 0 ? (IEngineeringServiceProvider)project! : HardwareServiceProvider(ExactHardware(devicePathJson, itemPathJson));
                var provider = owner.GetService<SystemdiagnosticsSettingsDataProvider>() ?? throw new NotSupportedException("SystemdiagnosticsSettingsDataProvider unavailable on the selected owner.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["ownerType"] = owner.GetType().FullName;
                if (action == "export")
                {
                    var file = HardwareAmlPolicy.Plan(filePath); meta["plannedFile"] = file.FullName;
                    if (dryRun) return "System diagnostics settings export preview; no file written.";
                    meta["mayHaveWrittenFiles"] = true;
                    var result = provider.Export(file);
                    HardwareOfficialService.AttachResult(meta, result);
                    meta["file"] = HardwareAmlPolicy.Verify(file);
                    if (result?.State.ToString() == "Error") throw new HardwareAddressingException("ExportFailed", "Native export reported Error state.");
                    return "System diagnostics settings exported to a new file and hashed; content semantics not verified.";
                }
                var source = HardwareServicesPolicy.RequireExistingInputFile(filePath, "filePath"); meta["sourceFile"] = HardwareAmlPolicy.Verify(source);
                if (dryRun) return "System diagnostics settings import preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                var imported = provider.Import(source);
                HardwareOfficialService.AttachResult(meta, imported);
                if (imported?.State.ToString() == "Error") throw new HardwareAddressingException("ImportFailed", "Native import reported Error state.");
                return "System diagnostics settings imported (native state attached); project not saved, compiled or downloaded.";
            });
#else
        public HardwareAddressingReply HardwareExchangeSystemDiagnosticsSettings(string action, string filePath, string[] devicePathJson = null!, string[] itemPathJson = null!, bool confirmImport = false, bool dryRun = true) => throw new NotSupportedException("System diagnostics settings require V17.");
#endif

        public HardwareAddressingReply HardwareReadHardwareFeatures(string[] devicePathJson, string[] itemPathJson = null!, int offset = 0, int limit = 100)
            => RunHardwareAddressStep("GetHardwareFeatures", meta => {
                HardwareServicesPolicy.ValidatePagination(offset, limit);
                var owner = ExactHardware(devicePathJson, itemPathJson);
                meta["owner"] = HardwareScalarEvidence.Read(owner);
                var advertised = HardwareServiceProvider(owner).GetServiceInfos().Select(i => i.Type.FullName ?? i.Type.Name).ToArray();
                meta["advertisedServices"] = new List<object?>(advertised.Select(x => (object)x).ToArray());
                var getService = typeof(IEngineeringServiceProvider).GetMethod("GetService")!;
                var rows = new System.Collections.Generic.List<Dictionary<string, object?>>();
                foreach (var feature in HardwareServicesPolicy.FeatureCatalog)
                {
                    var typeName = HardwareServicesPolicy.FeatureTypeName(feature);
                    var type = typeof(HardwareObject).Assembly.GetType(typeName) ?? typeof(PlcSoftware).Assembly.GetType(typeName);
                    var row = new Dictionary<string, object?> { ["feature"] = feature, ["type"] = typeName, ["typeAvailableInApi"] = type != null, ["present"] = false };
                    if (type == null || !typeof(IEngineeringService).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface) { row["skipped"] = type == null ? "type absent on this version" : "not a concrete service type"; rows.Add(row); continue; }
                    object? service;
                    try { service = getService.MakeGenericMethod(type).Invoke(owner, null); }
                    catch (TargetInvocationException ex) { row["probeError"] = (ex.InnerException ?? ex).GetBaseException().Message; rows.Add(row); continue; }
                    row["present"] = service != null;
                    if (service != null) row["values"] = HardwareServicesPolicy.FeatureValuesAllowed(feature) ? HardwareScalarEvidence.Read(service) : new Dictionary<string, object?> { ["withheld"] = "credential-related feature; values not dumped" };
                    rows.Add(row);
                }
                var page = rows.Skip(offset).Take(limit).Select(r => (object)r).ToArray();
                meta["records"] = new List<object?>(page);
                meta["presentCount"] = rows.Count(r => Equals(r["present"], true));
                foreach (var pair in HardwareServicesPolicy.PageMeta(rows.Count, offset, limit, page.Length)) meta[pair.Key] = pair.Value;
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "Catalog of Siemens.Engineering.HW.Features.* from the V21 XML docs; presence via GetService<T>, scalar values only. Nothing written.";
                return "Hardware feature services probed on the exact hardware object; read-only.";
            });

#if PLC_HARDWARE_CERTIFICATES
        private static Dictionary<string, object?> HardwareCertificateServiceRow(CertificateSupportedService service) => new Dictionary<string, object?>
        {
#if !PLC_HARDWARE_LINKED_TAGS
            ["id"] = service.Id, ["serviceType"] = service.ServiceType.ToString(), ["serviceGroupName"] = service.ServiceGroupName   // V20: UInt16 Id, no ApplicationUri / Guid
#else
            ["id"] = service.Id, ["serviceType"] = service.ServiceType.ToString(), ["serviceGroupName"] = service.ServiceGroupName, ["applicationUri"] = service.ApplicationUri, ["guid"] = service.Guid.ToString()
#endif
        };
#endif

#if PLC_HARDWARE_CERTIFICATES
        private static Dictionary<string, object?> HardwareCertificateConfigurationRow(CertificateManagementConfiguration configuration)
        {
            var row = new Dictionary<string, object?>();
            try { row["usage"] = configuration.Usage.ToString(); } catch (Exception ex) { row["usageError"] = ex.GetBaseException().Message; }
            try { row["certificateExpirationEventActivated"] = configuration.CertificateExpirationEventActivated; } catch (Exception ex) { row["certificateExpirationEventActivatedError"] = ex.GetBaseException().Message; }
            try { row["remainingCertificateLifetime"] = configuration.RemainingCertificateLifetime; } catch (Exception ex) { row["remainingCertificateLifetimeError"] = ex.GetBaseException().Message; }
            try { row["services"] = new List<object?>(HardwareGroupOperations.Items(configuration.CertificateSupportedServices).Cast<CertificateSupportedService>().Select(s => (object)HardwareCertificateServiceRow(s)).ToArray()); }
            catch (Exception ex) { row["servicesError"] = ex.GetBaseException().Message; }
            return row;
        }
#endif

        public HardwareAddressingReply HardwareManageDeviceServiceObjects(string[] devicePathJson, string[] itemPathJson, string family, string action = "read", string name = "", Dictionary<string, HardwareScalar> propertiesJson = null!, string filePath = "", bool confirmChange = false, bool dryRun = true)
            => RunHardwareAddressStep("ManageDeviceServiceObjects", meta => {
                var properties = HardwareNetworkPolicy.ParseObject(propertiesJson, "propertiesJson");
                HardwareServiceObjectPolicy.ValidateServiceObjectRequest(family, action, name, properties, filePath, confirmChange, dryRun);
                bool write = action != "read" && !dryRun;
                using var access = write ? HardwareEditAccess() : null;
                var owner = ExactHardware(devicePathJson, itemPathJson);
                meta["family"] = family; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["ownerPath"] = HardwareOwnerPath(owner);
                if (properties.Count > 0) meta["requestedProperties"] = HardwareScalarEvidence.Requested(properties);
                switch (family)
                {
                    case "webApplications":
#if !PLC_HARDWARE_LINKED_TAGS
                        throw new NotSupportedException("DefaultWebPagesFeature.WebApplicationConfigurations exists in the V21 PublicAPI only.");
#else
                    {
                        DefaultWebPagesFeature feature = HardwareRequireHardwareService<DefaultWebPagesFeature>(owner, "itemPathJson");
                        List<object?> Rows() => new List<object?>(HardwareGroupOperations.Items(feature.WebApplicationConfigurations).Cast<WebApplicationConfiguration>().Select(w => (object)new Dictionary<string, object?> { ["name"] = w.Name, ["applicationType"] = w.ApplicationType.ToString(), ["isDefault"] = w.IsDefault }).ToArray());
                        meta["records"] = Rows();
                        if (action == "read") { meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "Web application configurations read (DefaultWebPagesFeature); no modification."; }
                        WebApplicationConfiguration target = feature.WebApplicationConfigurations.Find(name) ?? throw new HardwareAddressingException("NotFound", "Exact web application not found: " + name);
                        meta["before"] = new Dictionary<string, object?> { ["name"] = target.Name, ["isDefault"] = target.IsDefault };
                        if (!write) return "setDefault preview; nothing changed.";
                        meta["mayHaveChanged"] = true; target.IsDefault = true;
                        if (!target.IsDefault) throw new InvalidOperationException("IsDefault readback differs.");
                        meta["after"] = Rows(); meta["apiCallSuccess"] = true; return "Default web application set and read back; no save.";
                    }
#endif
#if PLC_HARDWARE_TELECONTROL && !PLC_HARDWARE_LINKED_TAGS
                    case "telecontrolDataPoints":
                    {
                        // V20: TelecontrolManagement exposes ExportDataPoints / ImportDataPoints only (typed data point rows are V21).
                        TelecontrolManagement management = HardwareRequireHardwareService<TelecontrolManagement>(owner, "itemPathJson");
                        if (action != "export" && action != "import") throw new NotSupportedException("Telecontrol data point objects (TelecontrolDataPoint*) exist in the V21 PublicAPI only; V20 offers export / import.");
                        var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)); meta["filePath"] = file.FullName;
                        if (action == "export" && file.Exists) throw new IOException("Export refuses to overwrite an existing file.");
                        if (action == "import" && !file.Exists) throw new FileNotFoundException("Data point file not found.", file.FullName);
                        if (!write) return action + " preview; nothing " + (action == "export" ? "written" : "imported") + ".";
                        meta["mayHaveChanged"] = true;
                        if (action == "export") { management.ExportDataPoints(file); file.Refresh(); meta["fileBytes"] = file.Exists ? file.Length : 0; } else management.ImportDataPoints(file);
                        meta["apiCallSuccess"] = true; return "Telecontrol data points " + action + " completed; no save.";
                    }
#elif PLC_HARDWARE_LINKED_TAGS
                    case "telecontrolDataPoints":
                    {
                        TelecontrolManagement management = HardwareRequireHardwareService<TelecontrolManagement>(owner, "itemPathJson");
                        TelecontrolDataPointComposition points = management.TelecontrolDataPoints;
                        List<object?> Rows() => new List<object?>(HardwareGroupOperations.Items(points).Cast<TelecontrolDataPoint>().Select(p => (object)TelecontrolRow(p)).ToArray());
                        if (action == "read") { meta["records"] = Rows(); meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "Telecontrol data points read (TelecontrolManagement); no modification."; }
                        if (action == "export" || action == "import")
                        {
                            var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)); meta["filePath"] = file.FullName;
                            if (action == "export" && file.Exists) throw new IOException("Export refuses to overwrite an existing file.");
                            if (action == "import" && !file.Exists) throw new FileNotFoundException("Data point file not found.", file.FullName);
                            if (!write) return action + " preview; nothing " + (action == "export" ? "written" : "imported") + ".";
                            meta["mayHaveChanged"] = true;
                            if (action == "export") { management.ExportDataPoints(file); file.Refresh(); meta["fileBytes"] = file.Exists ? file.Length : 0; }
                            else { int before = HardwareGroupOperations.Items(points).Count(); management.ImportDataPoints(file); meta["countBefore"] = before; meta["countAfter"] = HardwareGroupOperations.Items(points).Count(); }
                            meta["apiCallSuccess"] = true; return "Telecontrol data points " + action + " completed; no save.";
                        }
                        var point = HardwareGroupOperations.Items(points).Cast<TelecontrolDataPoint>().FirstOrDefault(p => p.Name == name) ?? throw new HardwareAddressingException("NotFound", "Exact telecontrol data point not found: " + name);
                        meta["before"] = TelecontrolRow(point);
                        var prepared = HardwareAddressingPolicy.Prepare(point.GetType(), properties);
                        if (!write) return action + " preview; nothing changed.";
                        meta["mayHaveChanged"] = true;
                        if (action == "delete") { point.Delete(); if (HardwareGroupOperations.Items(points).Cast<TelecontrolDataPoint>().Any(p => p.Name == name)) throw new InvalidOperationException("Delete returned but the data point is still listed."); meta["verifiedAbsent"] = true; return "Telecontrol data point deleted and verified absent; no save."; }
                        HardwareScalarEvidence.Apply(point, prepared, meta); meta["after"] = TelecontrolRow(point); meta["apiCallSuccess"] = true;
                        return "Telecontrol data point updated and read back; no save.";
                    }
#endif
                    default:
#if PLC_HARDWARE_CERTIFICATES
                    {
                        CertificateManagementConfiguration configuration = HardwareRequireHardwareService<CertificateManagementConfiguration>(owner, "itemPathJson");
                        meta["before"] = HardwareCertificateConfigurationRow(configuration);
                        if (action == "read") { meta["apiCallSuccess"] = true; meta["dataComplete"] = true; meta["scope"] = "CertificateManagementConfiguration (Usage TIAPortal/Runtime, CertificateExpirationEventActivated, RemainingCertificateLifetime %) and its CertificateSupportedServices (Id, ServiceType, ServiceGroupName, ApplicationUri, Guid). PLC families V3.0+."; return "Dynamic certificate configuration read; no modification."; }
                        CertificateSupportedServiceComposition services = configuration.CertificateSupportedServices;
                        CertificateSupportedService? service = null;
#if !PLC_HARDWARE_LINKED_TAGS
                        if (action == "createService" && services.Find(ushort.Parse(name)) != null) throw new InvalidOperationException("A certificate-supported service with Id " + name + " already exists.");
                        if (action == "setServiceGroupName" || action == "deleteService")
                        {
                            service = (ushort.TryParse(name, out var id) ? services.Find(id) : null)
                                ?? throw new HardwareAddressingException("NotFound", "No certificate-supported service with Id " + name + " (V20: UInt16 Id, no Guid lookup).");
#else
                        if (action == "createService" && services.Find(uint.Parse(name)) != null) throw new InvalidOperationException("A certificate-supported service with Id " + name + " already exists.");
                        if (action == "setServiceGroupName" || action == "deleteService")
                        {
                            service = (uint.TryParse(name, out var id) ? services.Find(id) : null) ?? (Guid.TryParse(name, out var guid) ? services.Find(guid) : null)
                                ?? throw new HardwareAddressingException("NotFound", "No certificate-supported service with Id or Guid " + name + ".");
#endif
                            meta["service"] = HardwareCertificateServiceRow(service);
                        }
                        var preparedConfiguration = action == "update" ? HardwareAddressingPolicy.Prepare(typeof(CertificateManagementConfiguration), properties) : null;
                        if (!write) return action + " preview; nothing changed (Runtime usage is refused while web server and OPC UA server are deactivated; the expiration event needs Runtime usage).";
                        meta["mayHaveChanged"] = true;
                        switch (action)
                        {
                            case "update": HardwareScalarEvidence.Apply(configuration, preparedConfiguration!, meta); break;
                            case "setServiceGroupName":
                                var groupName = properties["ServiceGroupName"].Text; service!.ServiceGroupName = groupName;
                                if (service.ServiceGroupName != groupName) throw new InvalidOperationException("ServiceGroupName readback differs."); break;
                            case "createService":
                            {
                                // CertificateSupportedServiceComposition.Create(id, serviceGroupName, serviceType): name = new Id, propertiesJson carries the other two.
                                int before = HardwareGroupOperations.Items(services).Count();
#if !PLC_HARDWARE_LINKED_TAGS
                                var created = services.Create(ushort.Parse(name), properties["ServiceGroupName"].Text, (CertificateSupportedServiceName)Enum.Parse(typeof(CertificateSupportedServiceName), properties["ServiceType"].Text));
#else
                                var created = services.Create(uint.Parse(name), properties["ServiceGroupName"].Text, (CertificateSupportedServiceName)Enum.Parse(typeof(CertificateSupportedServiceName), properties["ServiceType"].Text));
#endif
                                meta["created"] = HardwareCertificateServiceRow(created);
                                if (HardwareGroupOperations.Items(services).Count() != before + 1) throw new InvalidOperationException("Create returned but the service count did not grow by one."); break;
                            }
                            case "deleteService":
                                var deletedId = service!.Id; service.Delete();
                                if (services.Find(deletedId) != null) throw new InvalidOperationException("Delete returned but the service is still found by Id."); meta["verifiedAbsent"] = true; break;
                        }
                        meta["after"] = HardwareCertificateConfigurationRow(configuration); meta["apiCallSuccess"] = true;
                        return "Dynamic certificate configuration " + action + " executed and read back; no save/compile/download.";
                    }
#else
                        throw new NotSupportedException("CertificateManagementConfiguration is absent in this API.");
#endif
                }
            });

        public HardwareAddressingReply HardwareManageHardwareUtilities(string action = "list", string typeIdentifier = "", string[] devicePathJson = null!, string[] itemPathJson = null!, string filePath = "", string password = "", bool dryRun = true)
            => RunHardwareAddressStep("ManageHardwareUtilities", meta => {
                HardwareUtilityPolicy.ValidateHardwareUtilityRequest(action, typeIdentifier, devicePathJson, filePath, password, dryRun);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                if (action == "list")
                {
                    meta["records"] = new List<object?>(HardwareGroupOperations.Items(project!.HwUtilities).Cast<HardwareUtility>().Select(u => (object)new Dictionary<string, object?> { ["identifier"] = u.Identifier, ["utilityClass"] = u.GetType().Name }).ToArray());
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "Hardware utilities listed (Project.HwUtilities); no modification.";
                }
                if (action == "findModuleTypes" || action == "findContainerTypes" || action == "normalizeTypeIdentifier")
                {
                    ModuleInformationProvider provider = HardwareRequireUtility<ModuleInformationProvider>("ModuleInformationProvider");
                    meta["typeIdentifier"] = typeIdentifier;
                    switch (action)
                    {
                        case "findModuleTypes": meta["moduleTypes"] = new List<object?>(provider.FindModuleTypes(typeIdentifier).Select(x => (object)x).ToArray()); break;
                        case "findContainerTypes": meta["containerTypes"] = new List<object?>(provider.FindContainerTypes(typeIdentifier).Select(x => (object)x).ToArray()); break;
                        default:
#if PLC_HARDWARE_DIAGNOSTICS
                            meta["normalizedTypeIdentifier"] = provider.GetTypeIdentifierNormalized(typeIdentifier); break;
#else
                            throw new NotSupportedException("GetTypeIdentifierNormalized requires V17.");
#endif
                    }
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "ModuleInformationProvider." + action + " answered; no modification.";
                }
                var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)); if (file.Exists) throw new IOException("Export refuses to overwrite an existing file: " + file.FullName);
                var owner = ExactHardware(devicePathJson, itemPathJson); meta["ownerPath"] = HardwareOwnerPath(owner); meta["filePath"] = file.FullName;
                if (dryRun) return action + " preview; nothing written (" + (action == "exportOpcUa" ? "OpcUaExportProvider.Export(DeviceItem, FileInfo) writes the PLC data as OPC UA XML" : "CardReaderPscProvider.Export(Device, FileInfo[, SecureString]) creates a .psc card image; f-activated devices refuse on V18 and below, encryption needs CPU V40.0+") + ").";
                meta["mayHaveChanged"] = true;
                if (action == "exportOpcUa")
                {
                    var item = owner as DeviceItem ?? throw new ArgumentException("exportOpcUa needs the PLC DeviceItem (non-empty itemPathJson).");
                    OpcUaExportProvider provider = HardwareRequireUtility<OpcUaExportProvider>("OPCUAExportProvider");
                    provider.Export(item, file);
                }
                else
                {
                    var device = owner as Device ?? throw new ArgumentException("exportCardReaderPsc needs the Device (empty itemPathJson).");
#if PLC_HARDWARE_TRANSFER_AREAS
                    CardReaderPscProvider provider = HardwareRequireUtility<CardReaderPscProvider>("CardReaderPscProvider");
                    if (string.IsNullOrEmpty(password)) provider.Export(device, file);
#if PLC_HARDWARE_MRP_INSTANCES
                    else using (var secure = HardwareSecureString(password)) provider.Export(device, file, secure);
#else
                    else throw new NotSupportedException("Encrypted PSC export requires V20.");
#endif
#else
                    throw new NotSupportedException("CardReaderPscProvider requires V15.1.");
#endif
                }
                file.Refresh(); if (!file.Exists || file.Length == 0) throw new InvalidOperationException("Export returned but no file was written.");
                meta["fileBytes"] = file.Length; meta["apiCallSuccess"] = true;
                return action + " completed; file written. No save.";
            });

#if PLC_HARDWARE_LINKED_TAGS
        private static Dictionary<string, object?> TelecontrolRow(TelecontrolDataPoint point)
        {
            var row = new Dictionary<string, object?> { ["name"] = point.Name, ["dataPointType"] = point.DataPointType, ["pointClass"] = point.GetType().Name };
            switch (point)
            {
                case TelecontrolDnp3DataPoint dnp3: row["dataPointIndex"] = dnp3.DataPointIndex; row["masterFunction"] = dnp3.MasterFunction; break;
                case TelecontrolIecDataPoint iec: row["dataPointIndex"] = iec.DataPointIndex; row["masterFunction"] = iec.MasterFunction; break;
                case TelecontrolWdcDataPoint wdc: row["dataPointIndex"] = wdc.DataPointIndex; break;
            }
            return row;
        }
#endif

        public HardwareAddressingReply HardwareManageHardwareObject(string[] devicePathJson, string action, string[] itemPathJson = null!,
            string[] destinationDevicePathJson = null!, string[] destinationItemPathJson = null!, int position = -1, bool dryRun = true)
            => RunHardwareAddressStep("ManageHardwareObject", meta => {
                if (!new[] { "deleteDevice", "deleteItem", "moveItem", "copyItem" }.Contains(action)) throw new ArgumentException("action must be one of: deleteDevice/deleteItem/moveItem/copyItem (case-sensitive).");
                using var access = dryRun ? null : HardwareEditAccess();
                var source = ExactHardware(devicePathJson, itemPathJson);
                meta["source"] = HardwareScalarEvidence.Read(source); meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["dependencyImpact"] = "Hardware descendants and dependent software/configuration can be affected. No online action or download is performed.";
                if (action == "deleteDevice")
                {
                    if (source is not Device device) throw new ArgumentException("deleteDevice requires empty itemPathJson.");
                    if (!dryRun)
                    {
                        meta["mayHaveChanged"] = true; device.Delete();
                        try { ExactHardware(devicePathJson, Array.Empty<string>()); throw new InvalidOperationException("Device is still present after Delete."); }
                        catch (InvalidOperationException ex) when (ex.Message == "Exact unique device not found." || ex.Message == "Device not found.") { meta["verifiedAbsent"] = true; }
                    }
                }
                else
                {
                    if (source is not DeviceItem item) throw new ArgumentException("This operation requires a nonempty exact device item path.");
                    if (action == "deleteItem")
                    {
                        var parent = item.Parent as HardwareObject ?? throw new InvalidOperationException("Hardware parent unavailable.");
                        var name = item.Name;
                        if (!dryRun) { meta["mayHaveChanged"] = true; item.Delete(); if (HardwareGroupOperations.Find(parent.DeviceItems, name) != null) throw new InvalidOperationException("Item remains after Delete."); meta["verifiedAbsent"] = true; }
                    }
                    else
                    {
                        if (position < 0) throw new ArgumentException("A nonnegative destination slot position is required.");
                        var destination = ExactHardware(destinationDevicePathJson, destinationItemPathJson);
                        bool allowed = action == "moveItem" ? destination.CanPlugMove(item, position) : destination.CanPlugCopy(item, position);
                        meta["canPlug"] = allowed; meta["position"] = position;
                        if (!allowed) throw new InvalidOperationException("TIA CanPlugMove/CanPlugCopy refused the destination.");
                        if (!dryRun)
                        {
                            meta["mayHaveChanged"] = true;
                            var result = action == "moveItem" ? destination.PlugMove(item, position) : destination.PlugCopy(item, position);
                            meta["after"] = HardwareScalarEvidence.Read(result);
                        }
                    }
                }
                return dryRun ? "Hardware preview; no modification." : "Hardware operation completed; project not saved or downloaded.";
            });
    }
}
