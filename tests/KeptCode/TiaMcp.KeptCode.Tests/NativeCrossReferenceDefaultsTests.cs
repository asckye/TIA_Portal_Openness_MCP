using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcpServer.Siemens;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class NativeCrossReferenceDefaultsTests
{
    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Release_policy_is_identical_in_host_and_native_adapter(string release)
    {
        foreach (var setting in new string?[] { null, "", "0", "true", "yes", " 1 ", "1" })
        foreach (bool consent in new[] { false, true })
        foreach (bool unified in new[] { false, true })
        {
            var expected = release != "21" || setting == "1" || consent;
            var host = CrossReferenceGuardLogic.PolicyRefusal(setting, unified, release, consent);
            Assert.Equal(host, PlcCrossReferencePolicy.PolicyRefusal(setting, unified, release, consent));
            Assert.Equal(expected, host == null);
            if (!expected)
            {
                Assert.Contains("No native query was made", host);
                Assert.Contains("does not mean there are no references", host);
                Assert.Contains("Save the project", host);
                Assert.Contains("do not automatically retry", host);
            }
        }
    }

    [Fact]
    public void Unknown_release_fails_closed_and_consent_does_not_bypass_consistency_or_reflection()
    {
        Assert.NotNull(CrossReferenceGuardLogic.PolicyRefusal(null, release: "future"));
        foreach (bool? consistent in new bool?[] { false, null })
        {
            var blocks = new[] { (Path: "Group/Block", Consistent: consistent) };
            var refusal = CrossReferenceGuardLogic.Refusal(blocks, "PLC");
            Assert.Equal(refusal, PlcCrossReferencePolicy.Refusal(blocks, "PLC"));
            Assert.Contains("No native query was made", refusal);
        }
        foreach (var service in new[] { "CrossReferenceService", "CrossReference", "Siemens.Engineering.CrossReference.CrossReferenceService" })
        {
            var refusal = CrossReferenceGuardLogic.ReflectionRefusal(service, "GetCrossReferences");
            Assert.NotNull(refusal);
            Assert.Equal(refusal, PlcCrossReferencePolicy.ReflectionRefusal(service, "GetCrossReferences"));
            Assert.Equal(refusal, OpennessReflectionPolicy.ReflectionRefusal(service, "GetCrossReferences"));
        }
    }

    [Fact]
    public async Task Consent_is_session_scoped_and_requires_a_live_owner_with_the_same_start_time()
    {
        string directory = Path.Combine(Path.GetTempPath(), "xref-consent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string allowed = Path.Combine(directory, "allowed.settings"), other = Path.Combine(directory, "other.settings");
        try
        {
            using var owner = Process.GetCurrentProcess();
            string secret = NativeCrossReferenceConsent.CreateSecret();
            File.WriteAllText(allowed, NativeCrossReferenceConsent.Record(secret, true, owner.Id, owner.StartTime.ToUniversalTime().Ticks));
            Assert.True(NativeCrossReferenceConsent.Read(allowed, secret));
            using (NativeCrossReferenceConsent.EnterSession(allowed, secret))
            {
                Assert.True(NativeCrossReferenceConsent.Enabled);
                Assert.True(await Task.Run(() => NativeCrossReferenceConsent.Enabled));
                using (NativeCrossReferenceConsent.EnterSession(other, NativeCrossReferenceConsent.CreateSecret())) Assert.False(NativeCrossReferenceConsent.Enabled);
                Assert.True(NativeCrossReferenceConsent.Enabled);
            }
            Assert.False(NativeCrossReferenceConsent.Read(allowed, NativeCrossReferenceConsent.CreateSecret()));
            File.WriteAllText(allowed, NativeCrossReferenceConsent.Record(secret, false, owner.Id, owner.StartTime.ToUniversalTime().Ticks));
            Assert.False(NativeCrossReferenceConsent.Read(allowed, secret));
            File.WriteAllText(allowed, NativeCrossReferenceConsent.Record(secret, true, owner.Id, 1));
            Assert.False(NativeCrossReferenceConsent.Read(allowed, secret));
            File.WriteAllText(allowed, "enabled=true\n2147483647\n1\n");
            Assert.False(NativeCrossReferenceConsent.Read(allowed, secret));
            File.WriteAllText(allowed, "enabled=true\n");
            Assert.False(NativeCrossReferenceConsent.Read(allowed, secret));
            Assert.False(NativeCrossReferenceConsent.Read(other, secret));
        }
        finally { Directory.Delete(directory, true); }
    }
}
