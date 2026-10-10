namespace TiaMcp.Adapters.Contracts
{
    public sealed class MotionConnectionTarget
    {
        public string Mode { get; set; } = "";
        public string[]? DevicePath { get; set; }
        public string[]? ItemPath { get; set; }
        public string[]? SecondItemPath { get; set; }
        public string DbMemberPath { get; set; } = "";
        public string PlcTagPath { get; set; } = "";
        public string ConnectOption { get; set; } = "Default";
        public bool HasConnectOption { get; set; }
        public int InputBitAddress { get; set; } = -1;
        public int OutputBitAddress { get; set; } = -1;
        public int Address { get; set; } = -1;
        public int ChannelIndex { get; set; } = -1;
        // Connect(Channel) target - the exact channel (ChannelComposition.Find(type, ioType, number)) of the device item.
        public string ChannelType { get; set; } = "";
        public string ChannelIoType { get; set; } = "";
        public int ChannelNumber { get; set; } = -1;
    }
}
