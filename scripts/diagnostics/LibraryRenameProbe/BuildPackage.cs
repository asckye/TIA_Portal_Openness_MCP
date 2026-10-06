#!/usr/bin/env dotnet
// File-based package builder: dotnet run BuildPackage.cs -- [--output-directory path] [--sdk-directory path]
#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

var options = Parse(args);
string repository = FindRepository(Directory.GetCurrentDirectory());
string sourceDirectory = Path.Combine(repository, "scripts", "diagnostics", "LibraryRenameProbe");
string output = Path.GetFullPath(options.Output ?? Path.Combine(repository, "bin-build", "diagnostics",
    "LibraryRenameProbe-V21-r4-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
string zip = output + ".zip";
if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output must be a new directory; existing packages are never overwritten.");
if (File.Exists(zip) || Directory.Exists(zip)) throw new IOException("ZIP already exists: " + zip);

string project = Path.Combine(sourceDirectory, "LibraryRenameProbe.csproj");
string config = Path.Combine(Path.GetTempPath(), "tia-probe-nuget-" + Guid.NewGuid().ToString("N") + ".config");
try
{
    File.WriteAllText(config, "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><clear /></packageSources></configuration>");
    var restore = Start("dotnet", ["restore", project, "--configfile", config, "--ignore-failed-sources", "-p:NuGetAudit=false"]);
    if (restore.ExitCode != 0) throw new InvalidOperationException("Diagnostic probe restore failed (offline sources are cleared).\n" + restore.Output);
    var buildArguments = new List<string> { "build", project, "-c", "Release", "-o", output, "--nologo", "--no-restore" };
    if (options.SdkDirectory != null)
        buildArguments.Add("-p:SiemensEngineeringDirectory=" + Path.GetFullPath(options.SdkDirectory));
    var build = Start("dotnet", buildArguments);
    Console.WriteLine(build.Output);
    if (build.ExitCode != 0) throw new InvalidOperationException("Diagnostic probe build failed.");
    string executable = Path.Combine(output, "LibraryRenameProbe.exe");
    var selfTest = Start(executable, ["--self-test"]);
    Console.WriteLine(selfTest.Output);
    if (selfTest.ExitCode != 0) throw new InvalidOperationException("Offline checks failed; package was not created.");
    if (Directory.GetFiles(output, "Siemens*.dll", SearchOption.AllDirectories).Length != 0)
        throw new InvalidOperationException("Siemens redistributable found; refuse packaging.");

    File.Copy(Path.Combine(sourceDirectory, "README.md"), Path.Combine(output, "README.md"));
    string sourceCopy = Directory.CreateDirectory(Path.Combine(output, "source")).FullName;
    foreach (string name in new[] { "Program.cs", "Native.cs", "CampaignRunner.cs", "LibraryRenameProbe.csproj", "BuildPackage.cs" })
        File.Copy(Path.Combine(sourceDirectory, name), Path.Combine(sourceCopy, name));
    CopyEvidence(repository, output);

    var fileEntries = Directory.GetFiles(output, "*", SearchOption.AllDirectories)
        .Select(path => new { path = Path.GetRelativePath(output, path).Replace('\\', '/'), bytes = new FileInfo(path).Length, sha256 = Hash(path) })
        .ToArray();
    var manifest = new { schemaVersion = 1, packageRevision = 4, builtUtc = DateTime.UtcNow.ToString("o"),
        target = "net48 / x64 / TIA V21", nativeExecution = "NOT_RUN_BY_BUILD", files = fileEntries };
    File.WriteAllText(Path.Combine(output, "package-manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        foreach (string path in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
            archive.CreateEntryFromFile(path, Path.GetFileName(output) + "/" + Path.GetRelativePath(output, path).Replace('\\', '/'), CompressionLevel.Optimal);
    Console.WriteLine("Package: " + zip);
    Console.WriteLine("SHA256: " + Hash(zip));
}
finally { if (File.Exists(config)) File.Delete(config); }

static (string? Output, string? SdkDirectory) Parse(string[] arguments)
{
    string? output = null, sdk = null;
    for (int i = 0; i < arguments.Length; i++)
    {
        if (arguments[i] == "--output-directory" && i + 1 < arguments.Length) output = arguments[++i];
        else if (arguments[i] == "--sdk-directory" && i + 1 < arguments.Length) sdk = arguments[++i];
        else if (arguments[i] is "--help" or "-h")
        {
            Console.WriteLine("Build the standalone V21 probe ZIP. Options: --output-directory <new path> --sdk-directory <V21 net48 PublicAPI>");
            Environment.Exit(0);
        }
        else throw new ArgumentException("Unknown or incomplete option: " + arguments[i]);
    }
    return (output, sdk);
}

static string FindRepository(string start)
{
    for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "Version.props"))) return directory.FullName;
    throw new DirectoryNotFoundException("Run this app from inside the repository.");
}

static (int ExitCode, string Output) Start(string executable, IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
        RedirectStandardError = true, CreateNoWindow = true };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + executable);
    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
}

static void CopyEvidence(string repository, string output)
{
    var sets = new[]
    {
        (Manifest: "manifest/history/unified-library-rename-followup-20261001.json", Directory: "existing-mcp-evidence"),
        (Manifest: "manifest/history/library-rename-probe-first-vm-run-20261001.json", Directory: "first-vm-run-evidence"),
        (Manifest: "manifest/history/library-rename-probe-second-vm-run-20261001.json", Directory: "second-vm-run-evidence"),
    };
    foreach (var set in sets)
    {
        string manifestPath = Path.GetFullPath(Path.Combine(repository, set.Manifest));
        if (!File.Exists(manifestPath)) continue;
        string evidenceDirectory = Directory.CreateDirectory(Path.Combine(output, set.Directory)).FullName;
        File.Copy(manifestPath, Path.Combine(evidenceDirectory, Path.GetFileName(manifestPath)));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var index = new List<object>();
        foreach (var evidence in document.RootElement.GetProperty("evidence").EnumerateArray())
        {
            string relative = evidence.GetProperty("path").GetString()!;
            string file = Path.GetFullPath(Path.Combine(repository, relative));
            string boundary = Path.GetFullPath(repository).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!file.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Evidence path escaped repository.");
            string expected = evidence.GetProperty("sha256").GetString()!;
            if (!Hash(file).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Evidence hash changed: " + relative);
            File.Copy(file, Path.Combine(evidenceDirectory, Path.GetFileName(file)));
            index.Add(new { key = evidence.GetProperty("key").GetString(), file = Path.GetFileName(file), sha256 = expected });
        }
        File.WriteAllText(Path.Combine(evidenceDirectory, "index.json"), JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
    }
}

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}
