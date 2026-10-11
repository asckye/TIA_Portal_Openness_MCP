using System;
using Siemens.Engineering;
namespace TiaMcpServer.Siemens;
public partial class Portal
{
    internal void AdoptProjectLifecycleConnection(TiaPortal connection)
    { _portal = connection; RememberBoundProcess(); sharedPortalOwned = true; }
    internal void AdoptProjectLifecycleRetrieved(object project)
        => AdoptProject(project as ProjectBase, "Retrieve returned no project; inspect destination for partial files.");
    internal void RefreshProjectLifecycleSavedAs() => CaptureBinding();
    internal void ReleaseProjectLifecycleBinding() => ReleaseProject();
    internal void ClearProjectLifecycleConnectError() => LastConnectError = null;
}
