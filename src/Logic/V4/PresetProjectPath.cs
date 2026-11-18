namespace TiaMcp.Logic.V4
{
    public static class PresetProjectPath
    {
        public static string Canonical(string path) => TiaMcp.Adapters.Contracts.ProjectFileCanonicalizer.Canonical(path);
        public static bool Same(string expected, string actual) => TiaMcp.Adapters.Contracts.ProjectFileCanonicalizer.Same(expected, actual);
    }
}
