using System;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private readonly object _globalScriptEditGate = new object();
        public ResponseMessage UpdateUnifiedGlobalScript(string softwarePath, string expectedProject, string moduleName,
            string scriptCode, bool dryRun = true, string expectedToken = "")
        {
            lock (_globalScriptEditGate)
                return RunHmiStepTool("UpdateUnifiedGlobalScript", meta =>
                {
                    meta["softwarePath"] = softwarePath; meta["moduleName"] = moduleName;
                    if (string.IsNullOrWhiteSpace(expectedProject) || string.IsNullOrWhiteSpace(softwarePath))
                        throw new ArgumentException("Exact expectedProject and explicit softwarePath are required.");
                    IDisposable? exclusive = null;
                    try
                    {
                        if (!dryRun) exclusive = AcquireHmiEditAccess();
                        if (!string.Equals(MigrationRead.Get(CurrentProject!, "Name")?.ToString(), expectedProject, StringComparison.Ordinal))
                            throw new InvalidOperationException("Exact expectedProject mismatch; no script operation attempted.");
                        var message = UnifiedGlobalScriptEdit.Execute(() => ResolveHmiSoftwareOrThrow(softwarePath), expectedProject,
                            softwarePath, moduleName, scriptCode, dryRun, expectedToken, meta);
                        if (meta["connectionUnavailable"]?.GetValue<bool>() == true) RecordHmiReadFault(meta);
                        return message;
                    }
                    catch (Exception ex)
                    {
                        if (HmiReadSafety.ConnectionUnavailable(ex)) meta["connectionUnavailable"] = true;
                        throw;
                    }
                    finally
                    {
                        // After IPC failure, do not make another remote call even
                        // through lease disposal. Preserve the original diagnostic.
                        if (exclusive != null && meta["connectionUnavailable"]?.GetValue<bool>() == true)
                            meta["exclusiveReleaseSkipped"] = true;
                        else exclusive?.Dispose();
                    }
                });
        }
    }
}
