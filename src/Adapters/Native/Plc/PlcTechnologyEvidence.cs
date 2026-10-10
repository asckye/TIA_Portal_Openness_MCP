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
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcp.Adapters.Native.Plc.PlcMotionPolicy;
using WatchNative = TiaMcp.Adapters.WatchTechnologyPrimitives;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        public static Dictionary<string, object?> ReadTechnologyObjectEvidence(TechnologicalInstanceDB db) => TechnologyObjectRow(db);
        public static Dictionary<string, object?> ReadTechnologyParameterEvidence(TechnologicalParameter parameter) => ParameterRow(parameter);
        public static Dictionary<string, object?> ReadTechnologyMotionEvidence(TechnologicalInstanceDB db) => TypedMotionView(db);
        public static Dictionary<string, object?> ReadTechnologyMappingEvidence(object mapping) => MappingRow(mapping);
        public static Dictionary<string, object?> ReadTechnologyInterfaceEvidence(object value) => InterfaceRow(value);
        public static bool DisconnectTechnologyInterface(object value) => DisconnectTyped(value);
        public static bool? TechnologyInterfaceConnected(object value) => IsConnectedTyped(value);
        public bool ConnectTechnologyInterface(object value, MotionConnectionTarget target, string softwarePath, ConnectOption option) => ConnectTyped(value, target, softwarePath, option);

        // ---- rows -------------------------------------------------------------------------------------------------------------------
        private static object? ModuleRef(DeviceItem? item) => item == null ? null : new Dictionary<string, object?> { ["name"] = item.Name, ["ownerPath"] = HardwareAddressPrimitives.HardwareOwnerPath(item) };
        private static object? ChannelRef(Channel? channel) => channel == null ? null : new Dictionary<string, object?> { ["number"] = channel.Number, ["type"] = channel.Type.ToString(), ["ioType"] = channel.IoType.ToString(), ["ownerPath"] = HardwareAddressPrimitives.HardwareOwnerPath(channel) };
        private static Dictionary<string, object?> AxisEncoderInterfaceRow(AxisEncoderHardwareConnectionInterface i)
        {
            var row = new Dictionary<string, object?> { ["interfaceClass"] = i.GetType().Name };
            Safe(row, "isConnected", () => i.IsConnected); Safe(row, "connectOption", () => i.ConnectOption.ToString());
            Safe(row, "inputAddress", () => i.InputAddress); Safe(row, "outputAddress", () => i.OutputAddress); Safe(row, "pathToDbMember", () => i.PathToDBMember); Safe(row, "sensorIndexInActorTelegram", () => i.SensorIndexInActorTelegram);
            Safe(row, "inputModule", () => ModuleRef(i.InputModule)); Safe(row, "outputModule", () => ModuleRef(i.OutputModule)); Safe(row, "inputOutputModule", () => ModuleRef(i.InputOutputModule));
            Safe(row, "outputTag", () => i.OutputTag?.Name); Safe(row, "channel", () => ChannelRef(i.Channel));
            return row;
        }
#if PLC_WATCH_READ
        private static Dictionary<string, object?> TorqueInterfaceRow(TorqueHardwareConnectionInterface i)
        {
            var row = new Dictionary<string, object?> { ["interfaceClass"] = i.GetType().Name };
            Safe(row, "isConnected", () => i.IsConnected); Safe(row, "connectOption", () => i.ConnectOption.ToString());
            Safe(row, "inputAddress", () => i.InputAddress); Safe(row, "outputAddress", () => i.OutputAddress); Safe(row, "pathToDbMember", () => i.PathToDBMember);
            Safe(row, "inputModule", () => ModuleRef(i.InputModule)); Safe(row, "outputModule", () => ModuleRef(i.OutputModule)); Safe(row, "inputOutputModule", () => ModuleRef(i.InputOutputModule));
            return row;
        }
