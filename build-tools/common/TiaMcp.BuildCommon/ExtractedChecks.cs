using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace TiaMcp.BuildCommon;

// Compile the current production members into a managed stand-in with no package sources.
public static class ExtractedChecks
{
    public static int Run(string root, string work, string program, string[]? links = null, string[]? copies = null)
    {
        work = Path.GetFullPath(work);
        var relative = Path.GetRelativePath(root, work);
        if (relative == "." || Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Contains("..")) throw new ArgumentException("Fixture directory must be inside the worktree");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "Program.cs"), program, new UTF8Encoding(false));
        foreach (var name in copies ?? []) File.Copy(Path.Combine(root, name), Path.Combine(work, Path.GetFileName(name)), true);
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), new XElement("PropertyGroup",
            new XElement("OutputType", "Exe"), new XElement("TargetFramework", "net10.0"), new XElement("Nullable", "enable"), new XElement("NuGetAudit", "false")));
        if (links is { Length: > 0 }) project.Add(new XElement("ItemGroup", links.Select(p => new XElement("Compile", new XAttribute("Include", Path.Combine(root, p))))));
        var path = Path.Combine(work, "Checks.csproj");
        File.WriteAllText(path, project.ToString(), new UTF8Encoding(false));
        var config = Path.Combine(work, "NuGet.Config");
        File.WriteAllText(config, "<configuration><packageSources><clear /></packageSources></configuration>", new UTF8Encoding(false));
        int Execute(params string[] args)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
            start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit(); Console.Write(output.GetAwaiter().GetResult()); Console.Error.Write(error.GetAwaiter().GetResult());
            return process.ExitCode;
        }
        var restored = Execute("restore", path, "--configfile", config, "-m:1", "-nodeReuse:false");
        return restored == 0 ? Execute("run", "--project", path, "-c", "Release", "--no-restore", "-p:UseSharedCompilation=false", "--", work) : restored;
    }
}
