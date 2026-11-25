using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Native.Plc;
using Xunit;

namespace Siemens.Engineering.SW.Blocks
{
    public sealed class FB { public string Name => "Motor"; }
    public sealed class InstanceDB { public string Name { get; set; } = ""; }
    public sealed class PlcBlockGroup { public PlcBlockComposition Blocks { get; } = new(); }
    public sealed class PlcBlockComposition : List<InstanceDB>
    {
        internal Action<string>? Creating;
        internal bool Unreadable;
        public InstanceDB? Find(string name) => Unreadable ? throw new IOException() : this.SingleOrDefault(db => db.Name == name);
        public InstanceDB CreateInstanceDB(string name, bool auto, int number, string fb)
        {
            Assert.True(auto);
            Assert.Equal(1, number);
            Assert.Equal("Motor", fb);
            Creating!(name);
            throw new InvalidOperationException("CreateInstanceDB refused.");
        }
    }
}

namespace TiaMcp.Engine.Tests
{
    public sealed class InstanceDbRefusalTests
    {
        [Theory]
        [InlineData("unchanged")]
        [InlineData("created")]
        [InlineData("other-name")]
        [InlineData("unreadable")]
        [InlineData("timeout")]
        public void Failed_create_checks_the_destination_composition(string failure)
        {
            var group = new PlcBlockGroup();
            var meta = new Dictionary<string, object?> { ["mayHaveChanged"] = true };
            int calls = 0;
            group.Blocks.Creating = name => {
                calls++;
                if (failure == "created" || failure == "other-name") group.Blocks.Add(new() { Name = failure == "created" ? name : "DB1" });
                if (failure == "unreadable") group.Blocks.Unreadable = true;
                if (failure == "timeout") throw new TimeoutException();
            };
            Assert.NotNull(Record.Exception(() => PlcOrganisationAdapter.CreateInstanceDb(group, "DriveDB", true, 0, new FB(), meta)));
            Assert.Equal(1, calls);
            Assert.Equal(failure != "unchanged", meta["mayHaveChanged"]);
        }
    }
}
