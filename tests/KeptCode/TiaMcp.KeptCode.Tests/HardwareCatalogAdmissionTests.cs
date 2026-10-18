using System;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class HardwareCatalogAdmissionTests
    {
        [Theory]
        [InlineData(true, "ConnectPortal", "AttachOpenProject")]
        [InlineData(false, "ListPortalProcessProjects", "ConnectProject")]
        public void Unbound_catalog_refuses_with_host_connect_steps(bool foundation, string discover, string connect)
        {
            var error = Assert.Throws<AdapterPreconditionException>(() => HardwareCatalogAdmission.RequireBound(false, foundation));
            Assert.False(error.IsArgument); Assert.Contains(discover, error.Message); Assert.Contains(connect, error.Message);
            Assert.Equal(HostFailureKind.Precondition, HostFailurePolicy.Classify(error, true, true));
        }

        [Theory]
        [InlineData("6ES7 513-1AM03-0AB0", "V1.7")]
        [InlineData("6ES7513-1AM03-0AB0", "V3.1")]
        [InlineData("6ES7 513-1AL00-0AB0", "V3.1")]
        public void Missing_or_mixed_catalog_rows_refuse_without_any_create(string article, string version)
        {
            var rows = new[] {
                new DeviceCatalogEntry { ArticleNumber="6ES7 513-1AM03-0AB0", Version="V3.1", TypeIdentifier="OrderNumber:6ES7 513-1AM03-0AB0/V3.1" },
                new DeviceCatalogEntry { ArticleNumber="6ES7 513-1AL00-0AB0", Version="V1.7", TypeIdentifier="OrderNumber:6ES7 513-1AL00-0AB0/V1.7" } };
            var error = Assert.Throws<AdapterPreconditionException>(() => HardwareCatalogAdmission.ExactRow(rows, article, version));
            Assert.True(error.IsArgument); Assert.Equal("preferredMlfb/preferredVersion", error.ParamName);
            Assert.Contains("SearchHardwareCatalog", error.Message);
            Assert.Equal(HostFailureKind.Argument, HostFailurePolicy.Classify(error, true, false));
            HardwareCatalogAdmission.ExactRow(rows, rows[0].ArticleNumber, rows[0].Version);
            HardwareCatalogAdmission.ExactRow(rows, rows[0].TypeIdentifier, "");
        }

        [Fact]
        public void Duplicate_name_is_typed_before_native_write_even_without_host_approval()
        {
            var error = Assert.Throws<AdapterPreconditionException>(() => HardwareCatalogAdmission.AvailableName(new[] { "PLC_2" }, "plc_2"));
            Assert.False(error.IsArgument); Assert.Equal("deviceName", error.ParamName);
            Assert.Equal(HostFailureKind.Precondition, HostFailurePolicy.Classify(error, true, false));
            HardwareCatalogAdmission.AvailableName(new[] { "PLC_2" }, "PLC_3");
        }
    }
}
