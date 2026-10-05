using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public sealed class GitWorkflowTools
    {
        [McpServerTool(Name = "ManagePlcGitRepository"), Description("[L2][VersionControl][FILE] Git workflow for a local PLC export/VCI repository: status, history, diff, show, stage or commit. Calls git.exe directly without a shell. Read actions never fetch/pull/push or change branches. show requires a revision and exactly one relative file path. stage/commit require an explicit filesJson array and dryRun=false; commit includes only selected files using --only, never unrelated staged content. A commit can run repository Git hooks; commit failure may leave selected paths staged. No remote publication or TIA actions. For graphical comparison export two revisions to files and use RenderPlcVisualDiff.")]
        public async Task<ResponseMessage> ManagePlcGitRepository([Description("Existing absolute Git working tree directory for exported PLC files.")] string repositoryPath, [Description("status | history | diff | show | stage | commit. status/history/diff/show/stage/commit.")] string action = "status", [Description("JSON array of explicit repository-relative file paths.")] string filesJson = "[]", [Description("Git revision used by show, default HEAD.")] string revision = "HEAD", [Description("Commit message; required for commit.")] string message = "", [Description("true previews without executing the requested write/action; false executes.")] bool dryRun = true, [Description("Maximum returned items; see tool limits.")] int limit = 30)
        {
            var meta = ResponseMeta.Unstamped(false, ("action", action), ("offlineOnly", true));
            try
            {
                if (!Path.IsPathRooted(repositoryPath) || !Directory.Exists(repositoryPath)) throw new ArgumentException("repositoryPath must exist and be absolute.");
                if (limit < 1 || limit > 200) throw new ArgumentException("limit must be 1..200.");
                var nodes = JsonNode.Parse(filesJson) as JsonArray ?? throw new ArgumentException("filesJson must be an array.");
                if (nodes.Count > 500) throw new ArgumentException("At most 500 selected files.");
                var paths = nodes.Select(n => n?.GetValue<string>() ?? "").ToList();
                string root = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (var path in paths)
                    if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || !Path.GetFullPath(Path.Combine(root, path)).StartsWith(root, StringComparison.OrdinalIgnoreCase) || Directory.Exists(Path.Combine(root, path))) throw new ArgumentException("Select individual repository-relative files, no directories/traversal.");
                var prefix = new[] { "--no-pager", "--literal-pathspecs", "-c", "color.ui=false", "-c", "core.quotepath=false" };
                var args = new List<string>(prefix); string? input = null;
                switch (action)
                {
                    case "status": args.AddRange(new[] { "status", "--porcelain=v1", "--untracked-files=all" }); break;
                    case "history": args.AddRange(new[] { "log", "-n", limit.ToString(), "--format=%H%x09%aI%x09%an%x09%s" }); break;
                    case "diff": args.AddRange(new[] { "diff", "--no-ext-diff", "--no-textconv", "HEAD", "--" }); args.AddRange(paths); break;
                    case "show":
                        if (paths.Count != 1 || revision.Length > 128 || !System.Text.RegularExpressions.Regex.IsMatch(revision, @"\A[A-Za-z0-9][A-Za-z0-9_./~^{}@-]*\z")) throw new ArgumentException("show needs exactly one file and a safe revision name/hash.");
                        args.AddRange(new[] { "show", "--no-ext-diff", "--no-textconv", revision + ":" + paths[0].Replace('\\', '/') }); break;
                    case "stage": case "commit":
                        if (paths.Count == 0) throw new ArgumentException("Select files explicitly.");
                        if (action == "commit" && (string.IsNullOrWhiteSpace(message) || message.Length > 10000)) throw new ArgumentException("A commit message of 1..10000 characters is required.");
                        args.AddRange(new[] { "add", "-A", "--" }); args.AddRange(paths); break;
                    default: throw new ArgumentException("action must be status/history/diff/show/stage/commit.");
                }
                meta["selectedFiles"] = nodes.DeepClone();
                // envelope: legacy-single-verdict
                if ((action == "stage" || action == "commit") && dryRun) { meta["success"] = true; meta["executed"] = false; return new ResponseMessage { Message = "Git change preview; selected files only.", Meta = meta }; }
                var result = await EcosystemFiles.Run("git.exe", args, root, input, 60).ConfigureAwait(false);
                if (action == "commit" && result["success"]?.GetValue<bool>() == true)
                {
                    meta["staging"] = result.DeepClone(); args = new List<string>(prefix); args.AddRange(new[] { "commit", "--only", "--file=-", "--" }); args.AddRange(paths);
                    result = await EcosystemFiles.Run("git.exe", args, root, message, 60).ConfigureAwait(false);
                }
                foreach (var item in result) meta[item.Key] = item.Value?.DeepClone();
                meta["executed"] = true; meta["mayHaveModifiedRepository"] = action == "stage" || action == "commit";
                return new ResponseMessage { Message = "Git action finished; inspect exitCode and output.", Meta = meta };
            }
            catch (Exception ex) { meta["error"] = ex.Message; return new ResponseMessage { Message = "Git action failed: " + ex.Message, Meta = meta }; }
        }
    }
}
