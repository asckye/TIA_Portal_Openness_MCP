using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class DocumentImportSafetySources
{
    private static readonly string ROOT = Repository.FindRoot(AppContext.BaseDirectory);
    private static readonly string SRC = Path.Combine(ROOT, "src/Engine");
    private static readonly string LOGIC = Path.Combine(ROOT, "src/Logic");
    private static readonly EngineSources Sources = new(ROOT);
    private static string Read(string path) => Repository.ReadSource(path);
    private static bool Has(string source, string value) => source.Contains(value, StringComparison.Ordinal);
    private static int Count(string source, string value) => Regex.Matches(source, Regex.Escape(value)).Count;
    private static int Find(string source, string value, int start = 0)
    {
        var index = source.IndexOf(value, start, StringComparison.Ordinal);
        Assert.True(index >= 0, "Missing source fragment: " + value); return index;
    }
    private static readonly string SINGLE = Sources.Member("ImportFromDocuments", signature: "bool ImportFromDocuments");
    private static readonly string BATCH = Sources.Member("ImportBlocksFromDocuments", signature: "IEnumerable<PlcBlock>");
    private static readonly string MCP = Sources.Member("ImportBlocksFromDocuments", signature: "Task<ResponseImportBlocksFromDocuments>");
    private static readonly string V4_MCP = Sources.Member("ImportBlocksFromDocumentsV4", tool: true);
    private static readonly string PRIMITIVES = Read(Path.Combine(ROOT, "src/Adapters/Native/Plc/PlcDocumentPrimitives.cs"));

    [Fact]
    public void V4BoundaryKeepsTheExistingNativeAndReportingPath()
    {
        Assert.Contains("PlcExchangeContract.RunAsync(\"ImportPlcBlocksDocuments\"", V4_MCP, StringComparison.Ordinal);
        Assert.Contains("ImportBlocksFromDocuments(server, context, softwarePath, groupPath, importPath, regexName, importOption)", V4_MCP, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedPrimitivesKeepEachNativeOperationAndArgument()
    {
        foreach (var declaration in new[] { "public static PlcBlockSystemGroup BlockGroup(PlcSoftware software) => software.BlockGroup;", "public static PlcBlockComposition Blocks(PlcBlockGroup group) => group.Blocks;", "public static DocumentImportResultForBlocks Import(PlcBlockComposition blocks, DirectoryInfo directory, string name, ImportDocumentOptions option) => blocks.ImportFromDocuments(directory, name, option);", "public static DocumentResultState State(DocumentImportResult result) => result.State;", "public static PlcBlockAssociation ImportedBlocks(DocumentImportResultForBlocks result) => result.ImportedPlcBlocks;", "public static void SetNumber(PlcBlock block, int number) => block.Number = number;", "public static void SetAutoNumber(PlcBlock block, bool autoNumber) => block.AutoNumber = autoNumber;" })
        {
            Assert.Equal(1, Count(PRIMITIVES, declaration));
        }
        Assert.Equal(1, Count(PRIMITIVES, ".ImportFromDocuments("));
        Assert.Contains("Documents.Import(Documents.Blocks(targetGroup), dir, fileNameWithoutExtension, option)", SINGLE, StringComparison.Ordinal);
        Assert.Contains("Documents.SetAutoNumber(imported, false);", SINGLE, StringComparison.Ordinal);
        Assert.True(Find(SINGLE, "Documents.SetAutoNumber(imported, false);") < Find(SINGLE, "Documents.SetNumber(imported, prevNumber.Value);"));
    }

    [Fact]
    public void WorkerBlocksReadsAndPreviewsAfterUncertainDocumentBatch()
    {
        var worker = Path.Combine(ROOT, "src/PlcWorker");
        var program = Read(Path.Combine(worker, "FoundationWorkerDispatcher.cs"));
        var guard = Find(program, "sessionOutcome.RequireUsable(readOnly);");
        Assert.True(guard < Find(program, "MutationIdentityPolicy.ValidateTarget"));
        Assert.True(guard < Find(program, "method.Invoke(engine, call)"));
        Assert.Contains("if (result is IWorkerOperationReply reply && reply.RequiresSessionReset)", program, StringComparison.Ordinal);
        Assert.Contains("sessionOutcome.MarkUncertain(blockReads: reply.BlockReadsAfterUncertain);", program, StringComparison.Ordinal);
        var batch_reply = Read(Path.Combine(ROOT, "src/Adapters.Contracts/PlcBatchDocumentImportContracts.cs"));
        Assert.Contains("public sealed class PlcBatchDocumentImportResult : TiaMcp.Adapters.Contracts.IWorkerOperationReply", batch_reply, StringComparison.Ordinal);
        Assert.Contains("bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.BlockReadsAfterUncertain => true;", batch_reply, StringComparison.Ordinal);
        Assert.Contains("public bool RequiresSessionReset {get;internal set;}", batch_reply, StringComparison.Ordinal);
        Assert.DoesNotContain("batchOutcomeUnknown", program, StringComparison.Ordinal);
        Assert.Contains("WorkerSessionOutcomeState.cs", Read(Path.Combine(worker, "TiaMcp.PlcWorker.csproj")), StringComparison.Ordinal);
        Assert.Contains("FoundationWorkerDispatcher.cs", Read(Path.Combine(worker, "TiaMcp.PlcWorker.csproj")), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingGroupRejectedBeforeNativeCall()
    {
        Assert.Contains("string.IsNullOrWhiteSpace(groupPath) ? Documents.BlockGroup(plcSoftware)", BATCH, StringComparison.Ordinal);
        var gate = Find(BATCH, "?? throw new PortalException(PortalErrorCode.NotFound");
        Assert.True(gate < Find(BATCH, "Documents.Import(Documents.Blocks(group), dir, name, option)"));
        Assert.DoesNotContain("Documents.Import(Documents.Blocks(Documents.BlockGroup(plcSoftware))", BATCH, StringComparison.Ordinal);
        Assert.DoesNotContain("catch", BATCH[..gate], StringComparison.Ordinal);
    }

    [Fact]
    public void OneNativeCallSiteNoRetryAndStopForAmbiguousState()
    {
        Assert.Equal(1, Count(BATCH, "Documents.Import("));
        Assert.Contains("result == null || !TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(Documents.State(result)) || Documents.ImportedBlocks(result) == null", BATCH, StringComparison.Ordinal);
        Assert.Equal(1, Count(BATCH, "LastImportFromDocumentsStopped = true;\n                    break;"));
        Assert.Equal(1, Count(BATCH, "LastImportFromDocumentsStopped = true;\n                        break;"));
        Assert.DoesNotContain("continue;", BATCH, StringComparison.Ordinal);
    }

    [Fact]
    public void CountsAreDocumentSetsNotBlocks()
    {
        Assert.Contains("LastImportFromDocumentsAttempted++;", BATCH, StringComparison.Ordinal);
        Assert.Contains("LastImportFromDocumentsSucceeded++;", BATCH, StringComparison.Ordinal);
        Assert.True(Find(BATCH, "imported.AddRange(blocks)") < Find(BATCH, "LastImportFromDocumentsSucceeded++;"));
        Assert.Contains("selected - attempted", MCP, StringComparison.Ordinal);
        Assert.Contains("!stopped && !responseReportingFailed && succeeded == selected", MCP, StringComparison.Ordinal);
        foreach (var field in new[] { "selectedFiles", "attemptedFiles", "succeededFiles", "notAttemptedFiles", "stopped", "mayHaveChanged" })
        {
            Assert.Contains(string.Concat("[\"", field, "\"]"), MCP, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PartialResultsEvidenceAndNoFalseRollback()
    {
        Assert.Contains("DocumentImportedNamesSuffix(result) + DocumentMessageSuffix(result)", BATCH, StringComparison.Ordinal);
        Assert.Contains("DocumentImportedNamesSuffix(result) + DocumentMessageSuffix(result)", SINGLE, StringComparison.Ordinal);
        foreach (var text in new[] { SINGLE, BATCH, MCP })
        {
            Assert.Contains("may have changed", text, StringComparison.Ordinal);
            Assert.DoesNotContain("整份文档未导入", text, StringComparison.Ordinal);
            Assert.DoesNotContain("一个都没导进去", text, StringComparison.Ordinal);
            Assert.DoesNotContain("The document set was not imported", text, StringComparison.Ordinal);
        }
        Assert.Contains("Materialize before adding", BATCH, StringComparison.Ordinal);
    }

    [Fact]
    public void DeterministicSelectionBeforeAttempt()
    {
        Assert.Contains(".OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.Ordinal)", BATCH, StringComparison.Ordinal);
        Assert.True(Find(BATCH, "var selected =") < Find(BATCH, "LastImportFromDocumentsAttempted++;"));
    }

    [Fact]
    public void PostNativeReportingKeepsEvidence()
    {
        Assert.True(Find(MCP, "var failures = _domain.LastImportFromDocumentsFailures.ToList()") < Find(MCP, "Helper.GetAttributeList(block)"));
        Assert.Contains("Block metadata readback failed after import:", MCP, StringComparison.Ordinal);
        Assert.Contains("Progress notification failed after import:", MCP, StringComparison.Ordinal);
        Assert.Contains("[\"responseReportingFailed\"] = responseReportingFailed", MCP, StringComparison.Ordinal);
        Assert.Contains("Post-import result or identity readback failed:", SINGLE, StringComparison.Ordinal);
        Assert.DoesNotContain("McpHints.Recovery(ex)", MCP, StringComparison.Ordinal);
    }

    [Fact]
    public void OverrideFeaturePreservedButNotNone()
    {
        Assert.Contains("(option & ImportDocumentOptions.Override) != 0 && existing != null", SINGLE, StringComparison.Ordinal);
        Assert.Contains("if (prevNumber.HasValue)", SINGLE, StringComparison.Ordinal);
        Assert.Contains("Documents.SetNumber(imported, prevNumber.Value)", SINGLE, StringComparison.Ordinal);
        Assert.Contains("Documents.SetAutoNumber(imported, prevAutoNumber)", SINGLE, StringComparison.Ordinal);
    }
}
