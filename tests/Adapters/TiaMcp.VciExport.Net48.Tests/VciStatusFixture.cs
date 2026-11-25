using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.VersionControl;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Vci;

internal static class VciStatusFixture
{
    private static EngineeringException ConnectionFailure()
    {
        var cause = new EndOfStreamException("The worker connection closed.");
        var failure = new EngineeringException("Connection lost", cause);
#if VCI_SDK_EXCEPTIONS
        // The public SDK constructor flattens the cause to text. The native remoting constructor keeps its type.
        var dataType = failure.MessageData.GetType();
        var constructor = typeof(EngineeringException).GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new[] { dataType, dataType.MakeArrayType(), typeof(byte[]), typeof(Exception) }, null)!;
        return (EngineeringException)constructor.Invoke(new object?[] { failure.MessageData, null, null, cause });
#else
        return failure;
#endif
    }

    internal static object Run(DirectoryInfo root, bool refreshesKnownState)
    {
        var workspace = new Workspace(root);
        var project = new Project();
        project.Vci.WorkspaceGroup.Workspaces.Add(workspace);
        var adapter = new VersionControlAdapter(() => project);
        var replies = new Dictionary<string, object>();

        WorkspaceMapping Add(string name, CompareState state, CompareState? refreshed = null)
        {
            var mapping = new WorkspaceMapping(workspace, name + ".xml");
            mapping.Status.State = state;
            mapping.Status.RefreshesKnownState = refreshesKnownState;
            mapping.Status.Refreshed = refreshed;
            workspace.Mappings.Add(mapping);
            return mapping;
        }
        void Record(string scenario, VersionControlResult reply)
        {
            replies.Add(scenario, new { reply, calls = workspace.Mappings.ToDictionary(
                m => Path.GetFileNameWithoutExtension(m.RelativeWorkspacePath), m => m.Status.Calls.ToArray()) });
            foreach (var mapping in workspace.Mappings) mapping.Status.Calls.Clear();
        }
        VersionControlResult Sync(bool changedOnly, bool dryRun) => VersionControlAdapter.WithWriteOutcome(
            new VersionControlRequest { Operation = "SynchronizeVersionControlWorkspace", DryRun = dryRun },
            adapter.SyncVersionControlWorkspace(changedOnly: changedOnly, dryRun: dryRun));

        var motor = Add("Motor", CompareState.Equal);
        Record("equalPreview", Sync(false, true));
        Record("equalApply", Sync(false, false));
        motor.Status.Refreshed = CompareState.Unequal;
        Record("edited", adapter.GetVersionControlStatus(changedOnly: false));
        motor.Status.State = CompareState.Unequal;
        Add("Refreshed", CompareState.Unknown, CompareState.Equal);
        Add("Missing", CompareState.WorkspaceFileMissing);
        Add("Pending", CompareState.Unknown);
        var deleted = Add("Deleted", CompareState.Unknown);
        // V17 rc.4 row 63. Append a planted secret to exercise host diagnostic redaction.
        deleted.Status.RefreshError = new EngineeringTargetInvocationException("Error when calling method 'UpdateStatus' of type "
            + IndividualObjectSynchronizationStatus.NativeType + "\r\nThe file is deleted password=fixture-password");
        Add("Next", CompareState.Equal);
        Record("status", adapter.GetVersionControlStatus(changedOnly: false));
        Record("changedStatus", adapter.GetVersionControlStatus(changedOnly: true));
        Record("changedSync", Sync(true, false));
        Record("allSync", Sync(false, false));

        workspace.Mappings.Clear();
        var unreadable = Add("BothRefused", CompareState.Unknown).Status;
        unreadable.RefreshError = new EngineeringException(deleted.Status.RefreshError.Message, new InvalidOperationException("Inner detail"));
        unreadable.ReadError = new EngineeringTargetInvocationException("Status unavailable");
        Add("ReadRefused", CompareState.Unknown).Status.ReadError = unreadable.ReadError;
        Add("MissingFile", CompareState.WorkspaceFileMissing).Status.RefreshError = deleted.Status.RefreshError;
        Add("Disposed", CompareState.Equal).Status.RefreshError = new EngineeringObjectDisposedException("Object disposed");
        Add("Aborted", CompareState.Equal).Status.RefreshError = new EngineeringUserAbortException("Operation aborted");
        Add("Disconnected", CompareState.Equal).Status.RefreshError = ConnectionFailure();
        Add("StreamClosed", CompareState.Equal).Status.RefreshError = new EndOfStreamException("The worker connection closed.");
        Add("Nonrecoverable", CompareState.Equal).Status.RefreshError = new NonRecoverableException("Nonrecoverable failure");
        Add("Unexpected", CompareState.Equal).Status.RefreshError = new InvalidOperationException("Unexpected error");
        Record("probeFailures", adapter.GetVersionControlStatus(changedOnly: false));
        return replies;
    }
}
