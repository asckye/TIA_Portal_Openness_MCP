using System.Text;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

public sealed class AdapterLatchTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            yield return new object[] { release, "device-add", "Hardware/PlcDeviceAdd.cs", "deviceAddOutcomeUnknown" };
            yield return new object[] { release, "source-delete", "Plc/PlcExternalSourceDelete.cs", "externalDeleteOutcomeUnknown" };
            yield return new object[] { release, "source-workflow", "Plc/PlcExternalSourceWorkflow.cs", "externalSourceOutcomeUnknown" };
            if (release is "20" or "21")
            {
                yield return new object[] { release, "document", "Plc/PlcDocumentImport.cs", "documentImportOutcomeUnknown" };
                yield return new object[] { release, "batch-document", "Plc/PlcBatchDocumentImport.cs", "documentImportOutcomeUnknown" };
            }
        }
    }

    [Theory, MemberData(nameof(Cases))]
    public void Unknown_call_is_not_replayed_and_a_later_independent_policy_call_runs(string release, string kind, string file, string latch)
    {
        string root = TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        Assert.DoesNotContain(latch, File.ReadAllText(Path.Combine(root, "src/Adapters/Native", file)), StringComparison.Ordinal);
        int calls = 0;
        bool fail = true;
        void Native()
        {
            calls++;
            if (fail) throw new IOException("Synthetic native write lost its reply.");
        }
        IWorkerOperationReply Execute(string name)
        {
            return kind switch {
                "device-add" => Device(release, name, Native),
                "source-delete" => Delete(release, name, Native),
                "source-workflow" => ImportSource(release, name, Native),
                _ => ImportDocument(release, name, kind == "batch-document", Native)
            };
        }
        if (kind == "device-add" && release is "14sp1" or "15.1" or "16" or "17")
        {
            Assert.Throws<NotSupportedException>(() => Execute("First"));
            Assert.Throws<NotSupportedException>(() => Execute("Independent"));
            Assert.Equal(0, calls);
            return;
        }
        var first = Execute("First");
        Assert.True(first.RequiresSessionReset);
        Assert.Equal(1, calls);
        fail = false;
        var later = Execute("Independent");
        Assert.False(later.RequiresSessionReset);
        Assert.True(later.MayHaveChanged);
        Assert.Equal(2, calls);
        Assert.True(first.RequiresSessionReset);
    }

    private const string Project = "C:/fixture/Project.ap21";

    private static PlcDeviceAddResult Device(string release, string name, Action native)
    {
        var request = new PlcDeviceAddRequest { Release = release, Project = Project, ProcessId = 123, RootIdentity = "root",
            PreferredMlfb = "6ES7513-1AM03-0AB0", PreferredVersion = "V3.0", Name = name, Family = "S7-1500" };
        var items = new List<PlcDeviceAddItem>();
        PlcDeviceAddResult Run() => PlcDeviceAddPolicy.Run(request, () => new[] { new PlcHardwareCatalogCandidate {
            ArticleNumber = request.PreferredMlfb, Version = request.PreferredVersion,
            TypeIdentifier = "OrderNumber:6ES7513-1AM03-0AB0/V3.0" } }, () => items, () => { }, (_, createdName) => {
                native();
                var item = new PlcDeviceAddItem { Name = createdName, Identity = createdName, ParentIdentity = "root", ParentVerified = true };
                items.Add(item);
                return item;
            });
        var preview = Run();
        request.DryRun = false; request.Confirm = true; request.ExpectedProject = Project; request.ExpectedHash = preview.PlanHash;
        return Run();
    }

    private static PlcExternalSourceDeleteResult Delete(string release, string name, Action native)
    {
        var request = new PlcExternalSourceDeleteRequest { Release = release, Project = Project, ProcessId = 123,
            Software = "PLC", Name = name + ".scl", RootIdentity = "root" };
        var items = new List<PlcExternalSourceDeleteItem> { new() { Name = request.Name, Identity = name, ParentVerified = true } };
        PlcExternalSourceDeleteResult Run() => PlcExternalSourceDeletePolicy.Run(request, () => items, () => { }, _ => { }, _ => {
            native(); items.Clear();
        });
        var preview = Run();
        request.DryRun = false; request.Confirm = true; request.ExpectedProject = Project; request.ExpectedHash = preview.PlanHash;
        return Run();
    }

    private static PlcExternalSourceImportResult ImportSource(string release, string name, Action native)
    {
        var names = new List<string>();
        byte[] code = Encoding.ASCII.GetBytes("FUNCTION " + name + " : Void\nBEGIN\nEND_FUNCTION\n");
        PlcExternalSourceImportResult Run(bool preview, string hash = "") => PlcExternalSourceWorkflowPolicy.Import(
            new PlcExternalSourceImportResult { Operation = "ImportPlcExternalSource", Release = release, ProjectFile = Project,
                ProcessId = 123, SoftwarePath = "PLC", FilePath = "C:/fixture/" + name + ".scl", RequestedSourceName = name + ".scl" },
            new MemoryStream(code), preview, hash, !preview, preview ? "" : Project, () => names, () => { }, (source, _) => {
                native(); names.Add(source); return source;
            });
        return Run(false, Run(true).PlanHash);
    }

    private static IWorkerOperationReply ImportDocument(string release, string name, bool batch, Action native)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "documents");
        var inventory = new List<string>();
        string code = "DATA_BLOCK " + name + " VAR Flag:Bool; END_VAR END_DATA_BLOCK";
        var context = new PlcDocumentImportContext {
            Request = new() { Release = release, Project = Project, ProcessId = 123, Software = "PLC", Group = "",
                Directory = directory, Name = name, TargetIdentity = "root" },
            Scan = () => release == "20" ? new[] { Path.Combine(directory, name + ".s7dcl"), Path.Combine(directory, name + ".s7res") }
                : new[] { Path.Combine(directory, name + ".s7dcl") },
            Open = path => new MemoryStream(Encoding.ASCII.GetBytes(path.EndsWith(".s7dcl", StringComparison.Ordinal) ? code : "resource")),
            Inventory = () => inventory.ToArray(), Collision = _ => false, Exists = _ => true, Recheck = () => { },
            Import = () => {
                native();
                inventory.Add(PlcDocumentImportPolicy.InventoryItem("", name, "GlobalDB", name));
                return new() { State = "Success", Success = true, ExistsVerified = true, IdentitiesRead = true,
                    Identities = new[] { PlcDocumentImportPolicy.InventoryItem("", name, "GlobalDB", "returned") } };
            }
        };
        if (batch)
        {
            var preview = PlcBatchDocumentImportPolicy.Run(new[] { context }, true, "", false, "");
            return PlcBatchDocumentImportPolicy.Run(new[] { context }, false, preview.PlanHash, true, Project);
        }
        var single = context.Run();
        context.Request.DryRun = false; context.Request.Confirm = true;
        context.Request.ExpectedProject = Project; context.Request.ExpectedHash = single.PlanHash;
        return context.Run();
    }
}
