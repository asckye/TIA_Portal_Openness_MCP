using System.Text.RegularExpressions;

namespace TiaMcp.WriteGuard;

public static class ToolNames
{
    // Claude Code prefixes plugin servers and replaces each unsupported character with '_'.
    public const string HookMatcher = "^mcp__(?:tia-portal|plugin_[A-Za-z0-9_-]+_tia-portal)__(.+)$";

    public static string Pattern(string pluginName)
    {
        if (string.IsNullOrWhiteSpace(pluginName)) throw new InvalidDataException("Plugin name is missing.");
        string server = Regex.Replace("plugin_" + pluginName + "_tia-portal", "[^A-Za-z0-9_-]", "_");
        return "^mcp__(?:tia-portal|" + Regex.Escape(server) + ")__(.+)$";
    }
}
