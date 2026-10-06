using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TiaOpenness.Shared;

namespace TiaMcpServer.Cli
{
    internal static class InstallPlcToolsCommand
    {
        // Kept in sync with the external requirements of the pinned companion packages.
        internal static readonly string[] ExternalPackages =
        {
            "asyncua>=1.1.0",
            "click>=8.0",
            "click>=8.1.0",
            "fastapi>=0.109.0",
            "fastapi>=0.128.0",
            "httpx>=0.27.0",
            "jinja2>=3.0.0",
            "mkdocs-material>=9.7.0",
            "mkdocs>=1.6.1",
            "mkdocstrings[python]>=0.24.0",
            "msgpack>=1.0.0",
            "openpyxl>=3.1.0",
            "psycopg[binary]>=3.1.0",
            "pydantic>=2.0.0",
            "pydantic>=2.6.0",
            "pymodbus>=3.6,<4.0",
            "python-docx>=1.0.0",
            "pyyaml>=6.0.0",
            "redis>=5.0.0",
            "rich>=13.0",
            "rich>=13.0.0",
            "scapy>=2.5",
            "uvicorn[standard]>=0.27.0",
            "uvicorn[standard]>=0.40.0",
        };

        private static readonly string[] ToolingPackages = { "pytest", "pytest-asyncio", "pytest-cov", "reportlab" };

        internal static int Run(string[] args)
        {
            string python = Option(args, "--python") ?? "python";
            string? suppliedEnvironment = Option(args, "--environment-path");
            string? localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(suppliedEnvironment) && string.IsNullOrWhiteSpace(localAppData))
            {
                Console.Error.WriteLine("IO_FAILED: LocalAppData is unavailable: TiaMcp/ecosystem-python");
                return 2;
            }
            string environment = suppliedEnvironment ?? Path.Combine(localAppData!, "TiaMcp", "ecosystem-python");
            if (!Path.IsPathRooted(environment))
            {
                Console.Error.WriteLine("IO_FAILED: --environment-path must be absolute: " + environment);
                return 2;
            }
            environment = Path.GetFullPath(environment);

            try
            {
                Directory.CreateDirectory(environment);
                string probePath = Path.Combine(environment, ".write-probe-" + Guid.NewGuid().ToString("N"));
                try
                {
                    using (var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) probe.WriteByte(0);
                }
                finally { if (File.Exists(probePath)) File.Delete(probePath); }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("IO_FAILED: Python environment is not writable: " + environment + "; " + ex.Message);
                return 2;
            }

            int versionResult;
            try { versionResult = RunProcess(python, new[] { "-c", "import sys; assert sys.version_info >= (3,12), \"Python 3.12+ required\"" }); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
            if (versionResult != 0) { Console.Error.WriteLine("Unsupported Python"); return 2; }

            int venvResult;
            try { venvResult = RunProcess(python, new[] { "-m", "venv", environment }); }
            catch (Exception ex) { Console.Error.WriteLine("IO_FAILED: venv creation failed: " + environment + "; " + ex.Message); return 2; }
            if (venvResult != 0) { Console.Error.WriteLine("IO_FAILED: venv creation failed: " + environment); return 2; }

            string executable = Path.Combine(environment, "Scripts", "python.exe");
            string[] packages = ExternalPackages.Concat(ToolingPackages).ToArray();
            int pipResult;
            try { pipResult = RunProcess(executable, new[] { "-m", "pip", "install" }.Concat(packages).ToArray()); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
            if (pipResult != 0) { Console.Error.WriteLine("PLC Tools dependency installation failed"); return pipResult; }

            Console.WriteLine("Installed. Set TIA_MCP_PLC_TOOLS_PYTHON=" + executable + " in the MCP server environment.");
            Console.WriteLine("This command installs local tooling only; it does not connect to any PLC or TIA project.");
            return 0;
        }

        private static int RunProcess(string executable, string[] arguments)
        {
            string commandLine = string.Join(" ", arguments.Select(ProcessArguments.Quote));
            var start = new ProcessStartInfo(executable, commandLine)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            using (var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + executable))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        private static string? Option(string[] args, string name)
        {
            for (int index = 1; index + 1 < args.Length; index++)
                if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            return null;
        }
    }
}
