using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        internal static void PlanHardwareAddresses(Dictionary<string, object?> meta, int inputBitAddress, int outputBitAddress)
        {
            if (inputBitAddress < 0 || outputBitAddress < 0) throw new ArgumentException("Nonnegative BIT addresses required; no implicit byte conversion.");
            meta["inputBitAddress"] = inputBitAddress;
            meta["outputBitAddress"] = outputBitAddress;
        }

        internal static void ProbeMotionConnect(Dictionary<string, object?> meta, Func<object> resolveService,
            Func<object, string> identity, Func<Dictionary<string, object?>, string> serialize, Action connect)
        {
            object?[]? Fingerprint()
            {
                var service = resolveService();
                var interfaces = new List<object>();
                if (service is AxisHardwareConnectionProvider axis)
                {
                    interfaces.Add(axis.ActorInterface);
                    interfaces.AddRange(axis.SensorInterface.Cast<object>());
#if PLC_WATCH_READ
                    interfaces.Add(axis.TorqueInterface);
#endif
                }
                else interfaces.Add(service is EncoderHardwareConnectionProvider encoder ? encoder.SensorInterface : service);

                var rows = new List<object?>();
                foreach (var iface in interfaces)
                {
                    if (iface == null) { rows.Add(null); continue; }
                    var row = InterfaceRow(iface, identity);
                    if (row.Keys.Any(key => key.EndsWith("Error", StringComparison.Ordinal))) return null;
                    rows.Add(serialize(row));
                }
                return rows.ToArray();
            }
            NativeRefusalProbe.Run(meta, Fingerprint, connect);
        }

        internal static void VerifyMotionConnect(Dictionary<string, object?> meta, Func<bool?> readConnected)
        {
            bool? connected;
            try { connected = readConnected(); }
            catch (Exception error) when (!HmiReadPolicy.ConnectionUnavailable(error))
            {
                meta["writeOutcomeUnknown"] = true;
                throw new InvalidOperationException("Connect returned but connection state unreadable.", error);
            }
            VerifyMotionConnect(meta, connected);
        }

        internal static void VerifyMotionConnect(Dictionary<string, object?> meta, bool? connected)
        {
            if (connected == true) return;
            meta["writeOutcomeUnknown"] = true;
            throw new InvalidOperationException(connected == null ? "Connect returned but connection state unreadable."
                : "Connect returned but IsConnected is false.");
        }

        private static object? ModuleRef(DeviceItem? item, Func<object, string>? identity = null)
        {
            if (item == null) return null;
            if (identity != null) return identity(item);
            return new Dictionary<string, object?> { ["name"] = item.Name, ["ownerPath"] = HardwareAddressPrimitives.HardwareOwnerPath(item) };
        }
        private static object? ChannelRef(Channel? channel, Func<object, string>? identity) => channel == null ? null : new Dictionary<string, object?> {
            ["number"] = channel.Number, ["type"] = channel.Type.ToString(), ["ioType"] = channel.IoType.ToString(), ["ownerPath"] = identity == null
                ? (object)HardwareAddressPrimitives.HardwareOwnerPath(channel) : identity(channel)
        };
        private static object? TagRef(Siemens.Engineering.SW.Tags.PlcTag? tag, Func<object, string>? identity)
            => tag == null ? null : identity == null ? tag.Name : identity(tag);

        private static Dictionary<string, object?> AxisEncoderInterfaceRow(AxisEncoderHardwareConnectionInterface i, Func<object, string>? identity = null)
        {
            var row = new Dictionary<string, object?> { ["interfaceClass"] = i.GetType().Name };
            Safe(row, "isConnected", () => i.IsConnected); Safe(row, "connectOption", () => i.ConnectOption.ToString());
            Safe(row, "inputAddress", () => i.InputAddress); Safe(row, "outputAddress", () => i.OutputAddress);
            Safe(row, "pathToDbMember", () => i.PathToDBMember); Safe(row, "sensorIndexInActorTelegram", () => i.SensorIndexInActorTelegram);
            Safe(row, "inputModule", () => ModuleRef(i.InputModule, identity)); Safe(row, "outputModule", () => ModuleRef(i.OutputModule, identity));
            Safe(row, "inputOutputModule", () => ModuleRef(i.InputOutputModule, identity));
            Safe(row, "outputTag", () => TagRef(i.OutputTag, identity));
            Safe(row, "channel", () => ChannelRef(i.Channel, identity));
            return row;
        }
#if PLC_WATCH_READ
        private static Dictionary<string, object?> TorqueInterfaceRow(TorqueHardwareConnectionInterface i, Func<object, string>? identity = null)
        {
            var row = new Dictionary<string, object?> { ["interfaceClass"] = i.GetType().Name };
            Safe(row, "isConnected", () => i.IsConnected); Safe(row, "connectOption", () => i.ConnectOption.ToString());
            Safe(row, "inputAddress", () => i.InputAddress); Safe(row, "outputAddress", () => i.OutputAddress);
            Safe(row, "pathToDbMember", () => i.PathToDBMember);
            Safe(row, "inputModule", () => ModuleRef(i.InputModule, identity)); Safe(row, "outputModule", () => ModuleRef(i.OutputModule, identity));
            Safe(row, "inputOutputModule", () => ModuleRef(i.InputOutputModule, identity));
            return row;
        }
#endif
        private static Dictionary<string, object?> MeasuringInputRow(MeasuringInputHardwareConnectionProvider p, Func<object, string>? identity = null)
        {
            var row = new Dictionary<string, object?> { ["interfaceClass"] = p.GetType().Name };
            Safe(row, "isConnected", () => p.IsConnected); Safe(row, "inputAddress", () => p.InputAddress); Safe(row, "channelIndex", () => p.ChannelIndex);
            Safe(row, "inputModule", () => ModuleRef(p.InputModule, identity));
            Safe(row, "channel", () => ChannelRef(p.Channel, identity));
            return row;
        }
        private static Dictionary<string, object?> OutputCamRow(OutputCamHardwareConnectionProvider p, Func<object, string>? identity = null)
        {
            var row = new Dictionary<string, object?> { ["interfaceClass"] = p.GetType().Name };
            Safe(row, "isConnected", () => p.IsConnected); Safe(row, "outputAddress", () => p.OutputAddress);
            Safe(row, "outputTag", () => TagRef(p.OutputTag, identity));
            Safe(row, "channel", () => ChannelRef(p.Channel, identity));
            return row;
        }
        // The typed interface row for any hardware-connection object, or the generic node description for anything else.
        private static Dictionary<string, object?> InterfaceRow(object iface, Func<object, string>? identity = null) => iface switch
        {
            AxisEncoderHardwareConnectionInterface a => AxisEncoderInterfaceRow(a, identity),
#if PLC_WATCH_READ
            TorqueHardwareConnectionInterface t => TorqueInterfaceRow(t, identity),
#endif
            MeasuringInputHardwareConnectionProvider m => MeasuringInputRow(m, identity),
            OutputCamHardwareConnectionProvider o => OutputCamRow(o, identity),
            _ => identity == null ? DescribeNode(iface, 1) : throw new NotSupportedException("Unknown motion connection interface.")
        };
    }
}
