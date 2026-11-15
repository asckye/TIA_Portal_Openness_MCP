namespace TiaMcp.ReleaseTool;

internal static class GuiTestDesktop
{
    internal const string EnvironmentKey = "TIA_MCP_GUI_TEST_DESKTOP";

    internal static bool RequiresIsolation(string name) => name == "studio-gui"
        || DerivedSuiteGates.Supersets.GetValueOrDefault(name) == "studio-gui";

    internal static IDictionary<string, string?>? EnvironmentFor(string name, IDictionary<string, string?>? environment)
    {
        if (!RequiresIsolation(name)) return environment;
        var isolated = environment == null ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
        isolated[EnvironmentKey] = $"TiaMcp.Gui.{System.Environment.ProcessId}.{Guid.NewGuid():N}";
        return isolated;
    }
}
