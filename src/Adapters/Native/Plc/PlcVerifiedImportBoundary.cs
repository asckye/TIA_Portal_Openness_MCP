using System;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        public HardwareAddressingReply ImportPlcBlockVerified(PlcSoftwareRequest request, Func<PlcSoftwareRequest, PlcVerifiedCandidate> validate, Func<PlcVerifiedExecution, string> execute)
            => _session.RunHmiStepTool("ImportPlcBlockVerified", meta => {
                string softwarePath = request.SoftwarePath, blockPath = request.Path;
                var candidate = validate(request);
                var parts = PlcGroupOperations.Parts(blockPath);
                if (parts.Length == 0 || parts.Last() != candidate.Name) throw new ArgumentException("Exact blockPath must end with candidate Name.");
                meta["mayHaveChanged"] = false; meta["softwarePath"] = softwarePath; meta["blockPath"] = blockPath;
                IDisposable? exclusive = null;
                try {
                    exclusive = _session.AcquireHmiEditAccess();
                    var plc = _session.ExactPlcForEngineering(softwarePath, true); // Offline even for export preview.
                    var group = (PlcBlockGroup)PlcGroupOperations.Group(PlcBlockPrimitives.BlockGroup(plc), string.Join("/", parts.Take(parts.Length - 1)));
                    PlcBlock Resolve() => PlcBlockPrimitives.Find(PlcBlockPrimitives.Blocks(group), parts.Last()) ?? throw new InvalidOperationException("Exact existing block not found in its target group.");
                    void Check() { _session.VerifyBinding("ImportPlcBlockVerified"); _session.ExactPlcForEngineering(softwarePath, true); }
                    void Export(string path) {
                        var block = Resolve();
                        if (!PlcBlockPrimitives.IsConsistent(block)) throw new InvalidOperationException("Block is inconsistent; export verification is unavailable. No automatic compile or further import.");
                        PlcBlockPrimitives.Export(block, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path)), ExportOptions.WithDefaults | ExportOptions.WithReadOnly);
                    }
                    void Import(string path) {
                        Resolve();
                        var result = PlcBlockPrimitives.Import(PlcBlockPrimitives.Blocks(group), new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path)), ImportOptions.Override);
                        if (result == null || result.Count != 1) throw new InvalidOperationException("Import did not return exactly one block; inspect retained evidence.");
                    }
                    void Compile() {
                        var compiler = PlcBlockPrimitives.Compiler(Resolve()) ?? throw new NotSupportedException("Block compiler service unavailable.");
                        var result = PlcBlockPrimitives.Compile(compiler);
                        if (result == null) throw new InvalidOperationException("Block compiler returned no result.");
                        meta["compileState"] = PlcBlockPrimitives.State(result).ToString(); meta["compileErrorCount"] = PlcBlockPrimitives.ErrorCount(result); meta["compileWarningCount"] = PlcBlockPrimitives.WarningCount(result);
                        if (PlcBlockPrimitives.ErrorCount(result) != 0 || (PlcBlockPrimitives.State(result).ToString() != "Success" && !TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(PlcBlockPrimitives.State(result)))) throw new InvalidOperationException("Imported block compilation failed. Backup retained; no further native readback attempted.");
                    }
                    var identity = _session.BindingIdentity();
                    if (string.IsNullOrEmpty(identity)) throw new InvalidOperationException("Exact binding identity required.");
                    var scope = identity + "\n" + softwarePath + "\n" + blockPath;
                    return execute(new PlcVerifiedExecution { Request = request, CandidateXml = candidate.Xml, Binding = scope,
                        Export = Export, Import = Import, VerifyBinding = Check, Meta = meta, Compile = request.CompileAfterImport ? (Action)Compile : null });
                }
                catch (Exception ex) { if (TiaMcpServer.ModelContextProtocol.PortalFailureClassifier.IsPortalProcessLost(ex)) meta["connectionUnavailable"] = true; throw; }
                finally {
                    if (exclusive != null && Equals(meta["connectionUnavailable"], true)) meta["exclusiveReleaseSkipped"] = true;
                    else exclusive?.Dispose();
                }
            });
    }
}