#endif
        private static Dictionary<string, object?> MeasuringInputRow(MeasuringInputHardwareConnectionProvider p)
        {
            var row = new Dictionary<string, object?> { ["interfaceClass"] = p.GetType().Name };
            Safe(row, "isConnected", () => p.IsConnected); Safe(row, "inputAddress", () => p.InputAddress); Safe(row, "channelIndex", () => p.ChannelIndex);
            Safe(row, "inputModule", () => ModuleRef(p.InputModule)); Safe(row, "channel", () => ChannelRef(p.Channel));
            return row;
        }
        private static Dictionary<string, object?> OutputCamRow(OutputCamHardwareConnectionProvider p)
        {
            var row = new Dictionary<string, object?> { ["interfaceClass"] = p.GetType().Name };
            Safe(row, "isConnected", () => p.IsConnected); Safe(row, "outputAddress", () => p.OutputAddress); Safe(row, "outputTag", () => p.OutputTag?.Name); Safe(row, "channel", () => ChannelRef(p.Channel));
            return row;
        }
        private static object?[] AssociationNames(TechnologicalInstanceDBAssociation? association)
            => association == null ? Array.Empty<object?>() : PlcGroupOperations.Items(association).Cast<TechnologicalInstanceDB>().Select(x => (object?)x.Name).ToArray();
        private static Dictionary<string, object?> ParameterRow(TechnologicalParameter parameter)
        {
            var row = new Dictionary<string, object?> { ["name"] = parameter.Name };
            Safe(row, "value", () => HardwareAddressingPolicy.ScalarValue(parameter.Value)); Safe(row, "valueType", () => parameter.Value?.GetType().Name);
            return row;
        }
        private static Dictionary<string, object?> TechnologyObjectRow(TechnologicalInstanceDB db)
        {
            var row = new Dictionary<string, object?> { ["name"] = db.Name, ["blockClass"] = db.GetType().Name };
            Safe(row, "number", () => db.Number); Safe(row, "ofSystemLibElement", () => db.OfSystemLibElement); Safe(row, "ofSystemLibVersion", () => db.OfSystemLibVersion?.ToString());
            Safe(row, "isConsistent", () => db.IsConsistent); Safe(row, "parameterCount", () => { TechnologicalParameterComposition parameters = db.Parameters; return parameters.Count; });
            return row;
        }
        // The typed interface row for any hardware-connection object, or the generic node description for anything else.
        private static Dictionary<string, object?> InterfaceRow(object iface) => iface switch
        {
            AxisEncoderHardwareConnectionInterface a => AxisEncoderInterfaceRow(a),
#if PLC_WATCH_READ
            TorqueHardwareConnectionInterface t => TorqueInterfaceRow(t),
#endif
            MeasuringInputHardwareConnectionProvider m => MeasuringInputRow(m),
            OutputCamHardwareConnectionProvider o => OutputCamRow(o),
            _ => DescribeNode(iface, 1)
        };
        private static bool? IsConnectedTyped(object iface) => iface switch
        {
            AxisEncoderHardwareConnectionInterface a => a.IsConnected,
#if PLC_WATCH_READ
            TorqueHardwareConnectionInterface t => t.IsConnected,
#endif
            MeasuringInputHardwareConnectionProvider m => m.IsConnected,
            OutputCamHardwareConnectionProvider o => o.IsConnected,
            _ => null
        };
#if PLC_MOTION_V21
        private static Dictionary<string, object?> ToMappingRow(TOMapping mapping)
        {
            var row = new Dictionary<string, object?> { ["alias"] = mapping.Alias, ["mappingClass"] = mapping.GetType().Name };
            Safe(row, "technologicalObject", () => mapping.TechnologicalObject?.Name);
            return row;
        }
        private static Dictionary<string, object?> DbMemberMappingRow(DBMemberMapping mapping)
        {
            var row = new Dictionary<string, object?> { ["alias"] = mapping.Alias, ["mappingClass"] = mapping.GetType().Name };
            Safe(row, "memberPath", () => mapping.MemberPath); Safe(row, "dataType", () => mapping.DataType); Safe(row, "readonly", () => mapping.Readonly); Safe(row, "startIndex", () => mapping.StartIndex);
            return row;
        }
