using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Engineering;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        // ConnectDeviceNodesToProfinetSubnet. The operation name is kept for the worker wire; this is a guarded write, not a probe:
        // the subnet comes from Project.Subnets (an existing subnet of that name is joined explicitly, never through a silent
        // CreateAndConnectToSubnet name collision), a node attached to another subnet is refused, and success is the typed
        // Node.ConnectedSubnet read-back of both nodes. Connections are read from the HMI software under the second root.
        public string HardwareProbeConnectDeviceNodesToSubnet(string plcRootPath, string hmiRootPath, string subnetName)
        {
            var evidence = new Dictionary<string, object?> { ["firstRootPath"] = plcRootPath, ["secondRootPath"] = hmiRootPath, ["subnetName"] = subnetName };
            bool nestedWrite = EngineeringWriteJournal.Active;
            IDisposable? editAccess = null;
            bool operationStarted = false;
            try
            {
                var report = EngineeringWriteJournal.RunReported(evidence, () => {
                    editAccess = nestedWrite || HardwareProjectMissing() ? null : EngineeringPlcSession.AcquireHmiEditAccess();
                    operationStarted = true;
                    string result = ConnectHardwareNodes(plcRootPath, hmiRootPath, subnetName);
                    evidence["operationSuccess"] = result.IndexOf("Result: ConnectToSubnet: OK", StringComparison.Ordinal) >= 0;
                    return result;
                }, error => {
                    if (!operationStarted)
                    {
                        if (HmiReadPolicy.ConnectionUnavailable(error))
                        {
                            evidence["failureCode"] = "CONNECTION_UNAVAILABLE";
                            evidence["connectionUnavailable"] = true;
                            evidence["mayHaveChanged"] = false;
                            return "Result: NOT CONNECTED: Portal connection unavailable before the operation started. " + HardwareExceptionDetail(error);
                        }
                        evidence["failureCode"] = "PRECONDITION_FAILED";
                        evidence["mayHaveChanged"] = false;
                        return "Result: NOT CONNECTED: exclusive access refused; nothing changed. " + HardwareExceptionDetail(error);
                    }
                    if (HmiReadPolicy.ConnectionUnavailable(error)) evidence["connectionUnavailable"] = true;
                    return "Result: NOT CONNECTED: " + HardwareExceptionDetail(error);
                });
                return evidence["operationSuccess"] is true ? report : report + "\nRollback evidence: " + EngineeringPlcSession.SerializeEvidence(evidence);
            }
            finally { editAccess?.Dispose(); }
        }
    }
}
