using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Logic.ModelContextProtocol;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    internal const string DecoderPythonVariable = "TIA_MCP_DECODER_PYTHON";
    internal const string DecoderRuntimeVariable = "TIA_MCP_DECODER_RUNTIME";

    internal static void ConfigureDecoderTestEnvironment(IDictionary<string, string?> environment, string root, Options options)
    {
        environment[DecoderRuntimeVariable] = Path.GetFullPath(Path.Combine(root, DecoderRuntime.RelativeDirectory));
        environment[DecoderPythonVariable] = options.Get("Python") ?? Environment.GetEnvironmentVariable(DecoderPythonVariable);
        environment["TIA_MCP_TEST_DECODER_PREPARE_TOOL"] = typeof(ReleaseCommands).Assembly.Location;
    }

    internal static Options DecoderPythonOptions(Options options, Func<string, string?>? environment = null)
    {
        string? python = options.Get("Python") ?? (environment ?? Environment.GetEnvironmentVariable)(DecoderPythonVariable);
        if (string.IsNullOrWhiteSpace(python))
            throw new ReleaseException("Pinned decoder CPython " + DecoderRuntime.Version + " x64 is required. Pass -Python <absolute-python.exe> or set " + DecoderPythonVariable + ". No PATH fallback is used.");
        return Options.Parse([.. options.Arguments(), "-Python=" + python], new HashSet<string>());
    }

    // Build a relocatable, isolated stdlib-only runtime from the explicitly selected
    // release-machine CPython. Never copy a venv or any site-packages into delivery.
    internal static int PrepareDecoderPython(Options options)
        => PrepareDecoderPython(options, Root);

    internal static int PrepareDecoderPython(Options options, string bundleRoot)
    {
        EnsureWindows("prepare-decoder-python");
        string? python = options.Get("Python");
        if (string.IsNullOrEmpty(python) || !Path.IsPathFullyQualified(python) || !File.Exists(python))
            throw new ReleaseException("Specify -Python as the absolute path to pinned CPython " + DecoderRuntime.Version + ".");
        if (DecoderRuntime.Hash(python) != DecoderRuntime.InterpreterSha256)
            throw new ReleaseException("Decoder interpreter SHA-256 does not match the pinned CPython release.");
        // Standalone fixture processes can share a checkout. Only one may publish
        // its runtime; a fixture reuses the complete, validated build prerequisite.
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(bundleRoot).ToUpperInvariant())));
        using var preparation = new Mutex(false, "Local\\TiaMcp.DecoderRuntime." + identity);
        try { preparation.WaitOne(); }
        catch (AbandonedMutexException) { }
        try
        {
            if (options.Has("Reuse"))
            {
                try
                {
                    DecoderRuntime.Require(bundleRoot);
                    Console.WriteLine("Verified existing isolated decoder CPython " + DecoderRuntime.Version + ".");
                    return 0;
                }
                catch (DecoderRuntimeException) { }
            }
            return PrepareDecoderPythonPayload(python, bundleRoot);
        }
        finally { preparation.ReleaseMutex(); }
    }

    private static int PrepareDecoderPythonPayload(string python, string bundleRoot)
    {
        var probe = ProcessRunner.Run(python, ["-I", "-S", "-X", "utf8", "-c",
            "import sys,json; print(json.dumps(dict(root=sys.base_prefix,version=list(sys.version_info[:3]),bits=64 if sys.maxsize>2**32 else 32)))"], bundleRoot);
        ProcessRunner.RequireSuccess(probe, "CPython runtime probe failed");
        var info = JsonNode.Parse(probe.StandardOutput)!.AsObject();
        var version = info["version"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
        if (string.Join(".", version) != DecoderRuntime.Version || info["bits"]!.GetValue<int>() != 64)
            throw new ReleaseException("Decoder delivery requires pinned 64-bit CPython " + DecoderRuntime.Version + ".");
        string source = Path.GetFullPath((string)info["root"]!);
        string target = Path.GetFullPath(Path.Combine(bundleRoot, DecoderRuntime.RelativeDirectory));
        string root = Path.GetFullPath(bundleRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || source.Equals(target, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException("Use the release-machine CPython outside the decoder output directory.");
        string tag = "python" + version[0] + version[1];
        string Binary(string name) => File.Exists(Path.Combine(source, name)) ? Path.Combine(source, name) : Path.Combine(source, "DLLs", name);
        var inputs = DecoderRuntime.BinaryFiles.Select(Binary)
            .Concat(DecoderRuntime.StandardLibraryFiles.Select(name => Path.Combine(source, "Lib", name)))
            .Concat(new[] { Path.Combine(source, "LICENSE.txt"), Path.Combine(source, "Doc/html/license.html") });
        foreach (string input in inputs)
            if (!File.Exists(input)) throw new ReleaseException("Incomplete pinned CPython installation: " + Path.GetFileName(input));
        if (DecoderRuntime.Hash(Binary("python312.dll")) != DecoderRuntime.CoreSha256)
            throw new ReleaseException("Decoder core SHA-256 does not match the pinned CPython release.");
        string receipt = Path.Combine(target, "runtime.json");
        // Verify this exact worktree output, including every directory, before recursive cleanup.
        if (Directory.Exists(Path.GetDirectoryName(target))
            && (File.GetAttributes(Path.GetDirectoryName(target)!) & FileAttributes.ReparsePoint) != 0)
            throw new ReleaseException("Decoder output parent must not be a directory link.");
        if (Directory.Exists(target))
        {
            var pending = new Stack<string>();
            pending.Push(target);
            while (pending.TryPop(out string? directory))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new ReleaseException("Decoder output must not contain directory links.");
                foreach (string child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            }
            Directory.Delete(target, true);
        }
        Directory.CreateDirectory(target);
        foreach (string name in DecoderRuntime.BinaryFiles) File.Copy(Binary(name), Path.Combine(target, name));
        File.Copy(Path.Combine(source, "LICENSE.txt"), Path.Combine(target, "LICENSE.txt"));
        File.Copy(Path.Combine(source, "Doc/html/license.html"), Path.Combine(target, "THIRD-PARTY-LICENSES.html"), true);
        string archivePath = Path.Combine(target, "stdlib.zip");
        using (var output = File.Create(archivePath))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
        {
            string library = Path.Combine(source, "Lib");
            foreach (string relative in DecoderRuntime.StandardLibraryFiles)
            {
                var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var input = File.OpenRead(Path.Combine(library, relative));
                using var destination = entry.Open();
                input.CopyTo(destination);
            }
        }
        File.WriteAllText(Path.Combine(target, "python._pth"), ".\nstdlib.zip\n", new UTF8Encoding(false));
        WriteJson(receipt, new JsonObject { ["tag"] = tag, ["version"] = string.Join(".", version), ["architecture"] = "x64",
            ["license"] = "LICENSE.txt", ["interpreterSha256"] = DecoderRuntime.InterpreterSha256,
            ["files"] = new JsonObject(Directory.EnumerateFiles(target).Where(p => p != receipt)
                .Order(StringComparer.Ordinal).Select(p => new KeyValuePair<string, JsonNode?>(Path.GetFileName(p), JsonValue.Create(FileHash.Sha256(p))))) });
        var smoke = ProcessRunner.Run(DecoderRuntime.Require(bundleRoot), ["-I", "-S", "-B", "-X", "utf8", "-c",
            "import json,xml.etree.ElementTree,pathlib; print('decoder-runtime-ok')"], target);
        ProcessRunner.RequireSuccess(smoke, "Relocatable decoder runtime smoke failed");
        Console.WriteLine("Prepared isolated decoder CPython " + string.Join(".", version) + " with its original license at " + target);
        return 0;
    }
}
