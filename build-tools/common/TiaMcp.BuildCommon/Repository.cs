using System.Diagnostics;
using System.Text;

namespace TiaMcp.BuildCommon;

public static class Repository
{
    public static string FindRoot(string? start = null)
    {
        for (var directory = new DirectoryInfo(start ?? Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TiaPortalOpenness.Offline.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    public static IReadOnlyList<string> GitFiles(string root, params string[] patterns)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false, true)
        };
        foreach (var argument in new[] { "ls-files", "-z", "--" }.Concat(patterns)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start git");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("git ls-files failed: " + error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult().Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public static string ReadSource(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes.AsSpan(offset)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
