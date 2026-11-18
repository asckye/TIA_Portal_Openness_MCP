namespace TiaMcp.Adapters.Contracts
{
    public static class ProjectFilePreset
    {
        public const string WorkerVariable = "TIA_MCP_PRESET_PROJECT_FILE";

        public static void RequireTarget(string? preset, string? projectFile)
        {
            if (string.IsNullOrEmpty(preset) || projectFile == null) return;
            if (!ProjectFileCanonicalizer.Same(preset!, projectFile))
                throw new AdapterPreconditionException("The automatic project binding differs from --preset-project-file (" + preset + ").",
                    "server-project-preset", null);
        }
    }
}
