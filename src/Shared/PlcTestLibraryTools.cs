using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.Testing;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcTestLibraryTools
    {
        private readonly Func<string> libraryDirectory;
        private readonly Func<string> runsDirectory;
        public PlcTestLibraryTools() : this(() => DataLocations.Current.TestLibraryDirectory, () => DataLocations.Current.TestRunsDirectory) { }
        internal PlcTestLibraryTools(Func<string> libraryDirectory, Func<string> runsDirectory)
        { this.libraryDirectory = libraryDirectory; this.runsDirectory = runsDirectory; }

        [McpServerTool(Name = "ManagePlcTestLibrary"),
            Description("[L1][Simulation][FILE] Manage stored PLC test cases and read run history under the data root on all eight releases. " +
                "Actions: list (including invalid files), read, save, delete, listRuns, readRun. Save validates the closed schemaVersion=1 JSON; " +
                "plcsimScenario uses the existing scenario validator; testSuite stores exact project case/test-set names. Existing cases need " +
                "overwrite=true. Delete returns the document for restoration. list filters tags (any match) and uses offset/limit; history is newest " +
                "first; invalid run files remain visible and only the requested directory page is parsed. readRun latest selects the newest valid record. " +
                "Unreadable runs return IO_FAILED naming the run. Incomplete runs return exitCode=5. Host files only; no TIA or PLCSIM calls. " +
                "Stored Test Suite selections require V17+, system V18+, application test sets V20+ when executed. Real installation acceptance pending.")]
        public CallToolResult ManagePlcTestLibrary(
            [Description("list | read | save | delete | listRuns | readRun.")] string action,
            [Description("Case id; read/delete only. 1..64 letters, digits, dot, underscore or hyphen; starts with a letter or digit. " +
                "Windows device stems (CON, PRN, AUX, NUL, COM1-9, LPT1-9) are refused before any extension.")] string id = "",
            [Description("Closed schemaVersion=1 case document; required for save. kind selects scenario or testSuite, exclusively.")]
            PlcTestCaseDocument? testCase = null,
            [Description("Allow save to replace an existing case, returning its previous SHA-256.")] bool overwrite = false,
            [Description("Optional tags for list; any case-insensitive match selects a case. Invalid files remain visible.")] string[]? tags = null,
            [Description("Zero-based offset for list/listRuns.")] int offset = 0,
            [Description("Page size for list/listRuns, 1..100.")] int limit = 100,
            [Description("Exact history run id, or latest for the newest valid record; readRun only.")] string runId = "latest")
        {
            const string tool = "ManagePlcTestLibrary";
            try
            {
                if (offset < 0 || limit < 1 || limit > 100) throw new ArgumentException("offset must be nonnegative and limit must be 1..100.");
                if (tags != null && (tags.Length > 32 || tags.Any(t => string.IsNullOrEmpty(t) || t.Length > 64)))
                    throw new ArgumentException("Supply at most 32 tags of 1..64 characters.");
                if (action == "read" || action == "delete") PlcTestCaseDocument.ValidateId(id);
                var library = new PlcTestLibrary(libraryDirectory());
                JsonObject data;
                switch (action)
                {
                    case "list": return Page(library.List(tags)["items"]!.AsArray(), offset, limit);
                    case "read": data = library.Read(id); break;
                    case "save": data = library.Save(testCase ?? throw new ArgumentException("save requires testCase."), overwrite); break;
                    case "delete": data = library.Delete(id); break;
                    case "listRuns": return Page(PlcTestRunRecord.List(runsDirectory(), offset, limit), offset, limit);
                    case "readRun": data = new JsonObject { ["run"] = PlcTestRunRecord.Read(runsDirectory(), runId).Document }; break;
                    default: throw new ArgumentException("Unknown library action.");
                }
                return McpServer.V4Result(tool, data, completed: action == "save" || action == "delete");
            }
            catch (PlcTestRunReadException ex)
            { return McpServer.V4Reject(tool, new Error(ex.Message, new IoFailedDetails("plc-test-run", ex.RunId))); }
            catch (FileNotFoundException) /* swallow(privacy): return NOT_FOUND without exposing the full local file path */
            { return McpServer.V4Reject(tool, new Error("Case or run was not found.", new NotFoundDetails(null))); }
            catch (DirectoryNotFoundException) /* swallow(privacy): return NOT_FOUND without exposing the full local directory path */
            { return McpServer.V4Reject(tool, new Error("Case or run was not found.", new NotFoundDetails(null))); }
            catch (Exception ex) when (ex is ArgumentException || ex is JsonException || ex is InputRejectionException)
            { return McpServer.V4Reject(tool, new Error("Invalid library input or stored document: " + ex.Message,
                new InvalidArgumentDetails("", Array.Empty<string>()))); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { return McpServer.V4Reject(tool, new Error("PLC test library file operation failed: " + ex.Message,
                new IoFailedDetails("plc-test-library", null))); }
        }

        private static CallToolResult Page(JsonArray rows, int offset, int limit)
        {
            if (offset > rows.Count) throw new ArgumentException("offset exceeds the matching row count.");
            return Page(new JsonObject {
                ["items"] = new JsonArray(rows.Skip(offset).Take(limit).Select(r => r!.DeepClone()).ToArray()),
                ["matchingCount"] = rows.Count, ["returnedCount"] = Math.Min(limit, rows.Count - offset)
            }, offset, limit);
        }

        private static CallToolResult Page(JsonObject page, int offset, int limit)
        {
            int total = (int)page["matchingCount"]!;
            return McpServer.V4Result("ManagePlcTestLibrary", page,
                paging: new Paging(PagingMode.Offset, offset, limit, offset + limit < total ? offset + limit : (int?)null,
                    null, null, total, offset + limit >= total, PagingUnit.Items));
        }
    }
}
