using System.Globalization;
using TiaMcp.Adapters.Contracts.Candidates;
using Xunit;

public sealed class CandidateDigestTests
{
    [Fact]
    public void ObservationEncodingIsCultureIndependentAndInventoryOrderIndependent()
    {
        var identity = new CandidateIdentity(42, DateTimeOffset.Parse("2026-10-03T00:00:00Z"), @"C:\Test.ap19", 1);
        var catalog = new DeviceCatalogEntry { TypeIdentifier = "type", Description = "生产线" };
        var rows = new[] { new DeviceInventoryItem { Id = "b", Name = "B", ParentId = "root" }, new DeviceInventoryItem { Id = "a", Name = "A", ParentId = "root" } };
        string hash = CandidateDigest.DeviceObservation(identity, catalog, rows, "type", "PLC");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(hash, CandidateDigest.DeviceObservation(identity, catalog, rows.Reverse(), "type", "PLC"));
            identity.ProcessStartUtc = identity.ProcessStartUtc.Value.ToOffset(TimeSpan.FromHours(8));
            Assert.Equal(hash, CandidateDigest.DeviceObservation(identity, catalog, rows, "type", "PLC"));
            identity.BindingEpoch++;
            Assert.NotEqual(hash, CandidateDigest.DeviceObservation(identity, catalog, rows, "type", "PLC"));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void LengthPrefixesAndNullTagsKeepDistinctObservationsDistinct()
    {
        Assert.NotEqual(CandidateDigest.Document("a", "bc"), CandidateDigest.Document("ab", "c"));
        Assert.NotEqual(CandidateDigest.Document("é", null), CandidateDigest.Document("é", ""));
        var check = new PlcImportCheck { Inputs = new[] { new PlcImportInput { Path = "a" }, new PlcImportInput { Path = "b" } } };
        string hash = CandidateDigest.ImportObservation(check);
        check.Inputs = check.Inputs.Reverse().ToArray();
        Assert.NotEqual(hash, CandidateDigest.ImportObservation(check));
        check.Inputs = check.Inputs.Reverse().ToArray(); check.Request.InputPath = "different";
        Assert.NotEqual(hash, CandidateDigest.ImportObservation(check));
        check.Request.InputPath = ""; check.GroupIdentity = "other-group";
        Assert.NotEqual(hash, CandidateDigest.ImportObservation(check));
    }
}