#endif
        private static Dictionary<string, object?> MappingRow(object mapping) => mapping switch
        {
#if PLC_MOTION_V21
            TOMapping t => ToMappingRow(t),
            DBMemberMapping d => DbMemberMappingRow(d),
#endif
            _ => DescribeNode(mapping, 1)
        };

        // ---- typed view of every Motion / Ident service of one technology object ----------------------------------------------------
        private static Dictionary<string, object?> TypedMotionView(TechnologicalInstanceDB to)
        {
            var view = new Dictionary<string, object?>();
            AxisHardwareConnectionProvider? axis = to.GetService<AxisHardwareConnectionProvider>();
            if (axis != null)
            {
                Safe(view, "actorInterface", () => AxisEncoderInterfaceRow(axis.ActorInterface));
                Safe(view, "sensorInterfaces", () => { AxisEncoderHardwareConnectionInterfaceComposition sensors = axis.SensorInterface; return PlcGroupOperations.Items(sensors).Cast<AxisEncoderHardwareConnectionInterface>().Select(s => (object?)AxisEncoderInterfaceRow(s)).ToArray(); });
#if PLC_WATCH_READ
                Safe(view, "torqueInterface", () => TorqueInterfaceRow(axis.TorqueInterface));
#endif
            }
            EncoderHardwareConnectionProvider? encoder = to.GetService<EncoderHardwareConnectionProvider>();
            if (encoder != null) Safe(view, "encoderSensorInterface", () => AxisEncoderInterfaceRow(encoder.SensorInterface));
            MeasuringInputHardwareConnectionProvider? measuring = to.GetService<MeasuringInputHardwareConnectionProvider>();
            if (measuring != null) view["measuringInput"] = MeasuringInputRow(measuring);
            OutputCamHardwareConnectionProvider? outputCam = to.GetService<OutputCamHardwareConnectionProvider>();
            if (outputCam != null) view["outputCam"] = OutputCamRow(outputCam);
            SynchronousAxisMasterValues? synchronous = to.GetService<SynchronousAxisMasterValues>();
            if (synchronous != null) Safe(view, "synchronousMasterValues", () => new Dictionary<string, object?> { ["setPointCoupling"] = AssociationNames(synchronous.SetPointCoupling), ["actualValueCoupling"] = AssociationNames(synchronous.ActualValueCoupling),
#if PLC_MOTION_MODERN
                ["delayedCoupling"] = AssociationNames(synchronous.DelayedCoupling)
#else
                ["delayedCoupling"] = null, ["delayedCouplingNote"] = "DelayedCoupling requires V16."
#endif
            });
#if PLC_MOTION_MODERN
            ConveyorTrackingLeadingValues? conveyor = to.GetService<ConveyorTrackingLeadingValues>();
            if (conveyor != null) Safe(view, "conveyorLeadingValues", () => new Dictionary<string, object?> { ["setPointCoupling"] = AssociationNames(conveyor.SetPointCoupling), ["actualValueCoupling"] = AssociationNames(conveyor.ActualValueCoupling), ["delayedCoupling"] = AssociationNames(conveyor.DelayedCoupling) });
#endif
            OutputCamMeasuringInputContainer? container = to.GetService<OutputCamMeasuringInputContainer>();
            if (container != null) Safe(view, "outputCamMeasuringInputContainer", () => new Dictionary<string, object?> { ["outputCams"] = Names(container.OutputCams), ["measuringInputs"] = Names(container.MeasuringInputs) });
#if PLC_WATCH_READ
            view["camDataSupport"] = to.GetService<CamDataSupport>() != null;
#else
            view["camDataSupport"] = false;
#endif
#if PLC_DOCUMENT_EXPORT
            view["interpreterProgramSupport"] = to.GetService<InterpreterProgramSupport>() != null;
#else
            view["interpreterProgramSupport"] = OptionalOfficialService(to, Logic.MotionNs + "InterpreterProgramSupport", out _) != null;
#endif
#if !PLC_MOTION_V21
            view["v20Note"] = "InterpreterMappings (TOMapping / DBMemberMapping), SuperimposingAxes and IdentTechnologicalObjectProvider exist in the V21 PublicAPI only.";
#else
            SuperimposingAxes? superimposing = to.GetService<SuperimposingAxes>();
            if (superimposing != null) Safe(view, "superimposingSetPointCoupling", () => AssociationNames(superimposing.SetPointCoupling));
            InterpreterMappings? interpreter = to.GetService<InterpreterMappings>();
            if (interpreter != null)
            {
                Safe(view, "toMappings", () => { TOMappingComposition mappings = interpreter.TechnologicalObjectMapping; return PlcGroupOperations.Items(mappings).Cast<TOMapping>().Select(m => (object?)ToMappingRow(m)).ToArray(); });
                Safe(view, "dbMemberMappings", () => { DBMemberMappingComposition mappings = interpreter.DBMemberMapping; return PlcGroupOperations.Items(mappings).Cast<DBMemberMapping>().Select(m => (object?)DbMemberMappingRow(m)).ToArray(); });
            }
            global::Siemens.Engineering.SW.TechnologicalObjects.Ident.IdentTechnologicalObjectProvider? ident = to.GetService<global::Siemens.Engineering.SW.TechnologicalObjects.Ident.IdentTechnologicalObjectProvider>();
            if (ident != null) Safe(view, "connectedIdentDevice", () => ModuleRef(ident.ConnectedIdentDevice));
#endif
            return view;
        }

        // ---- typed Connect / Disconnect (reflective fallback stays for overloads outside the typed set, e.g. V20 Telegram) ------------------
        private bool ConnectTyped(object iface, MotionConnectionTarget target, string softwarePath, ConnectOption option)
        {
            DeviceItem First() => ExactDeviceItem(target.DevicePath!, target.ItemPath!);
            DeviceItem Second() => ExactDeviceItem(target.DevicePath!, target.SecondItemPath!);
            Channel ChannelOf() => ExactChannel(First(), target.ChannelType, target.ChannelIoType, target.ChannelNumber);
            switch (iface)
            {
                case AxisEncoderHardwareConnectionInterface a:
                    switch (target.Mode)
                    {
                        case "channel": a.Connect(ChannelOf()); return true;
                        case "deviceItem": a.Connect(First()); return true;
                        case "plcTag": a.Connect(ExactPlcTag(softwarePath, target.PlcTagPath)); return true;
                        case "dbMember": a.Connect(target.DbMemberPath); return true;
                        case "deviceItems": if (target.HasConnectOption) a.Connect(First(), Second(), option); else a.Connect(First(), Second()); return true;
                        case "addresses": a.Connect(target.InputBitAddress, target.OutputBitAddress, option); return true;
                        default: return false;
                    }
#if PLC_WATCH_READ
                case TorqueHardwareConnectionInterface t:
                    switch (target.Mode)
                    {
                        case "deviceItem": t.Connect(First()); return true;
                        case "dbMember": t.Connect(target.DbMemberPath); return true;
                        case "deviceItems": if (target.HasConnectOption) t.Connect(First(), Second(), option); else t.Connect(First(), Second()); return true;
                        case "addresses": t.Connect(target.InputBitAddress, target.OutputBitAddress, option); return true;
                        default: return false;
                    }
#endif
                case MeasuringInputHardwareConnectionProvider m:
                    switch (target.Mode)
                    {
                        case "channel": m.Connect(ChannelOf()); return true;
                        case "address": m.Connect(target.Address); return true;
                        case "deviceItemChannel": m.Connect(First(), target.ChannelIndex); return true;
                        default: return false;
                    }
                case OutputCamHardwareConnectionProvider o:
                    switch (target.Mode)
                    {
                        case "channel": o.Connect(ChannelOf()); return true;
                        case "plcTag": o.Connect(ExactPlcTag(softwarePath, target.PlcTagPath)); return true;
                        case "address": o.Connect(target.Address); return true;
                        default: return false;
                    }
                default: return false;
            }
        }
        private static bool DisconnectTyped(object iface)
        {
            switch (iface)
            {
                case AxisEncoderHardwareConnectionInterface a: a.Disconnect(); return true;
#if PLC_WATCH_READ
                case TorqueHardwareConnectionInterface t: t.Disconnect(); return true;
#endif
                case MeasuringInputHardwareConnectionProvider m: m.Disconnect(); return true;
                case OutputCamHardwareConnectionProvider o: o.Disconnect(); return true;
                default: return false;
            }
        }

        // Scalar read plus one bounded level of references/collections; no parent navigation.
        private static Dictionary<string, object?> DescribeNode(object target, int depth)
        {
            var row = HardwareScalarEvidence.Read(target);
            var references = new Dictionary<string, object?>(); var failures = (List<object?>)row["failures"]!;
            foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
            {
                if (p.GetIndexParameters().Length != 0 || p.GetMethod?.IsPublic != true || p.Name == "Parent" || HardwareScalarEvidence.Scalar(p.PropertyType) || p.PropertyType == typeof(object)) continue;
                try
                {
                    var value = p.GetValue(target);
                    if (value == null) { references[p.Name] = null; continue; }
                    if (value is IEnumerable && value is not string)
                    {
                        if (depth > 0) { references[p.Name] = new Dictionary<string, object?> { ["type"] = value.GetType().FullName, ["enumerated"] = false }; continue; }
                        var items = PlcGroupOperations.Items(value).Take(501).ToArray();
                        references[p.Name] = new Dictionary<string, object?> { ["type"] = value.GetType().FullName, ["count"] = Math.Min(items.Length, 500), ["truncated"] = items.Length > 500,
                            ["items"] = items.Take(500).Select(i => (object?)DescribeNode(i, depth + 1)).ToArray() };
                    }
                    else references[p.Name] = depth > 0 ? new Dictionary<string, object?> { ["type"] = value.GetType().FullName, ["name"] = NativeName(value) } : DescribeNode(value, depth + 1);
                }
                catch (Exception ex) { failures.Add(new Dictionary<string, object?> { ["property"] = p.Name, ["error"] = ex.GetBaseException().Message }); if (PortalFailureClassifier.IsPortalProcessLost(ex)) throw; }
            }
            row["references"] = references; row["dataComplete"] = failures.Count == 0;
            return row;
        }

        public PlcTag ExactPlcTag(string softwarePath, string tagPath)
        {
            var plc = _session.ExactPlcForEngineering(softwarePath, false);
            var parts = PlcGroupOperations.Parts(tagPath);
            if (parts.Length < 2) throw new ArgumentException("plcTagPath must be [group/...]/table/tag.");
            var group = PlcGroupOperations.Group(plc.TagTableGroup, string.Join("/", parts.Take(parts.Length - 2)));
            var table = PlcGroupOperations.Find(PlcGroupOperations.Get(group, "TagTables"), parts[parts.Length - 2]) ?? throw new InvalidOperationException("Exact PLC tag table not found.");
            return PlcGroupOperations.Find(PlcGroupOperations.Get(table, "Tags"), parts.Last()) as PlcTag ?? throw new InvalidOperationException("Exact PLC tag not found.");
        }

        private DeviceItem ExactDeviceItem(string[] devicePath, string[] itemPath)
            => _session.ExactEngineeringHardware(devicePath, itemPath) as DeviceItem
               ?? throw new ArgumentException("itemPath must identify a device item, not a device.");

        private static string? NativeName(object value) => value.GetType().GetProperty("Name")?.GetValue(value)?.ToString();
        private object ExactTechnology(string softwarePath, string objectPath, bool writing)
        {
            var plc = _session.ExactPlcForEngineering(softwarePath, writing); var parts = PlcGroupOperations.Parts(objectPath);
            var group = PlcGroupOperations.Group(WatchNative.TechnologyGroup(plc), string.Join("/", parts.Take(parts.Length - 1)));
            return PlcGroupOperations.Find(PlcGroupOperations.Get(group, "TechnologicalObjects"), parts.Last()) ?? throw new InvalidOperationException("Exact technology object not found.");
        }
        public static Channel ExactChannel(DeviceItem item, string channelType, string channelIoType, int channelNumber)
            => item.Channels.Find((ChannelType)Enum.Parse(typeof(ChannelType), channelType), (ChannelIoType)Enum.Parse(typeof(ChannelIoType), channelIoType), channelNumber)
               ?? throw new PlcSoftwareException("NotFound", "Channel not found: " + channelType + "/" + channelIoType + "/" + channelNumber);
    }
}
