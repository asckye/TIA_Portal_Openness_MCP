using System.Text.Json;
using System.Runtime.InteropServices;

namespace TiaMcp.ReleaseTool;

internal sealed record SuiteWorkerRuntime(string Assembly, string Fixtures, string RegisteredOutput);

// Only the audited Foundation assembly is splittable. Other assemblies retain
// their shared-output groups until their writable inputs have been isolated.
internal sealed class SuiteWorkerCopies(string output)
{
    // Third-party loader inputs only. Product/test assemblies, configs and data
    // remain private because fixtures rewrite their identities and contents.
    internal static bool Immutable(string relative) => relative.IndexOfAny(['\\', '/']) < 0 && Path.GetFileName(relative) is
        "System.Text.Json.dll" or "System.Text.Encodings.Web.dll" or "System.IO.Pipelines.dll" or "Newtonsoft.Json.dll" or
        "System.Memory.dll" or "System.Buffers.dll" or "System.Numerics.Vectors.dll" or "System.Threading.Tasks.Extensions.dll" or
        "System.Runtime.CompilerServices.Unsafe.dll" or "Microsoft.Bcl.AsyncInterfaces.dll" or "xunit.assert.dll" or "xunit.core.dll";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string name);

    internal static bool TryLink(string source, string destination)
    {
        try { return OperatingSystem.IsWindows() ? CreateHardLinkW(destination, source, IntPtr.Zero) : Link(source, destination) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException) { return false; }
    }

    internal static string CopyFile(string source, string destination, string relative, Func<string, string, bool>? link = null)
    {
        if (Immutable(relative) && (link ?? TryLink)(source, destination)) return "hard-link";
        File.Copy(source, destination);
        return "copy";
    }

    internal const string FoundationProject = "tests/FoundationHost/TiaMcp.FoundationHost.Tests/TiaMcp.FoundationHost.Tests.csproj";
    private readonly Dictionary<string, (CheckBuild Unit, string Results)> requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SuiteWorkerRuntime> runtimes = new(StringComparer.Ordinal);

    internal string Register(string job, CheckBuild unit, string results, string group)
    {
        if (unit.Project != FoundationProject) return group;
        requests.Add(job, (unit, results));
        return unit.Project + "#isolated-" + job;
    }

    internal SuiteWorkerRuntime? ForJob(string job) => !requests.ContainsKey(job) ? null : runtimes.TryGetValue(job, out var runtime)
        ? runtime : throw new ReleaseException("Foundation worker copy was not prepared: " + job);

    internal void Prepare(string root, IReadOnlyList<CheckJob> jobs, string manifest)
    {
        // Keep copies in the tested checkout, including -Clean snapshots. Source
        // discovery must never walk back into the reporting/original worktree.
        var directory = Path.Combine(root, "bin-build/w", Guid.NewGuid().ToString("N")[..8]);
        var copies = new Dictionary<string, SuiteWorkerRuntime>(StringComparer.Ordinal);
        var inputsBySource = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var files = new List<object>(); var assignments = new List<object>();
        var fixtureUnits = CheckAllBuild.WithTestFixtures([new(FoundationProject, "", [])])
            .Where(unit => unit.Project != FoundationProject).ToArray();
        int next = 0;
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs.Where(job => requests.ContainsKey(job.Name) && job.CiCoverage == null))
        {
            var request = requests[job.Name];
            if (!results.Add(Path.GetFullPath(request.Results))) throw new ReleaseException("Colliding Foundation results: " + request.Results);
            // A sensitive lane has capacity one; ordinary shards can overlap,
            // so each ordinary shard needs its own writable assembly directory.
            string owner = job.Lane.StartsWith("sensitive-", StringComparison.Ordinal) ? job.Lane : job.Name;
            string key = owner + "|" + request.Unit.Variant;
            if (!copies.TryGetValue(key, out var runtime))
            {
                var worker = Path.Combine(directory, (next++).ToString(System.Globalization.CultureInfo.InvariantCulture));
                var assembly = CheckAllBuild.TestAssembly(root, request.Unit, manifest, job.Name)!;
                var target = Path.Combine(worker, "a");
                Copy(Path.GetDirectoryName(assembly)!, target);
                var fixtures = Path.Combine(worker, "f");
                foreach (var unit in fixtureUnits)
                {
                    var fixture = CheckAllBuild.TestAssembly(root, unit, manifest, job.Name)!;
                    Copy(Path.GetDirectoryName(fixture)!, Path.Combine(fixtures, Path.GetFileNameWithoutExtension(unit.Project)));
                }
                runtime = new(Path.Combine(target, Path.GetFileName(assembly)), fixtures, assembly);
                copies.Add(key, runtime);
            }
            runtimes.Add(job.Name, runtime);
            assignments.Add(new { job.Name, job.Lane, job.SensitiveGroup, runtime.Assembly, runtime.Fixtures,
                runtime.RegisteredOutput, results = request.Results });
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "suite-worker-copies.json"), JsonSerializer.Serialize(new { schemaVersion = 1,
            directory, assignments, files }, new JsonSerializerOptions { WriteIndented = true }) + "\n");

        void Copy(string source, string target)
        {
            ReleaseCommands.RequireCheckAllOwnedPath(root, source);
            ReleaseCommands.RequireCheckAllOwnedPath(root, target);
            if (Directory.Exists(target)) throw new ReleaseException("Worker copy must be fresh: " + target);
            if (!inputsBySource.TryGetValue(source, out var inputs))
            {
                inputs = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                    .ToDictionary(path => path, ReleaseRecords.HashFile, StringComparer.Ordinal);
                inputsBySource.Add(source, inputs);
            }
            Directory.CreateDirectory(target);
            var copied = new object[inputs.Count];
            // Each target file is private. Bound the independent copy/hash I/O
            // without serializing thousands of small file opens before dispatch.
            Parallel.ForEach(inputs.Select((pair, index) => (pair, index)), new ParallelOptions { MaxDegreeOfParallelism = 4 }, item =>
            {
                var (input, hash) = item.pair;
                string relative = Path.GetRelativePath(source, input), destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var method = CopyFile(input, destination, relative);
                if (hash != ReleaseRecords.HashFile(destination) || hash != ReleaseRecords.HashFile(input))
                    throw new ReleaseException("Worker copy differs from built input: " + input);
                copied[item.index] = new { source = input, destination, relative, method, sha256 = hash, bytes = new FileInfo(input).Length };
            });
            files.AddRange(copied);
            if (Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length != inputs.Count ||
                !Directory.GetFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).SequenceEqual(inputs.Keys))
                throw new ReleaseException("Worker copy file inventory differs: " + target);
        }
    }
}
