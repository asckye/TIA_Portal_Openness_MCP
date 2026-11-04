using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using TiaMcpServer.Siemens;
using Xunit;
using static TiaMcpServer.Siemens.WindowsOpennessGroup;

public sealed class WindowsOpennessGroupTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public void PassiveObservationReportsBothMembershipsWithoutElevationOrReadinessChanges(bool token, bool? direct)
    {
        var platform = new FakePlatform(token, direct);
        Assert.Equal(token, ReadEffectiveMembership(platform));
        Assert.Equal(new[] { "token", "identity", "local:S-1-5-21-42" }, platform.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableDirectoryDiagnosticPreservesTheTokenResult(bool token)
    {
        var platform = new FakePlatform(token, false) { ReadError = new Win32Exception(5) };
        Assert.Equal(token, ReadEffectiveMembership(platform));
        Assert.Equal(3, platform.Calls.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void EffectiveTokenRemainsReadyAndReportsDirectMembership(bool? direct)
    {
        var platform = new FakePlatform(true, direct);
        var result = EnsureMembership(platform);
        Assert.Equal(Outcome.Ready, result.Status);
        Assert.True(result.TokenMember);
        Assert.Equal(direct, result.DirectLocalMember);
        Assert.True(RequireEffectiveMembership(result));
        Assert.Equal(new[] { "identity", "token", "local:S-1-5-21-42" }, platform.Calls);
    }

    [Theory]
    [InlineData(null, nameof(Outcome.MissingGroup), "OPENNESS_GROUP_MISSING")]
    [InlineData(true, nameof(Outcome.PendingLogon), "OPENNESS_GROUP_RELOGON_REQUIRED")]
    public void MissingOrPendingMembershipNeverStartsElevation(bool? direct, string outcome, string code)
    {
        var platform = new FakePlatform(false, direct);
        var result = EnsureMembership(platform);
        Assert.Equal(outcome, result.Status.ToString());
        Assert.False(result.TokenMember);
        Assert.Equal(direct, result.DirectLocalMember);
        Assert.Contains(code, Assert.Throws<InvalidOperationException>(() => RequireEffectiveMembership(result)).Message);
        Assert.DoesNotContain(platform.Calls, call => call.StartsWith("add:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, true, nameof(Outcome.AddedPendingLogon))]
    [InlineData(2, true, nameof(Outcome.AddedPendingLogon))]
    [InlineData(0, false, nameof(Outcome.AddFailed))]
    [InlineData(2, false, nameof(Outcome.AddFailed))]
    [InlineData(2, null, nameof(Outcome.MissingGroup))]
    public void RepairAlwaysRequeriesTheSameSidAndNeverClaimsTokenReadiness(int exitCode, bool? after, string outcome)
    {
        var platform = new FakePlatform(false, false, after) { ExitCode = exitCode };
        var result = EnsureMembership(platform);
        Assert.Equal(outcome, result.Status.ToString());
        Assert.False(result.TokenMember);
        Assert.Equal(after, result.DirectLocalMember);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Throws<InvalidOperationException>(() => RequireEffectiveMembership(result));
        Assert.Equal(new[] { "identity", "token", "local:S-1-5-21-42", "add:DOMAIN\\Current User", "local:S-1-5-21-42" }, platform.Calls);
        if (after == true) Assert.Contains("OPENNESS_GROUP_ADDED_RELOGON_REQUIRED", result.Message);
    }

    [Fact]
    public void DeclinedElevationHasItsOwnResultAndDoesNotPretendMembershipChanged()
    {
        var platform = new FakePlatform(false, false) { AddError = new Win32Exception(1223) };
        var result = EnsureMembership(platform);
        Assert.Equal(Outcome.ElevationDeclined, result.Status);
        Assert.Equal(1223, result.ExitCode);
        Assert.False(result.DirectLocalMember);
        Assert.Contains("OPENNESS_GROUP_ELEVATION_DECLINED", result.Message);
        Assert.Throws<InvalidOperationException>(() => RequireEffectiveMembership(result));
        Assert.Equal(4, platform.Calls.Count);
    }

    [Fact]
    public void UnknownMembershipFailsClosedWithoutRepair()
    {
        var original = new Win32Exception(5);
        var platform = new FakePlatform(false, false) { ReadError = original };
        Assert.Same(original, Assert.Throws<Win32Exception>(() => EnsureMembership(platform)));
        Assert.Equal(3, platform.Calls.Count);
    }

    [Fact]
    public void OtherElevationErrorsArePreserved()
    {
        var original = new Win32Exception(2);
        var platform = new FakePlatform(false, false) { AddError = original };
        Assert.Same(original, Assert.Throws<Win32Exception>(() => EnsureMembership(platform)));
    }

    [Fact]
    public void RepeatedRepairAfterAdditionDoesNotPromptAgain()
    {
        var platform = new FakePlatform(false, false, true, true);
        Assert.Equal(Outcome.AddedPendingLogon, EnsureMembership(platform).Status);
        Assert.Equal(Outcome.PendingLogon, EnsureMembership(platform).Status);
        Assert.Single(platform.Calls.FindAll(call => call.StartsWith("add:", StringComparison.Ordinal)));
    }

    [Fact]
    public void ElevationRunsOnlyTheSystemNetExecutableWithTheFixedLocalGroupAndCurrentAccount()
    {
        var start = AddStartInfo("C:\\Windows\\System32", "DOMAIN\\Current User & name");
        Assert.Equal(Path.Combine("C:\\Windows\\System32", "net.exe"), start.FileName);
        Assert.Equal("localgroup \"Siemens TIA Openness\" \"DOMAIN\\Current User & name\" /add", start.Arguments);
        Assert.Equal("runas", start.Verb);
        Assert.True(start.UseShellExecute);
        Assert.False(start.RedirectStandardOutput);
        Assert.False(start.RedirectStandardError);
        Assert.Equal("C:\\Windows\\System32", start.WorkingDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("DOMAIN\\name\" /delete")]
    [InlineData("DOMAIN\\name\n/add")]
    [InlineData("DOMAIN\\name\r/add")]
    [InlineData("DOMAIN\\name\0/add")]
    public void InvalidAccountArgumentCannotReachElevation(string account)
        => Assert.Throws<ArgumentException>(() => AddStartInfo("C:\\Windows\\System32", account));

    private sealed class FakePlatform : IPlatform
    {
        private readonly bool token;
        private readonly Queue<bool?> membership;
        internal List<string> Calls { get; } = new List<string>();
        internal int ExitCode { get; set; }
        internal Exception? AddError { get; set; }
        internal Exception? ReadError { get; set; }
        internal FakePlatform(bool token, params bool?[] membership)
        {
            this.token = token; this.membership = new Queue<bool?>(membership);
        }
        public CurrentUser ReadCurrentUser() { Calls.Add("identity"); return new CurrentUser("S-1-5-21-42", "DOMAIN\\Current User"); }
        public bool ReadTokenMembership() { Calls.Add("token"); return token; }
        public bool? ReadDirectLocalMembership(string sid)
        {
            Calls.Add("local:" + sid);
            if (ReadError != null) throw ReadError;
            return membership.Dequeue();
        }
        public int AddCurrentUser(string accountName)
        {
            Calls.Add("add:" + accountName);
            if (AddError != null) throw AddError;
            return ExitCode;
        }
    }
}
