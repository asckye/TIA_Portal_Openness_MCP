using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.MotionProDiagClassicHmiLogic;

namespace TiaMcpServer.Siemens
{
    // Phase 4 sub-batch 3 (2.7.36): typed technology-object layer used by ReadTechnologyObjectTree, ReadMotionAxisConfiguration,
    // ManageMotionAxis, ManageTechnologyObject and ConfigureMotionHardwareConnection. TechnologicalInstanceDBGroup /
    // TechnologicalInstanceDB / TechnologicalParameter, AxisHardwareConnectionProvider with its AxisEncoderHardwareConnectionInterface
    // (actor + sensors) and TorqueHardwareConnectionInterface, EncoderHardwareConnectionProvider, MeasuringInput / OutputCam
    // providers, master-value associations, OutputCamMeasuringInputContainer and (V21) InterpreterMappings (TOMapping /
    // DBMemberMapping), SuperimposingAxes and IdentTechnologicalObjectProvider. Everything is the official Siemens.Engineering.Step7 API
    // (XML summaries; the Motion Control chapter of the Openness map is not indexed by the docs-site search).
    public partial class Portal
    {
        // ---- rows -------------------------------------------------------------------------------------------------------------------
        private static JsonNode? ModuleRef(DeviceItem? item) => item == null ? null : new JsonObject { ["name"] = item.Name, ["ownerPath"] = HardwareOwnerPath(item) };
        private static JsonNode? ChannelRef(Channel? channel) => channel == null ? null : new JsonObject { ["number"] = channel.Number, ["type"] = channel.Type.ToString(), ["ioType"] = channel.IoType.ToString(), ["ownerPath"] = HardwareOwnerPath(channel) };
        private static JsonObject AxisEncoderInterfaceRow(AxisEncoderHardwareConnectionInterface i)
        {
            var row = new JsonObject { ["interfaceClass"] = i.GetType().Name };
            Safe(row, "isConnected", () => i.IsConnected); Safe(row, "connectOption", () => i.ConnectOption.ToString());
            Safe(row, "inputAddress", () => i.InputAddress); Safe(row, "outputAddress", () => i.OutputAddress); Safe(row, "pathToDbMember", () => i.PathToDBMember); Safe(row, "sensorIndexInActorTelegram", () => i.SensorIndexInActorTelegram);
            Safe(row, "inputModule", () => ModuleRef(i.InputModule)); Safe(row, "outputModule", () => ModuleRef(i.OutputModule)); Safe(row, "inputOutputModule", () => ModuleRef(i.InputOutputModule));
            Safe(row, "outputTag", () => i.OutputTag?.Name); Safe(row, "channel", () => ChannelRef(i.Channel));
            return row;
        }
        private static JsonObject TorqueInterfaceRow(TorqueHardwareConnectionInterface i)
        {
            var row = new JsonObject { ["interfaceClass"] = i.GetType().Name };
            Safe(row, "isConnected", () => i.IsConnected); Safe(row, "connectOption", () => i.ConnectOption.ToString());
            Safe(row, "inputAddress", () => i.InputAddress); Safe(row, "outputAddress", () => i.OutputAddress); Safe(row, "pathToDbMember", () => i.PathToDBMember);
            Safe(row, "inputModule", () => ModuleRef(i.InputModule)); Safe(row, "outputModule", () => ModuleRef(i.OutputModule)); Safe(row, "inputOutputModule", () => ModuleRef(i.InputOutputModule));
            return row;
        }
        private static JsonObject MeasuringInputRow(MeasuringInputHardwareConnectionProvider p)
        {
            var row = new JsonObject { ["interfaceClass"] = p.GetType().Name };
            Safe(row, "isConnected", () => p.IsConnected); Safe(row, "inputAddress", () => p.InputAddress); Safe(row, "channelIndex", () => p.ChannelIndex);
            Safe(row, "inputModule", () => ModuleRef(p.InputModule)); Safe(row, "channel", () => ChannelRef(p.Channel));
            return row;
        }
        private static JsonObject OutputCamRow(OutputCamHardwareConnectionProvider p)
        {
            var row = new JsonObject { ["interfaceClass"] = p.GetType().Name };
            Safe(row, "isConnected", () => p.IsConnected); Safe(row, "outputAddress", () => p.OutputAddress); Safe(row, "outputTag", () => p.OutputTag?.Name); Safe(row, "channel", () => ChannelRef(p.Channel));
            return row;
        }
        private static JsonArray AssociationNames(TechnologicalInstanceDBAssociation? association)
            => association == null ? new JsonArray() : new JsonArray(EngineeringGroupOperations.Items(association).Cast<TechnologicalInstanceDB>().Select(x => (JsonNode)x.Name).ToArray());
        private static JsonObject ParameterRow(TechnologicalParameter parameter)
        {
            var row = new JsonObject { ["name"] = parameter.Name };
            Safe(row, "value", () => EngineeringScalarProperties.Json(parameter.Value)); Safe(row, "valueType", () => parameter.Value?.GetType().Name);
            return row;
        }
        private static JsonObject TechnologyObjectRow(TechnologicalInstanceDB db)
        {
            var row = new JsonObject { ["name"] = db.Name, ["blockClass"] = db.GetType().Name };
            Safe(row, "number", () => db.Number); Safe(row, "ofSystemLibElement", () => db.OfSystemLibElement); Safe(row, "ofSystemLibVersion", () => db.OfSystemLibVersion?.ToString());
            Safe(row, "isConsistent", () => db.IsConsistent); Safe(row, "parameterCount", () => { TechnologicalParameterComposition parameters = db.Parameters; return parameters.Count; });
            return row;
        }
        // The typed interface row for any hardware-connection object, or the generic node description for anything else.
        private static JsonObject InterfaceRow(object iface) => iface switch
        {
            AxisEncoderHardwareConnectionInterface a => AxisEncoderInterfaceRow(a),
            TorqueHardwareConnectionInterface t => TorqueInterfaceRow(t),
            MeasuringInputHardwareConnectionProvider m => MeasuringInputRow(m),
            OutputCamHardwareConnectionProvider o => OutputCamRow(o),
            _ => DescribeNode(iface, 1)
        };
        private static bool? IsConnectedTyped(object iface) => iface switch
        {
            AxisEncoderHardwareConnectionInterface a => a.IsConnected,
            TorqueHardwareConnectionInterface t => t.IsConnected,
            MeasuringInputHardwareConnectionProvider m => m.IsConnected,
            OutputCamHardwareConnectionProvider o => o.IsConnected,
            _ => null
        };
#if !TIA_V20
        private static JsonObject ToMappingRow(TOMapping mapping)
        {
            var row = new JsonObject { ["alias"] = mapping.Alias, ["mappingClass"] = mapping.GetType().Name };
            Safe(row, "technologicalObject", () => mapping.TechnologicalObject?.Name);
            return row;
        }
        private static JsonObject DbMemberMappingRow(DBMemberMapping mapping)
        {
            var row = new JsonObject { ["alias"] = mapping.Alias, ["mappingClass"] = mapping.GetType().Name };
            Safe(row, "memberPath", () => mapping.MemberPath); Safe(row, "dataType", () => mapping.DataType); Safe(row, "readonly", () => mapping.Readonly); Safe(row, "startIndex", () => mapping.StartIndex);
            return row;
        }
#endif
        private static JsonObject MappingRow(object mapping) => mapping switch
        {
#if !TIA_V20
            TOMapping t => ToMappingRow(t),
            DBMemberMapping d => DbMemberMappingRow(d),
#endif
            _ => DescribeNode(mapping, 1)
        };

