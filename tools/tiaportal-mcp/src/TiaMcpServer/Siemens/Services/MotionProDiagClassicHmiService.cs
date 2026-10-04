using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.MotionProDiagClassicHmiLogic;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class MotionProDiagClassicHmiService
    {
        private readonly IEngineeringSession _session;

        public MotionProDiagClassicHmiService(IEngineeringSession session) => _session = session;

        private static Type? OfficialType(string typeName)
            => typeof(PlcSoftware).Assembly.GetType(typeName) ?? typeof(HmiTarget).Assembly.GetType(typeName) ?? typeof(IEngineeringObject).Assembly.GetType(typeName)
               ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
        // Distinguishes a type missing from the installed API from a service the object does not provide.
        private static object? OptionalOfficialService(object owner, string typeName, out string state)
        {
            var type = OfficialType(typeName);
            if (type == null) { state = "typeAbsentOnThisVersion"; return null; }
            if (!typeof(IEngineeringService).IsAssignableFrom(type) || owner is not IEngineeringServiceProvider) { state = "notAService"; return null; }
            object? service;
            try { service = typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(type).Invoke(owner, null); }
            catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
            state = service == null ? "notProvidedByObject" : "available";
            return service;
        }
        private static object RequireOfficialService(object owner, string typeName)
            => OptionalOfficialService(owner, typeName, out var state) ?? throw new NotSupportedException(typeName + " is " + state + " for " + owner.GetType().FullName + ".");

        public ResponseMessage ReadMotionAxisConfiguration(string softwarePath, string objectPath, bool includeParameters = false, int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("ReadMotionAxisConfiguration", meta => {
                Logic.RequirePagination(offset, limit);
                var target = _session.ExactTechnology(softwarePath, objectPath, false);
                meta["softwarePath"] = softwarePath; meta["objectPath"] = objectPath; meta["object"] = EngineeringScalarProperties.Read(target);
                var services = new JsonObject(); var states = new JsonObject(); bool complete = true;
                foreach (var typeName in Logic.ReadableMotionServices)
                {
                    var service = OptionalOfficialService(target, typeName, out var state); states[typeName] = state;
                    if (service == null) continue;
                    var row = _session.DescribeNode(service, 0); services[Logic.ShortTypeName(typeName)] = row;
                    if (row["dataComplete"]?.GetValue<bool>() == false) complete = false;
                }
                meta["services"] = services; meta["serviceStates"] = states;
                if (target is TechnologicalInstanceDB typedObject) { meta["object"] = _session.TechnologyObjectRow(typedObject); try { meta["typed"] = _session.TypedMotionView(typedObject); } catch (Exception ex) { meta["typedError"] = ex.GetBaseException().Message; } }
                if (includeParameters)
                {
                    var parameters = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(target, "Parameters")).ToArray();
                    var page = parameters.Skip(offset).Take(limit).Select(p => p is TechnologicalParameter tp ? (JsonObject)_session.ParameterRow(tp) : EngineeringScalarProperties.Read(p)).ToArray();
                    meta["parameters"] = new JsonArray(page.Cast<JsonNode>().ToArray()); meta["parameterCount"] = parameters.Length;
                    meta["nextOffset"] = offset + page.Length < parameters.Length ? offset + page.Length : (int?)null;
                    complete &= offset == 0 && page.Length == parameters.Length && page.All(p => p["dataComplete"]?.GetValue<bool>() ?? !p.ContainsKey("valueError"));
                }
                meta["apiCallSuccess"] = true; meta["dataComplete"] = complete;
                meta["scope"] = "Scalar values of the technology object and every Motion/Ident service it provides (one bounded reference level). Absent services are listed by state, not omitted.";
                return "Motion technology object configuration read; no drive or motion command.";
            });

        public ResponseMessage ManageMotionAxis(string softwarePath, string objectPath, string action, string aspect = "", string name = "", string targetJson = "{}",
            string propertiesJson = "{}", int sensorIndex = 0, bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageMotionAxis", meta => {
                var category = Logic.MotionCategory(action, aspect);
                bool writing = action != "read" && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var to = _session.ExactTechnology(softwarePath, objectPath, writing);
                meta["objectPath"] = objectPath; meta["action"] = action; meta["aspect"] = aspect; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (category == "read")
                {
                    meta["object"] = EngineeringScalarProperties.Read(to);
                    var states = new JsonObject();
                    foreach (var typeName in Logic.ReadableMotionServices) { OptionalOfficialService(to, typeName, out var state); states[typeName] = state; }
                    meta["serviceStates"] = states; meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                    return "Technology object summary read; ReadMotionAxisConfiguration returns service detail.";
                }
                if (category == "masterValue")
                {
                    var (serviceName, property) = Logic.MasterValueAspects[aspect];
                    var association = EngineeringGroupOperations.Get(RequireOfficialService(to, serviceName), property);
                    var master = _session.ExactTechnology(softwarePath, name, false) as TechnologicalInstanceDB ?? throw new ArgumentException("name must be the exact path of the master technology object.");
                    if (ReferenceEquals(master, to) || Equals(master, to)) throw new ArgumentException("A technology object cannot be its own master value.");
                    var signature = new[] { typeof(TechnologicalInstanceDB) };
                    var method = action == "addMasterValue" ? "Add" : "Remove";
                    if (association.GetType().GetMethod(method, signature) == null || association.GetType().GetMethod("Contains", signature) == null) throw new NotSupportedException("Native association " + method + "/Contains signature unavailable.");
                    TechnologicalInstanceDBAssociation? typedAssociation = association as TechnologicalInstanceDBAssociation;
                    bool present = typedAssociation != null ? typedAssociation.Contains(master) : (bool)EngineeringGroupOperations.Call(association, "Contains", signature, master);
                    meta["masterObjectPath"] = name; meta["before"] = new JsonObject { ["coupled"] = present, ["members"] = new JsonArray(EngineeringGroupOperations.Items(association).Select(x => (JsonNode)(EngineeringDynamicAccess.Name(x) ?? "")).ToArray()) };
                    if (action == "addMasterValue" && present) throw new InvalidOperationException("Master value is already coupled.");
                    if (action == "removeMasterValue" && !present) throw new InvalidOperationException("Master value is not coupled.");
                    if (!writing) return "Master value coupling preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    if (typedAssociation != null) { if (action == "addMasterValue") typedAssociation.Add(master); else typedAssociation.Remove(master); }
                    else EngineeringGroupOperations.Call(association, method, signature, master);
                    meta["apiCallSuccess"] = true;
                    bool after = typedAssociation != null ? typedAssociation.Contains(master) : (bool)EngineeringGroupOperations.Call(association, "Contains", signature, master);
                    if (after != (action == "addMasterValue")) throw new InvalidOperationException("Association call returned but readback differs.");
                    meta["after"] = new JsonObject { ["coupled"] = after };
                    return "Master value coupling changed and verified; no save/compile/download or motion command.";
                }
                if (category == "mapping")
                {
                    var (serviceName, property) = Logic.MappingAspects[aspect];
                    var composition = EngineeringGroupOperations.Get(RequireOfficialService(to, serviceName), property);
                    Logic.RequireName(name, "alias");
                    var matches = EngineeringGroupOperations.Items(composition).Where(x => string.Equals(EngineeringGroupOperations.Get(x, "Alias").ToString(), name, StringComparison.Ordinal)).Take(2).ToList();
                    if (matches.Count > 1) throw new InvalidOperationException("Ambiguous alias: " + name);
                    var existing = matches.SingleOrDefault();
#if !TIA_V20
                    // Typed composition lookups (TOMappingComposition / DBMemberMappingComposition Find by alias).
                    if (existing == null && composition is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.TOMappingComposition toMappings) existing = toMappings.Find(name);
                    if (existing == null && composition is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.DBMemberMappingComposition dbMappings) existing = dbMappings.Find(name);
#endif
                    var changes = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                    if (changes.ContainsKey("Alias")) throw new ArgumentException("Alias is fixed by name; renaming is not supported.");
                    string? masterPath = null;
                    if (changes.ContainsKey("TechnologicalObject"))
                    {
                        if (aspect != "toMapping") throw new ArgumentException("TechnologicalObject applies to toMapping only.");
                        masterPath = changes["TechnologicalObject"]?.GetValue<string>() ?? throw new ArgumentException("TechnologicalObject must be an exact object path.");
                        changes.Remove("TechnologicalObject");
                    }
                    var itemType = composition.GetType().GetProperty("Item")?.PropertyType ?? throw new NotSupportedException("Mapping composition item type unavailable.");
                    var prepared = EngineeringScalarProperties.Prepare(itemType, changes);
                    var masterObject = masterPath == null ? null : _session.ExactTechnology(softwarePath, masterPath, false);
                    meta["alias"] = name;
                    if (action == "createMapping")
                    {
                        if (existing != null) throw new InvalidOperationException("Mapping alias already exists.");
                        if (composition.GetType().GetMethod("Create", new[] { typeof(string) }) == null) throw new NotSupportedException("Native mapping Create(string) unavailable.");
                        if (!writing) return "Mapping creation preview; no changes.";
                        meta["mayHaveChanged"] = true;
#if !TIA_V20
                        existing = composition switch
                        {
                            global::Siemens.Engineering.SW.TechnologicalObjects.Motion.TOMappingComposition toCreate => toCreate.Create(name),
                            global::Siemens.Engineering.SW.TechnologicalObjects.Motion.DBMemberMappingComposition dbCreate => dbCreate.Create(name),
                            _ => EngineeringGroupOperations.Call(composition, "Create", new[] { typeof(string) }, name)
                        };
#else
                        existing = EngineeringGroupOperations.Call(composition, "Create", new[] { typeof(string) }, name);
#endif
                    }
                    else
                    {
                        if (existing == null) throw new InvalidOperationException("Exact mapping alias not found.");
                        meta["before"] = _session.MappingRow(existing);
                        if (action == "deleteMapping")
                        {
                            if (prepared.Count != 0 || masterObject != null) throw new ArgumentException("No properties allowed for deleteMapping.");
                            if (!confirmDelete) throw new ArgumentException("confirmDelete=true is required to delete a mapping.");
                            if (existing.GetType().GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Native mapping Delete unavailable.");
                            if (!writing) return "Mapping deletion preview; no changes.";
                            meta["mayHaveChanged"] = true;
#if !TIA_V20
                            if (existing is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.TOMapping toDelete) toDelete.Delete();
                            else if (existing is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.DBMemberMapping dbDelete) dbDelete.Delete();
                            else EngineeringGroupOperations.Call(existing, "Delete", Type.EmptyTypes);
#else
                            EngineeringGroupOperations.Call(existing, "Delete", Type.EmptyTypes);
#endif
                            meta["apiCallSuccess"] = true;
                            if (EngineeringGroupOperations.Items(composition).Any(x => string.Equals(EngineeringGroupOperations.Get(x, "Alias").ToString(), name, StringComparison.Ordinal))) throw new InvalidOperationException("Mapping remains after Delete.");
                            meta["verifiedAbsent"] = true;
                            return "Mapping deleted and verified absent; no save/compile/download.";
                        }
                        if (prepared.Count == 0 && masterObject == null) throw new ArgumentException("updateMapping requires at least one property.");
                        if (!writing) return "Mapping update preview; no changes.";
                    }
                    EngineeringScalarProperties.Apply(existing, prepared, meta);
                    if (masterObject != null)
                    {
#if !TIA_V20
                        if (existing is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.TOMapping toMapping && masterObject is TechnologicalInstanceDB masterDb)
                        {
                            meta["mayHaveChanged"] = true;
                            toMapping.TechnologicalObject = masterDb;
                            if (!Equals(toMapping.TechnologicalObject, masterDb)) throw new InvalidOperationException("TechnologicalObject readback differs.");
                        }
                        else
#endif
                        {
                            var reference = existing.GetType().GetProperty("TechnologicalObject") ?? throw new NotSupportedException("TechnologicalObject property unavailable.");
                            if (reference.SetMethod?.IsPublic != true) throw new NotSupportedException("TechnologicalObject is not writable.");
                            meta["mayHaveChanged"] = true;
                            reference.SetValue(existing, masterObject);
                            if (!Equals(reference.GetValue(existing), masterObject)) throw new InvalidOperationException("TechnologicalObject readback differs.");
                        }
                    }
                    meta["after"] = _session.MappingRow(existing); meta["apiCallSuccess"] = true;
                    return "Mapping " + (action == "createMapping" ? "created" : "updated") + " and verified; no save/compile/download.";
                }
                if (category == "ident")
                {
                    var provider = RequireOfficialService(to, Logic.IdentProvider);
                    var target = Logic.ParseConnectionTarget(targetJson);
                    if (target.Mode != "deviceItem") throw new ArgumentException("connectIdent requires a deviceItem target.");
                    var item = _session.ExactDeviceItem(target.DevicePath!, target.ItemPath!);
                    if (provider.GetType().GetMethod("Connect", new[] { typeof(DeviceItem) }) == null) throw new NotSupportedException("Native Ident Connect(DeviceItem) unavailable.");
                    meta["before"] = new JsonObject { ["connectedIdentDevice"] = (provider.GetType().GetProperty("ConnectedIdentDevice")?.GetValue(provider) as DeviceItem)?.Name };
                    meta["deviceItem"] = item.Name;
                    if (!writing) return "Ident device connection preview; no changes.";
                    meta["mayHaveChanged"] = true;
#if !TIA_V20
                    if (provider is global::Siemens.Engineering.SW.TechnologicalObjects.Ident.IdentTechnologicalObjectProvider identProvider) identProvider.Connect(item);
                    else EngineeringGroupOperations.Call(provider, "Connect", new[] { typeof(DeviceItem) }, item);
#else
                    EngineeringGroupOperations.Call(provider, "Connect", new[] { typeof(DeviceItem) }, item);
#endif
                    meta["apiCallSuccess"] = true;
                    var connected = provider.GetType().GetProperty("ConnectedIdentDevice")?.GetValue(provider) as DeviceItem;
                    if (!Equals(connected, item)) throw new InvalidOperationException("Connect returned but ConnectedIdentDevice differs.");
                    meta["after"] = new JsonObject { ["connectedIdentDevice"] = connected!.Name };
                    return "Ident device connected and verified; no save/compile/download.";
                }
                // connection
                {
                    var (serviceName, property) = Logic.ConnectionAspects[aspect];
                    var service = RequireOfficialService(to, serviceName);
                    object iface;
                    if (property == "") iface = service;
                    else if (service is AxisHardwareConnectionProvider axisProvider)
                    {
                        // Typed AxisHardwareConnectionProvider navigation (ActorInterface / SensorInterface[i] / TorqueInterface).
                        if (aspect == "sensor")
                        {
                            AxisEncoderHardwareConnectionInterfaceComposition sensors = axisProvider.SensorInterface;
                            if (sensorIndex < 0 || sensorIndex >= sensors.Count) throw new ArgumentException("sensorIndex out of range (0.." + (sensors.Count - 1) + ").");
                            iface = sensors[sensorIndex];
                        }
                        else iface = aspect == "torque" ? (object)axisProvider.TorqueInterface : axisProvider.ActorInterface;
                    }
                    else if (service is EncoderHardwareConnectionProvider encoderProvider) iface = encoderProvider.SensorInterface;
                    else if (aspect == "sensor")
                    {
                        var sensors = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(service, property)).ToArray();
                        if (sensorIndex < 0 || sensorIndex >= sensors.Length) throw new ArgumentException("sensorIndex out of range (0.." + (sensors.Length - 1) + ").");
                        iface = sensors[sensorIndex];
                    }
                    else iface = EngineeringGroupOperations.Get(service, property);
                    meta["before"] = _session.InterfaceRow(iface);
                    var isConnected = iface.GetType().GetProperty("IsConnected");
                    if (action == "disconnect")
                    {
                        if (iface.GetType().GetMethod("Disconnect", Type.EmptyTypes) == null) throw new NotSupportedException("Native Disconnect unavailable.");
                        if (!writing) return "Hardware disconnect preview; no changes.";
                        meta["mayHaveChanged"] = true;
                        if (!_session.DisconnectTyped(iface)) EngineeringGroupOperations.Call(iface, "Disconnect", Type.EmptyTypes);
                        meta["apiCallSuccess"] = true;
                        meta["after"] = _session.InterfaceRow(iface);
                        if (_session.IsConnectedTyped(iface) ?? (isConnected != null && (bool)isConnected.GetValue(iface)!)) throw new InvalidOperationException("Disconnect returned but IsConnected is still true.");
                        return "Hardware interface disconnected and verified; no save/compile/download or motion command.";
                    }
                    var target = Logic.ParseConnectionTarget(targetJson);
                    Logic.RequireConnectionMode(aspect, target.Mode);
                    meta["targetMode"] = target.Mode;
                    Type[] signature; object[] args;
                    var optionType = OfficialType(Logic.MotionNs + "ConnectOption") ?? throw new NotSupportedException("ConnectOption enum unavailable.");
                    object Option() => EngineeringScalarProperties.ConvertValue(JsonValue.Create(target.ConnectOption), optionType)!;
                    switch (target.Mode)
                    {
                        case "deviceItem": signature = new[] { typeof(DeviceItem) }; args = new object[] { _session.ExactDeviceItem(target.DevicePath!, target.ItemPath!) }; break;
                        case "deviceItems":
                            var first = _session.ExactDeviceItem(target.DevicePath!, target.ItemPath!); var second = _session.ExactDeviceItem(target.DevicePath!, target.SecondItemPath!);
                            signature = target.HasConnectOption ? new[] { typeof(DeviceItem), typeof(DeviceItem), optionType } : new[] { typeof(DeviceItem), typeof(DeviceItem) };
                            args = target.HasConnectOption ? new object[] { first, second, Option() } : new object[] { first, second }; break;
                        case "deviceItemChannel": signature = new[] { typeof(DeviceItem), typeof(int) }; args = new object[] { _session.ExactDeviceItem(target.DevicePath!, target.ItemPath!), target.ChannelIndex }; break;
                        case "channel": signature = new[] { typeof(Channel) }; args = new object[] { _session.ExactChannel(_session.ExactDeviceItem(target.DevicePath!, target.ItemPath!), target.ChannelType, target.ChannelIoType, target.ChannelNumber) }; break;
                        case "dbMember": signature = new[] { typeof(string) }; args = new object[] { target.DbMemberPath }; break;
                        case "plcTag": signature = new[] { typeof(PlcTag) }; args = new object[] { _session.ExactPlcTag(softwarePath, target.PlcTagPath) }; break;
                        case "addresses": signature = new[] { typeof(int), typeof(int), optionType }; args = new object[] { target.InputBitAddress, target.OutputBitAddress, Option() }; break;
                        default: signature = new[] { typeof(int) }; args = new object[] { target.Address }; break;
                    }
                    if (iface.GetType().GetMethod("Connect", signature) == null) throw new NotSupportedException("Native Connect(" + string.Join(",", signature.Select(t => t.Name)) + ") unavailable on " + iface.GetType().FullName + ".");
                    meta["nativeSignature"] = "Connect(" + string.Join(",", signature.Select(t => t.Name)) + ")";
                    if (!writing) return "Hardware connection preview; native overload validated, no changes.";
                    meta["mayHaveChanged"] = true;
                    if (!_session.ConnectTyped(iface, target, softwarePath, (ConnectOption)Option())) EngineeringGroupOperations.Call(iface, "Connect", signature, args);
                    meta["apiCallSuccess"] = true;
                    meta["after"] = _session.InterfaceRow(iface);
                    if (!(_session.IsConnectedTyped(iface) ?? (isConnected == null || (bool)isConnected.GetValue(iface)!))) throw new InvalidOperationException("Connect returned but IsConnected is false.");
                    meta["mappingVerified"] = isConnected != null;
                    return "Offline hardware connection changed; inspect native readback. No save/compile/download or motion command.";
                }
            });

        public ResponseMessage ManagePlcSupervision(string softwarePath, string action, string blockPath = "", string providerKind = "supervision", string compositionName = "",
            string entryName = "", string typeName = "", string filePath = "", string attributesJson = "{}", int offset = 0, int limit = 100, bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcSupervision", meta => {
                Logic.RequireAction(action, Logic.SupervisionActions);
                if (providerKind != "supervision" && providerKind != "settings") throw new ArgumentException("providerKind must be supervision/settings.");
                Logic.RequirePagination(offset, limit);
                bool write = action is "createEntry" or "deleteEntry" or "setAttributes" or "importSettings";
                bool writing = write && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                object owner = string.IsNullOrEmpty(blockPath) ? plc : _session.ExactMasterCopyPlcSource(softwarePath, blockPath, true);
                meta["softwarePath"] = softwarePath; meta["blockPath"] = blockPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                meta["nativeLimits"] = "Openness V20/V21 expose no typed supervision composition; entries are reachable only through IEngineeringObject composition metadata of the providers or through XLSX exchange (ExchangePlcSupervisions).";
                var supervision = OptionalOfficialService(owner, Logic.SupervisionProvider, out var supervisionState);
                var settings = OptionalOfficialService(owner, Logic.SupervisionSettingsProvider, out var settingsState);
                meta["providerStates"] = new JsonObject { ["SupervisionProvider"] = supervisionState, ["SupervisionSettingsProvider"] = settingsState };
                if (action == "read")
                {
                    if (supervision == null && settings == null) throw new NotSupportedException("No ProDiag supervision provider on this object/version (" + supervisionState + "/" + settingsState + ").");
                    bool complete = true;
                    foreach (var (key, provider) in new[] { ("supervisionProvider", supervision), ("settingsProvider", settings) })
                    {
                        if (provider == null) continue;
                        var row = EngineeringDynamicAccess.Read(provider); row["compositions"] = new JsonArray(EngineeringDynamicAccess.CompositionNames(provider).Select(n => (JsonNode)n).ToArray());
                        row["clrProperties"] = EngineeringScalarProperties.Read(provider); meta[key] = row; complete &= row["dataComplete"]!.GetValue<bool>();
                    }
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = complete;
                    return "ProDiag provider metadata read; use readComposition for an advertised composition.";
                }
                if (action == "exportSettings" || action == "importSettings")
                {
                    var provider = settings ?? throw new NotSupportedException("SupervisionSettingsProvider is " + settingsState + ".");
                    bool import = action == "importSettings";
                    var file = import ? new FileInfo(filePath) : NativeFileOutput.Plan(filePath);
                    if (import && !file.Exists) throw new FileNotFoundException("Supervision settings input (.dat) not found.");
                    var method = import ? "Import" : "Export";
                    if (provider.GetType().GetMethod(method, new[] { typeof(FileInfo) }) == null) throw new NotSupportedException("Native SupervisionSettingsProvider." + method + "(FileInfo) unavailable.");
                    if (dryRun) return "Supervision settings " + method.ToLowerInvariant() + " preview; no file or project change.";
                    meta["mayHaveChanged"] = import; meta["mayHaveWrittenFiles"] = !import;
                    var result = EngineeringGroupOperations.Call(provider, method, new[] { typeof(FileInfo) }, file);
                    OfficialServiceAccess.AttachResult(meta, result);
                    // Typed SupervisionSettingsExportImportResult (State / ErrorCount / WarningCount / Messages of SupervisionSettingsExportImportResultMessage).
                    if (result is global::Siemens.Engineering.SW.Supervision.SupervisionSettingsExportImportResult typed)
                    {
                        meta["errorCount"] = typed.ErrorCount; meta["warningCount"] = typed.WarningCount;
                        meta["messages"] = new JsonArray(EngineeringGroupOperations.Items(typed.Messages).Cast<global::Siemens.Engineering.SW.Supervision.SupervisionSettingsExportImportResultMessage>().Select(m => (JsonNode)m.Message).ToArray());
                    }
                    var state = result?.GetType().GetProperty("State")?.GetValue(result)?.ToString();
                    meta["nativeState"] = state; meta["nativeSuccessVerified"] = state == "Success";
                    if (state != "Success") meta["operationSuccess"] = false;
                    if (!import) meta["file"] = NativeFileOutput.Verify(file);
                    return "Native supervision settings " + method.ToLowerInvariant() + " returned; inspect native state and messages. No save/compile/download.";
                }
                var host = providerKind == "settings" ? settings : supervision;
                if (host == null) throw new NotSupportedException("Selected provider is " + (providerKind == "settings" ? settingsState : supervisionState) + ".");
                if (action == "setAttributes")
                {
                    var prepared = EngineeringDynamicAccess.Prepare(host, Logic.ParseAttributes(attributesJson, true));
                    meta["before"] = EngineeringDynamicAccess.Read(host);
                    if (!writing) return "Provider attribute update preview; no changes.";
                    EngineeringDynamicAccess.Apply(host, prepared, meta); meta["apiCallSuccess"] = true;
                    meta["after"] = EngineeringDynamicAccess.Read(host);
                    return "Provider attributes changed and verified; no save/compile/download.";
                }
                var composition = EngineeringDynamicAccess.Composition(host, compositionName);
                meta["compositionName"] = compositionName;
                if (action == "readComposition")
                {
                    Logic.ParseAttributes(attributesJson, false);
                    var items = EngineeringGroupOperations.Items(composition).ToArray();
                    var rows = items.Skip(offset).Take(limit).Select(x => { var row = EngineeringDynamicAccess.Read(x); row["clrProperties"] = EngineeringScalarProperties.Read(x); row["name"] = EngineeringDynamicAccess.Name(x); return (JsonNode)row; }).ToArray();
                    meta["records"] = new JsonArray(rows); meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Length;
                    meta["nextOffset"] = offset + rows.Length < items.Length ? offset + rows.Length : (int?)null;
                    try { meta["creationTypes"] = new JsonArray(EngineeringDynamicAccess.CreationTypes(composition).Select(t => (JsonNode)t.FullName!).ToArray()); }
                    catch (NotSupportedException ex) { meta["creationTypes"] = null; meta["creationTypesError"] = ex.Message; }
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Length == items.Length && rows.All(r => r!["dataComplete"]!.GetValue<bool>());
                    return "Advertised supervision composition read with live pagination.";
                }
                if (action == "createEntry")
                {
                    var type = EngineeringDynamicAccess.CreationType(composition, typeName);
                    var attributes = Logic.ParseAttributes(attributesJson, true);
                    meta["typeName"] = type.FullName; meta["requestedAttributes"] = attributes.DeepClone();
                    if (!writing) return "Supervision entry creation preview; advertised type validated, no changes.";
                    meta["mayHaveChanged"] = true;
                    var created = EngineeringDynamicAccess.Create(composition, type, attributes); meta["apiCallSuccess"] = true;
                    meta["after"] = EngineeringDynamicAccess.Read(created);
                    if (attributes.ContainsKey("Name") && !string.Equals(EngineeringDynamicAccess.Name(created), attributes["Name"]?.GetValue<string>(), StringComparison.Ordinal)) throw new InvalidOperationException("Created entry Name readback differs.");
                    return "Supervision entry created through the official dynamic composition; no save/compile/download.";
                }
                // deleteEntry
                Logic.ParseAttributes(attributesJson, false); Logic.RequireName(entryName, "entryName");
                var entry = EngineeringDynamicAccess.FindByName(composition, entryName);
                meta["before"] = EngineeringDynamicAccess.Read(entry);
                if (!confirmDelete) throw new ArgumentException("confirmDelete=true is required to delete a supervision entry.");
                if (!EngineeringDynamicAccess.CanDelete(entry)) throw new NotSupportedException("Native Delete is not advertised for this entry type.");
                if (!writing) return "Supervision entry deletion preview; no changes.";
                meta["mayHaveChanged"] = true;
                EngineeringDynamicAccess.Delete(entry); meta["apiCallSuccess"] = true;
                if (EngineeringGroupOperations.Items(composition).Any(x => string.Equals(EngineeringDynamicAccess.Name(x), entryName, StringComparison.Ordinal))) throw new InvalidOperationException("Entry remains after Delete.");
                meta["verifiedAbsent"] = true;
                return "Supervision entry deleted and verified absent; no save/compile/download.";
            });

        // Typed VBScriptSystemFolder / VBScriptUserFolder navigation (Portal.ClassicHmiFolders.cs).
        private object ClassicScriptFolder(HmiTarget hmi, IEnumerable<string> folderPath) => _session.ExactVbScriptFolder(hmi, folderPath);
        private static JsonObject ClassicRow(object target, string? path = null)
        {
            var row = EngineeringDynamicAccess.Read(target); row["clrProperties"] = EngineeringScalarProperties.Read(target); row["name"] = EngineeringDynamicAccess.Name(target);
            if (path != null) row["path"] = path;
            row["dataComplete"] = row["dataComplete"]!.GetValue<bool>() && row["clrProperties"]!["dataComplete"]!.GetValue<bool>();
            return row;
        }
        private static JsonObject ClassicPage(JsonObject meta, object[] items, int offset, int limit, Func<object, JsonNode> project)
        {
            var rows = items.Skip(offset).Take(limit).Select(project).ToArray();
            meta["records"] = new JsonArray(rows); meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Length;
            meta["nextOffset"] = offset + rows.Length < items.Length ? offset + rows.Length : (int?)null;
            meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Length == items.Length && rows.All(r => r!["dataComplete"]?.GetValue<bool>() != false);
            return meta;
        }
        private static string ClassicExport(object target, string filePath, JsonObject meta, bool dryRun)
        {
            var file = NativeFileOutput.Plan(filePath);
            var signature = new[] { typeof(FileInfo), typeof(ExportOptions) };
            if (target.GetType().GetMethod("Export", signature) == null) throw new NotSupportedException("Native Export(FileInfo, ExportOptions) unavailable on " + target.GetType().FullName + ".");
            if (dryRun) return "Native export preview; no file written.";
            meta["mayHaveWrittenFiles"] = true;
            EngineeringGroupOperations.Call(target, "Export", signature, file, ExportOptions.None);
            meta["file"] = NativeFileOutput.Verify(file); meta["apiCallSuccess"] = true;
            return "Native XML export written and hashed; content semantics not verified.";
        }
        private static string ClassicImport(object composition, string filePath, string importOptions, bool confirmDelete, JsonObject meta, bool writing)
        {
            var file = new FileInfo(filePath);
            if (!file.Exists) throw new FileNotFoundException("Import input not found.");
            var options = (ImportOptions)EngineeringScalarProperties.ConvertValue(JsonValue.Create(importOptions), typeof(ImportOptions))!;
            Logic.RequireImportConfirmation(options.ToString(), confirmDelete);
            var signature = new[] { typeof(FileInfo), typeof(ImportOptions) };
            if (composition.GetType().GetMethod("Import", signature) == null) throw new NotSupportedException("Native Import(FileInfo, ImportOptions) unavailable on " + composition.GetType().FullName + ".");
            meta["importOptions"] = options.ToString();
            if (!writing) return "Native import preview; no changes.";
            meta["mayHaveChanged"] = true;
            var result = EngineeringGroupOperations.Call(composition, "Import", signature, file, options);
            OfficialServiceAccess.AttachResult(meta, result);
            meta["importedNames"] = new JsonArray((result as IEnumerable)?.Cast<object>().Select(x => (JsonNode)(EngineeringDynamicAccess.Name(x) ?? "")).ToArray() ?? Array.Empty<JsonNode>());
            return "Native import returned; inspect imported names. No save/compile/download.";
        }
        private static string ClassicDelete(object target, object composition, string name, bool confirmDelete, JsonObject meta, bool writing)
        {
            if (!confirmDelete) throw new ArgumentException("confirmDelete=true is required for delete.");
            if (target.GetType().GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Native Delete unavailable on " + target.GetType().FullName + ".");
            if (!writing) return "Native deletion preview; no changes.";
            meta["mayHaveChanged"] = true;
            EngineeringGroupOperations.Call(target, "Delete", Type.EmptyTypes); meta["apiCallSuccess"] = true;
            if (EngineeringGroupOperations.Find(composition, name) != null) throw new InvalidOperationException("Object remains after Delete.");
            meta["verifiedAbsent"] = true;
            return "Object deleted and verified absent; no save/compile/download.";
        }
        private static string ClassicSetAttributes(object target, string attributesJson, JsonObject meta, bool writing)
        {
            var prepared = EngineeringDynamicAccess.Prepare(target, Logic.ParseAttributes(attributesJson, true));
            meta["before"] = ClassicRow(target);
            if (!writing) return "Attribute update preview; advertised writable attributes validated, no changes.";
            EngineeringDynamicAccess.Apply(target, prepared, meta); meta["apiCallSuccess"] = true;
            meta["after"] = ClassicRow(target);
            return "Attributes changed and verified by readback; no save/compile/download.";
        }

        public ResponseMessage ReadClassicHmiScripts(string softwarePath, string folderPath = "", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("ReadClassicHmiScripts", meta => {
                Logic.RequirePagination(offset, limit);
                var hmi = _session.ExactClassicHmi(softwarePath);
                var root = ClassicScriptFolder(hmi, EngineeringGroupOperations.Parts(folderPath, true));
                var scripts = new List<(string Path, object Script)>(); var folders = new JsonArray();
                void Walk(object folder, string path, int depth)
                {
                    if (depth > 64) throw new InvalidOperationException("Script folder depth exceeds 64.");
                    foreach (var script in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(folder, "VBScripts")))
                    {
                        if (scripts.Count >= 10000) throw new InvalidOperationException("More than 10000 scripts; narrow folderPath.");
                        scripts.Add((path + (EngineeringDynamicAccess.Name(script) ?? ""), script));
                    }
                    foreach (var child in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(folder, "Folders")))
                    {
                        var childPath = path + (EngineeringDynamicAccess.Name(child) ?? "") + "/"; folders.Add(childPath.TrimEnd('/'));
                        Walk(child, childPath, depth + 1);
                    }
                }
                Walk(root, folderPath == "" ? "" : folderPath.TrimEnd('/') + "/", 0);
                meta["softwarePath"] = softwarePath; meta["folderPath"] = folderPath; meta["folders"] = folders;
                meta["codeAccess"] = "VB script source is not a typed Openness property; readable attributes are listed per script and the native XML is available via ManageClassicHmiScript export.";
                ClassicPage(meta, scripts.Select(s => s.Script).ToArray(), offset, limit, s => ClassicRow(s, scripts.First(x => ReferenceEquals(x.Script, s)).Path));
                return "Classic HMI VB scripts listed with advertised attributes; live pagination.";
            });

        public ResponseMessage ManageClassicHmiScript(string softwarePath, string scriptPath, string action, string filePath = "", string importOptions = "None",
            string attributesJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageClassicHmiScript", meta => {
                Logic.RequireAction(action, Logic.ScriptActions);
                bool write = action != "read" && action != "export";
                bool writing = write && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var hmi = _session.ExactClassicHmi(softwarePath);
                meta["softwarePath"] = softwarePath; meta["scriptPath"] = scriptPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                if (action == "import")
                {
                    Logic.ParseAttributes(attributesJson, false);
                    var folder = ClassicScriptFolder(hmi, EngineeringGroupOperations.Parts(scriptPath, true));
                    return ClassicImport(EngineeringGroupOperations.Get(folder, "VBScripts"), filePath, importOptions, confirmDelete, meta, writing);
                }
                var (folderPath, name) = Logic.SplitObjectPath(scriptPath);
                var parent = ClassicScriptFolder(hmi, folderPath);
                if (action == "createFolder" || action == "deleteFolder")
                {
                    Logic.ParseAttributes(attributesJson, false);
                    var folders = EngineeringGroupOperations.Get(parent, "Folders");
                    var existing = EngineeringGroupOperations.Find(folders, name);
                    if (action == "createFolder")
                    {
                        if (existing != null) throw new InvalidOperationException("Script folder already exists.");
                        if (folders.GetType().GetMethod("Create", new[] { typeof(string) }) == null) throw new NotSupportedException("Native VBScriptUserFolderComposition.Create(string) unavailable.");
                        if (!writing) return "Script folder creation preview; no changes.";
                        meta["mayHaveChanged"] = true;
                        object created = folders is global::Siemens.Engineering.Hmi.RuntimeScripting.VBScriptUserFolderComposition typedFolders ? typedFolders.Create(name) : EngineeringGroupOperations.Call(folders, "Create", new[] { typeof(string) }, name); meta["apiCallSuccess"] = true;
                        if (!string.Equals(EngineeringDynamicAccess.Name(created), name, StringComparison.Ordinal)) throw new InvalidOperationException("Created folder name readback differs.");
                        meta["after"] = ClassicRow(created);
                        return "Script folder created and verified; no save/compile/download.";
                    }
                    if (existing == null) throw new PortalException(PortalErrorCode.NotFound, "Script folder not found: " + name);
                    int childCount = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(existing, "Folders")).Count(), scriptCount = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(existing, "VBScripts")).Count();
                    meta["childCount"] = childCount; meta["scriptCount"] = scriptCount;
                    if (childCount != 0 || scriptCount != 0) throw new InvalidOperationException("Only empty script folders can be deleted.");
                    return ClassicDelete(existing, folders, name, confirmDelete, meta, writing);
                }
                var scripts = EngineeringGroupOperations.Get(parent, "VBScripts");
                var script = EngineeringGroupOperations.Find(scripts, name) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact VB script not found: " + scriptPath);
                switch (action)
                {
                    case "read": Logic.ParseAttributes(attributesJson, false); meta["script"] = ClassicRow(script, scriptPath); meta["apiCallSuccess"] = true; meta["dataComplete"] = meta["script"]!["dataComplete"]!.DeepClone(); return "VB script read.";
                    case "export":
                        Logic.ParseAttributes(attributesJson, false);
                        if (script is global::Siemens.Engineering.Hmi.RuntimeScripting.VBScript typedScript)
                        {
                            // Typed VBScript.Export(FileInfo, ExportOptions).
                            var file = NativeFileOutput.Plan(filePath);
                            if (dryRun) return "Native export preview; no file written.";
                            meta["mayHaveWrittenFiles"] = true; typedScript.Export(file, ExportOptions.None); meta["apiCallSuccess"] = true; meta["file"] = NativeFileOutput.Verify(file);
                            return "VB script exported to a new XML file and hashed; no project change.";
                        }
                        return ClassicExport(script, filePath, meta, dryRun);
                    case "delete":
                        Logic.ParseAttributes(attributesJson, false); meta["before"] = ClassicRow(script, scriptPath);
                        if (script is global::Siemens.Engineering.Hmi.RuntimeScripting.VBScript deletable && scripts is global::Siemens.Engineering.Hmi.RuntimeScripting.VBScriptComposition typedScripts)
                        {
                            if (!confirmDelete) throw new ArgumentException("confirmDelete=true is required to delete a VB script.");
                            if (!writing) return "VB script deletion preview; no changes.";
                            meta["mayHaveChanged"] = true; deletable.Delete(); meta["apiCallSuccess"] = true;
                            if (typedScripts.Find(name) != null) throw new InvalidOperationException("VB script remains after Delete.");
                            meta["verifiedAbsent"] = true;
                            return "VB script deleted and absence verified; no save/compile/download.";
                        }
                        return ClassicDelete(script, scripts, name, confirmDelete, meta, writing);
                    default: return ClassicSetAttributes(script, attributesJson, meta, writing);
                }
            });

        public ResponseMessage ManageClassicHmiCycle(string softwarePath, string action, string cycleName = "", string filePath = "", string importOptions = "None",
            string attributesJson = "{}", int offset = 0, int limit = 100, bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageClassicHmiCycle", meta => {
                Logic.RequireAction(action, Logic.CycleActions); Logic.RequirePagination(offset, limit);
                bool write = action != "read" && action != "export";
                bool writing = write && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var hmi = _session.ExactClassicHmi(softwarePath);
                var cycles = hmi.Cycles ?? throw new NotSupportedException("Cycles composition unavailable.");
                meta["softwarePath"] = softwarePath; meta["cycleName"] = cycleName; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                meta["nativeLimits"] = "CycleComposition exposes no Create; new cycles arrive only through native XML Import. Cycle time/unit are dynamic attributes (see schema).";
                if (action == "import") { Logic.ParseAttributes(attributesJson, false); return ClassicImport(cycles, filePath, importOptions, confirmDelete, meta, writing); }
                if (action == "read" && cycleName == "")
                {
                    Logic.ParseAttributes(attributesJson, false);
                    ClassicPage(meta, EngineeringGroupOperations.Items(cycles).ToArray(), offset, limit, c => ClassicRow(c));
                    return "Classic HMI cycles listed with advertised attributes; live pagination.";
                }
                Logic.RequireName(cycleName, "cycleName");
                var cycle = EngineeringGroupOperations.Find(cycles, cycleName) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact cycle not found: " + cycleName);
                bool system = cycle.GetType().GetProperty("IsSystemObject")?.GetValue(cycle) as bool? == true;
                meta["isSystemObject"] = system;
                switch (action)
                {
                    case "read": Logic.ParseAttributes(attributesJson, false); meta["cycle"] = ClassicRow(cycle); meta["apiCallSuccess"] = true; meta["dataComplete"] = meta["cycle"]!["dataComplete"]!.DeepClone(); return "Cycle read.";
                    case "export": Logic.ParseAttributes(attributesJson, false); return ClassicExport(cycle, filePath, meta, dryRun);
                    case "delete":
                        Logic.ParseAttributes(attributesJson, false); meta["before"] = ClassicRow(cycle);
                        if (system) throw new InvalidOperationException("System cycles cannot be deleted.");
                        return ClassicDelete(cycle, cycles, cycleName, confirmDelete, meta, writing);
                    default:
                        if (system) throw new InvalidOperationException("System cycles are not editable.");
                        return ClassicSetAttributes(cycle, attributesJson, meta, writing);
                }
            });

        public ResponseMessage ManageClassicHmiTextGraphicList(string softwarePath, string listKind, string action, string listName = "", string compositionName = "", string entryName = "",
            string typeName = "", string filePath = "", string importOptions = "None", string attributesJson = "{}", int offset = 0, int limit = 100, bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageClassicHmiTextGraphicList", meta => {
                Logic.RequireAction(action, Logic.ListActions); Logic.RequirePagination(offset, limit);
                if (!Logic.ListKinds.ContainsKey(listKind)) throw new ArgumentException("listKind must be text/graphic.");
                bool write = action is "createEntry" or "deleteEntry" or "import" or "delete" or "setAttributes";
                bool writing = write && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var hmi = _session.ExactClassicHmi(softwarePath);
                var (property, expectedType) = Logic.ListKinds[listKind];
                var lists = EngineeringGroupOperations.Get(hmi, property);
                meta["softwarePath"] = softwarePath; meta["listKind"] = listKind; meta["listName"] = listName; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                meta["nativeLimits"] = "Text/graphic list compositions expose no Create; new lists arrive only through native XML Import. Entries are reachable only as compositions advertised by GetCompositionInfos.";
                if (action == "import") { Logic.ParseAttributes(attributesJson, false); return ClassicImport(lists, filePath, importOptions, confirmDelete, meta, writing); }
                if (action == "read" && listName == "")
                {
                    Logic.ParseAttributes(attributesJson, false);
                    ClassicPage(meta, EngineeringGroupOperations.Items(lists).ToArray(), offset, limit, l => ClassicRow(l));
                    return "Classic HMI " + listKind + " lists listed with advertised attributes; live pagination.";
                }
                Logic.RequireName(listName, "listName");
                var list = EngineeringGroupOperations.Find(lists, listName) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact " + listKind + " list not found: " + listName);
                if (list.GetType().FullName != expectedType) throw new NotSupportedException("Unexpected native list type: " + list.GetType().FullName);
                switch (action)
                {
                    case "read":
                        Logic.ParseAttributes(attributesJson, false);
                        var row = ClassicRow(list); row["compositions"] = new JsonArray(EngineeringDynamicAccess.CompositionNames(list).Select(n => (JsonNode)n).ToArray());
                        meta["list"] = row; meta["apiCallSuccess"] = true; meta["dataComplete"] = row["dataComplete"]!.DeepClone();
                        return "List read with advertised compositions; use readEntries with a compositionName.";
                    case "export": Logic.ParseAttributes(attributesJson, false); return ClassicExport(list, filePath, meta, dryRun);
                    case "delete": Logic.ParseAttributes(attributesJson, false); meta["before"] = ClassicRow(list); return ClassicDelete(list, lists, listName, confirmDelete, meta, writing);
                    case "setAttributes": return ClassicSetAttributes(list, attributesJson, meta, writing);
                }
                var composition = EngineeringDynamicAccess.Composition(list, compositionName);
                meta["compositionName"] = compositionName;
                if (action == "readEntries")
                {
                    Logic.ParseAttributes(attributesJson, false);
                    ClassicPage(meta, EngineeringGroupOperations.Items(composition).ToArray(), offset, limit, e => ClassicRow(e));
                    try { meta["creationTypes"] = new JsonArray(EngineeringDynamicAccess.CreationTypes(composition).Select(t => (JsonNode)t.FullName!).ToArray()); }
                    catch (NotSupportedException ex) { meta["creationTypes"] = null; meta["creationTypesError"] = ex.Message; }
                    return "List entries read from the advertised composition; live pagination.";
                }
                if (action == "createEntry")
                {
                    var type = EngineeringDynamicAccess.CreationType(composition, typeName);
                    var attributes = Logic.ParseAttributes(attributesJson, true);
                    meta["typeName"] = type.FullName; meta["requestedAttributes"] = attributes.DeepClone();
                    if (!writing) return "List entry creation preview; advertised type validated, no changes.";
                    meta["mayHaveChanged"] = true;
                    var created = EngineeringDynamicAccess.Create(composition, type, attributes); meta["apiCallSuccess"] = true;
                    meta["after"] = ClassicRow(created);
                    if (attributes.ContainsKey("Name") && !string.Equals(EngineeringDynamicAccess.Name(created), attributes["Name"]?.GetValue<string>(), StringComparison.Ordinal)) throw new InvalidOperationException("Created entry Name readback differs.");
                    return "List entry created through the official dynamic composition; no save/compile/download.";
                }
                Logic.ParseAttributes(attributesJson, false); Logic.RequireName(entryName, "entryName");
                var entry = EngineeringDynamicAccess.FindByName(composition, entryName);
                meta["before"] = ClassicRow(entry);
                if (!confirmDelete) throw new ArgumentException("confirmDelete=true is required to delete an entry.");
                if (!EngineeringDynamicAccess.CanDelete(entry)) throw new NotSupportedException("Native Delete is not advertised for this entry type.");
                if (!writing) return "List entry deletion preview; no changes.";
                meta["mayHaveChanged"] = true;
                EngineeringDynamicAccess.Delete(entry); meta["apiCallSuccess"] = true;
                if (EngineeringGroupOperations.Items(composition).Any(x => string.Equals(EngineeringDynamicAccess.Name(x), entryName, StringComparison.Ordinal))) throw new InvalidOperationException("Entry remains after Delete.");
                meta["verifiedAbsent"] = true;
                return "List entry deleted and verified absent; no save/compile/download.";
            });

        public ResponseMessage ReadClassicHmiGlobalization(string softwarePath, int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("ReadClassicHmiGlobalization", meta => {
                Logic.RequirePagination(offset, limit);
                var hmi = _session.ExactClassicHmi(softwarePath);
                var provider = RequireOfficialService(hmi, Logic.GraphicsProvider);
                var graphics = EngineeringGroupOperations.Get(provider, "Graphics");
                meta["softwarePath"] = softwarePath; meta["provider"] = provider.GetType().FullName;
                ClassicPage(meta, EngineeringGroupOperations.Items(graphics).ToArray(), offset, limit, g => ClassicRow(g));
                meta["scope"] = "Multilingual graphics of the classic HMI GraphicsProvider: names, CLR scalars and advertised attributes. Image bytes are not read.";
                return "Classic HMI multilingual graphics listed; live pagination.";
            });

        public ResponseMessage ReadClassicHmiFaceplates(string kind = "faceplate", string libraryName = "", string folderPath = "", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("ReadClassicHmiFaceplates", meta => {
                Logic.RequirePagination(offset, limit);
                if (kind != "all" && !Logic.LibraryTypeKinds.ContainsKey(kind)) throw new ArgumentException("kind must be faceplate/vbScript/cScript/all.");
                var wanted = kind == "all" ? Logic.LibraryTypeKinds.Values.ToArray() : new[] { Logic.LibraryTypeKinds[kind] };
                var library = _session.ExactOpenEngineeringLibrary(libraryName);
                var root = _session.EngineeringLibraryFolder(library, folderPath, "TypeFolder");
                var found = new List<(string Path, object Type)>();
                void Walk(object folder, string path, int depth)
                {
                    if (depth > 64) throw new InvalidOperationException("Library folder depth exceeds 64.");
                    foreach (var type in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(folder, "Types")))
                        if (wanted.Contains(type.GetType().FullName)) { if (found.Count >= 10000) throw new InvalidOperationException("More than 10000 matching types; narrow folderPath."); found.Add((path + (EngineeringDynamicAccess.Name(type) ?? ""), type)); }
                    foreach (var child in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(folder, "Folders")))
                        Walk(child, path + (EngineeringDynamicAccess.Name(child) ?? "") + "/", depth + 1);
                }
                Walk(root, folderPath == "" ? "" : folderPath.TrimEnd('/') + "/", 0);
                meta["kind"] = kind; meta["libraryName"] = libraryName; meta["folderPath"] = folderPath; meta["nativeTypes"] = new JsonArray(wanted.Select(w => (JsonNode)w).ToArray());
                ClassicPage(meta, found.Select(f => f.Type).ToArray(), offset, limit, t => {
                    var row = EngineeringScalarProperties.Read(t); row["path"] = found.First(f => ReferenceEquals(f.Type, t)).Path;
                    var versions = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(t, "Versions")).Take(101).ToArray();
                    row["versions"] = new JsonArray(versions.Take(100).Select(v => (JsonNode)EngineeringScalarProperties.Read(v)).ToArray()); row["versionsTruncated"] = versions.Length > 100;
                    row["dataComplete"] = row["dataComplete"]!.GetValue<bool>() && versions.Length <= 100 && row["versions"]!.AsArray().All(v => v!["dataComplete"]!.GetValue<bool>());
                    return (JsonNode)row;
                });
                meta["scope"] = "Classic HMI faceplate/VB-script/C-script library types with their versions (scalar properties only). No instantiation.";
                return "Classic HMI library types listed; live pagination.";
            });
        // ---- ProDiag CSV export -------------------------------------------------------------------------------------------------------------------
        public ResponseMessage ExportPlcProDiagInfo(string softwarePath, string blockPath, string directoryPath, string unitName = "", string unitKind = "unit", bool dryRun = true)
            => _session.RunHmiStepTool("ExportPlcProDiagInfo", meta => {
                ProDiagExportRules.ValidateProDiagRequest(blockPath, directoryPath, unitName, unitKind);
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var unit = _session.OptionalUnit(plc, unitName, unitKind);
                var block = (PlcBlock)_session.ExactObjectUnder(_session.BlockRootOf(plc, unit), blockPath, "Blocks", "block");
                CodeBlock code = block as CodeBlock ?? throw new ArgumentException("blockPath must name a code block (FB); " + block.GetType().Name + " has no ExportProDIAGInfo.");
                meta["block"] = new JsonObject { ["name"] = code.Name, ["blockClass"] = code.GetType().Name, ["programmingLanguage"] = code.ProgrammingLanguage.ToString(), ["isConsistent"] = code.IsConsistent, ["unit"] = unit?.Name };
                var refusal = ProDiagExportRules.ProDiagRefusal(code.ProgrammingLanguage.ToString(), code.IsConsistent);
                if (refusal != null) throw new PortalException(PortalErrorCode.InvalidState, refusal);
                var directory = new DirectoryInfo(directoryPath);
                if (!directory.Exists) throw new DirectoryNotFoundException("directoryPath must already exist: " + directoryPath);
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false;
                if (dryRun) return "ProDiag CSV export preview; no files written.";
                var before = SoftwareUnitDeepLogic.SnapshotDirectory(directory.FullName);
                meta["mayHaveWrittenFiles"] = true;
                code.ExportProDIAGInfo(directory); meta["apiCallSuccess"] = true;
                meta["newFiles"] = SoftwareUnitDeepLogic.NewFilesSince(directory.FullName, before);
                return "ProDiag alarm messages exported as CSV (new files hashed); no project change.";
            });

        public ResponseMessage ExchangeMotionCamData(string softwarePath,string objectPath,string action,string filePath,string format="",string separator="",int pointCount=0,bool dryRun=true)
            =>_session.RunHmiStepTool("ExchangeMotionCamData",meta=>{
                var method=action switch {"import"=>"LoadCamData","importBinary"=>"LoadCamDataBinary","export"=>"SaveCamData","exportBinary"=>"SaveCamDataBinary","exportPoints"=>"SaveCamDataPointList",_=>throw new ArgumentException("Invalid cam exchange action.")};
                bool write=action.StartsWith("import",StringComparison.Ordinal);
                using var access=write&&!dryRun ? _session.AcquireHmiEditAccess() : null;
                var target=_session.ExactTechnology(softwarePath,objectPath,write&&!dryRun);
                var service=OfficialServiceAccess.Require(target,"Siemens.Engineering.SW.TechnologicalObjects.Motion.CamDataSupport","Siemens.Engineering.Step7");
                var call=service.GetType().GetMethods().Single(m=>m.Name==method);
                var file=write ? new FileInfo(filePath) : NativeFileOutput.Plan(filePath);
                if(write&&!file.Exists)throw new FileNotFoundException("Cam input not found.");
                var parameters=call.GetParameters();var args=new object[parameters.Length];args[0]=file;
                for(int i=1;i<parameters.Length;i++)args[i]=parameters[i].ParameterType==typeof(int) ? pointCount : EngineeringScalarProperties.ConvertValue(JsonValue.Create(parameters[i].ParameterType.Name=="CamDataFormat" ? format : separator),parameters[i].ParameterType)!;
                if(action=="exportPoints"&&pointCount<1)throw new ArgumentException("Positive pointCount required.");
                meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;meta["mayHaveWrittenFiles"]=false;meta["objectPath"]=objectPath;
                if(dryRun)return "Native cam data exchange preview.";
                meta["mayHaveChanged"]=write;meta["mayHaveWrittenFiles"]=!write;
                var result=EngineeringGroupOperations.Call(service,method,parameters.Select(p=>p.ParameterType).ToArray(),args);
                OfficialServiceAccess.AttachResult(meta,result);if(result is bool ok&&!ok)meta["operationSuccess"]=false;
                if(!write)meta["file"]=NativeFileOutput.Verify(file);
                return "Native cam exchange returned; file/diagnostic evidence supplied. No save/compile/download or motion command.";
            });

        public ResponseMessage ConfigureMotionHardwareConnection(string softwarePath,string objectPath,string interfaceKind,string action,int inputBitAddress=0,int outputBitAddress=0,string connectOption="Default",int sensorIndex=0,bool dryRun=true)
            =>_session.RunHmiStepTool("ConfigureMotionHardwareConnection",meta=>{
                if (!new[] { "read", "connect", "disconnect" }.Contains(action)) throw new ArgumentException("action must be one of: read/connect/disconnect (case-sensitive).");
                bool write=action!="read"&&!dryRun;using var access=write ? _session.AcquireHmiEditAccess() : null;
                var target=_session.ExactTechnology(softwarePath,objectPath,write);
                // Typed AxisHardwareConnectionProvider (ActorInterface / SensorInterface[i] : AxisEncoderHardwareConnectionInterface, TorqueInterface : TorqueHardwareConnectionInterface).
                global::Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisHardwareConnectionProvider service=(target as global::Siemens.Engineering.IEngineeringServiceProvider)?.GetService<global::Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisHardwareConnectionProvider>()
                    ?? throw new NotSupportedException("AxisHardwareConnectionProvider is not provided by "+target.GetType().Name+" (axis technology objects only).");
                object selected;
                if(interfaceKind=="actor")selected=service.ActorInterface;
                else if(interfaceKind=="torque")selected=service.TorqueInterface;
                else if(interfaceKind=="sensor") {
                    global::Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisEncoderHardwareConnectionInterfaceComposition sensors=service.SensorInterface;
                    if(sensorIndex<0||sensorIndex>=sensors.Count)throw new ArgumentException("Sensor index out of range.");selected=sensors[sensorIndex];
                } else throw new ArgumentException("interfaceKind must be actor/sensor/torque.");
                meta["before"]=_session.InterfaceRow(selected);meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                var method=action=="connect" ? selected.GetType().GetMethods().SingleOrDefault(m=>m.Name=="Connect"&&m.GetParameters().Length==3&&m.GetParameters()[0].ParameterType==typeof(int)&&m.GetParameters()[1].ParameterType==typeof(int)) : selected.GetType().GetMethod("Disconnect",Type.EmptyTypes);
                if(action!="read"&&method==null)throw new NotSupportedException("Native hardware connection signature unavailable.");
                object? mode=null;if(action=="connect") {
                    if(inputBitAddress<0||outputBitAddress<0)throw new ArgumentException("Nonnegative BIT addresses required; no implicit byte conversion.");
                    mode=EngineeringScalarProperties.ConvertValue(JsonValue.Create(connectOption),method!.GetParameters()[2].ParameterType);
                }
                if(!write)return "Motion hardware mapping read/preview. Addresses are bits; no drive operation performed.";
                meta["mayHaveChanged"]=true;
                if(action=="connect") {
                    var option=(global::Siemens.Engineering.SW.TechnologicalObjects.Motion.ConnectOption)mode!;
                    if(selected is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisEncoderHardwareConnectionInterface axisInterface)axisInterface.Connect(inputBitAddress,outputBitAddress,option);
                    else ((global::Siemens.Engineering.SW.TechnologicalObjects.Motion.TorqueHardwareConnectionInterface)selected).Connect(inputBitAddress,outputBitAddress,option);
                } else if(!_session.DisconnectTyped(selected))EngineeringGroupOperations.Call(selected,"Disconnect",Type.EmptyTypes);
                meta["after"]=_session.InterfaceRow(selected);meta["mappingVerified"]=_session.IsConnectedTyped(selected)==(action=="connect");
                return "Offline motion hardware mapping changed; inspect native readback. No save/compile/download or motion command.";
            });
    }
}
