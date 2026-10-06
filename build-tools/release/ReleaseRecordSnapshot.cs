namespace TiaMcp.ReleaseTool;

internal sealed class ReleaseRecordSnapshot
{
    private readonly string root;
    private readonly string originalRoot;
    private readonly string stateRoot;
    private readonly string[] paths;

    internal ReleaseRecordSnapshot(string root, string originalRoot, string stateRoot, IEnumerable<string> relativePaths)
    {
        this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        this.originalRoot = Path.GetFullPath(originalRoot);
        this.stateRoot = Path.GetFullPath(stateRoot);
        paths = relativePaths.Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) throw new ReleaseException("Cannot snapshot an empty release-record set");
        Directory.CreateDirectory(this.originalRoot);
        Directory.CreateDirectory(this.stateRoot);
        foreach (var relative in paths)
        {
            var source = Resolve(this.root, relative);
            if (!File.Exists(source)) throw new ReleaseException("Tracked release record is missing: " + relative);
            Copy(source, Resolve(this.originalRoot, relative));
            Copy(source, Resolve(this.stateRoot, relative));
        }
    }

    internal void RestoreStepState()
    {
        foreach (var relative in paths) Copy(Resolve(stateRoot, relative), Resolve(root, relative));
    }

    internal void CaptureStepState()
    {
        foreach (var relative in paths) Copy(Resolve(root, relative), Resolve(stateRoot, relative));
    }

    internal void RestoreOriginal()
    {
        foreach (var relative in paths)
        {
            var source = Resolve(originalRoot, relative);
            var target = Resolve(root, relative);
            Copy(source, target);
            if (!ReleaseRecords.HashFile(target).Equals(ReleaseRecords.HashFile(source), StringComparison.Ordinal))
                throw new ReleaseException("Release record was not restored: " + relative);
        }
    }

    private static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new ReleaseException("Release record path must be relative: " + relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Release record path escaped its snapshot root: " + relative);
        return fullPath;
    }

    private static void Copy(string source, string destination)
    {
        if (!File.Exists(source)) throw new ReleaseException("Release record snapshot is missing: " + source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, true);
    }
}
