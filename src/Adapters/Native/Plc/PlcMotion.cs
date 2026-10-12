using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using Logic = TiaMcp.Adapters.Native.Plc.PlcMotionPolicy;
using WatchNative = TiaMcp.Adapters.WatchTechnologyPrimitives;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
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
        public static object? ResolveMotionOfficialService(object owner, string typeName, out string state)
            => OptionalOfficialService(owner, typeName, out state);
        public static object RequireMotionOfficialService(object owner, string typeName)
            => RequireOfficialService(owner, typeName);

        public HardwareAddressingReply ReadMotionAxisConfiguration(string softwarePath, string objectPath, bool includeParameters = false, int offset = 0, int limit = 100)
            => RunTool("GetMotionAxisConfiguration", meta => {
                Logic.RequirePagination(offset, limit);
                var target = ExactTechnology(softwarePath, objectPath, false);
                meta["softwarePath"] = softwarePath; meta["objectPath"] = objectPath; meta["object"] = HardwareScalarEvidence.Read(target);
                var services = new Dictionary<string, object?>(); var states = new Dictionary<string, object?>(); bool complete = true;
                foreach (var typeName in Logic.ReadableMotionServices)
                {
                    var service = OptionalOfficialService(target, typeName, out var state); states[typeName] = state;
                    if (service == null) continue;
                    var row = DescribeNode(service, 0); services[Logic.ShortTypeName(typeName)] = row;
                    if (row["dataComplete"] as bool? == false) complete = false;
                }
                meta["services"] = services; meta["serviceStates"] = states;
                if (target is TechnologicalInstanceDB typedObject) { meta["object"] = TechnologyObjectRow(typedObject); try { meta["typed"] = TypedMotionView(typedObject); } catch (Exception ex) { meta["typedError"] = ex.GetBaseException().Message; } }
                if (includeParameters)
                {
                    var parameters = PlcGroupOperations.Items(PlcGroupOperations.Get(target, "Parameters")).ToArray();
                    var page = parameters.Skip(offset).Take(limit).Select(p => p is TechnologicalParameter tp ? ParameterRow(tp) : HardwareScalarEvidence.Read(p)).ToArray();
                    meta["parameters"] = page.Cast<object?>().ToArray(); meta["parameterCount"] = parameters.Length;
                    meta["offset"] = offset; meta["limit"] = limit; meta["total"] = parameters.Length;
                    meta["nextOffset"] = offset + page.Length < parameters.Length ? offset + page.Length : (int?)null;
                    complete &= offset == 0 && page.Length == parameters.Length && page.All(p => (p.TryGetValue("dataComplete", out var completeValue) ? completeValue as bool? : null) ?? !p.ContainsKey("valueError"));
                }
                meta["apiCallSuccess"] = true; meta["dataComplete"] = complete;
                meta["scope"] = "Scalar values of the technology object and every Motion/Ident service it provides (one bounded reference level). Absent services are listed by state, not omitted.";
                return "Motion technology object configuration read; no drive or motion command.";
            });

        public HardwareAddressingReply ManageMotionAxis(string softwarePath, string objectPath, string action, string aspect, string name, MotionConnectionTarget targetRequest, Dictionary<string, HardwareScalar> properties, string targetError, string propertiesError, int sensorIndex = 0, bool confirmDelete = false, bool dryRun = true)
            => RunTool("ManageMotionAxis", meta => {
                var category = Logic.MotionCategory(action, aspect);
                bool writing = action != "read" && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var to = ExactTechnology(softwarePath, objectPath, writing);
                meta["objectPath"] = objectPath; meta["action"] = action; meta["aspect"] = aspect; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (category == "read")
                {
                    meta["object"] = HardwareScalarEvidence.Read(to);
                    var states = new Dictionary<string, object?>();
                    foreach (var typeName in Logic.ReadableMotionServices) { OptionalOfficialService(to, typeName, out var state); states[typeName] = state; }
                    meta["serviceStates"] = states; meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                    return "Technology object summary read; GetMotionAxisConfiguration returns service detail.";
                }
                if (category == "masterValue")
                {
                    var (serviceName, property) = Logic.MasterValueAspects[aspect];
                    var association = PlcGroupOperations.Get(RequireOfficialService(to, serviceName), property);
                    var master = ExactTechnology(softwarePath, name, false) as TechnologicalInstanceDB ?? throw new ArgumentException("name must be the exact path of the master technology object.");
                    if (ReferenceEquals(master, to) || Equals(master, to)) throw new ArgumentException("A technology object cannot be its own master value.");
                    var signature = new[] { typeof(TechnologicalInstanceDB) };
                    var method = action == "addMasterValue" ? "Add" : "Remove";
                    if (association.GetType().GetMethod(method, signature) == null || association.GetType().GetMethod("Contains", signature) == null) throw new NotSupportedException("Native association " + method + "/Contains signature unavailable.");
                    TechnologicalInstanceDBAssociation? typedAssociation = association as TechnologicalInstanceDBAssociation;
                    bool present = typedAssociation != null ? typedAssociation.Contains(master) : (bool)PlcGroupOperations.Call(association, "Contains", signature, master);
                    meta["masterObjectPath"] = name; meta["before"] = new Dictionary<string, object?> { ["coupled"] = present, ["members"] = PlcGroupOperations.Items(association).Select(x => (object?)(NativeName(x) ?? "")).ToArray() };
                    if (action == "addMasterValue" && present) throw new InvalidOperationException("Master value is already coupled.");
                    if (action == "removeMasterValue" && !present) throw new InvalidOperationException("Master value is not coupled.");
                    if (!writing) return "Master value coupling preview; no changes.";
                    meta["mayHaveChanged"] = true; MutationStarted = true;
                    if (typedAssociation != null) { if (action == "addMasterValue") typedAssociation.Add(master); else typedAssociation.Remove(master); }
                    else PlcGroupOperations.Call(association, method, signature, master);
                    meta["apiCallSuccess"] = true;
                    bool after = typedAssociation != null ? typedAssociation.Contains(master) : (bool)PlcGroupOperations.Call(association, "Contains", signature, master);
                    if (after != (action == "addMasterValue")) throw new InvalidOperationException("Association call returned but readback differs.");
                    meta["after"] = new Dictionary<string, object?> { ["coupled"] = after };
                    return "Master value coupling changed and verified; no save/compile/download or motion command.";
                }
                if (category == "mapping")
                {
                    var (serviceName, property) = Logic.MappingAspects[aspect];
                    var composition = PlcGroupOperations.Get(RequireOfficialService(to, serviceName), property);
                    Logic.RequireName(name, "alias");
                    var matches = PlcGroupOperations.Items(composition).Where(x => string.Equals(PlcGroupOperations.Get(x, "Alias").ToString(), name, StringComparison.Ordinal)).Take(2).ToList();
                    if (matches.Count > 1) throw new InvalidOperationException("Ambiguous alias: " + name);
                    var existing = matches.SingleOrDefault();
#if PLC_MOTION_V21
                    // Typed composition lookups (TOMappingComposition / DBMemberMappingComposition Find by alias).
                    if (existing == null && composition is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.TOMappingComposition toMappings) existing = toMappings.Find(name);
                    if (existing == null && composition is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.DBMemberMappingComposition dbMappings) existing = dbMappings.Find(name);
#endif
                    if (propertiesError.Length != 0) throw new ArgumentException(propertiesError);
                    var changes = new Dictionary<string, HardwareScalar>(properties);
                    if (changes.ContainsKey("Alias")) throw new ArgumentException("Alias is fixed by name; renaming is not supported.");
                    string? masterPath = null;
                    if (changes.ContainsKey("TechnologicalObject"))
                    {
                        if (aspect != "toMapping") throw new ArgumentException("TechnologicalObject applies to toMapping only.");
                        masterPath = changes["TechnologicalObject"].Text ?? throw new ArgumentException("TechnologicalObject must be an exact object path.");
                        changes.Remove("TechnologicalObject");
                    }
                    var itemType = composition.GetType().GetProperty("Item")?.PropertyType ?? throw new NotSupportedException("Mapping composition item type unavailable.");
                    var prepared = HardwareAddressingPolicy.Prepare(itemType, changes);
                    var masterObject = masterPath == null ? null : ExactTechnology(softwarePath, masterPath, false);
                    meta["alias"] = name;
                    if (action == "createMapping")
                    {
                        if (existing != null) throw new InvalidOperationException("Mapping alias already exists.");
                        if (composition.GetType().GetMethod("Create", new[] { typeof(string) }) == null) throw new NotSupportedException("Native mapping Create(string) unavailable.");
                        if (!writing) return "Mapping creation preview; no changes.";
                        meta["mayHaveChanged"] = true; MutationStarted = true;
#if PLC_MOTION_V21
                        existing = composition switch
                        {
                            global::Siemens.Engineering.SW.TechnologicalObjects.Motion.TOMappingComposition toCreate => toCreate.Create(name),
                            global::Siemens.Engineering.SW.TechnologicalObjects.Motion.DBMemberMappingComposition dbCreate => dbCreate.Create(name),
                            _ => PlcGroupOperations.Call(composition, "Create", new[] { typeof(string) }, name)
                        };
#else
                        existing = PlcGroupOperations.Call(composition, "Create", new[] { typeof(string) }, name);
#endif
                    }
                    else
                    {
                        if (existing == null) throw new InvalidOperationException("Exact mapping alias not found.");
                        meta["before"] = MappingRow(existing);
                        if (action == "deleteMapping")
                        {
                            if (prepared.Count != 0 || masterObject != null) throw new ArgumentException("No properties allowed for deleteMapping.");
                            if (!confirmDelete) throw new ArgumentException("confirmDelete=true is required to delete a mapping.");
                            if (existing.GetType().GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Native mapping Delete unavailable.");
                            if (!writing) return "Mapping deletion preview; no changes.";
                            meta["mayHaveChanged"] = true; MutationStarted = true;
#if PLC_MOTION_V21
                            if (existing is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.TOMapping toDelete) toDelete.Delete();
                            else if (existing is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.DBMemberMapping dbDelete) dbDelete.Delete();
                            else PlcGroupOperations.Call(existing, "Delete", Type.EmptyTypes);
#else
                            PlcGroupOperations.Call(existing, "Delete", Type.EmptyTypes);
#endif
                            meta["apiCallSuccess"] = true;
                            if (PlcGroupOperations.Items(composition).Any(x => string.Equals(PlcGroupOperations.Get(x, "Alias").ToString(), name, StringComparison.Ordinal))) throw new InvalidOperationException("Mapping remains after Delete.");
                            meta["verifiedAbsent"] = true;
                            return "Mapping deleted and verified absent; no save/compile/download.";
                        }
                        if (prepared.Count == 0 && masterObject == null) throw new ArgumentException("updateMapping requires at least one property.");
                        if (!writing) return "Mapping update preview; no changes.";
                    }
                    MutationStarted |= prepared.Count > 0;
                    HardwareScalarEvidence.Apply(existing, prepared, meta);
                    if (masterObject != null)
                    {
#if PLC_MOTION_V21
                        if (existing is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.TOMapping toMapping && masterObject is TechnologicalInstanceDB masterDb)
                        {
                            meta["mayHaveChanged"] = true; MutationStarted = true;
                            toMapping.TechnologicalObject = masterDb;
                            if (!Equals(toMapping.TechnologicalObject, masterDb)) throw new InvalidOperationException("TechnologicalObject readback differs.");
                        }
                        else
#endif
                        {
                            var reference = existing.GetType().GetProperty("TechnologicalObject") ?? throw new NotSupportedException("TechnologicalObject property unavailable.");
                            if (reference.SetMethod?.IsPublic != true) throw new NotSupportedException("TechnologicalObject is not writable.");
                            meta["mayHaveChanged"] = true; MutationStarted = true;
                            reference.SetValue(existing, masterObject);
                            if (!Equals(reference.GetValue(existing), masterObject)) throw new InvalidOperationException("TechnologicalObject readback differs.");
                        }
                    }
                    meta["after"] = MappingRow(existing); meta["apiCallSuccess"] = true;
                    return "Mapping " + (action == "createMapping" ? "created" : "updated") + " and verified; no save/compile/download.";
                }
                if (category == "ident")
                {
                    var provider = RequireOfficialService(to, Logic.IdentProvider);
                    var target = (targetError.Length == 0 ? targetRequest : throw new ArgumentException(targetError));
                    if (target.Mode != "deviceItem") throw new ArgumentException("connectIdent requires a deviceItem target.");
                    var item = ExactDeviceItem(target.DevicePath!, target.ItemPath!);
                    if (provider.GetType().GetMethod("Connect", new[] { typeof(DeviceItem) }) == null) throw new NotSupportedException("Native Ident Connect(DeviceItem) unavailable.");
                    meta["before"] = new Dictionary<string, object?> { ["connectedIdentDevice"] = (provider.GetType().GetProperty("ConnectedIdentDevice")?.GetValue(provider) as DeviceItem)?.Name };
                    meta["deviceItem"] = item.Name;
                    if (!writing) return "Ident device connection preview; no changes.";
                    meta["mayHaveChanged"] = true; MutationStarted = true;
#if PLC_MOTION_V21
                    if (provider is global::Siemens.Engineering.SW.TechnologicalObjects.Ident.IdentTechnologicalObjectProvider identProvider) identProvider.Connect(item);
                    else PlcGroupOperations.Call(provider, "Connect", new[] { typeof(DeviceItem) }, item);
#else
                    PlcGroupOperations.Call(provider, "Connect", new[] { typeof(DeviceItem) }, item);
#endif
                    meta["apiCallSuccess"] = true;
                    var connected = provider.GetType().GetProperty("ConnectedIdentDevice")?.GetValue(provider) as DeviceItem;
                    if (!Equals(connected, item)) throw new InvalidOperationException("Connect returned but ConnectedIdentDevice differs.");
                    meta["after"] = new Dictionary<string, object?> { ["connectedIdentDevice"] = connected!.Name };
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
                        else
                        {
#if PLC_WATCH_READ
                            iface = aspect == "torque" ? (object)axisProvider.TorqueInterface : axisProvider.ActorInterface;
#else
                            if (aspect == "torque") throw new NotSupportedException("TorqueHardwareConnectionInterface requires V15.1.");
                            iface = axisProvider.ActorInterface;
#endif
                        }
                    }
                    else if (service is EncoderHardwareConnectionProvider encoderProvider) iface = encoderProvider.SensorInterface;
                    else if (aspect == "sensor")
                    {
                        var sensors = PlcGroupOperations.Items(PlcGroupOperations.Get(service, property)).ToArray();
                        if (sensorIndex < 0 || sensorIndex >= sensors.Length) throw new ArgumentException("sensorIndex out of range (0.." + (sensors.Length - 1) + ").");
                        iface = sensors[sensorIndex];
                    }
                    else iface = PlcGroupOperations.Get(service, property);
                    meta["before"] = InterfaceRow(iface);
                    var isConnected = iface.GetType().GetProperty("IsConnected");
                    if (action == "disconnect")
                    {
                        if (iface.GetType().GetMethod("Disconnect", Type.EmptyTypes) == null) throw new NotSupportedException("Native Disconnect unavailable.");
                        if (!writing) return "Hardware disconnect preview; no changes.";
                        meta["mayHaveChanged"] = true; MutationStarted = true;
                        if (!DisconnectTyped(iface)) PlcGroupOperations.Call(iface, "Disconnect", Type.EmptyTypes);
                        meta["apiCallSuccess"] = true;
                        meta["after"] = InterfaceRow(iface);
                        if (IsConnectedTyped(iface) ?? (isConnected != null && (bool)isConnected.GetValue(iface)!)) throw new InvalidOperationException("Disconnect returned but IsConnected is still true.");
                        return "Hardware interface disconnected and verified; no save/compile/download or motion command.";
                    }
                    var target = (targetError.Length == 0 ? targetRequest : throw new ArgumentException(targetError));
                    Logic.RequireConnectionMode(aspect, target.Mode);
                    meta["targetMode"] = target.Mode;
                    Type[] signature; object[] args;
                    var optionType = OfficialType(Logic.MotionNs + "ConnectOption") ?? throw new NotSupportedException("ConnectOption enum unavailable.");
                    object Option() => HardwareAddressingPolicy.ConvertValue(new HardwareScalar { Kind = "string", Text = target.ConnectOption },optionType)!;
                    switch (target.Mode)
                    {
                        case "deviceItem": signature = new[] { typeof(DeviceItem) }; args = new object[] { ExactDeviceItem(target.DevicePath!, target.ItemPath!) }; break;
                        case "deviceItems":
                            var first = ExactDeviceItem(target.DevicePath!, target.ItemPath!); var second = ExactDeviceItem(target.DevicePath!, target.SecondItemPath!);
                            signature = target.HasConnectOption ? new[] { typeof(DeviceItem), typeof(DeviceItem), optionType } : new[] { typeof(DeviceItem), typeof(DeviceItem) };
                            args = target.HasConnectOption ? new object[] { first, second, Option() } : new object[] { first, second }; break;
                        case "deviceItemChannel": signature = new[] { typeof(DeviceItem), typeof(int) }; args = new object[] { ExactDeviceItem(target.DevicePath!, target.ItemPath!), target.ChannelIndex }; break;
                        case "channel": signature = new[] { typeof(Channel) }; args = new object[] { ExactChannel(ExactDeviceItem(target.DevicePath!, target.ItemPath!), target.ChannelType, target.ChannelIoType, target.ChannelNumber) }; break;
                        case "dbMember": signature = new[] { typeof(string) }; args = new object[] { target.DbMemberPath }; break;
                        case "plcTag": signature = new[] { typeof(PlcTag) }; args = new object[] { ExactPlcTag(softwarePath, target.PlcTagPath) }; break;
                        case "addresses": signature = new[] { typeof(int), typeof(int), optionType }; args = new object[] { target.InputBitAddress, target.OutputBitAddress, Option() }; break;
                        default: signature = new[] { typeof(int) }; args = new object[] { target.Address }; break;
                    }
                    if (iface.GetType().GetMethod("Connect", signature) == null) throw new NotSupportedException("Native Connect(" + string.Join(",", signature.Select(t => t.Name)) + ") unavailable on " + iface.GetType().FullName + ".");
                    meta["nativeSignature"] = "Connect(" + string.Join(",", signature.Select(t => t.Name)) + ")";
                    if (!writing) return "Hardware connection preview; native overload validated, no changes.";
                    meta["mayHaveChanged"] = true; MutationStarted = true;
                    if (!ConnectTyped(iface, target, softwarePath, (ConnectOption)Option())) PlcGroupOperations.Call(iface, "Connect", signature, args);
                    meta["apiCallSuccess"] = true;
                    meta["after"] = InterfaceRow(iface);
                    if (!(IsConnectedTyped(iface) ?? (isConnected == null || (bool)isConnected.GetValue(iface)!))) throw new InvalidOperationException("Connect returned but IsConnected is false.");
                    meta["mappingVerified"] = isConnected != null;
                    return "Offline hardware connection changed; inspect native readback. No save/compile/download or motion command.";
                }
            });

        public HardwareAddressingReply ExchangeMotionCamData(string softwarePath,string objectPath,string action,string filePath,string format="",string separator="",int pointCount=0,bool dryRun=true)
            =>RunTool("ExchangeMotionCamData",meta=>{
                var method=action switch {"import"=>"LoadCamData","importBinary"=>"LoadCamDataBinary","export"=>"SaveCamData","exportBinary"=>"SaveCamDataBinary","exportPoints"=>"SaveCamDataPointList",_=>throw new ArgumentException("Invalid cam exchange action.")};
                bool write=action.StartsWith("import",StringComparison.Ordinal);
                using var access=write&&!dryRun ? _session.AcquireHmiEditAccess() : null;
                var target=ExactTechnology(softwarePath,objectPath,write&&!dryRun);
                var service=HardwareOfficialService.Require(target,"Siemens.Engineering.SW.TechnologicalObjects.Motion.CamDataSupport","Siemens.Engineering.Step7");
                var call=service.GetType().GetMethods().Single(m=>m.Name==method);
                var file=write ? new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)) : PlcSourceFileOutput.Plan(filePath);
                if(write&&!file.Exists)throw new FileNotFoundException("Cam input not found.");
                var parameters=call.GetParameters();var args=new object[parameters.Length];args[0]=file;
                for(int i=1;i<parameters.Length;i++)args[i]=parameters[i].ParameterType==typeof(int) ? pointCount : HardwareAddressingPolicy.ConvertValue(new HardwareScalar { Kind = "string", Text = parameters[i].ParameterType.Name=="CamDataFormat" ? format : separator },parameters[i].ParameterType)!;
                if(action=="exportPoints"&&pointCount<1)throw new ArgumentException("Positive pointCount required.");
                meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;meta["mayHaveWrittenFiles"]=false;meta["objectPath"]=objectPath;
                if(dryRun)return "Native cam data exchange preview.";
                meta["mayHaveChanged"]=write;MutationStarted|=write;meta["mayHaveWrittenFiles"]=!write;
                var result=PlcGroupOperations.Call(service,method,parameters.Select(p=>p.ParameterType).ToArray(),args);
                HardwareOfficialService.AttachResult(meta,result);if(result is bool ok&&!ok)meta["operationSuccess"]=false;
                if(!write)meta["file"]=PlcSourceFileOutput.Verify(file);
                return "Native cam exchange returned; file/diagnostic evidence supplied. No save/compile/download or motion command.";
            });

        public HardwareAddressingReply ConfigureMotionHardwareConnection(string softwarePath,string objectPath,string interfaceKind,string action,int inputBitAddress=0,int outputBitAddress=0,string connectOption="Default",int sensorIndex=0,bool dryRun=true)
            =>RunTool("ConfigureMotionHardwareConnection",meta=>{
                if (!new[] { "read", "connect", "disconnect" }.Contains(action)) throw new ArgumentException("action must be one of: read/connect/disconnect (case-sensitive).");
                bool write=action!="read"&&!dryRun;using var access=write ? _session.AcquireHmiEditAccess() : null;
                var target=ExactTechnology(softwarePath,objectPath,write);
                // Typed AxisHardwareConnectionProvider (ActorInterface / SensorInterface[i] : AxisEncoderHardwareConnectionInterface, TorqueInterface : TorqueHardwareConnectionInterface).
                global::Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisHardwareConnectionProvider service=(target as global::Siemens.Engineering.IEngineeringServiceProvider)?.GetService<global::Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisHardwareConnectionProvider>()
                    ?? throw new NotSupportedException("AxisHardwareConnectionProvider is not provided by "+target.GetType().Name+" (axis technology objects only).");
                object selected;
                if(interfaceKind=="actor")selected=service.ActorInterface;
#if PLC_WATCH_READ
                else if(interfaceKind=="torque")selected=service.TorqueInterface;
#endif
                else if(interfaceKind=="sensor") {
                    global::Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisEncoderHardwareConnectionInterfaceComposition sensors=service.SensorInterface;
                    if(sensorIndex<0||sensorIndex>=sensors.Count)throw new ArgumentException("Sensor index out of range.");selected=sensors[sensorIndex];
                } else throw new ArgumentException("interfaceKind must be actor/sensor/torque.");
                meta["before"]=InterfaceRow(selected);meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                var method=action=="connect" ? selected.GetType().GetMethods().SingleOrDefault(m=>m.Name=="Connect"&&m.GetParameters().Length==3&&m.GetParameters()[0].ParameterType==typeof(int)&&m.GetParameters()[1].ParameterType==typeof(int)) : selected.GetType().GetMethod("Disconnect",Type.EmptyTypes);
                if(action!="read"&&method==null)throw new NotSupportedException("Native hardware connection signature unavailable.");
                object? mode=null;if(action=="connect") {
                    if(inputBitAddress<0||outputBitAddress<0)throw new ArgumentException("Nonnegative BIT addresses required; no implicit byte conversion.");
                    mode=HardwareAddressingPolicy.ConvertValue(new HardwareScalar { Kind = "string", Text = connectOption },method!.GetParameters()[2].ParameterType);
                }
                if(!write)return "Motion hardware mapping read/preview. Addresses are bits; no drive operation performed.";
                meta["mayHaveChanged"]=true;MutationStarted=true;
                if(action=="connect") {
                    var option=(global::Siemens.Engineering.SW.TechnologicalObjects.Motion.ConnectOption)mode!;
                    if(selected is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisEncoderHardwareConnectionInterface axisInterface)axisInterface.Connect(inputBitAddress,outputBitAddress,option);
#if PLC_WATCH_READ
                    else ((global::Siemens.Engineering.SW.TechnologicalObjects.Motion.TorqueHardwareConnectionInterface)selected).Connect(inputBitAddress,outputBitAddress,option);
#endif
                } else if(!DisconnectTyped(selected))PlcGroupOperations.Call(selected,"Disconnect",Type.EmptyTypes);
                meta["after"]=InterfaceRow(selected);meta["mappingVerified"]=IsConnectedTyped(selected)==(action=="connect");
                return "Offline motion hardware mapping changed; inspect native readback. No save/compile/download or motion command.";
            });
        private HardwareAddressingReply RunTool(string tool, Func<Dictionary<string, object?>, string> action)
            => _session.RunHmiStepTool(tool, meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                try { return action(meta); }
                catch (Exception ex) {
                    var cause = ex.GetBaseException();
                    meta["v4FailureCode"] = cause is NotSupportedException ? "UNSUPPORTED_CAPABILITY"
                        : cause is ArgumentException or TiaMcp.Adapters.Contracts.AdapterPreconditionException ? "INVALID_ARGUMENT"
                        : cause is FileNotFoundException || cause is DirectoryNotFoundException ? "NOT_FOUND"
                        : cause is PlcSoftwareException portal ? portal.Code == "InvalidState" ? "PRECONDITION_FAILED" : portal.Code
                        : cause is InvalidOperationException ? "PRECONDITION_FAILED" : "NATIVE_OPERATION_FAILED";
                    throw;
                }
            });
    }
}
