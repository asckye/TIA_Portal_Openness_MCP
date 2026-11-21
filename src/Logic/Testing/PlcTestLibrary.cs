using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.Testing
{
    public sealed class PlcTestLibrary
    {
        private readonly string casesDirectory;
        public PlcTestLibrary(string libraryDirectory) => casesDirectory = Path.Combine(libraryDirectory, "cases");

        public JsonObject List(string[]? tags = null)
        {
            var rows = new JsonArray();
            if (!Directory.Exists(casesDirectory)) return new JsonObject { ["items"] = rows };
            foreach (string path in Directory.GetFiles(casesDirectory, "*.json").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                string id = Path.GetFileNameWithoutExtension(path);
                JsonObject row;
                try
                {
                    var (document, hash) = Load(path);
                    row = new JsonObject {
                        ["id"] = document.Id, ["title"] = document.Title, ["kind"] = document.Kind,
                        ["tags"] = new JsonArray(document.Tags.Select(t => (JsonNode)t).ToArray()),
                        ["requirement"] = document.Requirement, ["valid"] = true, ["error"] = null, ["sha256"] = hash
                    };
                    if (tags != null && tags.Length > 0 && !tags.Any(t => document.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) continue;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                    || ex is JsonException || ex is V4.Inputs.InputRejectionException)
                {
                    row = new JsonObject { ["id"] = id, ["title"] = null, ["kind"] = null, ["tags"] = new JsonArray(),
                        ["requirement"] = null, ["valid"] = false, ["error"] = ex.GetType().Name, ["sha256"] = null };
                }
                rows.Add(row);
            }
            return new JsonObject { ["items"] = rows };
        }

        public JsonObject Read(string id)
        {
            var (document, hash) = Load(FindPath(id));
            return new JsonObject { ["testCase"] = document.Document, ["sha256"] = hash };
        }

        public JsonObject Save(PlcTestCaseDocument document, bool overwrite = false)
        {
            string path = FindPath(document.Id);
            if (File.Exists(path) && !overwrite) throw new IOException("Case already exists; use overwrite=true to replace it.");
            string? previous = File.Exists(path) ? PlcTestCaseDocument.Hash(File.ReadAllBytes(path)) : null;
            PlcTestRunRecord.WriteAtomic(path, document.CanonicalJson, overwrite);
            return new JsonObject { ["id"] = document.Id, ["sha256"] = document.Sha256, ["previousSha256"] = previous };
        }

        public JsonObject Delete(string id)
        {
            string path = FindPath(id);
            var row = Read(id);
            File.Delete(path);
            return row;
        }

        private string FindPath(string id)
        {
            PlcTestCaseDocument.ValidateId(id);
            if (Directory.Exists(casesDirectory))
            {
                string? existing = Directory.GetFiles(casesDirectory, "*.json")
                    .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(id, StringComparison.OrdinalIgnoreCase));
                if (existing != null) return existing;
            }
            return Path.Combine(casesDirectory, id + ".json");
        }

        private static (PlcTestCaseDocument Document, string Hash) Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var document = PlcTestCaseDocument.Parse(new UTF8Encoding(false, true).GetString(bytes));
            if (!document.Id.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Case id must equal its file name.");
            return (document, PlcTestCaseDocument.Hash(bytes));
        }
    }
}
