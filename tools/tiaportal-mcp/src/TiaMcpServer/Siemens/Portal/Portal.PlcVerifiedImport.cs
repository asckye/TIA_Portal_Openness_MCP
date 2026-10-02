using System;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.Compiler;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ImportPlcBlockVerified(string softwarePath, string blockPath, string importPath, string evidenceDirectory, bool dryRun, string expectedToken, bool compileAfterImport)
            => RunHmiStepTool("ImportPlcBlockVerified", meta => {
                var candidate = PlcDocumentEditing.Read(importPath);
                var candidateDocument = PlcDocumentEditing.Parse(candidate);
                var parts = EngineeringGroupOperations.Parts(blockPath);
                if (parts.Length == 0 || parts.Last() != PlcDocumentEditing.Name(candidateDocument)) throw new ArgumentException("Exact blockPath must end with candidate Name.");
                meta["mayHaveChanged"] = false; meta["softwarePath"] = softwarePath; meta["blockPath"] = blockPath;
                IDisposable? exclusive = null;
                try {
                    exclusive = AcquireHmiEditAccess();
                    var plc = ExactPlcForEngineering(softwarePath, true); // Offline even for export preview.
                    var group = (PlcBlockGroup)EngineeringGroupOperations.Group(plc.BlockGroup, string.Join("/", parts.Take(parts.Length - 1)));
                    PlcBlock Resolve() => group.Blocks.Find(parts.Last()) ?? throw new InvalidOperationException("Exact existing block not found in its target group.");
                    void Check() { VerifyBinding("ImportPlcBlockVerified"); ExactPlcForEngineering(softwarePath, true); }
                    void Export(string path) {
                        var block = Resolve();
                        if (!block.IsConsistent) throw new InvalidOperationException("Block is inconsistent; export verification is unavailable. No automatic compile or further import.");
                        block.Export(new FileInfo(path), ExportOptions.WithDefaults | ExportOptions.WithReadOnly);
                    }
                    void Import(string path) {
                        Resolve();
                        var result = group.Blocks.Import(new FileInfo(path), ImportOptions.Override);
                        if (result == null || result.Count != 1) throw new InvalidOperationException("Import did not return exactly one block; inspect retained evidence.");
                    }
                    void Compile() {
                        var compiler = Resolve().GetService<ICompilable>() ?? throw new NotSupportedException("Block compiler service unavailable.");
                        var result = compiler.Compile();
                        if (result == null) throw new InvalidOperationException("Block compiler returned no result.");
                        meta["compileState"] = result.State.ToString(); meta["compileErrorCount"] = result.ErrorCount; meta["compileWarningCount"] = result.WarningCount;
                        if (result.ErrorCount != 0 || (result.State.ToString() != "Success" && result.State.ToString() != "Warning")) throw new InvalidOperationException("Imported block compilation failed. Backup retained; no further native readback attempted.");
                    }
                    var identity = GetBindingIdentity();
                    if (identity["identity"] == null) throw new InvalidOperationException("Exact binding identity required.");
                    var scope = identity.ToJsonString() + "\n" + softwarePath + "\n" + blockPath;
                    return PlcVerifiedImport.Execute(candidate, scope, evidenceDirectory, dryRun, expectedToken, Export, Import, Check, meta, compileAfterImport ? (Action)Compile : null);
                }
                catch (Exception ex) { if (HmiReadSafety.ConnectionUnavailable(ex)) meta["connectionUnavailable"] = true; throw; }
                finally {
                    if (exclusive != null && meta["connectionUnavailable"]?.GetValue<bool>() == true) meta["exclusiveReleaseSkipped"] = true;
                    else exclusive?.Dispose();
                }
            });
    }
}
