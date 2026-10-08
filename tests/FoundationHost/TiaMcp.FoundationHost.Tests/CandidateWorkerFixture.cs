using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.FoundationHost;

// Dispatches the real JSON-free final-check primitives; no native assembly is loaded.
internal sealed class CandidateWorkerFixture : IFoundationWorker
{
    internal const string Identifier = "OrderNumber:6ES7513-1AM03-0AB0/V3.0";
    internal readonly DeviceNative Device = new();
    internal readonly ImportNative Import = new();
    internal readonly WorkerOutcomeState Outcome = new();
    internal int Calls;
    internal string? Operation;
    internal JsonObject? Arguments;
    internal JsonNode? LastResult;
    internal bool MissingProject, Malformed, CorruptReadback;
    internal string InputPath { get; }
    internal CandidateWorkerFixture()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "candidate-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        InputPath = Path.Combine(dir, "input.xml");
        File.WriteAllText(InputPath, "<Document><Engineering version=\"V19\"/><SW.Blocks.FC ID=\"1\"><AttributeList><Name>F</Name><Number>7</Number></AttributeList></SW.Blocks.FC></Document>", new UTF8Encoding(false));
    }
    public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Outcome.RequireUsable(); Calls++; Operation = operation; Arguments = args;
        object reply;
        if (operation == "CreateHardwareDeviceCandidate")
        {
            var call = args["candidate"]!.Deserialize<DeviceCandidateCall>()!;
            var result = new DeviceCandidateReply { RootId = Device.RootId };
            if (MissingProject) result.Fault = new() { Kind = "project" };
            else switch (call.Action)
            {
                case "identity": result.Identity = Device.ReadIdentity(); break;
                case "catalog": result.Catalog = Device.ReadCatalog(call.TypeIdentifier).ToArray(); break;
                case "inventory": result.Inventory = Device.ReadInventory().ToArray(); break;
                case "execute": result.Attempt = CandidateExecution.Create(Device, call.Check!); result.RequiresSessionReset = result.Attempt.RequiresSessionReset; break;
                default: throw new InvalidOperationException();
            }
            reply = result;
        }
        else
        {
            var call = args["candidate"]!.Deserialize<ImportCandidateCall>()!; var result = new ImportCandidateReply();
            var locks = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (MissingProject) result.Fault = new() { Kind = "project" };
                else switch (call.Action)
                {
                    case "identity": result.Identity = Import.ReadIdentity(); break;
                    case "inputs": result.Inputs = Import.ReadInputs("19", call.Tool, call.Request, locks).ToArray(); break;
                    case "inventory": result.Inventory = Import.ReadInventory().ToArray(); break;
                    case "group": result.GroupIdentity = Import.TargetGroupIdentity(call.Target!); break;
                    case "overwrite": result.OverwriteSupported = Import.SupportsOverwrite(call.Input!); break;
                    case "execute":
                        foreach (var file in call.Check!.Files) locks.Add(file.Path, File.OpenRead(file.Path));
                        result.Attempt = CandidateExecution.Import(Import, call.Check, locks); result.RequiresSessionReset = result.Attempt.RequiresSessionReset; break;
                    default: throw new InvalidOperationException();
                }
            }
            finally { foreach (var stream in locks.Values) stream.Dispose(); }
            reply = result;
        }
        LastResult = JsonSerializer.SerializeToNode(reply);
        if (Malformed && (string?)args["candidate"]?["Action"] == "execute") LastResult!["unexpected"] = true;
        if (CorruptReadback && (string?)args["candidate"]?["Action"] == "execute")
            LastResult!["Attempt"]!["After"] = new JsonArray(new JsonObject());
        try { Outcome.AcceptResult(operation, args, LastResult); }
        catch (Exception ex) { Outcome.Failed(true, ex); throw; }
        return Task.FromResult(LastResult);
    }
    public void Dispose() { }

    internal sealed class DeviceNative : IDeviceCreationAdapter
    {
        internal int Creates;
        internal string Fault = "";
        internal long Epoch = 1;
        internal Action? Before;
        internal readonly List<DeviceInventoryItem> Rows = new();
        public string RootId => "root";
        public CandidateIdentity ReadIdentity() => new(42, DateTimeOffset.Parse("2026-10-03T00:00:00Z"), @"C:\Test.ap19", Epoch);
        public IReadOnlyList<DeviceCatalogEntry> ReadCatalog(string type) => type == Identifier
            ? new[] { new DeviceCatalogEntry { TypeIdentifier = Identifier, ArticleNumber = "6ES7513-1AM03-0AB0", Version = "V3.0" } } : Array.Empty<DeviceCatalogEntry>();
        public IReadOnlyList<DeviceInventoryItem> ReadInventory()
        { if (Creates > 0 && Fault == "after") throw new IOException(); return Rows; }
        public void BeforeCreate() { Before?.Invoke(); if (Fault == "before") throw new IOException(); }
        public DeviceInventoryItem Create(string type, string name)
        {
            Creates++; if (Fault == "during-before") throw new IOException();
            var created = new DeviceInventoryItem { Id = "created", Name = name, ParentId = RootId }; Rows.Add(created);
            if (Fault == "during-after") throw new IOException();
            if (Fault == "identity-after") Epoch++;
            if (Fault == "wrong-parent") created.ParentId = "elsewhere";
            return created;
        }
    }
    internal sealed class ImportNative : IPlcImportAdapter
    {
        internal int Calls;
        internal string Fault = "";
        internal long Epoch = 1;
        internal readonly List<PlcImportObject> Rows = new();
        public CandidateIdentity ReadIdentity() => new(42, DateTimeOffset.Parse("2026-10-03T00:00:00Z"), @"C:\Test.ap19", Epoch);
        public IReadOnlyList<PlcImportInput> ReadInputs(string release, string tool, PlcImportRequest request, IDictionary<string, Stream> locks)
            => CandidateImportFiles.Read(release, tool, request, locks);
        public IReadOnlyList<PlcImportObject> ReadInventory()
        { if (Calls > 0 && Fault == "inventory-after") throw new IOException(); return Rows; }
        public string TargetGroupIdentity(PlcImportObject target) => "group-identity";
        public bool SupportsOverwrite(PlcImportInput input) => true;
        public void BeforeImport(PlcImportInput input) { if (Fault == "before") throw new IOException(); }
        public PlcImportObject Import(PlcImportInput input, bool overwrite)
        {
            Calls++; if (Fault == "during-before") throw new IOException();
            var row = JsonSerializer.Deserialize<PlcImportObject>(JsonSerializer.Serialize(input.Target))!; row.Id = "imported"; row.ContentHash = input.ContentHash; Rows.Add(row);
            if (Fault == "during-after") throw new IOException();
            if (Fault == "identity-after") Epoch++;
            if (Fault == "wrong-parent") row.GroupPath = "elsewhere";
            return row;
        }
        public string ReadContent(PlcImportInput input, PlcImportObject imported)
        { if (Fault == "after") throw new IOException(); return Fault == "content-mismatch" ? new string('0', 64) : input.ContentHash; }
    }
}