        // ---- typed view of every Motion / Ident service of one technology object ----------------------------------------------------
        private static JsonObject TypedMotionView(TechnologicalInstanceDB to)
        {
            var view = new JsonObject();
            AxisHardwareConnectionProvider? axis = to.GetService<AxisHardwareConnectionProvider>();
            if (axis != null)
            {
                Safe(view, "actorInterface", () => AxisEncoderInterfaceRow(axis.ActorInterface));
                Safe(view, "sensorInterfaces", () => { AxisEncoderHardwareConnectionInterfaceComposition sensors = axis.SensorInterface; return new JsonArray(EngineeringGroupOperations.Items(sensors).Cast<AxisEncoderHardwareConnectionInterface>().Select(s => (JsonNode)AxisEncoderInterfaceRow(s)).ToArray()); });
                Safe(view, "torqueInterface", () => TorqueInterfaceRow(axis.TorqueInterface));
            }
            EncoderHardwareConnectionProvider? encoder = to.GetService<EncoderHardwareConnectionProvider>();
            if (encoder != null) Safe(view, "encoderSensorInterface", () => AxisEncoderInterfaceRow(encoder.SensorInterface));
            MeasuringInputHardwareConnectionProvider? measuring = to.GetService<MeasuringInputHardwareConnectionProvider>();
            if (measuring != null) view["measuringInput"] = MeasuringInputRow(measuring);
            OutputCamHardwareConnectionProvider? outputCam = to.GetService<OutputCamHardwareConnectionProvider>();
            if (outputCam != null) view["outputCam"] = OutputCamRow(outputCam);
            SynchronousAxisMasterValues? synchronous = to.GetService<SynchronousAxisMasterValues>();
            if (synchronous != null) Safe(view, "synchronousMasterValues", () => new JsonObject { ["setPointCoupling"] = AssociationNames(synchronous.SetPointCoupling), ["actualValueCoupling"] = AssociationNames(synchronous.ActualValueCoupling), ["delayedCoupling"] = AssociationNames(synchronous.DelayedCoupling) });
            ConveyorTrackingLeadingValues? conveyor = to.GetService<ConveyorTrackingLeadingValues>();
            if (conveyor != null) Safe(view, "conveyorLeadingValues", () => new JsonObject { ["setPointCoupling"] = AssociationNames(conveyor.SetPointCoupling), ["actualValueCoupling"] = AssociationNames(conveyor.ActualValueCoupling), ["delayedCoupling"] = AssociationNames(conveyor.DelayedCoupling) });
            OutputCamMeasuringInputContainer? container = to.GetService<OutputCamMeasuringInputContainer>();
            if (container != null) Safe(view, "outputCamMeasuringInputContainer", () => new JsonObject { ["outputCams"] = Names(container.OutputCams), ["measuringInputs"] = Names(container.MeasuringInputs) });
            view["camDataSupport"] = to.GetService<CamDataSupport>() != null; view["interpreterProgramSupport"] = to.GetService<InterpreterProgramSupport>() != null;
#if TIA_V20
            view["v20Note"] = "InterpreterMappings (TOMapping / DBMemberMapping), SuperimposingAxes and IdentTechnologicalObjectProvider exist in the V21 PublicAPI only.";
#else
            SuperimposingAxes? superimposing = to.GetService<SuperimposingAxes>();
            if (superimposing != null) Safe(view, "superimposingSetPointCoupling", () => AssociationNames(superimposing.SetPointCoupling));
            InterpreterMappings? interpreter = to.GetService<InterpreterMappings>();
            if (interpreter != null)
            {
                Safe(view, "toMappings", () => { TOMappingComposition mappings = interpreter.TechnologicalObjectMapping; return new JsonArray(EngineeringGroupOperations.Items(mappings).Cast<TOMapping>().Select(m => (JsonNode)ToMappingRow(m)).ToArray()); });
                Safe(view, "dbMemberMappings", () => { DBMemberMappingComposition mappings = interpreter.DBMemberMapping; return new JsonArray(EngineeringGroupOperations.Items(mappings).Cast<DBMemberMapping>().Select(m => (JsonNode)DbMemberMappingRow(m)).ToArray()); });
            }
            global::Siemens.Engineering.SW.TechnologicalObjects.Ident.IdentTechnologicalObjectProvider? ident = to.GetService<global::Siemens.Engineering.SW.TechnologicalObjects.Ident.IdentTechnologicalObjectProvider>();
            if (ident != null) Safe(view, "connectedIdentDevice", () => ModuleRef(ident.ConnectedIdentDevice));
#endif
            return view;
        }

