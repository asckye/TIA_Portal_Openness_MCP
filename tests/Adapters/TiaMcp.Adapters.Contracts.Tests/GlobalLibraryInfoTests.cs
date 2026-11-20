using Siemens.Engineering.Library;
using TiaMcp.Adapters.Libraries;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class GlobalLibraryInfoTests
    {
        private sealed class KnownLibraries : List<GlobalLibraryInfo>, IList<GlobalLibraryInfo>
        {
            GlobalLibraryInfo IList<GlobalLibraryInfo>.this[int index]
            {
                get => index == 1 ? throw new InvalidOperationException("Library entry unavailable.") : this[index];
                set => this[index] = value;
            }
        }

        [Fact]
        public void Infos_retain_each_slot_and_preserve_readable_fields_after_an_error()
        {
            var infos = new KnownLibraries { new("System"), new("Unavailable"), new("Corporate") { PathUnavailable = true },
                null!, new("User") };
            var meta = new Dictionary<string, object?>();
            GlobalLibraryInfoRows.Read(infos, meta);

            Assert.Equal(5, meta["actualCount"]);
            Assert.Equal(false, meta["dataComplete"]);
            Assert.Equal(true, meta["apiCallSuccess"]);
            Assert.Equal(false, meta["truncated"]);
            var rows = Assert.IsType<List<object?>>(meta["records"]).Cast<Dictionary<string, object?>>().ToArray();
            Assert.Equal("System", rows[0]["name"]);
            Assert.Equal(typeof(GlobalLibraryInfo).FullName, rows[0]["elementType"]);
            Assert.Null(rows[1]["elementType"]);
            Assert.Contains("Library entry unavailable", Errors(1)["entry"]);
            Assert.Equal("Corporate", rows[2]["name"]);
            Assert.Equal(typeof(GlobalLibraryInfo).FullName, rows[2]["elementType"]);
            Assert.Null(rows[2]["path"]);
            Assert.Contains("Library path unavailable", Errors(2)["path"]);
            Assert.Contains("null entry", Errors(3)["entry"]);
            Assert.Equal("User", rows[4]["name"]);
            Assert.Equal(new[] { "infos[1]", "infos[2].path", "infos[3]" }, Assert.IsType<List<string>>(meta["unavailableFields"]));
            Dictionary<string, string> Errors(int index) => Assert.IsType<Dictionary<string, string>>(rows[index]["readErrors"]);
        }
    }
}

namespace Siemens.Engineering.Library
{
    public sealed class GlobalLibraryInfo(string name)
    {
        public string Name => name;
        public bool PathUnavailable { get; set; }
        public FileInfo? Path => PathUnavailable ? throw new InvalidOperationException("Library path unavailable.") : null;
        public string LibraryType => "User";
        public bool IsOpen => false;
    }
}
