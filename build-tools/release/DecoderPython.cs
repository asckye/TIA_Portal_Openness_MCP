using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    // Build a relocatable, isolated stdlib-only runtime from the explicitly selected
    // release-machine CPython. Never copy a venv or any site-packages into delivery.
    internal static int PrepareDecoderPython(Options options)
    {
        EnsureWindows("prepare-decoder-python");
        var python = options.Get("Python", Py);
        var probe = ProcessRunner.Run(python, ["-I", "-S", "-c",
            "import sys,json,struct; print(json.dumps(dict(root=sys.base_prefix,version=list(sys.version_info[:3]),bits=struct.calcsize('P')*8)))"], Root);
        ProcessRunner.RequireSuccess(probe, "CPython runtime probe failed");
        var info = JsonNode.Parse(probe.StandardOutput)!.AsObject();
        var version = info["version"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
        if (version[0] != 3 || version[1] < 11 || info["bits"]!.GetValue<int>() != 64)
            throw new ReleaseException("Decoder delivery requires 64-bit CPython 3.11+.");
        string source = Path.GetFullPath((string)info["root"]!);
        string target = Path.Combine(Root, "runtime/decoder-python");
        if (source.StartsWith(target, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Use the release-machine CPython, not a previous decoder runtime.");
        string tag = "python" + version[0] + version[1];
        foreach (string required in new[] { "python.exe", tag + ".dll", "LICENSE.txt", "Doc/html/license.html", "Lib/encodings/__init__.py" })
            if (!File.Exists(Path.Combine(source, required))) throw new ReleaseException("Incomplete CPython installation: " + required);
        // Refuse mixed interpreter versions rather than retaining stale DLLs from a previous build.
        string receipt = Path.Combine(target, "runtime.json");
        if (File.Exists(receipt) && (string?)JsonNode.Parse(File.ReadAllText(receipt))!["tag"] != tag)
            throw new ReleaseException("Decoder CPython version changed; use a fresh worktree runtime directory.");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source).Where(p => Path.GetExtension(p) == ".dll"
            || Path.GetFileName(p) is "python.exe" or "LICENSE.txt"))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "DLLs")))
            if (Path.GetExtension(file) is ".dll" or ".pyd" || Path.GetFileName(file).Contains("LICENSE", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        File.Copy(Path.Combine(source, "Doc/html/license.html"), Path.Combine(target, "THIRD-PARTY-LICENSES.html"), true);
        string archivePath = Path.Combine(target, "stdlib.zip");
        using (var output = File.Create(archivePath))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
        {
            string library = Path.Combine(source, "Lib");
            foreach (string file in Directory.EnumerateFiles(library, "*.py", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(library, file).Replace('\\', '/');
                if (relative.Split('/').Any(part => part is "site-packages" or "__pycache__" or "test" or "tests" or "idlelib" or "tkinter" or "turtledemo" or "ensurepip")) continue;
                var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var input = File.OpenRead(file);
                using var destination = entry.Open();
                input.CopyTo(destination);
            }
        }
        File.WriteAllText(Path.Combine(target, "python._pth"), ".\nstdlib.zip\n", new UTF8Encoding(false));
        var smoke = ProcessRunner.Run(Path.Combine(target, "python.exe"), ["-I", "-S", "-B", "-c",
            "import json,xml.etree.ElementTree,ctypes,pathlib; print('decoder-runtime-ok')"], target);
        ProcessRunner.RequireSuccess(smoke, "Relocatable decoder runtime smoke failed");
        WriteJson(receipt, new JsonObject { ["tag"] = tag, ["version"] = string.Join(".", version), ["architecture"] = "x64",
            ["license"] = "LICENSE.txt", ["files"] = new JsonObject(Directory.EnumerateFiles(target).Where(p => p != receipt)
                .Order(StringComparer.Ordinal).Select(p => new KeyValuePair<string, JsonNode?>(Path.GetFileName(p), JsonValue.Create(ReleaseRecords.HashFile(p))))) });
        Console.WriteLine("Prepared isolated decoder CPython " + string.Join(".", version) + " with its original license at " + target);
        return 0;
    }
}
