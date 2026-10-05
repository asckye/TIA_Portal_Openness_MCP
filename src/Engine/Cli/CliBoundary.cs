using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TiaMcp.Logic.V4;

namespace TiaMcpServer.Cli
{
    internal static class CliBoundary
    {
        private static readonly string[] Tools = { "gen", "patch", "compile", "export", "import", "describe" };
        private static readonly string[] Commands = { "gen", "patch", "compile", "export", "import", "describe", "prewarm", "config", "doctor", "schema", "version", "help", "--help", "-h" };
        private static readonly string[] Values = { "--plc", "--out", "--block", "--from", "--host", "--tia-portal-location", "--tia-version", "--tia-major-version", "--logging", "--profile", "--worker-timeout-seconds" };
        private static string Canonical(string option) => new[] { "-tia-major-version", "-tia-portal-location", "-profile", "-logging", "-with-ui" }.Contains(option.ToLowerInvariant()) ? "-" + option : option;

        internal static bool IsTool(string[] args) => args.Length > 0 && Tools.Contains(args[0].ToLowerInvariant());

        internal static bool TryValidate(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args.Length == 0 || args[0].StartsWith("-", StringComparison.Ordinal)
                && !args[0].Equals("--help", StringComparison.OrdinalIgnoreCase)
                && !args[0].Equals("-h", StringComparison.OrdinalIgnoreCase)) return false;
            try { Validate(args); return false; }
            catch (ArgumentException error)
            {
                exitCode = Failure(CliFailure.Syntax, error, Console.Error);
                return true;
            }
        }

