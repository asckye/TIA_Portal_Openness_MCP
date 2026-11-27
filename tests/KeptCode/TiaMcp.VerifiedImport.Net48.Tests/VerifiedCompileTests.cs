extern alias plcpathfixture;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using System.Xml.Linq;
using Xunit;
using Boundary = plcpathfixture::TiaMcp.Adapters.PlcFoundationEngine;
using Block = plcpathfixture::Siemens.Engineering.SW.Blocks.PlcBlock;
using Result = plcpathfixture::TiaMcp.Adapters.Native.Plc.CompilerResult;

namespace TiaMcp.Engine.Tests;

public sealed class VerifiedCompileTests
{
    // V14 SP1 software compile following the failed verified import, sequence 99.
    internal const string Reason = "Data type NoSuchType_XYZ is unknown.";

    internal static PlcSoftwareReply Run(string scenario)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "verified-" + Guid.NewGuid().ToString("N")));
        try
        {
            bool recorded = scenario == "recorded";
            using var stream = typeof(VerifiedCompileTests).Assembly.GetManifestResourceStream(
                recorded ? "VerifiedImport.14sp1-MCP_RC_DB2_bogus.xml" : "VerifiedImport.17-T6.xml");
            using var reader = new StreamReader(stream!);
            string xml = reader.ReadToEnd();
            var before = XDocument.Parse(xml);
            before.Descendants().Single(m => m.Name.LocalName == "Member" && (string?)m.Attribute("Name") == "Bad").Remove();
            var stepEngine = new TiaMcp.Adapters.PlcFoundationEngine();
            var engine = new Boundary(recorded ? "14sp1" : "17") { Step = (tool, run) => stepEngine.RunHmiStep(tool, run, requiresProject: false) };
            var group = engine.Plc.BlockGroup;
            var target = new Block { Name = recorded ? "MCP_RC_DB2" : "MCP_RC_DB6", Language = "DB", Parent = group };
            group.Blocks.Add(target);
            group.AfterImport = _ => group.Blocks.Remove(target);
            var exports = new List<string>();
            group.AfterBlockExport = file => { exports.Add(file.Name); before.Save(file.FullName); };
            group.Compile = () => scenario switch {
                "null" => null, "thrown" => throw new InvalidOperationException("Compile threw"),
                "unknown-state" => new Result { State = "Unknown" },
                "lost" => throw new System.Runtime.InteropServices.COMException(),
                "disposed" => throw new ObjectDisposedException("compiler"),
                "abort" => throw new OperationCanceledException(),
                _ => new Result { State = scenario == "count" ? "Warning" : "Error",
                    ErrorCount = scenario == "state-only" ? 0 : 1, WarningCount = recorded ? 0 : 2,
                    ReadMessages = () => scenario switch {
                        "state-only" => Array.Empty<object>(),
                        "messages-unavailable" => throw new IOException("Messages unavailable"),
                        "messages-lost" => throw new System.Runtime.InteropServices.COMException(),
                        "child-abort" => new[] { new InterruptedMessage() },
                        _ => new[] { new { State = "Error", Path = "Interface", Description = Reason } } } }
            };
            var request = new PlcSoftwareRequest { FilePath = Path.Combine(directory.FullName, "input.xml"), SoftwarePath = "PLC", Path = target.Name,
                CompileAfterImport = true, EvidenceDirectory = directory.FullName, DryRun = true };
            File.WriteAllText(request.FilePath, xml);
            var candidate = PlcVerifiedImportBridge.Validate(request);
            HardwareAddressingReply Apply() => engine.VerifiedImport(request, candidate, PlcVerifiedImportBridge.Execute,
                (result, diagnose) => PlcVerifiedImportBridge.Compiler(result, diagnose, true));
            request.ExpectedToken = Assert.IsType<string>(Apply().Meta["token"]);
            request.DryRun = false;
            var step = Apply();
            Assert.Equal(new[] { "before.xml", "before.xml" }, exports);
            Assert.True(File.Exists(Path.Combine((string)step.Meta["evidenceDirectory"]!, "before.xml")));
            return new PlcSoftwareReply { Meta = step.Meta, Message = step.Message,
                MayHaveChanged = step.MayHaveChanged, OutcomeUnknown = step.OutcomeUnknown };
        }
        finally { directory.Delete(true); }
    }

    private sealed class InterruptedMessage { public string Description => throw new OperationCanceledException(); }

    [Fact]
    public void Recorded_v14_sp1_candidate_returns_its_compile_error()
    {
        var step = Run("recorded");
        Assert.False(step.OutcomeUnknown);
        Assert.Equal(0L, step.Meta!["compileWarningCount"]);
        Assert.Equal(1L, step.Meta["compileErrorCount"]);
    }

    [Fact]
    public void Returned_error_state_with_zero_reported_errors_is_settled()
    {
        var step = Run("state-only");
        Assert.False(step.OutcomeUnknown);
        Assert.True(step.MayHaveChanged);
        Assert.Equal("CompileErrors", step.Meta!["status"]);
        Assert.Equal(0L, step.Meta["compileErrorCount"]);
    }

    [Theory, InlineData("error"), InlineData("count"), InlineData("messages-unavailable")]
    public void Returned_errors_keep_the_backup_and_compiler_facts(string scenario)
    {
        var step = Run(scenario);
        Assert.False(step.OutcomeUnknown);
        Assert.True(step.MayHaveChanged);
        Assert.Equal(true, step.Meta!["importReturned"]);
        Assert.Equal(true, step.Meta["compileReturned"]);
        Assert.Equal("CompileErrors", step.Meta["status"]);
        Assert.Equal(scenario == "count" ? "Warning" : "Error", step.Meta["compileState"]);
        Assert.Equal(1L, step.Meta["compileErrorCount"]);
        Assert.Equal(2L, step.Meta["compileWarningCount"]);
        Assert.Equal(false, step.Meta["verificationSuccess"]);
        Assert.Equal(false, step.Meta["operationSuccess"]);
        Assert.Equal(false, step.Meta["dataComplete"]);
        Assert.True(step.Meta.ContainsKey("cleanupRequired"));
        Assert.Equal(scenario != "messages-unavailable", step.Meta.ContainsKey("compileMessages"));
    }

    [Theory, InlineData("null"), InlineData("thrown"), InlineData("unknown-state"), InlineData("lost"), InlineData("disposed"), InlineData("abort"),
        InlineData("messages-lost"), InlineData("child-abort")]
    public void Indeterminate_compile_keeps_the_import_unsettled(string scenario)
    {
        var step = Run(scenario);
        Assert.True(step.OutcomeUnknown);
        Assert.True(step.MayHaveChanged);
        Assert.True(step.Meta!.ContainsKey("cleanupRequired"));
        Assert.False(step.Meta.ContainsKey("compileReturned"));
    }
}
