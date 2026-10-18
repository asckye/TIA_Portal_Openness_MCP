using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal static class PackageDependencies
{
    private static readonly Lazy<HashSet<string>> Framework = new(FrameworkAssemblies);

    internal static IReadOnlyList<string> Check(string root, Func<string, bool> delivered)
    {
        var errors = new List<string>();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(Path.Combine(root, "runtime"))
                ? Directory.EnumerateFiles(Path.Combine(root, "runtime"), "*", SearchOption.AllDirectories) : [])
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(delivered).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var framework = Framework.Value;
        foreach (var program in files.Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                     !path.StartsWith("runtime/dotnet/", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            try { CheckProgram(root, program, files, framework, errors); }
            catch (Exception ex) { errors.Add($"Dependency closure: {program}: {ex.Message}"); }
        }
        return errors;
    }

    private static void CheckProgram(string root, string program, HashSet<string> files,
        HashSet<string> framework, List<string> errors)
    {
        var directory = Path.GetDirectoryName(program)!.Replace('\\', '/');
        string Local(string relative) => directory.Length == 0 ? relative : directory + "/" + relative;
        var runtimeConfig = Path.ChangeExtension(program, ".runtimeconfig.json");
        var modern = files.Contains(runtimeConfig);
        var shared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (modern)
        {
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, runtimeConfig)));
            var runtimeOptions = config.RootElement.GetProperty("runtimeOptions");
            var frameworks = new List<JsonElement>();
            if (runtimeOptions.TryGetProperty("framework", out var single)) frameworks.Add(single);
            if (runtimeOptions.TryGetProperty("frameworks", out var many)) frameworks.AddRange(many.EnumerateArray());
            foreach (var item in frameworks)
            {
                var name = item.GetProperty("name").GetString()!;
                var requested = Version.Parse(item.GetProperty("version").GetString()!);
                var prefix = "runtime/dotnet/shared/" + name + "/";
                var version = files.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(path => path[prefix.Length..].Split('/')[0]).Distinct()
                    .Select(value => Version.TryParse(value, out var parsed) ? parsed : null)
                    .Where(value => value is not null && value.Major == requested.Major && value >= requested)
                    .OrderDescending().FirstOrDefault();
                if (version is null) { errors.Add($"Dependency closure: {program}: missing shared framework {name}/{requested}"); continue; }
                foreach (var path in files.Where(path => path.StartsWith(prefix + version + "/", StringComparison.OrdinalIgnoreCase) &&
                             path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    shared[Path.GetFileNameWithoutExtension(path)] = path;
            }
        }

        var probes = new List<string> { directory };
        var configPath = program + ".config";
        if (!modern && files.Contains(configPath))
        {
            var config = XDocument.Load(Path.Combine(root, configPath));
            foreach (var value in config.Descendants().Where(node => node.Name.LocalName == "probing")
                         .SelectMany(node => ((string?)node.Attribute("privatePath") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)))
            {
                var path = Path.GetFullPath(Path.Combine(root, directory, value));
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                    throw new ReleaseException("Assembly probing path escapes bundle: " + value);
                probes.Add(relative);
            }
        }

        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runtimeAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var embedded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        void RequirePath(string path)
        {
            if (files.Contains(path)) queue.Enqueue(path);
            else errors.Add($"Dependency closure: {program}: missing assembly {Path.GetFileNameWithoutExtension(path)} at {path}");
        }
        void Require(string name, AssemblyName? reference = null)
        {
            if (!modern && reference is not null && InstalledAssembly(reference, framework)) return;
            if (embedded.ContainsKey(name)) { queue.Enqueue("embedded:" + name); return; }
            if (runtimeAssets.TryGetValue(name, out var assetPath)) { RequirePath(assetPath); return; }
            foreach (var probe in probes)
                foreach (var extension in new[] { ".dll", ".exe" })
                {
                    var path = probe.Length == 0 ? name + extension : probe + "/" + name + extension;
                    if (files.Contains(path)) { queue.Enqueue(path); return; }
                    if (modern) continue;
                    path = probe.Length == 0 ? name + "/" + name + extension : probe + "/" + name + "/" + name + extension;
                    if (files.Contains(path)) { queue.Enqueue(path); return; }
                }
            if (modern && shared.ContainsKey(name)) return;
            errors.Add($"Dependency closure: {program}: missing assembly {name} (probes: {string.Join(", ", probes)})");
        }

        var entry = program;
        if (ReadReferences(Path.Combine(root, program)) is not null) queue.Enqueue(program);
        else
        {
            var assembly = Path.ChangeExtension(program, ".dll");
            if (!files.Contains(assembly)) { errors.Add($"Dependency closure: {program}: missing entry assembly {assembly}"); return; }
            entry = assembly;
            queue.Enqueue(assembly);
        }
        if (OpennessGroupRuntime.IsEntryAssembly(Path.Combine(root, entry)))
            errors.AddRange(OpennessGroupRuntime.Inspect(Path.Combine(root, entry)).Errors);
        // Retain the general embedded-dependency closure check and its regression fixtures.
        // PlcWorker/host entries now additionally reject the retired group runtime above.
        foreach (var (name, bytes) in EmbeddedRuntime(Path.Combine(root, entry))) embedded.Add(name, bytes);
        var depsPath = Path.ChangeExtension(program, ".deps.json");
        if (modern && !files.Contains(depsPath)) errors.Add($"Dependency closure: {program}: missing {depsPath}");
        if (files.Contains(depsPath))
        {
            using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, depsPath)));
            var target = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
            foreach (var library in deps.RootElement.GetProperty("targets").GetProperty(target).EnumerateObject())
            {
                if (library.Value.TryGetProperty("runtime", out var assets))
                    foreach (var asset in assets.EnumerateObject())
                        if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) Require(Path.GetFileNameWithoutExtension(asset.Name));
                if (library.Value.TryGetProperty("runtimeTargets", out var runtimeTargets))
                    foreach (var asset in runtimeTargets.EnumerateObject())
                        if (asset.Value.GetProperty("assetType").GetString() == "runtime" &&
                            asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                            asset.Value.GetProperty("rid").GetString() is "win" or "win-x64")
                        {
                            var name = Path.GetFileNameWithoutExtension(asset.Name);
                            var nested = Local(asset.Name);
                            runtimeAssets[name] = files.Contains(nested) ? nested : Local(Path.GetFileName(asset.Name));
                            Require(name);
                        }
                if (library.Value.TryGetProperty("resources", out var resources))
                    foreach (var asset in resources.EnumerateObject())
                        RequirePath(Local(asset.Value.GetProperty("locale").GetString() + "/" + Path.GetFileName(asset.Name)));
            }
        }
        while (queue.TryDequeue(out var path))
        {
            if (!visited.Add(path)) continue;
            AssemblyName[]? references;
            if (path.StartsWith("embedded:", StringComparison.Ordinal))
            {
                using var stream = new MemoryStream(embedded[path["embedded:".Length..]], writable: false);
                references = ReadReferences(stream);
            }
            else references = ReadReferences(Path.Combine(root, path));
            if (references is null) throw new ReleaseException("Not a managed assembly: " + path);
            foreach (var reference in references) Require(reference.Name!, reference);
        }
    }

    private static AssemblyName[]? ReadReferences(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadReferences(stream);
    }

    private static AssemblyName[]? ReadReferences(Stream stream)
    {
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return null;
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences.Select(handle =>
        {
            var row = metadata.GetAssemblyReference(handle);
            var name = new AssemblyName { Name = metadata.GetString(row.Name), Version = row.Version,
                CultureName = metadata.GetString(row.Culture) };
            var key = metadata.GetBlobBytes(row.PublicKeyOrToken);
            if ((row.Flags & AssemblyFlags.PublicKey) != 0) name.SetPublicKey(key); else name.SetPublicKeyToken(key);
            return name;
        }).ToArray();
    }

    private static Dictionary<string, byte[]> EmbeddedRuntime(string path)
    {
        const string prefix = "ProjectGroupRuntime.";
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            var name = metadata.GetString(resource.Name);
            if (!resource.Implementation.IsNil || !name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            var block = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
            var offset = checked((int)resource.Offset);
            var length = block.GetReader(offset, 4).ReadInt32();
            result.Add(name[prefix.Length..^4], block.GetContent(offset + 4, length).ToArray());
        }
        return result;
    }

    private static string Identity(AssemblyName name) => name.Name + "/" +
        Convert.ToHexStringLower(name.GetPublicKeyToken() ?? []);

    internal static bool InstalledAssembly(AssemblyName name, IReadOnlySet<string> installed) =>
        installed.Contains(Identity(name)) || installed.Contains(name.FullName) || InstalledOpenness(Identity(name));

    private static HashSet<string> FrameworkAssemblies()
    {
        var assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mscorlib/b77a5c561934e089" };
        var reference = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8");
        var gac = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET/assembly");
        foreach (var directory in new[] { reference, gac })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
            {
                using var stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                if (pe.HasMetadata && pe.GetMetadataReader().IsAssembly)
                {
                    var name = AssemblyName.GetAssemblyName(file);
                    assemblies.Add(directory == gac ? name.FullName : Identity(name));
                }
            }
        }
        return assemblies;
    }

    // Workers resolve these vendor identities from the selected TIA installation's
    // PublicAPI directory. They are installation prerequisites, never redistributable SDK files.
    private static bool InstalledOpenness(string identity) => identity is
        "Siemens.Engineering/d29ec89bac048f84" or "Siemens.Engineering.Base/29bfe5fdf4ba5d3b" or
        "Siemens.Engineering.Step7/29bfe5fdf4ba5d3b" or "Siemens.Engineering.WinCC/29bfe5fdf4ba5d3b" or
        "Siemens.Engineering.WinCCUnified/29bfe5fdf4ba5d3b" or "Siemens.Engineering.Safety/29bfe5fdf4ba5d3b" or
        "Siemens.Engineering.CFC/29bfe5fdf4ba5d3b" or "Siemens.Engineering.TestSuite/29bfe5fdf4ba5d3b" or
        "Siemens.Engineering.WinCC.Extension/29bfe5fdf4ba5d3b" or "Siemens.Engineering.TeamcenterGateway/29bfe5fdf4ba5d3b" or
        "Siemens.Engineering.SafetyValidation/29bfe5fdf4ba5d3b" or "Siemens.Engineering.DCC/29bfe5fdf4ba5d3b" or
        "Siemens.Engineering.Startdrive/29bfe5fdf4ba5d3b" or "Siemens.Engineering.Sivarc/29bfe5fdf4ba5d3b";
}
