using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal sealed class PackagedEngineLayout : IDisposable
{
    internal string Root { get; }
    internal string EnginePath { get; }

    private PackagedEngineLayout(string root, string enginePath)
    {
        Root = root;
        EnginePath = enginePath;
    }

    internal static PackagedEngineLayout Create(Assembly engine, int major)
    {
        string root = Path.Combine(Path.GetTempPath(), "TiaMcp-packaged-no-tia-" + Guid.NewGuid().ToString("N"));
        string runtime = Path.Combine(root, "runtime", "v" + major);
        string source = Path.GetDirectoryName(engine.Location)!;
        string packageManifest = FindPackageManifest(source);
        Directory.CreateDirectory(runtime);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            string relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string target = Path.Combine(runtime, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }

        string manifest = Path.Combine(root, "manifest");
        Directory.CreateDirectory(manifest);
        File.Copy(packageManifest, Path.Combine(manifest, "package-manifest.json"), true);

        if (Directory.GetFiles(runtime, "Siemens.Engineering*.dll", SearchOption.AllDirectories).Any())
        {
            Directory.Delete(root, true);
            throw new InvalidOperationException("The test package still contains Siemens.Engineering DLLs.");
        }
        string enginePath = Path.Combine(runtime, Path.GetFileName(engine.Location));
        if (!File.Exists(enginePath))
        {
            Directory.Delete(root, true);
            throw new FileNotFoundException("Packaged engine was not staged.", enginePath);
        }
        return new PackagedEngineLayout(root, enginePath);
    }

    private static string FindPackageManifest(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            string marker = Path.Combine(directory.FullName, "manifest", "package-manifest.json");
            if (File.Exists(marker)) return marker;
        }
        throw new FileNotFoundException("Could not find manifest/package-manifest.json above the engine output directory.");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
