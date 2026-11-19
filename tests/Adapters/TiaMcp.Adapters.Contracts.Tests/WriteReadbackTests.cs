using TiaMcp.Adapters.Libraries;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Native.Security;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class WriteReadbackTests
{
    private sealed class Connection
    {
        internal string NativeName = "Connection_1";
        internal Action<string>? OnRename;
        internal Action? OnDelete;
        public string Name => NativeName;
        public string LocalConnectionName
        {
            get => NativeName;
            set { NativeName = value; OnRename?.Invoke(value); }
        }
        public void Delete() => OnDelete?.Invoke();
    }

    [Theory, InlineData("domain"), InlineData("connection")]
    public void Delete_reads_absence_and_count_on_fresh_compositions(string kind)
    {
        var native = new List<Connection>();
        var target = new Connection();
        native.Add(target);
        var stale = native.ToArray();
        target.OnDelete = () => native.Remove(target);
        int reads = 0;
        object Fresh() { reads++; return native.ToArray(); }
        var meta = new Dictionary<string, object?>();
        if (kind == "domain") PlcFoundationEngine.DeleteNetworkDomain(target, Fresh, target.Name, meta);
        else PlcFoundationEngine.DeleteCommunicationConnection(target, Fresh, target.LocalConnectionName, meta);
        Assert.Single(stale);
        Assert.Equal(2, reads);
        Assert.Equal(true, meta["verifiedAbsent"]);
        Assert.Equal(0, meta["countAfter"]);
        Assert.Equal(true, meta["writeReturned"]);
        Assert.False(meta.ContainsKey("readbackFailed"));
    }

    [Theory, InlineData("domain"), InlineData("connection")]
    public void Delete_that_leaves_target_present_is_unknown(string kind)
    {
        var target = new Connection();
        var reply = MutationEvidenceTests.RunStep("21", kind == "domain" ? "ManageNetworkDomain" : "ManageCommunicationConnection",
            meta => kind == "domain" ? PlcFoundationEngine.DeleteNetworkDomain(target, () => new[] { target }, target.Name, meta)
                : PlcFoundationEngine.DeleteCommunicationConnection(target, () => new[] { target }, target.LocalConnectionName, meta));
        Assert.True(reply.OutcomeUnknown);
        Assert.False(reply.Meta.ContainsKey("readbackFailed"));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Created_connection_commits_when_only_the_fresh_count_throws(bool countThrows)
    {
        var native = new List<Connection>();
        var reply = MutationEvidenceTests.RunStep("21", "ManageCommunicationConnection", meta => {
            meta["mayHaveChanged"] = true;
            var created = new Connection();
            created.OnDelete = () => native.Remove(created);
            native.Add(created);
            meta["writeReturned"] = true;
            return PlcFoundationEngine.FinishCommunicationConnection(created,
                () => countThrows ? throw new InvalidOperationException("count unavailable token=fixtureSecret") : native.ToArray(), "Link", 0, meta);
        });
        Assert.Equal("Link", Assert.Single(native).LocalConnectionName);
        Assert.True(reply.MayHaveChanged);
        Assert.False(reply.OutcomeUnknown);
        Assert.Equal(true, reply.Meta["operationSuccess"]);
        Assert.False(reply.Meta.ContainsKey("rollbackOutcome"));
        if (countThrows)
        {
            Assert.Equal(ReadbackStep.Message, reply.Message);
            Assert.Equal("countAfter", reply.Meta["phase"]);
            Assert.DoesNotContain("fixtureSecret", (string)reply.Meta["nativeMessage"]!);
        }
        else Assert.Equal(1, reply.Meta["countAfter"]);
    }

    [Theory, InlineData(true), InlineData(false)]
    public void Rename_that_changes_the_value_then_throws_requires_undo_of_both_writes(bool deleteWorks)
    {
        var native = new List<Connection>();
        var reply = MutationEvidenceTests.RunStep("21", "ManageCommunicationConnection", meta => {
            meta["mayHaveChanged"] = true;
            var created = new Connection { OnRename = value => { if (value == "Link") throw new InvalidOperationException("rename interrupted"); } };
            created.OnDelete = () => { if (deleteWorks) native.Remove(created); };
            native.Add(created);
            meta["writeReturned"] = true;
            return PlcFoundationEngine.FinishCommunicationConnection(created, () => native.ToArray(), "Link", 0, meta);
        });
        Assert.Equal(false, reply.Meta["writeReturned"]);
        Assert.False(reply.Meta.ContainsKey("readbackFailed"));
        Assert.Equal(deleteWorks ? "restored" : "unknown", reply.Meta["rollbackOutcome"]);
        if (deleteWorks) Assert.Empty(native);
        else Assert.Equal("Connection_1", Assert.Single(native).LocalConnectionName);
    }

    [Theory, InlineData("fresh"), InlineData("absent"), InlineData("unreadable"), InlineData("createRefused"), InlineData("ambiguous")]
    public void Custom_role_creation_uses_the_returned_write_and_fresh_role(string result)
    {
        var roles = new List<string>();
        var stale = roles.ToArray();
        var reply = MutationEvidenceTests.RunStep("21", "ManageProjectUserManagement", meta => {
            meta["mayHaveChanged"] = true;
            return ProjectRoleCreation.Run(meta, () => {
                if (result == "createRefused") throw new InvalidOperationException("Create refused.");
                roles.Add("Auditor");
            }, () => result == "absent" ? stale : result == "ambiguous" ? new[] { "Auditor", "Auditor" } : roles.ToArray(),
                role => result == "unreadable" ? throw new InvalidOperationException("Rights unavailable.") : role);
        });
        Assert.Equal(result is "absent" or "ambiguous" or "createRefused", reply.OutcomeUnknown);
        Assert.Equal(result != "createRefused", reply.Meta.ContainsKey("writeReturned"));
        Assert.Equal(result == "unreadable", reply.Meta.ContainsKey("readbackFailed"));
        if (result == "fresh") Assert.Equal("Auditor", reply.Meta["after"]);
    }

    [Fact]
    public void Archive_refreshes_previously_absent_directory_and_has_only_file_effects()
    {
        var archive = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "global-library-" + Guid.NewGuid().ToString("N")));
        var meta = new Dictionary<string, object?> { ["mayHaveChanged"] = false };
        try
        {
            Assert.False(archive.Exists);
            LibraryArchive.Write(meta, archive, () => {
                Directory.CreateDirectory(archive.FullName);
                File.WriteAllText(Path.Combine(archive.FullName, "LibraryArchive"), "library fixture");
            });
            var entry = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(meta["createdEntries"])));
            Assert.Equal("LibraryArchive", entry["name"]);
            Assert.Equal(true, meta["mayHaveWrittenFiles"]);
            Assert.Equal(false, meta["mayHaveChanged"]);
            Assert.Equal(true, meta["writeReturned"]);
        }
        finally { if (Directory.Exists(archive.FullName)) Directory.Delete(archive.FullName, true); }
    }

    [Theory, InlineData("absent"), InlineData("findThrows"), InlineData("exportFailed"), InlineData("exportThrows"), InlineData("verified")]
    public void Document_import_verifies_exact_names_and_the_native_export_result(string result)
    {
        bool verified = false;
        var reply = MutationEvidenceTests.RunStep("21", "ManagePlcBlockDocuments", meta => {
            meta["writeReturned"] = true;
            PlcB5Result.Record(meta, "Success", true);
            bool found = PlcDocumentReadback.VerifyPresence(meta, () => result == "findThrows" ? throw new IOException("lookup failed") : result != "absent");
            verified = found && PlcDocumentReadback.VerifyExport(meta, () => result switch {
                "exportFailed" => "Verification export returned Error. Inconsistent FC_A.", "exportThrows" => throw new IOException("export failed"), _ => null
            });
            return verified ? "Verified" : ReadbackStep.Message;
        });
        Assert.Equal(result == "verified", verified);
        Assert.True(reply.MayHaveChanged);
        Assert.Equal(result == "absent", reply.OutcomeUnknown);
        Assert.Equal(result != "absent", reply.Meta["operationSuccess"]);
        bool warning = result is "findThrows" or "exportFailed" or "exportThrows";
        Assert.Equal(warning, reply.Meta.ContainsKey("readbackFailed"));
        if (warning) Assert.Equal(result == "findThrows" ? "findImportedBlocks" : "verificationExport", reply.Meta["phase"]);
        if (result == "absent")
        {
            Assert.Equal(false, reply.Meta["existsVerified"]);
            Assert.Equal(typeof(InvalidOperationException).FullName, reply.Meta["nativeExceptionType"]);
            Assert.Equal("Import returned but exact target readback failed.", reply.Meta["nativeMessage"]);
        }
        if (result == "findThrows") Assert.False(reply.Meta.ContainsKey("existsVerified"));
    }

    private sealed class OpcInterface(string mode)
    {
        private bool enabled;
        public bool Enabled
        {
            get => mode == "readThrows" ? throw new IOException("Read refused.") : enabled;
            set
            {
                if (mode != "mismatch") enabled = value;
                if (mode == "writeThrows") throw new InvalidOperationException("Setter failed after writing.");
            }
        }
    }

    [Theory, InlineData("success"), InlineData("readThrows"), InlineData("mismatch"), InlineData("writeThrows"), InlineData("unsupported")]
    public void Opc_interface_requires_a_returned_setter_and_actual_Enabled_readback(string mode)
    {
        var meta = new PlcB5Object { ["mayHaveChanged"] = false };
        object target = mode == "unsupported" ? new { Enabled = false } : new OpcInterface(mode);
        PlcB5Reply Set() => PlcB5Scalar.SetEnabled(target, meta, "PLC", "Server1", "ServerInterface", true);
        if (mode is "writeThrows" or "mismatch")
        {
            var failure = Record.Exception(Set);
            Assert.NotNull(failure);
            Assert.Contains(mode == "mismatch" ? "differs" : "after writing", failure.GetBaseException().Message);
            Assert.Equal(mode == "mismatch", meta.ContainsKey("writeReturned"));
            Assert.False(meta.ContainsKey("readbackFailed"));
        }
        else
        {
            var reply = Set();
            Assert.Equal(mode != "unsupported", reply.Ok);
            Assert.Equal(mode != "unsupported", reply.MayHaveChanged);
            Assert.Equal(mode == "readThrows", meta.ContainsKey("readbackFailed"));
            if (mode == "unsupported") Assert.Equal("UNSUPPORTED_CAPABILITY", meta["v4Rejection"]);
            else Assert.Equal(true, meta["operationSuccess"]);
        }
    }

    // Both worker operation names dispatch to this native group manager.
    public static HardwareAddressingReply MissingBlockGroup()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        var software = new Siemens.Engineering.SW.PlcSoftware();
        var adapter = new PlcOrganisationAdapter(new GroupSession(fixture.Engine, software));
        return adapter.ManagePlcUserGroup("PLC", "blocks", "Missing", "deleteEmpty", dryRun: false);
    }

    [Fact]
    public void Missing_group_is_rejected_before_the_delete()
    {
        var reply = MissingBlockGroup();
        Assert.False(reply.MayHaveChanged);
        Assert.False(reply.OutcomeUnknown);
        Assert.Equal("NotFound", reply.Meta["status"]);
    }

    private sealed class EngineeringObjectDisposedException : Exception { }

    [Fact]
    public void Disposed_domain_proxy_after_Delete_does_not_verify_absence()
    {
        int reads = 0;
        var reply = MutationEvidenceTests.RunStep("21", "ManageNetworkDomain", meta =>
            PlcFoundationEngine.DeleteNetworkDomain(new Connection(), () =>
                ++reads == 1 ? throw new EngineeringObjectDisposedException() : Array.Empty<Connection>(), "domain", meta));
        Assert.False(reply.OutcomeUnknown);
        Assert.Equal(true, reply.Meta["operationSuccess"]);
        Assert.Equal(true, reply.Meta["readbackFailed"]);
        Assert.False(reply.Meta.ContainsKey("verifiedAbsent"));
        Assert.Equal(0, reply.Meta["countAfter"]);
    }

    private sealed class CommunicationObjectFaultedException : Exception { }

    [Fact]
    public void Lost_connection_during_readback_keeps_the_write_unknown()
    {
        var reply = MutationEvidenceTests.RunStep("21", "ManageCommunicationConnection", meta => {
            meta["mayHaveChanged"] = true;
            meta["writeReturned"] = true;
            return PlcFoundationEngine.FinishCommunicationConnection(new Connection(), () => throw new CommunicationObjectFaultedException(), "", 0, meta);
        });
        Assert.True(reply.OutcomeUnknown);
        Assert.False(reply.Meta.ContainsKey("readbackFailed"));
    }
}
