extern alias plcpathfixture;
using System.Runtime.InteropServices;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using Xunit;
using Boundary = plcpathfixture::TiaMcp.Adapters.PlcFoundationEngine;
using Block = plcpathfixture::Siemens.Engineering.SW.Blocks.PlcBlock;
using NativeException = plcpathfixture::Siemens.Engineering.EngineeringException;

namespace TiaMcp.Engine.Tests;

public sealed class VerifiedRefusalTests
{
    // Siemens nativeMessage from V17 apply row 77.
    internal const string Message = "Error when calling method 'Import' of type 'Siemens.Engineering.SW.Blocks.PlcBlockComposition'.; "
        + "Cannot create the 'SW.Blocks.GlobalDB' object with Simatic ML ID '0' at line number 4 at line position 4.; "
        + "Error when calling method 'Create' of type 'Siemens.Engineering.SW.Blocks.PlcBlockComposition'.; "
        + "MCP_RC_DB2.Motor: The Openness import failed: 找不到元素“Ghost”，请检查所用类型的一致性。";

    private sealed class EngineeringUserAbortException() : NativeException("Import aborted") { }
    private sealed class EngineeringObjectDisposedException() : NativeException("Block disposed") { }

    internal static PlcSoftwareReply Run(string scenario, string release = "17")
    {
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "refusal-" + Guid.NewGuid().ToString("N")));
        try
        {
            using var stream = typeof(VerifiedRefusalTests).Assembly.GetManifestResourceStream("VerifiedImport.17-T4.xml");
            using var reader = new StreamReader(stream!);
            string xml = reader.ReadToEnd();
            string before = xml.Replace("Ghost", "Speed").Replace("<MemoryLayout>", "<CodeModifiedDate>2026-11-25</CodeModifiedDate><MemoryLayout>");
            var stepEngine = new TiaMcp.Adapters.PlcFoundationEngine();
            var engine = new Boundary(release) { Step = (tool, run) => stepEngine.RunHmiStep(tool, run, requiresProject: false) };
            var group = engine.Plc.BlockGroup;
            var target = new Block { Name = "MCP_RC_DB2", Language = "DB", Parent = group };
            group.Blocks.Add(target);
            group.Blocks.Add(new Block { Name = "OtherDB", Parent = group });
            var exports = new List<string>();
            group.AfterBlockExport = file => {
                exports.Add(file.Name);
                if (file.Name == "before.xml") { File.WriteAllText(file.FullName, before); return; }
                File.WriteAllText(file.FullName, scenario switch {
                    "date" => before.Replace("2026-11-25", "2026-11-26"), "id" => before.Replace("ID=\"0\"", "ID=\"1\""),
                    "content" => before.Replace("Speed", "Other"), "malformed" => "<Document>",
                    "lost" => throw new COMException(), "unreadable" => throw new IOException(),
                    _ => before.Replace("<Engineering", "<DocumentInfo/><Engineering") });
            };
            var refused = new NativeException(Message);
            bool issued = false;
            int checksAfterImport = 0;
            engine.ReadBindingIdentity = () => issued && scenario == "binding" ? "another-project" : release + ":fixture";
            target.OnNameRead = () => {
                if ((!issued && scenario == "no-inventory") || (issued && scenario == "unreadable-group")) throw new IOException("Group unreadable");
            };
            group.OnCall = _ => {
                issued = true;
                if (scenario == "returned-empty") return;
                if (scenario == "names") group.Blocks.Add(new Block { Name = "Other", Parent = group });
                if (scenario == "reordered") group.Blocks.Reverse();
                if (scenario == "inconsistent") target.IsConsistent = false;
                if (scenario == "abort") throw new EngineeringUserAbortException();
                if (scenario == "disposed") throw new EngineeringObjectDisposedException();
                throw refused;
            };
            group.EmptyImportResult = scenario == "returned-empty";
            var request = new PlcSoftwareRequest { FilePath = Path.Combine(directory.FullName, "candidate.xml"), SoftwarePath = "PLC", Path = target.Name,
                EvidenceDirectory = directory.FullName, DryRun = true };
            File.WriteAllText(request.FilePath, xml);
            var candidate = PlcVerifiedImportBridge.Validate(request);
            HardwareAddressingReply Apply() => engine.VerifiedImport(request, candidate, execution => {
                if (scenario == "unmarked") execution.Import = _ => throw new NativeException(Message);
                var verify = execution.VerifyBinding;
                execution.VerifyBinding = () => {
                    if (issued) checksAfterImport++;
                    if (issued && scenario == "bind-lost") throw new COMException();
                    verify();
                };
                return PlcVerifiedImportBridge.Execute(execution);
            });
            request.ExpectedToken = Assert.IsType<string>(Apply().Meta["token"]);
            request.DryRun = false;
            var reply = Apply();
            Assert.Equal(scenario != "unmarked", issued);
            Assert.Equal(scenario is "unmarked" or "returned-empty" or "abort" or "disposed" or "no-inventory" ? 0 : 1, checksAfterImport);
            bool exported = scenario is "unchanged" or "reordered" or "date" or "id" or "content" or "malformed" or "lost" or "unreadable";
            Assert.Equal(exported ? new[] { "before.xml", "before.xml", "after-refusal.xml" } : new[] { "before.xml", "before.xml" }, exports);
            Assert.True(File.Exists(Path.Combine((string)reply.Meta["evidenceDirectory"]!, "before.xml")));
            Assert.Equal(scenario is "unchanged" or "reordered", refused.Data["foundationKnownNoMutation"] is true);
            return new PlcSoftwareReply { Meta = reply.Meta, Message = reply.Message,
                MayHaveChanged = reply.MayHaveChanged, OutcomeUnknown = reply.OutcomeUnknown };
        }
        finally { directory.Delete(true); }
    }

    [Theory, InlineData("success"), InlineData("unreadable"), InlineData("connection-lost")]
    public void Apply_reads_names_once_immediately_before_import(string scenario)
    {
        using var stream = typeof(VerifiedRefusalTests).Assembly.GetManifestResourceStream("VerifiedImport.17-T4.xml");
        using var reader = new StreamReader(stream!);
        string xml = reader.ReadToEnd().Replace("MCP_RC_DB2", "DB1").Replace("Ghost", "Speed");
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N")));
        try
        {
            var steps = new TiaMcp.Adapters.PlcFoundationEngine();
            var engine = new Boundary("17") { Step = (tool, run) => steps.RunHmiStep(tool, run, requiresProject: false) };
            var group = engine.Plc.BlockGroup;
            var calls = new List<string>();
            var target = new Block { Name = "DB1", Language = "DB", Parent = group, OnNameRead = () => calls.Add("name DB1") };
            group.Blocks.Add(target);
            group.Blocks.Add(new Block { Name = "DB2", Parent = group, OnNameRead = () => {
                calls.Add("name DB2");
                if (scenario == "connection-lost") throw new COMException();
                if (scenario == "unreadable") throw new IOException();
            } });
            group.AfterBlockExport = file => { calls.Add(file.Name); File.WriteAllText(file.FullName, xml); };
            group.OnCall = _ => calls.Add("import");
            group.AfterImport = _ => group.Blocks.Remove(target);
            var request = new PlcSoftwareRequest { FilePath = Path.Combine(directory.FullName, "candidate.xml"), SoftwarePath = "PLC", Path = "DB1",
                EvidenceDirectory = directory.FullName, DryRun = true };
            File.WriteAllText(request.FilePath, xml);
            var candidate = PlcVerifiedImportBridge.Validate(request);
            HardwareAddressingReply Apply() => engine.VerifiedImport(request, candidate, execution => {
                var verify = execution.VerifyBinding;
                execution.VerifyBinding = () => { calls.Add("bind"); verify(); };
                return PlcVerifiedImportBridge.Execute(execution);
            });
            var preview = Apply();
            Assert.True(preview.Meta.ContainsKey("token"), preview.Meta.TryGetValue("error", out var failure) ? failure?.ToString() : preview.Message);
            request.ExpectedToken = Assert.IsType<string>(preview.Meta["token"]);
            Assert.Equal(new[] { "bind", "before.xml" }, calls);
            calls.Clear();
            request.DryRun = false;
            var reply = Apply();
            if (scenario == "connection-lost")
            {
                Assert.Equal(new[] { "bind", "before.xml", "bind", "name DB1", "name DB2" }, calls);
                Assert.Equal(false, reply.Meta["mayHaveChanged"]);
                Assert.True(reply.Meta["connectionUnavailable"] is true);
                Assert.False(reply.Meta.ContainsKey("cleanupRequired"));
            }
            else
            {
                Assert.Equal(new[] { "bind", "before.xml", "bind", "name DB1", "name DB2", "import", "bind", "after.xml" }, calls);
                Assert.Equal("DocumentReadbackMatched", reply.Meta["status"]);
            }
        }
        finally { directory.Delete(true); }
    }

    [Theory, InlineData("unchanged"), InlineData("names"), InlineData("date"), InlineData("id"), InlineData("content"), InlineData("inconsistent"),
        InlineData("malformed"), InlineData("lost"), InlineData("bind-lost"), InlineData("binding"), InlineData("unreadable"), InlineData("abort"),
        InlineData("returned-empty"), InlineData("disposed"), InlineData("unmarked"), InlineData("no-inventory"), InlineData("unreadable-group"),
        InlineData("reordered")]
    public void Only_a_verified_native_refusal_settles(string scenario)
    {
        var reply = Run(scenario);
        bool proven = scenario is "unchanged" or "reordered";
        Assert.Equal(!proven, reply.MayHaveChanged);
        Assert.Equal(!proven, reply.OutcomeUnknown);
        Assert.Equal(!proven, reply.Meta!.ContainsKey("cleanupRequired"));
        Assert.False(reply.Meta.ContainsKey("importReturned"));
        if (proven) { Assert.Equal(Message, reply.Meta["nativeMessage"]); Assert.True(reply.Meta.ContainsKey("refusalProof")); }
        if (scenario is "lost" or "bind-lost") Assert.Equal(true, reply.Meta["connectionUnavailable"]);
    }
}
