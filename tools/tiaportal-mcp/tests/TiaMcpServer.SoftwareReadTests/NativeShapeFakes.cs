// Test doubles only. These prove control flow and C# source integration, never Siemens API compatibility.
namespace Siemens.Engineering
{
    public interface IEngineeringObject { object? GetAttribute(string name); }
}
namespace Siemens.Engineering.SW.Blocks
{
    public class PlcBlock : Siemens.Engineering.IEngineeringObject
    {
        public string Name { get; set; } = "";
        public object ProgrammingLanguage { get; set; } = "SCL";
        public bool FailNumber { get; set; }
        public object? GetAttribute(string name) => FailNumber ? throw new InvalidOperationException("unreadable") : 1;
    }
    public class PlcBlockGroup
    {
        public string Name { get; set; } = "";
        public bool FailEnumeration { get; set; }
        public List<PlcBlock> Items { get; } = new();
        public IEnumerable<PlcBlock> Blocks => FailEnumeration ? throw new IOException("native composition unavailable") : Items;
        public List<PlcBlockGroup> Groups { get; } = new();
    }
}
namespace Siemens.Engineering.SW.Types
{
    public class PlcType { public string Name { get; set; } = ""; }
    public class PlcTypeGroup
    {
        public string Name { get; set; } = "";
        public List<PlcType> Types { get; } = new();
        public List<PlcTypeGroup> Groups { get; } = new();
    }
}
namespace TiaMcp.PlcFoundation
{
    public sealed class PlcAttributeValue
    {
        public string? Name { get; set; }
        public object? Value { get; set; }
        public string? AccessMode { get; set; }
    }
    internal class FakeSoftware
    {
        internal string Name = "PLC";
        internal Siemens.Engineering.SW.Blocks.PlcBlockGroup BlockGroup = new();
        internal Siemens.Engineering.SW.Types.PlcTypeGroup? TypeGroup = new();
        internal PlcAttributeValue[] Attributes = Array.Empty<PlcAttributeValue>();
    }
    public sealed partial class PlcFoundationEngine
    {
        internal FakeSoftware Software = new();
        private PlcReadCandidate<FakeSoftware> ReadSelection(string path) => PlcReadPathPolicy.Select(new[]{new PlcReadCandidate<FakeSoftware> { Value=Software,ExactPath="devices/D/CPU",Device="D",Host="CPU" }},path);
        private static PlcAttributeValue[] ReadAttributes(FakeSoftware value) => value.Attributes;
        private static void Depth(int value) { if(value>128) throw new InvalidOperationException("too deep"); }
    }
}