        // ---- typed Connect / Disconnect (reflective fallback stays for overloads outside the typed set, e.g. V20 Telegram) ------------------
        private bool ConnectTyped(object iface, Logic.ConnectionTarget target, string softwarePath, ConnectOption option)
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
                case TorqueHardwareConnectionInterface t:
                    switch (target.Mode)
                    {
                        case "deviceItem": t.Connect(First()); return true;
                        case "dbMember": t.Connect(target.DbMemberPath); return true;
                        case "deviceItems": if (target.HasConnectOption) t.Connect(First(), Second(), option); else t.Connect(First(), Second()); return true;
                        case "addresses": t.Connect(target.InputBitAddress, target.OutputBitAddress, option); return true;
                        default: return false;
                    }
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
                case TorqueHardwareConnectionInterface t: t.Disconnect(); return true;
                case MeasuringInputHardwareConnectionProvider m: m.Disconnect(); return true;
                case OutputCamHardwareConnectionProvider o: o.Disconnect(); return true;
                default: return false;
            }
        }

        // ---- ReadTechnologyObjectTree ---------------------------------------------------------------------------------------------------
        private static JsonObject TechnologyGroupRow(TechnologicalInstanceDBGroup group, bool includeParameters, int depth, int maxDepth)
        {
            TechnologicalInstanceDBComposition objects = group.TechnologicalObjects; TechnologicalInstanceDBUserGroupComposition groups = group.Groups;
            var row = new JsonObject { ["name"] = group.Name, ["groupClass"] = group.GetType().Name, ["objectCount"] = objects.Count, ["groupCount"] = groups.Count };
            row["technologicalObjects"] = new JsonArray(EngineeringGroupOperations.Items(objects).Cast<TechnologicalInstanceDB>().Take(200).Select(db =>
            {
                var o = TechnologyObjectRow(db);
                if (includeParameters) Safe(o, "parameters", () => { TechnologicalParameterComposition parameters = db.Parameters; return new JsonArray(EngineeringGroupOperations.Items(parameters).Cast<TechnologicalParameter>().Take(500).Select(p => (JsonNode)ParameterRow(p)).ToArray()); });
                return (JsonNode)o;
            }).ToArray());
            if (depth < maxDepth) row["groups"] = new JsonArray(EngineeringGroupOperations.Items(groups).Cast<TechnologicalInstanceDBUserGroup>().Select(g => (JsonNode)TechnologyGroupRow(g, includeParameters, depth + 1, maxDepth)).ToArray());
            else row["groupsTruncated"] = groups.Count > 0;
            return row;
        }
        public ResponseMessage ReadTechnologyObjectTree(string softwarePath, string groupPath = "", bool includeParameters = false, bool includeMotionView = false, int maxDepth = 4)
            => RunHmiStepTool("ReadTechnologyObjectTree", meta => {
                if (maxDepth < 1 || maxDepth > 16) throw new ArgumentException("maxDepth 1..16 required.");
                var plc = ExactPlcForEngineering(softwarePath, false);
                TechnologicalInstanceDBGroup root = (TechnologicalInstanceDBGroup)EngineeringGroupOperations.Group(plc.TechnologicalObjectGroup, groupPath);
                meta["groupPath"] = groupPath; meta["tree"] = TechnologyGroupRow(root, includeParameters, 1, maxDepth);
                if (includeMotionView)
                {
                    var views = new JsonObject();
                    foreach (TechnologicalInstanceDB db in EngineeringGroupOperations.Items(root.TechnologicalObjects).Cast<TechnologicalInstanceDB>().Take(50)) Safe(views, db.Name, () => TypedMotionView(db));
                    meta["motionViews"] = views;
                }
                meta["apiCallSuccess"] = true;
                meta["scope"] = "TechnologicalInstanceDBGroup Name / TechnologicalObjects (TechnologicalInstanceDB Name, Number, OfSystemLibElement, OfSystemLibVersion, IsConsistent, parameter count) / Groups recursive to maxDepth; includeParameters adds TechnologicalParameter Name / Value (first 500 per object); includeMotionView adds the typed hardware interfaces, master values and mappings of the root group's objects (first 50). No modification.";
                return "Technology object tree read; no modification.";
            });
    }
}
