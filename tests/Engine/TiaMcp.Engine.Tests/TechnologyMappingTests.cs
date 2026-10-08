using System;
using System.Linq;
using MotionLogic = TiaMcpServer.Siemens.MotionProDiagClassicHmiLogic;

namespace TiaMcp.Engine.Tests
{
    // Phase 4 sub-batch 3 (2.7.36) pure logic: the Connect(Channel) target of ManageMotionAxis (channelType / channelIoType /
    // channelNumber on a device item) and the per-aspect overload catalog it extends.
    internal static class TechnologyMappingTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

        internal static void Run(Action<bool, string> check)
        {
            var channel = MotionLogic.ParseConnectionTarget("{\"devicePath\":[\"ET 200SP station_1\"],\"itemPath\":[\"+S1-K3\",\"+S1-K3\"],\"channelType\":\"Digital\",\"channelIoType\":\"Input\",\"channelNumber\":0}");
            check(channel.Mode == "channel" && channel.ChannelType == "Digital" && channel.ChannelIoType == "Input" && channel.ChannelNumber == 0 && channel.ItemPath!.Length == 2, "tomap: channel target parsed (type / ioType / number on the device item)");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{\"devicePath\":[\"D\"],\"itemPath\":[\"A\"],\"channelType\":\"Digital\"}")), "tomap: partial channel identity refused");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{\"channelType\":\"Digital\",\"channelIoType\":\"Input\",\"channelNumber\":0}")), "tomap: channel target without device item refused");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{\"devicePath\":[\"D\"],\"itemPath\":[\"A\"],\"channelIndex\":1,\"channelType\":\"Digital\",\"channelIoType\":\"Input\",\"channelNumber\":0}")), "tomap: channelIndex and channel identity are exclusive");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{\"devicePath\":[\"D\"],\"itemPath\":[\"A\"],\"secondItemPath\":[\"B\"],\"channelType\":\"Digital\",\"channelIoType\":\"Input\",\"channelNumber\":0}")), "tomap: secondItemPath and channel identity are exclusive");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{\"devicePath\":[\"D\"],\"itemPath\":[\"A\"],\"channelType\":\"Digital\",\"channelIoType\":\"Input\",\"channelNumber\":0,\"connectOption\":\"Default\"}")), "tomap: connectOption on a channel target refused");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{\"devicePath\":[\"D\"],\"itemPath\":[\"A\"],\"channelType\":\"Digital\",\"channelIoType\":\"Input\",\"channelNumber\":-1}")), "tomap: negative channelNumber refused");
            foreach (var aspect in new[] { "actor", "sensor", "encoder", "measuringInput", "outputCam" }) MotionLogic.RequireConnectionMode(aspect, "channel");
            check(true, "tomap: channel target allowed for actor / sensor / encoder / measuringInput / outputCam (native Connect(Channel))");
            check(Fails<ArgumentException>(() => MotionLogic.RequireConnectionMode("torque", "channel")), "tomap: torque has no Connect(Channel)");
            check(MotionLogic.ConnectionModes["torque"].SequenceEqual(new[] { "deviceItem", "deviceItems", "dbMember" }) && MotionLogic.ConnectionModes["encoder"].Contains("addresses"), "tomap: other overload catalogs unchanged");
            // the earlier target kinds still resolve to exactly one mode
            check(MotionLogic.ParseConnectionTarget("{\"devicePath\":[\"D\"],\"itemPath\":[\"A\"]}").Mode == "deviceItem" && MotionLogic.ParseConnectionTarget("{\"address\":16}").Mode == "address", "tomap: existing targets unaffected");
        }
    }
}
