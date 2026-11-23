using System;

namespace TiaMcp.Logic.V4
{
    public enum WriteApprovalOperation
    {
        OfflineWrite,
        SaveOrCloseProject,
        ImportStaging,
        OutsideOutputFolder,
        GitStageOrCommit,
        OpennessGroupChange,
        SimulatorWrite,
        SnapshotReadExportOrCapture,
        OpenPipeRead,
        DiscardOnClose,
        OnlineWrite,
        CompileWithSafetyPassword,
        EnvironmentRepair
    }

    public static class WriteApprovalPolicy
    {
        public static bool AlwaysConfirmed(this WriteApprovalOperation operation) => operation switch
        {
            WriteApprovalOperation.DiscardOnClose or WriteApprovalOperation.OnlineWrite => true,
            WriteApprovalOperation.OfflineWrite or WriteApprovalOperation.SaveOrCloseProject or WriteApprovalOperation.ImportStaging
                or WriteApprovalOperation.OutsideOutputFolder or WriteApprovalOperation.GitStageOrCommit or WriteApprovalOperation.OpennessGroupChange
                or WriteApprovalOperation.SimulatorWrite or WriteApprovalOperation.SnapshotReadExportOrCapture or WriteApprovalOperation.OpenPipeRead
                or WriteApprovalOperation.CompileWithSafetyPassword or WriteApprovalOperation.EnvironmentRepair => false,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }
}
