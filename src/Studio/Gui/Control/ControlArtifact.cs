using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.ControlChannel;

internal sealed class ControlArtifact(FileStream stream, string path) : IDisposable
{
    internal string Path { get; } = path;
    // Hold a read-only lease through dispatch/open: replacement and writes cannot race the hash.
    public void Dispose() => stream.Dispose();
    internal static async Task<ControlArtifact> Validate(WorkbenchRenderArtifact artifact, CancellationToken token)
    {
        string path = System.IO.Path.GetFullPath(artifact.Path);
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || artifact.Path != path || path.IndexOf(':', 2) >= 0
            || !string.Equals(System.IO.Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid render artifact path.");
        if (new DriveInfo(System.IO.Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            throw new InvalidDataException("Render artifacts must be local files.");
        for (string? entry = path; entry != null; entry = System.IO.Path.GetDirectoryName(entry))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Render artifact is a reparse point.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        try
        {
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            if (hash != artifact.Sha256) throw new InvalidDataException("Render artifact hash changed.");
            return new ControlArtifact(stream, path);
        }
        catch { stream.Dispose(); throw; }
    }
}
