using System;

namespace TiaMcp.Engine.Tests.Shared
{
    internal static class FixtureExit
    {
        internal static string Describe(int exitCode, string stderrTail)
        {
            const string prefix = "FIXTURE_UNHANDLED_EXCEPTION ";
            if (exitCode == 86)
            {
                foreach (string line in stderrTail.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                        return "fixture crashed with an unhandled exception (exit 86): " + line;
                return "fixture crashed with an unhandled exception (exit 86): " + stderrTail;
            }
            return "exit " + exitCode + (string.IsNullOrEmpty(stderrTail) ? "" : ": " + stderrTail);
        }
    }
}
