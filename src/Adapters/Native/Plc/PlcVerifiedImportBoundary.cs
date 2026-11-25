using System;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;
namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        public HardwareAddressingReply ImportPlcBlockVerified(PlcSoftwareRequest request, Func<PlcSoftwareRequest, PlcVerifiedCandidate> validate,
                Func<PlcVerifiedExecution, string> execute, Func<object, bool, PlcCompilerEvidence> compilerEvidence)
            => _session.RunHmiStepTool("ImportPlcBlockVerified", meta => {
                string softwarePath = request.SoftwarePath, blockPath = request.Path;
                var candidate = validate(request);
                var document = SimaticMlImportWindow.Load(System.Text.Encoding.UTF8.GetBytes(candidate.Xml));
                var parts = PlcObjectPath.Parts(blockPath, parameter: "blockPath");
                if (parts.Length == 0 || parts.Last() != candidate.Name) throw new ArgumentException("Exact blockPath must end with candidate Name.");
                meta["mayHaveChanged"] = false; meta["softwarePath"] = softwarePath; meta["blockPath"] = blockPath;
                IDisposable? exclusive = null;
                try {
                    exclusive = _session.AcquireHmiEditAccess();
                    var plc = _session.ExactPlcForEngineering(softwarePath, true); // Offline even for export preview.
                    var group = (PlcBlockGroup)PlcGroupOperations.Group(PlcBlockPrimitives.BlockGroup(plc), PlcObjectPath.Format(parts.Take(parts.Length - 1)),
                        pathParts: value => PlcObjectPath.Parts(value, true));
                    PlcBlock Resolve() => PlcBlockPrimitives.Find(PlcBlockPrimitives.Blocks(group), parts.Last())
                        ?? throw new PlcSoftwareException("NotFound", "Exact existing block not found in its target group.");
                    void Check() { _session.VerifyBinding("ImportPlcBlockVerified"); _session.ExactPlcForEngineering(softwarePath, true); }
                    const ExportOptions verifiedOptions = ExportOptions.WithDefaults | ExportOptions.WithReadOnly;
                    PlcBlock ConsistentBlock() {
                        var block = Resolve();
                        if (!PlcBlockPrimitives.IsConsistent(block))
                            throw new PlcSoftwareException("BlockInconsistent",
                                "Block is inconsistent; export verification is unavailable. No automatic compile or further import.");
                        return block;
                    }
                    void Export(string path) {
                        var block = ConsistentBlock();
                        PlcBlockPrimitives
                            .Export(block, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path)), verifiedOptions);
                    }
                    void ExportNone(string path) => PlcBlockPrimitives
                        .Export(ConsistentBlock(), new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path)), ExportOptions.None);
                    ILookup<string, object>? groupsByType = null;
                    bool ExportType(string name, string path)
                    {
                        groupsByType ??= ExistingTypes(PlcBlockPrimitives.TypeGroup(plc)).ToLookup(t => t.Name, t => t.Parent, StringComparer.Ordinal);
                        var groups = groupsByType[name].Take(2).ToArray();
                        if (groups.Length != 1) return false;
                        var type = ((PlcTypeGroup)groups[0]).Types.Find(name);
                        if (type == null) return false;
                        var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path));
                        try
                        {
                            type.Export(file, verifiedOptions);
                            return true;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && !TiaMcpServer.ModelContextProtocol.PortalFailureClassifier
                            .IsPortalProcessLost(ex))
                        {
                            /* swallow(probe-optional): compare strictly when the UDT declaration cannot be exported. */
                            return false;
                        }
                    }
                    void Import(string path) {
                        Resolve();
                        var blocks = PlcBlockPrimitives.Blocks(group);
                        var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path));
                        System.Collections.Generic.IList<PlcBlock> result;
                        try { result = PlcBlockPrimitives.Import(blocks, file, ImportOptions.Override); }
                        catch (EngineeringException error) { error.Data["nativeImportThrew"] = true; throw; }
                        if (result == null || result.Count != 1)
                            throw new InvalidOperationException("Import did not return exactly one block; inspect retained evidence.");
                    }
                    void Compile() {
                        var compiler = PlcBlockPrimitives.Compiler(Resolve()) ?? throw new NotSupportedException("Block compiler service unavailable.");
                        var result = PlcBlockPrimitives.Compile(compiler);
                        if (result == null) throw new InvalidOperationException("Block compiler returned no result.");
                        string state = PlcBlockPrimitives.State(result).ToString();
                        int errors = PlcBlockPrimitives.ErrorCount(result);
                        meta["compileState"] = state; meta["compileErrorCount"] = errors;
                        meta["compileWarningCount"] = PlcBlockPrimitives.WarningCount(result);
                        if (state != "Error" && !NativeResultStates.Succeeded(NativeResultStates.Compiler, state))
                            throw new InvalidOperationException("Unrecognized block compiler state: " + state);
                        if (state == "Error" || errors > 0)
                        {
                            try { meta["compileMessages"] = compilerEvidence(result, true).Meta; }
                            catch (Exception error) when (NativeRefusalProbe.AllowsRefusalProof(error)
                                && !TiaMcpServer.ModelContextProtocol.PortalFailureClassifier.IsPortalProcessLost(error))
                            { /* swallow(probe-optional): the returned compiler state and counts survive unavailable messages. */ }
                            throw new PlcSoftwareException("CompileErrors", "Imported block has compile errors. Original backup retained.");
                        }
                    }
                    string language = PlcOrganisationPrimitives.Language(Resolve()).ToString();
                    SimaticMlImportWindow.RequireBlockProgram(_session.ReleaseKey, document, "importPath", language);
                    var identity = _session.BindingIdentity();
                    if (string.IsNullOrEmpty(identity)) throw new InvalidOperationException("Exact binding identity required.");
                    string[]? ReadRefusalGroupNames() => exclusive != null && string.Equals(identity, _session.BindingIdentity(), StringComparison.Ordinal)
                        ? PlcBlockPrimitives.Blocks(group).Select(b => PlcBlockPrimitives.Name(b)).ToArray() : null;
                    var scope = identity + "\n" + softwarePath + "\n" + blockPath;
                    return execute(new PlcVerifiedExecution { Request = request, CandidateXml = candidate.Xml, Binding = scope,
                        Export = Export, Import = Import, ExportType = ExportType, ExportNone = ExportNone, VerifyBinding = Check, Meta = meta,
                        ReadRefusalGroupNames = ReadRefusalGroupNames,
                        Compile = request.CompileAfterImport ? (Action)Compile : null });
                }
                catch (Exception ex) {
                    if (TiaMcpServer.ModelContextProtocol.PortalFailureClassifier.IsPortalProcessLost(ex)) meta["connectionUnavailable"] = true; throw; }
                finally {
                    if (exclusive != null && meta.TryGetValue("connectionUnavailable", out var lost) && Equals(lost, true))
                        meta["exclusiveReleaseSkipped"] = true;
                    else exclusive?.Dispose();
                }
            });
    }
}
