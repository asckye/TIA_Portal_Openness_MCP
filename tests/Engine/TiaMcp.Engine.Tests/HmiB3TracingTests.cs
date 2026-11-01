using System;
using TiaMcp.Adapters.Native.Hmi;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class HmiB3TracingTests
    {
        public sealed class VersionInfo
        {
            public string VersionNumber => "2.3.4";
            public Guid Guid { get; } = Guid.NewGuid();
            public TypeInfo TypeObject { get; } = new();
        }
        public sealed class TypeInfo { public string Name => "Motor"; public Guid Guid { get; } = Guid.NewGuid(); }
        public sealed class Instance : Siemens.Engineering.IEngineeringServiceProvider
        {
            public Siemens.Engineering.Library.Types.LibraryTypeInstanceInfo? Info;
            public T GetService<T>() => (T)(object)Info!;
        }
        [Fact]
        public void Instance_identity_returns_the_connected_version_without_a_type_path_or_default_version_search()
        {
            var version = new VersionInfo();
            var instance = new Instance { Info = new() { LibraryTypeVersion = version } };
            var result = UnifiedNativeRead.InstanceLink(instance, "/instance", out var linked);
            Assert.Equal("ok", result["status"]);
            Assert.Same(version, linked);
            Assert.Equal("Motor", result["typeName"]);
            Assert.Equal("2.3.4", result["version"]);
            Assert.Equal(version.Guid.ToString(), result["versionGuid"]);
        }
        [Fact]
        public void Missing_service_and_missing_version_report_the_specific_unavailable_information()
        {
            var missing = UnifiedNativeRead.InstanceLink(new Instance(), "/instance", out var version);
            Assert.Null(version);
            Assert.Contains("does not expose", missing["reason"]!.ToString());
            var empty = UnifiedNativeRead.InstanceLink(new Instance { Info = new() }, "/instance", out version);
            Assert.Null(version);
            Assert.Contains("LibraryTypeVersion is null", empty["reason"]!.ToString());
        }
    }
}
namespace Siemens.Engineering.Library.Types
{
    public sealed class LibraryTypeInstanceInfo { public object? LibraryTypeVersion { get; set; } }
}
