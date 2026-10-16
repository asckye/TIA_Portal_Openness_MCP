using System;
using System.Collections.Generic;
using System.IO;

namespace TiaMcp.PlcWorker;

internal sealed class WorkerLaunchOptions
{
    internal string ReleaseKey { get; }
    internal string ApiDirectory { get; }
    internal string Nonce { get; }
    internal bool WithUi { get; }
    internal string? TiaPortalLocation { get; }

    internal WorkerLaunchOptions(string releaseKey, string apiDirectory, string nonce, bool withUi = false, string? tiaPortalLocation = null)
    {
        ReleaseKey = releaseKey; ApiDirectory = Path.GetFullPath(apiDirectory); Nonce = nonce;
        WithUi = withUi; TiaPortalLocation = tiaPortalLocation;
    }

    internal IEnumerable<string> Arguments()
    {
        yield return "--native-session"; yield return ReleaseKey; yield return ApiDirectory; yield return Nonce;
        if (WithUi) yield return "--with-ui";
        if (TiaPortalLocation != null) { yield return "--tia-portal-location"; yield return TiaPortalLocation; }
    }

    internal static WorkerLaunchOptions Parse(string[] args)
    {
        if (args.Length < 4 || args[0] != "--native-session")
            throw new ArgumentException("Usage: --native-session <exact-release-key> <verified-public-api-directory> <launch-nonce> [--with-ui] [--tia-portal-location <directory>].");
        bool ui = false, locationSeen = false;
        string? location = null;
        for (int i = 4; i < args.Length; i++)
        {
            if (args[i] == "--with-ui" && !ui) ui = true;
            else if (args[i] == "--tia-portal-location" && !locationSeen && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            { location = args[++i]; locationSeen = true; }
            else throw new ArgumentException("Unknown, repeated or incomplete worker option: " + args[i]);
        }
        return new WorkerLaunchOptions(args[1], args[2], args[3], ui, location);
    }
}
