using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed partial class PlcOrganisationPortService
    {
        private ResponseMessage SoftwareStep(PlcSoftwareRequest request)
        {
            var unavailable = PlcSoftwareCapabilities.Unsupported(HardwareContract.ReleaseKey, request.Operation, request.Action, request.Family, request.UnitName, request.UnitKind, request.TargetKind, request.CopyMode, request.GenerateOption);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            if (!hasProject()) return NoProject(request.Operation);
            return Step(Invoke(request));
        }
        public ResponseMessage CreatePlcInstanceDb(string softwarePath, string fbPath, string name, string groupPath = "", bool autoNumber = true, int number = 0, bool dryRun = true)
            => SoftwareStep(new PlcSoftwareRequest { Operation = "CreatePlcInstanceDb", SoftwarePath = softwarePath, Path = fbPath, Name = name, GroupPath = groupPath, AutoNumber = autoNumber, Number = number, DryRun = dryRun });
        public ResponseMessage GeneratePlcSourceFromBlocks(string softwarePath, string blockPathsJson, string filePath, bool dryRun = true)
        {
            if (!hasProject()) return NoProject("GeneratePlcSourceFromBlocks");
            var request = new PlcSoftwareRequest { Operation = "GeneratePlcSourceFromBlocks", SoftwarePath = softwarePath, FilePath = filePath, DryRun = dryRun };
            try {
                if (blockPathsJson.Length > 65536) throw new ArgumentException("Name list too large.");
                request.Paths = (JsonNode.Parse(blockPathsJson) as JsonArray ?? throw new ArgumentException("Expected a JSON array of exact paths.")).Select(n => n!.GetValue<string>()).ToArray();
                if (request.Paths.Length < 1 || request.Paths.Length > 500 || request.Paths.Any(string.IsNullOrWhiteSpace) || request.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Paths.Length) throw new ArgumentException("Provide 1..500 unique nonempty paths.");
            } catch (Exception error) { request.ValidationError = error.Message; }
            return SoftwareStep(request);
        }

        public ResponseMessage UpdatePlcProgram(string softwarePath, bool confirmUpdate = false, bool dryRun = true)
            => SoftwareStep(new PlcSoftwareRequest { Operation = "SetPlcProgram", SoftwarePath = softwarePath, Confirm = confirmUpdate, DryRun = dryRun });
        public ResponseMessage ReadPlcTagTableConstants(string softwarePath, string tablePath, string kind = "all", string unitName = "", string unitKind = "unit", int offset = 0, int limit = 200)
            => SoftwareStep(new PlcSoftwareRequest { Operation = "GetPlcTagTableConstants", SoftwarePath = softwarePath, Path = tablePath, Kind = kind, UnitName = unitName, UnitKind = unitKind, Offset = offset, Limit = limit });
        public ResponseMessage ManagePlcExternalSources(string softwarePath, string action, string name = "", string unitName = "", string unitKind = "unit", string groupPath = "", string filePath = "", string libraryName = "", string masterCopyPath = "", string copyMode = "", string generateOption = "None", string targetKind = "", string targetGroupPath = "", string newName = "", bool confirmDelete = false, bool dryRun = true)
            => SoftwareStep(new PlcSoftwareRequest { Operation = "ManagePlcExternalSources", SoftwarePath = softwarePath, Action = action, Name = name, UnitName = unitName, UnitKind = unitKind, GroupPath = groupPath, FilePath = filePath, LibraryName = libraryName, MasterCopyPath = masterCopyPath, CopyMode = copyMode, GenerateOption = generateOption, TargetKind = targetKind, TargetGroupPath = targetGroupPath, NewName = newName, Confirm = confirmDelete, DryRun = dryRun });
        public ResponseMessage ManagePlcTagDefinition(string softwarePath, string tablePath, string name, string kind, string action, string dataType = "", string addressOrValue = "", string propertiesJson = "{}", bool dryRun = true)
        {
            if (!hasProject()) return NoProject("ManagePlcTagDefinition");
            var request = new PlcSoftwareRequest { Operation = "ManagePlcTagDefinition", SoftwarePath = softwarePath, Path = tablePath, Name = name, Kind = kind, Action = action, DataType = dataType, AddressOrValue = addressOrValue, DryRun = dryRun };
            PlcTagEditingLogic.Request parsed;
            try { parsed = PlcTagEditingLogic.Validate(action, kind, name, dataType, addressOrValue, propertiesJson, dryRun); }
            catch (Exception error) { request.ValidationError = error.Message; return SoftwareStep(request); }
            foreach (var pair in parsed.Scalars)
            {
                using var doc = JsonDocument.Parse(pair.Value?.ToJsonString() ?? "null");
                var scalar = doc.RootElement;
                request.Properties[pair.Key] = new HardwareScalar { Kind = scalar.ValueKind == JsonValueKind.Null ? "null" : scalar.ValueKind == JsonValueKind.String ? "string" : scalar.ValueKind == JsonValueKind.Number ? "number" : scalar.ValueKind == JsonValueKind.True || scalar.ValueKind == JsonValueKind.False ? "boolean" : "complex", Text = scalar.ValueKind == JsonValueKind.String ? scalar.GetString()! : scalar.GetRawText() };
            }
            request.Comments = parsed.Comments.Select(c => new PlcTagComment { Culture = c.Culture, Text = c.Text }).ToArray();
            return SoftwareStep(request);
        }
        public System.Collections.Generic.List<CrossReferenceEntry>? GetCrossReferences(string softwarePath, string objectPath, string kind, string filter, out string? reason, out bool queried, string unitName, string unitKind)
        {
            var unavailable = PlcSoftwareCapabilities.Unsupported(HardwareContract.ReleaseKey, "GetPlcCrossReferences", unitName: unitName, unitKind: unitKind);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            reason = CrossReferenceGuardLogic.PolicyRefusal(Environment.GetEnvironmentVariable(CrossReferenceGuardLogic.NativeQueryOptInVariable)); queried = false;
            if (reason != null) return null;
            if (!hasProject()) { reason = "No project is open."; return null; }
            var reply = Invoke(new PlcSoftwareRequest { Operation = "GetPlcCrossReferences", SoftwarePath = softwarePath, Path = objectPath, Kind = kind, Filter = filter, UnitName = unitName, UnitKind = unitKind });
            reason = (string?)reply["Reason"]; queried = (bool?)reply["Queried"] == true;
            return reply["References"] == null ? null : JsonSerializer.Deserialize<System.Collections.Generic.List<CrossReferenceEntry>>(reply["References"]!.ToJsonString());
        }
        public ResponseMessage ImportPlcBlockVerified(string softwarePath, string blockPath, string importPath, string evidenceDirectory, bool dryRun, string expectedToken, bool compileAfterImport)
            => SoftwareStep(new PlcSoftwareRequest { Operation = "ImportPlcBlockVerified", SoftwarePath = softwarePath, Path = blockPath, FilePath = importPath, EvidenceDirectory = evidenceDirectory, DryRun = dryRun, ExpectedToken = expectedToken, CompileAfterImport = compileAfterImport });
        public ResponseImportBatch ImportPlcTagTablesFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
        {
            if (hasProject()) TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,
                new PlcArtifactPortSession(this, "ImportPlcTagTablesFromDirectory").SoftwarePath(softwarePath), true);

            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (!hasProject())
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Project is null" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Directory not found" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name)) continue;

                    try { new PlcArtifactPortSession(this, "ImportPlcTagTablesFromDirectory").ImportPlcTagTable(softwarePath, folderPath, file); imported.Add(name); }
                    catch (PortalException pex) { failed.Add(new ImportFailure { Path = file, Error = pex.Message }); }
                }

                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
        }
        public ResponseSeed SeedProjectFromReference(
            string plcSoftwarePath,
            string hmiSoftwarePath,
            string referenceDir,
            JsonObject? placeholders = null)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            placeholders ??= new JsonObject();

            try
            {
                if (!hasProject())
                {
                    failed.Add(new ImportFailure { Path = referenceDir, Error = "Project is null" });
                    return new ResponseSeed { Imported = imported, Failed = failed, Placeholders = placeholders };
                }

                if (string.IsNullOrWhiteSpace(referenceDir) || !Directory.Exists(referenceDir))
                {
                    failed.Add(new ImportFailure { Path = referenceDir, Error = "Reference directory not found" });
                    return new ResponseSeed { Imported = imported, Failed = failed, Placeholders = placeholders };
                }

                var manifestPath = Path.Combine(referenceDir, "manifest.json");
                JsonObject? manifest = null;
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new ImportFailure { Path = manifestPath, Error = $"Failed to parse manifest.json: {ex.Message}" });
                    }
                }

                string plcBlocksDir = Path.Combine(referenceDir, "plc", "blocks");
                string plcTypesDir = Path.Combine(referenceDir, "plc", "types");
                string hmiScreensDir = Path.Combine(referenceDir, "hmi", "screens");
                string hmiTagsDir = Path.Combine(referenceDir, "hmi", "tags");

                var plcBlockGroupPath = manifest?["plcBlockGroupPath"]?.ToString() ?? "";
                var plcTypeGroupPath = manifest?["plcTypeGroupPath"]?.ToString() ?? "";
                var hmiScreenFolderPath = manifest?["hmiScreenFolderPath"]?.ToString() ?? "";
                var hmiTagTableFolderPath = manifest?["hmiTagTableFolderPath"]?.ToString() ?? "";

                if (manifest?["plcBlocksDir"] != null) plcBlocksDir = Path.Combine(referenceDir, manifest["plcBlocksDir"]!.ToString());
                if (manifest?["plcTypesDir"] != null) plcTypesDir = Path.Combine(referenceDir, manifest["plcTypesDir"]!.ToString());
                if (manifest?["hmiScreensDir"] != null) hmiScreensDir = Path.Combine(referenceDir, manifest["hmiScreensDir"]!.ToString());
                if (manifest?["hmiTagTablesDir"] != null) hmiTagsDir = Path.Combine(referenceDir, manifest["hmiTagTablesDir"]!.ToString());

                if (HardwareContract.ReleaseKey != "20" && HardwareContract.ReleaseKey != "21" &&
                    new[] { hmiScreensDir, hmiTagsDir }.Any(dir => Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly).Any()))
                    throw new NotSupportedException("Seed HMI imports depend on the B7 engine worker port in this release.");

                var tempDir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia-seed-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                void CopyDirWithReplace(string srcDir, string dstDir)
                {
                    if (!Directory.Exists(srcDir)) return;
                    Directory.CreateDirectory(dstDir);

                    foreach (var file in Directory.EnumerateFiles(srcDir, "*.xml", SearchOption.TopDirectoryOnly))
                    {
                        var text = File.ReadAllText(file, Encoding.UTF8);
                        foreach (var kv in placeholders)
                        {
                            var k = kv.Key;
                            var v = kv.Value?.ToString() ?? "";
                            text = text.Replace("{{" + k + "}}", v);
                        }
                        var outPath = Path.Combine(dstDir, Path.GetFileName(file));
                        File.WriteAllText(outPath, text, Encoding.UTF8);
                    }
                }

                var tempPlcBlocks = Path.Combine(tempDir, "plc", "blocks");
                var tempPlcTypes = Path.Combine(tempDir, "plc", "types");
                var tempHmiScreens = Path.Combine(tempDir, "hmi", "screens");
                var tempHmiTags = Path.Combine(tempDir, "hmi", "tags");

                CopyDirWithReplace(plcBlocksDir, tempPlcBlocks);
                CopyDirWithReplace(plcTypesDir, tempPlcTypes);
                CopyDirWithReplace(hmiScreensDir, tempHmiScreens);
                CopyDirWithReplace(hmiTagsDir, tempHmiTags);

                // PLC blocks
                if (Directory.Exists(tempPlcBlocks))
                {
                    var r = new PlcArtifactPortSession(this, "SeedProjectFromReference").ImportBlocksFromDirectory(plcSoftwarePath, plcBlockGroupPath, tempPlcBlocks, "", overwrite: true);
                    imported.AddRange(r.Imported?.Select(x => "plc:block:" + x) ?? Array.Empty<string>());
                    failed.AddRange(r.Failed?.Select(x => new ImportFailure { Path = x.Path, Error = "plc:block:" + x.Error }) ?? Array.Empty<ImportFailure>());
                }

                // PLC types (UDT)
                if (Directory.Exists(tempPlcTypes))
                {
                    foreach (var file in Directory.EnumerateFiles(tempPlcTypes, "*.xml", SearchOption.TopDirectoryOnly))
                    {
                        var ok = new PlcArtifactPortSession(this, "SeedProjectFromReference").ImportType(plcSoftwarePath, plcTypeGroupPath, file);
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (ok) imported.Add("plc:type:" + name);
                        else failed.Add(new ImportFailure { Path = file, Error = "plc:type:Import failed" });
                    }
                }

                // HMI tag tables then screens
                if (Directory.Exists(tempHmiTags))
                {
                    var r = SeedHmiImport("tagTables", hmiSoftwarePath, hmiTagTableFolderPath, tempHmiTags);
                    imported.AddRange(r.Imported?.Select(x => "hmi:tagtable:" + x) ?? Array.Empty<string>());
                    failed.AddRange(r.Failed?.Select(x => new ImportFailure { Path = x.Path, Error = "hmi:tagtable:" + x.Error }) ?? Array.Empty<ImportFailure>());
                }

                if (Directory.Exists(tempHmiScreens))
                {
                    var r = SeedHmiImport("screens", hmiSoftwarePath, hmiScreenFolderPath, tempHmiScreens);
                    imported.AddRange(r.Imported?.Select(x => "hmi:screen:" + x) ?? Array.Empty<string>());
                    failed.AddRange(r.Failed?.Select(x => new ImportFailure { Path = x.Path, Error = "hmi:screen:" + x.Error }) ?? Array.Empty<ImportFailure>());
                }

                return new ResponseSeed
                {
                    Message = $"Seed applied from '{referenceDir}'",
                    Imported = imported,
                    Failed = failed,
                    Placeholders = placeholders,
                    TempDir = tempDir,
                };
            }
            catch (NotSupportedException) { throw; }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = referenceDir, Error = ex.ToString() });
                return new ResponseSeed { Imported = imported, Failed = failed, Placeholders = placeholders };
            }
        }
        public ResponseMessage CompileDevice(string devicePathJson, string itemPathJson = "[]")
        {
            if (!hasProject()) return NoProject("CompileDevice");
            return SoftwareStep(new PlcSoftwareRequest { Operation = "CompileDevice", DevicePath = JsonSerializer.Deserialize<string[]>(devicePathJson)!, ItemPath = JsonSerializer.Deserialize<string[]>(itemPathJson)!, DryRun = false });
        }
    }
}
