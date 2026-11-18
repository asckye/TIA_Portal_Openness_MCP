using TiaMcp.Adapters;

namespace Siemens.Engineering
{
    public enum ProjectOpenMode { Primary, Secondary }
    public sealed partial class FakeProjects
    {
        public int OpenCalls { get; private set; }
        public bool NextIsPrimary { get; set; }
        public ProjectOpenMode? LastMode { get; private set; }
        public Project Open(FileInfo file, object? login, ProjectOpenMode mode)
        {
            OpenCalls++;
            LastMode = mode;
            var result = new Project { Name = Path.GetFileNameWithoutExtension(file.Name), Path = file, IsPrimary = NextIsPrimary };
            result.OnClose = () => Remove(result);
            Add(result);
            return result;
        }
    }
    public sealed partial class Project
    {
        public bool IsPrimary { get; set; } = true;
        public int CloseCalls { get; private set; }
        public int SaveCalls { get; private set; }
        internal Action? OnClose;
        public void Close() { CloseCalls++; OnClose?.Invoke(); }
        public void Save() => SaveCalls++;
    }
}

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public Dictionary<string, object?>? BindingHmiFaultForTest { get => hmiReadFault; set => hmiReadFault = value; }
        private static PlcAttributeValue[] ReadAttributes(Siemens.Engineering.Project value) => Array.Empty<PlcAttributeValue>();
        private static void RequireVisibleProject(string file) { }
        private PlcMutationResult Mutation(string operation, bool dryRun) => new() { Operation = operation, Executed = !dryRun,
            ProjectFile = project?.Path.FullName };
        public Siemens.Engineering.Project ExactProjectForTest(string selector) => ExactOpenProject(selector);
        public string? SecondaryFileForTest => SecondaryProjectFile;
        public void ReattachForTest(int pid, string file = "", int holder = 0, long start = 0)
        {
            string? reference = ValidateSecondaryRecovery(file, holder, start);
            lifecycle.Detached(); lifecycle.Attached(pid);
            RestoreSecondaryRecord(reference, holder, start);
        }
        public PlcMutationResult OpenSecondaryForTest(string file, bool dryRun = true) => OpenSecondaryProject(file, dryRun, "", "", "");
        public PlcMutationResult CloseSecondaryForTest(string file, bool dryRun = true) => CloseSecondaryProject(file, dryRun);
        public void RequirePrimaryCloseForTest() => RequireSecondaryClosed("CloseProject");
        public void BorrowPrimaryForTest(Siemens.Engineering.Project value)
        {
            project = value;
            lifecycle.Bound(value.Path.FullName, false, false);
        }
    }
}

namespace TiaOpenness.Openness
{
    internal static class SelectionStandInExtensions
    {
        internal static IEnumerable<T> SafeEnumerate<T>(this IEnumerable<T> source) => source;
    }
    internal sealed partial class OpennessSession
    {
        internal static Siemens.Engineering.Project ReuseForTest(Siemens.Engineering.Project project) => RequirePrimaryProject(project);
        internal static Siemens.Engineering.Project AutoBindForTest(Siemens.Engineering.TiaPortal portal) => PrimaryProject(portal);
    }
}
