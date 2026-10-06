namespace TiaMcp.ReleaseTool;

internal sealed record ReleaseBuildStages(Action Prepare, Action BuildEngines, Action Complete);

internal static class ReleaseStageRunner
{
    internal static void Invoke(ReleaseBuildStages stages)
    {
        stages.Prepare();
        stages.BuildEngines();
        stages.Complete();
    }
}