        internal static void Validate(string[] args)
        {
            string verb = args[0].ToLowerInvariant();
            if (!Commands.Contains(verb)) throw new ArgumentException("Unknown command. Run tia help for supported commands.");
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            allowed.UnionWith(new[] { "--with-ui", "--tia-portal-location", "--tia-version", "--tia-major-version", "--logging", "--profile", "--full", "--lite", "--isolate-openness", "--worker-timeout-seconds" });
            switch (verb)
            {
                case "gen": allowed.UnionWith(new[] { "--dry-run", "--json" }); break;
                case "patch": allowed.UnionWith(new[] { "--dry-run", "--json", "--no-overwrite" }); break;
                case "compile": case "describe": allowed.UnionWith(new[] { "--plc", "--json" }); break;
                case "export": allowed.UnionWith(new[] { "--plc", "--out", "--block", "--scl", "--json" }); break;
                case "import": allowed.UnionWith(new[] { "--plc", "--from", "--no-overwrite", "--json" }); break;
                case "prewarm": allowed.Add("--stop"); break;
                case "doctor": allowed.Add("--fix"); break;
                case "config": allowed.UnionWith(new[] { "--host", "--print" }); break;
            }
            int paths = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("-", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(args[i])) throw new ArgumentException("The path must not be empty.");
                    paths++; continue;
                }
                string option = Canonical(args[i]).ToLowerInvariant();
                if (!allowed.Contains(option)) throw new ArgumentException("Unknown option for " + verb + ": " + args[i]);
                if (!seen.Add(option)) throw new ArgumentException("Duplicate option: " + args[i]);
                if (!Values.Contains(option)) continue;
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("-", StringComparison.Ordinal))
                    throw new ArgumentException("A value is required after " + option + ".");
                if (option == "--logging" && (!int.TryParse(args[i], out int logging) || logging < 0 || logging > 3))
                    throw new ArgumentException("--logging must be 0..3.");
                if (option == "--profile" && !args[i].Equals("full", StringComparison.OrdinalIgnoreCase)
                    && !args[i].Equals("lite", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--profile must be full or lite.");
            }
            if (paths != (Tools.Contains(verb) ? 1 : 0)) throw new ArgumentException("Expected " + (Tools.Contains(verb) ? "one path" : "no path") + " for " + verb + ".");
            foreach (string required in verb == "export" ? new[] { "--out", "--block" } : verb == "import" ? new[] { "--from" } : Array.Empty<string>())
                if (!seen.Contains(required)) throw new ArgumentException(verb + " requires " + required + ".");
            if (seen.Contains("--full") && seen.Contains("--lite")) throw new ArgumentException("--full and --lite conflict.");
            // The shared parser owns version selection and worker timeout validation.
            CliOptions.ParseArgs(args);
        }

        internal static string PathArgument(string[] args)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("-", StringComparison.Ordinal)) return args[i];
                if (Values.Contains(Canonical(args[i]).ToLowerInvariant())) i++;
            }
            throw new ArgumentException("A path argument is required.");
        }

        internal static int Failure(CliFailure failure, Exception error, TextWriter diagnostics)
        {
            diagnostics.WriteLine("ERROR: " + error.Message);
            return CliExitCode.From(failure);
        }

        internal static bool TryContextFailure(string[] args, Exception error, out int exitCode)
        {
            exitCode = 0;
            if (!IsTool(args)) return false;
            exitCode = Failure(CliFailure.ContextCreation, error, Console.Error);
            return true;
        }

        internal static int RunContext(string[] args, Action initialize, Func<int> run, TextWriter diagnostics)
        {
            var output = Console.Out;
            try
            {
                if (IsTool(args)) Console.SetOut(diagnostics);
                initialize();
            }
            catch (Exception error) when (IsTool(args)) { return Failure(CliFailure.ContextCreation, error, diagnostics); }
            finally { Console.SetOut(output); }
            return run();
        }

        internal static int Write(Envelope result, TextWriter output)
        {
            output.WriteLine(V4Json.Serialize(result));
            if (result.Meta.Outcome == Outcome.Succeeded)
            {
                if (result.Meta.Completeness == Completeness.Unknown) return 5;
                if (result.Meta.Completeness == Completeness.Partial) return 4;
                if (result.Meta.Completeness == Completeness.None) return 3;
            }
            return CliExitCode.From(result);
        }
        internal static Envelope Combine(string tool, IReadOnlyList<Envelope> values)
        {
            if (values.Count == 1) return values[0];
            if (values.Count == 0) throw new InvalidOperationException("A CLI tool command must return a result.");
            var unknown = values.FirstOrDefault(v => v.Meta.Outcome == Outcome.Unknown);
            int succeeded = values.Count(v => v.Ok);
            bool partial = values.Any(v => v.Meta.Outcome == Outcome.Partial) || succeeded > 0 && succeeded < values.Count;
            var failure = values.FirstOrDefault(v => v.Meta.Outcome == Outcome.Failed)
                ?? values.FirstOrDefault(v => v.Meta.Outcome == Outcome.ReadFailed) ?? values.FirstOrDefault(v => !v.Ok);
            var outcome = unknown != null ? Outcome.Unknown : partial ? Outcome.Partial : failure?.Meta.Outcome ?? Outcome.Succeeded;
            var error = unknown?.Error ?? (partial ? new Error("Some operations did not complete successfully.",
                new PartialFailureDetails(succeeded, succeeded == 0 ? 0 : values.Count - succeeded, 0)) : failure?.Error);
            var completeness = values.Any(v => v.Meta.Completeness == Completeness.Unknown) ? Completeness.Unknown
                : values.All(v => v.Meta.Completeness == Completeness.Complete) ? Completeness.Complete
                : values.All(v => v.Meta.Completeness == Completeness.None) ? Completeness.None : Completeness.Partial;
            var execution = outcome == Outcome.Unknown ? Execution.Unknown : outcome == Outcome.Partial ? Execution.Partial
                : failure != null ? failure.Meta.Execution : values.Any(v => v.Meta.Execution == Execution.Completed) ? Execution.Completed : Execution.ReadOnly;
            var warnings = values.SelectMany(v => v.Meta.Warnings).ToList();
            if (completeness == Completeness.Partial && warnings.Count == 0)
                warnings.Add(new Warning(WarningCode.IncompleteData, "The combined observation is incomplete.", new Dictionary<string, JsonElement>()));
            var meta = new Meta(DateTimeOffset.UtcNow, values[0].Meta.ReleaseKey, tool, Meta.Correlate(null), outcome, execution,
                values.Any(v => v.Meta.RequiresSessionReset), BehaviorPolicy.NotApplicable, completeness, null, warnings);
            return Envelope.Create(new BatchData(values.Select((v, i) => new BatchItem(i, v.Meta.Tool, v)).ToArray()), error, meta);
        }
    }
}
