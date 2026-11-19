using System.Text.Json;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcp.Adapters.Native.Plc;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class MotionConnectionRefusalTests
    {
        public static IEnumerable<object[]> Cases() =>
            from tool in new[] { "ManageMotionAxis", "ConfigureMotionHardwareConnection" }
            from outcome in new[] { "unchanged", "actor", "sensor", "torque", "module", "unreadable", "lookup", "connection", "returned" }
            select new object[] { tool, outcome };

        [Theory, MemberData(nameof(Cases))]
        public void Native_connect_refusal_checks_the_entire_fresh_axis(string tool, string outcome) => _ = MotionConnect(tool, outcome);

        public static Dictionary<string, object?> MotionConnect(string tool, string outcome)
        {
            var old = new AxisHardwareConnectionProvider();
            var fresh = new AxisHardwareConnectionProvider();
            var oldModule = new DeviceItem { Name = "IO" };
            var otherModule = new DeviceItem { Name = "IO" };
            old.SensorInterface[1].InputModule = oldModule;
            fresh.SensorInterface[1].InputModule = oldModule;
            bool called = false;
            void Refuse()
            {
                called = true;
                if (outcome == "returned") return;
                if (outcome == "actor") fresh.ActorInterface.IsConnected = true;
                if (outcome == "sensor") fresh.SensorInterface[1].OutputAddress = 16;
                if (outcome == "torque") fresh.TorqueInterface.InputAddress = 8;
                if (outcome == "module") fresh.SensorInterface[1].InputModule = otherModule;
                if (outcome == "unreadable") fresh.SensorInterface[1].ReadAddress = () => throw new IOException("Address unavailable.");
                if (outcome == "connection") throw new ObjectDisposedException("session");
                throw new InvalidOperationException("Native connect refused.");
            }
            old.ActorInterface.Connecting = Refuse;
            var reply = MutationEvidenceTests.RunStep("21", tool, meta => {
                PlcOrganisationAdapter.ProbeMotionConnect(meta,
                    () => !called ? old : outcome == "lookup" ? throw new IOException("Axis missing.") : fresh,
                    target => target == otherModule ? "devices/Second/IO" : "devices/First/IO",
                    row => JsonSerializer.Serialize(row), () => {
                        if (tool == "ManageMotionAxis") old.ActorInterface.Connect(oldModule);
                        else old.ActorInterface.Connect(8, 16, ConnectOption.Default);
                    });
                PlcOrganisationAdapter.VerifyMotionConnect(meta, old.ActorInterface.IsConnected);
                return "Connected";
            });
            Assert.True(called);
            Assert.Equal(outcome != "unchanged", reply.OutcomeUnknown);
            Assert.Equal(outcome != "unchanged", reply.MayHaveChanged);
            Assert.Equal(outcome == "unchanged", MutationEvidence.ProvesNoMutation(reply.Meta));
            return reply.Meta;
        }
    }
}

namespace Siemens.Engineering.SW.TechnologicalObjects.Motion
{
    internal enum ConnectOption { Default }
    internal class AxisEncoderHardwareConnectionInterface
    {
        internal Action? Connecting;
        public bool IsConnected { get; set; }
        public ConnectOption ConnectOption { get; set; }
        public int InputAddress { get; set; }
        internal Func<int>? ReadAddress;
        private int output;
        public int OutputAddress { get => ReadAddress?.Invoke() ?? output; set => output = value; }
        public string PathToDBMember { get; set; } = "";
        public int SensorIndexInActorTelegram { get; set; }
        public DeviceItem? InputModule { get; set; }
        public DeviceItem? OutputModule { get; set; }
        public DeviceItem? InputOutputModule { get; set; }
        public PlcTag? OutputTag { get; set; }
        public Channel? Channel { get; set; }
        public void Connect(DeviceItem module) => Connecting!();
        public void Connect(int input, int output, ConnectOption option) => Connecting!();
    }
    internal sealed class TorqueHardwareConnectionInterface
    {
        public bool IsConnected { get; set; }
        public ConnectOption ConnectOption { get; set; }
        public int InputAddress { get; set; }
        public int OutputAddress { get; set; }
        public string PathToDBMember { get; set; } = "";
        public DeviceItem? InputModule { get; set; }
        public DeviceItem? OutputModule { get; set; }
        public DeviceItem? InputOutputModule { get; set; }
    }
    internal sealed class AxisHardwareConnectionProvider
    {
        public AxisEncoderHardwareConnectionInterface ActorInterface { get; } = new();
        public AxisEncoderHardwareConnectionInterface[] SensorInterface { get; } = [new(), new()];
        public TorqueHardwareConnectionInterface TorqueInterface { get; } = new();
    }
    internal sealed class EncoderHardwareConnectionProvider { public AxisEncoderHardwareConnectionInterface SensorInterface { get; } = new(); }
    internal sealed class MeasuringInputHardwareConnectionProvider
    {
        public bool IsConnected { get; set; }
        public int InputAddress { get; set; }
        public int ChannelIndex { get; set; }
        public DeviceItem? InputModule { get; set; }
        public Channel? Channel { get; set; }
    }
    internal sealed class OutputCamHardwareConnectionProvider
    {
        public bool IsConnected { get; set; }
        public int OutputAddress { get; set; }
        public PlcTag? OutputTag { get; set; }
        public Channel? Channel { get; set; }
    }
}

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        private static Dictionary<string, object?> DescribeNode(object value, int depth) => throw new NotSupportedException();
        private static void Safe(Dictionary<string, object?> row, string key, Func<object?> read)
        {
            try { row[key] = read(); }
            catch (Exception error) { row[key] = null; row[key + "Error"] = error.GetBaseException().Message; }
        }
    }
}
