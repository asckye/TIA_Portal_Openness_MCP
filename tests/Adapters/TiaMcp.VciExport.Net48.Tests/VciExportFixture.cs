using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.VersionControl;
using TiaMcp.Adapters.Native.Vci;

internal static class VciExportFixture
{
    // V16 rc.6 experiment, rows 28–30, 36 and 41–43.
    internal const string Disposed = "Access to a disposed object of type 'Siemens.Engineering.VersionControl.IndividualObjectSynchronizationStatus'"
        + " is not possible. The object is no longer available.";
    private const string Deleted = "Error when calling method 'UpdateStatus' of type " + IndividualObjectSynchronizationStatus.NativeType
        + " The file is deleted";

    internal static object WorkspaceFileStatus(DirectoryInfo root)
    {
        var workspace = new Workspace(root);
        var project = new Project();
        project.Vci.WorkspaceGroup.Workspaces.Add(workspace);
        var adapter = new VersionControlAdapter(() => project);
        string xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MCP_RC_FC.xml"));
        foreach (var stem in new[] { "MappedByStem", "MappedByFileName" })
        {
            File.WriteAllText(Path.Combine(root.FullName, stem + ".xml"), xml);
            var block = new PlcBlock { Name = stem };
#if STUDIO_VCI_MODERN
            workspace.ConnectObject(block, root, stem == "MappedByStem" ? stem : stem + ".xml", "S7DCL");
#else
            var mapping = new WorkspaceMapping(workspace, stem + ".xml", block);
            mapping.Status.State = CompareState.Equal;
            workspace.Mappings.Add(mapping);
#endif
        }
        var mapped = adapter.GetVersionControlStatus(changedOnly: false);
        void AddFiles(int low, int high)
        {
            for (int i = high; i >= low; i--)
                File.WriteAllText(Path.Combine(root.FullName, $"Block{i:D3}.xml"), xml);
        }
        AddFiles(1, 20);
        var twenty = adapter.GetVersionControlStatus();
        AddFiles(21, 205);
        var many = adapter.GetVersionControlStatus();
        workspace.RootPath = new DirectoryInfo(Path.Combine(root.FullName, "Unavailable"));
        var unavailable = adapter.GetVersionControlStatus();

        // Keep the individual mapping paths readable while the workspace reports a malformed root.
        workspace.RootPath = root;
        var reported = new Workspace(root);
#if STUDIO_VCI_MODERN
        reported.MappedObjects.AddRange(workspace.MappedObjects);
#else
        reported.Mappings.AddRange(workspace.Mappings);
#endif
        project.Vci.WorkspaceGroup.Workspaces[0] = reported;
        object InvalidRoot(string suffix)
        {
            reported.RootPath = (DirectoryInfo)new ReportedDirectory(root.FullName + "\\" + suffix).GetTransparentProxy();
            return adapter.GetVersionControlStatus(changedOnly: false);
        }
        return new { mapped, twenty, many, unavailable, invalidCharacter = InvalidRoot("bad|root"), midPathColon = InvalidRoot("bad:root") };
    }

    // A local DirectoryInfo constructor validates the path before status can read FullName.
    private sealed class ReportedDirectory(string path) : RealProxy(typeof(DirectoryInfo))
    {
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            if (call.MethodName != "get_FullName") throw new NotSupportedException(call.MethodName);
            return new ReturnMessage(path, null, 0, call.LogicalCallContext, call);
        }
    }

    internal static object Run(DirectoryInfo parent, bool refreshesKnownState)
    {
#if !STUDIO_VCI_MODERN
        const string typeXml = "<Document><SW.Types.PlcStruct ID=\"0\"><AttributeList><Name>MCP_RC_B</Name>"
            + "</AttributeList></SW.Types.PlcStruct></Document>";
#endif
        var replies = new Dictionary<string, object>();
        foreach (var scenario in new[] { "equal", "unknown", "unequal", "collision", "exportRefused", "createRefused", "lookupRefused",
            "syncRefused", "nativeSuccess", "repair", "repairRefused", "reuse", "newStatusRefused", "seedDisposed", "seedAbort", "seedConnection",
            "seedNonrecoverable", "sameName", "differentXml", "mixed-reuse", "rc7-comment", "rc7-member", "rc7-after-preview",
            "rc7-reconnect", "rc7-sidecar", "rc7-after-create", "rc7-inconsistent", "rc7-fallback-differs", "catch-prior", "unlisted-status", "duplicate-status"
#if STUDIO_VCI_INITIAL
            , "multipleDisconnected", "manyDisconnected"
#else
            , "studio-conflict", "studio-reuse"
#endif
#if STUDIO_VCI_MODERN
            , "container-format-refusal", "leaf-format-refusal", "ungrouped-format-refusal", "disconnected-status"
#endif
        })
        {
            var root = parent.CreateSubdirectory(scenario);
            var workspace = new Workspace(root);
            var project = new Project { Name = "Project" };
            project.Vci.WorkspaceGroup.Workspaces.Add(workspace);
            var adapter = new VersionControlAdapter(() => project);
            var device = new Device { Name = "MCP_RC_PLC" };
            if (scenario == "lookupRefused") project.Devices.Add(new Device { Name = "PendingDevice" });
            project.Devices.Add(device);
            var item = new DeviceItem { Name = device.Name };
            device.DeviceItems.Add(item);
            var plc = (PlcSoftware)item.Container.Software;
            plc.Name = device.Name;
            var block = new PlcBlock { Name = scenario.StartsWith("rc7-", StringComparison.Ordinal) ? "MCP_RC_FC5" : "MCP_RC_FC" };
            if (scenario == "rc7-member") block.Name = "MCP_RC_DB5";
            plc.BlockGroup.Blocks.Add(block);
#if STUDIO_VCI_MODERN
            if (scenario.EndsWith("-format-refusal", StringComparison.Ordinal))
            {
                if (scenario == "ungrouped-format-refusal")
                {
                    project.Devices.Remove(device);
                    project.UngroupedDevicesGroup.Devices.Add(device);
                }
                var queried = new List<string>();
                workspace.QueryFormats = target => {
                    if (target != device && target != block) return;
                    string name = target == device ? device.Name : block.Name;
                    queried.Add(name);
                    if (target == device || scenario == "leaf-format-refusal") throw new EngineeringTargetInvocationException(name + " refused");
                };
                replies[scenario] = new { reply = adapter.ConnectProjectToWorkspace(dryRun: true), queried };
                continue;
            }
#endif
            if (scenario == "catch-prior")
            {
                var inner = new IOException(@"Catalog lookup refused. password=fixture-credential. File C:\PrivateFixture\Panel.xml");
                var refusal = new InvalidOperationException("Outer workspace refusal", inner);
                var direct = new VersionControlAdapter(() => throw refusal);
                replies["catch-direct"] = new { reply = direct.ConnectProjectToWorkspace(dryRun: false) };

                bool lookupFailed = false;
                void RefuseLookup() { lookupFailed = true; throw refusal; }
#if STUDIO_VCI_MODERN
                workspace.MappedObjects.BeforeLookup = RefuseLookup;
#else
                workspace.Mappings.BeforeLookup = RefuseLookup;
#endif
                var prior = new VersionControlAdapter(() => lookupFailed ? throw new IOException("Workspace reacquisition failed.") : project);
                replies[scenario] = new { reply = prior.ConnectProjectToWorkspace(dryRun: false) };
                continue;
            }
            var file = Path.Combine(root.FullName, device.Name + "_" + block.Name + ".xml");
            string xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MCP_RC_FC.xml"));
            if (scenario == "duplicate-status")
            {
                File.WriteAllText(file, xml);
#if STUDIO_VCI_MODERN
                string stem = Path.GetFileNameWithoutExtension(file);
                var stale = workspace.ConnectObject(block, root, stem, "xml");
                stale.Status.ReadError = new ObjectDisposedException(nameof(MappedObject));
                workspace.ConnectObject(block, root, stem, "xml");
#else
                var stale = new WorkspaceMapping(workspace, Path.GetFileName(file), block);
                stale.Status.RefreshError = new EngineeringObjectDisposedException(Disposed);
                workspace.Mappings.Add(stale);
                var live = new WorkspaceMapping(workspace, Path.GetFileName(file), block);
                live.Status.State = CompareState.Equal;
                live.Status.RefreshesKnownState = refreshesKnownState;
                workspace.Mappings.Add(live);
#endif
                replies.Add(scenario, new { reply = adapter.GetVersionControlStatus(changedOnly: false), filtered = adapter.GetVersionControlStatus() });
                continue;
            }
            if (scenario == "unlisted-status")
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                File.WriteAllText(file, xml);
#if STUDIO_VCI_MODERN
                workspace.ConnectObject(block, root, stem, "SimaticML");
#else
                var listed = new WorkspaceMapping(workspace, stem + ".xml", block);
                listed.Status.State = CompareState.Equal;
                workspace.Mappings.Add(listed);
#endif
                File.WriteAllText(Path.Combine(root.CreateSubdirectory("Nested").FullName, "Nested.xml"), xml);
                File.WriteAllText(Path.Combine(root.FullName, stem + ".xml.bak"), xml);
                var fullyMapped = adapter.GetVersionControlStatus();
                string unlisted = Path.Combine(root.FullName, "MCP_RC_PLC_MCP_RC_FC2.XML");
                File.WriteAllText(unlisted, xml.Replace("MCP_RC_FC", "MCP_RC_FC2"));
                var original = File.ReadAllBytes(unlisted);
                var reply = adapter.GetVersionControlStatus();
                replies.Add(scenario, new { fullyMapped, reply, preserved = original.SequenceEqual(File.ReadAllBytes(unlisted)) });
                continue;
            }
            if (scenario.StartsWith("rc7-", StringComparison.Ordinal))
                xml = xml.Replace("MCP_RC_FC", "MCP_RC_FC5").Replace("</SW.Blocks.FC>",
                    "<ObjectList><MultilingualText ID=\"1\" CompositionName=\"Comment\"><ObjectList>"
                    + "<MultilingualTextItem ID=\"2\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text />"
                    + "</AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.FC>");
            if (scenario == "rc7-member")
                xml = "<Document><Engineering version=\"V18\"/><SW.Blocks.GlobalDB ID=\"0\"><AttributeList>"
                    + "<Name>MCP_RC_DB5</Name><ProgrammingLanguage>DB</ProgrammingLanguage><Interface>"
                    + "<Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v5\"><Section Name=\"Static\">"
                    + "<Member Name=\"Count\" Datatype=\"Int\"><Comment><MultiLanguageText Lang=\"zh-CN\"></MultiLanguageText></Comment></Member>"
                    + "</Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>";
            block.Write = (target, options) =>
            {
                bool workspaceTarget = target.FullName.StartsWith(root.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                var prefix = workspaceTarget && scenario.StartsWith("studio-", StringComparison.Ordinal) ? "Workspace.Export:" : "Export:";
                workspace.Calls.Add(prefix + target.Name + ":" + options);
                if (scenario == "seedDisposed") throw new EngineeringObjectDisposedException("Disposed");
                if (scenario == "seedAbort") throw new EngineeringUserAbortException("Aborted");
                if (scenario == "seedConnection") throw new EndOfStreamException("Connection lost");
                if (scenario == "seedNonrecoverable") throw new NonRecoverableException("Nonrecoverable");
                if (scenario == "rc7-fallback-differs" && options == ExportOptions.None)
                    throw new EngineeringTargetInvocationException("Export without defaults refused.");
                if (scenario is "exportRefused" or "rc7-inconsistent")
                    throw new EngineeringTargetInvocationException("MCP_RC_FC [FC1] object(s) is not consistent");
                File.WriteAllText(target.FullName, xml);
            };
            if (scenario == "rc7-inconsistent")
            {
                block.IsConsistent = false;
                File.WriteAllText(file, xml);
            }
#if !STUDIO_VCI_INITIAL
            if (scenario.StartsWith("studio-", StringComparison.Ordinal))
            {
                var studio = new TiaOpenness.Openness.OpennessVersionControl(() => project);
                using var hash = System.Security.Cryptography.SHA256.Create();
                var digest = hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(device.Name + "\n" + block.Name));
                var stem = device.Name + "_" + block.Name + "_" + BitConverter.ToString(digest, 0, 4).Replace("-", "").ToLowerInvariant();
                var target = Path.Combine(root.FullName, stem + ".xml");
                File.WriteAllText(target, scenario == "studio-reuse" ? xml : xml.Replace("<Name>MCP_RC_FC</Name>", "<Name>Other</Name>"),
                    new System.Text.UTF8Encoding(true));
                var bytes = File.ReadAllBytes(target);
                var preview = studio.MapProject("", "", true, null);
                var previewCalls = workspace.Calls.ToArray();
                bool previewPreserved = File.ReadAllBytes(target).SequenceEqual(bytes);
                workspace.Calls.Clear();
                var applied = studio.MapProject("", "", false, null);
#if STUDIO_VCI_MODERN
                var states = workspace.MappedObjects.Select(m => m.Status.Calls);
#else
                var states = workspace.Mappings.Select(m => m.Status.Calls);
#endif
                replies.Add(scenario, new { preview, previewCalls, previewPreserved, apply = applied, workspace.Calls, states, stem,
                    preserved = File.ReadAllBytes(target).SequenceEqual(bytes) });
                continue;
            }
#endif
            if (scenario == "sameName") plc.BlockGroup.Blocks.Add(new PlcBlock { Name = block.Name, Write = block.Write });
            workspace.Mappings.RefuseInitialStatus = scenario == "newStatusRefused";
            workspace.Mappings.InitialState = scenario is "unknown" or "newStatusRefused" ? CompareState.Unknown
                : scenario is "unequal" or "syncRefused" ? CompareState.Unequal : CompareState.Equal;
            workspace.Mappings.RefreshesKnownState = refreshesKnownState;
            if (scenario == "mixed-reuse")
            {
                File.WriteAllText(file, xml);
                string otherXml = xml.Replace("MCP_RC_FC", "NewFC");
                plc.BlockGroup.Blocks.Add(new PlcBlock { Name = "NewFC", Write = (target, _) => File.WriteAllText(target.FullName, otherXml) });
#if !STUDIO_VCI_INITIAL
                workspace.Mappings.InitialState = CompareState.Unknown;
                workspace.Mappings.WriteOnSync = target => File.WriteAllText(target.FullName, otherXml);
#endif
            }
            if (scenario == "nativeSuccess")
            {
                workspace.Mappings.InitialState = CompareState.Unknown;
                workspace.Mappings.WriteOnSync = target => File.WriteAllText(target.FullName, xml);
            }
            workspace.Mappings.RefuseCreate = scenario is "createRefused" or "lookupRefused";
            workspace.Mappings.RefuseLookupAfterCreate = scenario == "lookupRefused";
            workspace.Mappings.RefuseSync = scenario == "syncRefused";
#if STUDIO_VCI_MODERN
            if (scenario == "disconnected-status")
            {
                var disposed = workspace.ConnectObject(block, root, "Motor", "xml");
                disposed.Status.ReadError = new ObjectDisposedException(nameof(MappedObject));
                replies.Add(scenario, new { reply = adapter.GetVersionControlStatus() });
                continue;
            }
            if (scenario == "seedDisposed")
            {
                var first = workspace.ExportObject(block, root, "Motor", "xml");
                first.Status.State = CompareState.Unequal;
                first.Status.OnSynchronized = () => throw new EngineeringObjectDisposedException("Mapped object disposed");
                var next = workspace.ExportObject(new PlcType(), root, "Speed", "xml");
                next.Status.State = CompareState.Unequal;
                var reply = adapter.SyncVersionControlWorkspace(dryRun: false);
                replies.Add(scenario, new { reply, states = workspace.MappedObjects.Select(mapping => mapping.Status.Calls) });
                continue;
            }
            if (scenario == "repair")
            {
                var first = workspace.ExportObject(block, root, "Motor", "xml");
                first.Status.State = CompareState.Unequal;
                first.Status.ReadError = new EngineeringTargetInvocationException("First status unavailable password=fixture-password");
                var second = workspace.ExportObject(new PlcType(), root, "Speed", "xml");
                second.Status.State = CompareState.Unequal;
                second.Status.ReadError = new EngineeringTargetInvocationException("Second status unavailable");
                var preview = adapter.SyncVersionControlWorkspace(dryRun: true, changedOnly: false);
                var reply = adapter.SyncVersionControlWorkspace(dryRun: false, changedOnly: false);
                replies.Add(scenario, new { preview, reply, states = workspace.MappedObjects.Select(m => m.Status.Calls) });
                continue;
            }
#else
#if STUDIO_VCI_INITIAL
            if (scenario == "repairRefused")
            {
                var mapping = new WorkspaceMapping(workspace, "MCP_RC_PLC_MCP_RC_FC.xml", block);
                mapping.Status.RefreshError = new EngineeringTargetInvocationException(Deleted);
                workspace.Mappings.Add(mapping);
                var reply = adapter.SyncVersionControlWorkspace(dryRun: false);
                replies.Add(scenario, new { reply, workspace.Calls, states = mapping.Status.Calls,
                    contents = File.Exists(file) ? File.ReadAllText(file) : null });
                continue;
            }
#endif
            if (scenario == "repair")
            {
                var nested = root.CreateSubdirectory("Types");
                var repair = new WorkspaceMapping(workspace, @"\Types\MCP_RC_PLC_MCP_RC_B.xml", new PlcType { Name = "MCP_RC_B" });
                var existing = new WorkspaceMapping(workspace, @"\MCP_RC_PLC_默认变量表.xml", new PlcTagTable { Name = "默认变量表" });
                ((PlcType)repair.LinkedProjectObject).Write = (target, options) =>
                {
                    workspace.Calls.Add("Export:" + target.Name + ":" + options);
                    File.WriteAllText(target.FullName, typeXml);
                    repair.Status.RefreshError = null;
                };
                repair.Status.RefreshError = new EngineeringTargetInvocationException(Deleted + " password=fixture-password " + new string('界', 1600));
                existing.Status.State = CompareState.Unequal;
                File.WriteAllText(Path.Combine(root.FullName, "MCP_RC_PLC_默认变量表.xml"), "existing contents");
                var stale = new WorkspaceMapping(workspace, "MCP_RC_PLC_MCP_RC_A.xml");
                stale.Status.RefreshError = new EngineeringObjectDisposedException(Disposed);
                var storedEqual = new WorkspaceMapping(workspace, "Stored.Equal.xml", new PlcBlock { Name = "Stored.Equal" });
                storedEqual.Status.State = CompareState.Equal;
                workspace.Mappings.AddRange(new[] { stale, repair, existing, storedEqual });
                var preview = adapter.SyncVersionControlWorkspace(dryRun: true, changedOnly: false);
                var previewCalls = workspace.Calls.ToArray();
                var changedPreview = adapter.SyncVersionControlWorkspace(dryRun: true, changedOnly: true);
                var mappedPath = Path.Combine(nested.FullName, "MCP_RC_PLC_MCP_RC_B.xml");
                File.WriteAllText(mappedPath, typeXml);
                var presentPreview = adapter.SyncVersionControlWorkspace(dryRun: true, changedOnly: false);
                File.Delete(mappedPath);
                var reply = adapter.SyncVersionControlWorkspace(dryRun: false, changedOnly: false);
                replies.Add(scenario, new { preview, changedPreview, presentPreview, previewCalls, reply, workspace.Calls,
                    states = workspace.Mappings.Select(m => m.Status.Calls).ToArray(),
                    repaired = File.Exists(Path.Combine(nested.FullName, "MCP_RC_PLC_MCP_RC_B.xml")),
                    storedFileExists = File.Exists(Path.Combine(root.FullName, "Stored.Equal.xml")),
                    original = File.ReadAllText(Path.Combine(root.FullName, "MCP_RC_PLC_默认变量表.xml")) });
                continue;
            }
#endif
            if (scenario.StartsWith("rc7-", StringComparison.Ordinal))
            {
#if STUDIO_VCI_MODERN
                workspace.ExportFormat = "SimaticML";
#endif
                string marker = scenario == "rc7-member" ? "other content" : "vci7 prewritten";
                string edited = scenario == "rc7-member" ? xml.Replace("</MultiLanguageText>", marker + "</MultiLanguageText>")
                    : xml.Replace("<Text />", "<Text>" + marker + "</Text>");
                if (!edited.Contains(marker)) throw new InvalidDataException("No empty multilingual text in the recorded XML.");
                if (scenario == "rc7-reconnect")
                {
                    // The official export is semantically equal. Reusing it must preserve the operator's bytes, including the BOM.
                    File.WriteAllText(file, xml, new System.Text.UTF8Encoding(true));
#if STUDIO_VCI_MODERN
                    workspace.ConnectObject(new PlcBlock(), root, Path.GetFileNameWithoutExtension(file), "xml");
#else
                    var stale = new WorkspaceMapping(workspace, Path.GetFileName(file));
                    stale.Status.RefreshError = new EngineeringObjectDisposedException(Disposed);
                    workspace.Mappings.Add(stale);
#endif
                    workspace.Calls.Clear();
                }
                if (scenario is "rc7-comment" or "rc7-member" or "rc7-fallback-differs") File.WriteAllText(file, edited);
#if STUDIO_VCI_INITIAL
                if (scenario == "rc7-member")
                {
                    var stale = new WorkspaceMapping(workspace, Path.GetFileName(file));
                    stale.Status.RefreshError = new EngineeringObjectDisposedException(Disposed);
                    workspace.Mappings.Add(stale);
                    // V16/V17: the import changed the project's Static.Count comment, leaving the workspace DB5 stale.
                    xml = xml.Replace("V18", refreshesKnownState ? "V16" : "V17");
                    File.WriteAllText(file, xml);
                    xml = xml.Replace("</MultiLanguageText>", "vci7 member</MultiLanguageText>");
                    string fb = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MCP_RC_FB.xml"));
                    File.WriteAllText(Path.Combine(root.FullName, device.Name + "_MCP_RC_FB.xml"), fb);
                    plc.BlockGroup.Blocks.Add(new PlcBlock { Name = "MCP_RC_FB", Write = (target, _) => File.WriteAllText(target.FullName, fb) });
                }
#endif
#if STUDIO_VCI_MODERN
                if (scenario == "rc7-sidecar")
                {
                    workspace.ExportFormat = "SIMATIC SD";
                    file = Path.ChangeExtension(file, ".s7res");
                    File.WriteAllText(file, marker);
                }
#endif
                var preview = adapter.ConnectProjectToWorkspace();
                if (scenario == "rc7-after-preview") File.WriteAllText(file, edited);
                if (scenario == "rc7-after-create")
                    workspace.Mappings.AfterCreate = target => File.WriteAllText(target.FullName, edited);
                var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
                workspace.Calls.Clear();
                var reply = adapter.ConnectProjectToWorkspace(dryRun: false);
                var connectedStatus = adapter.GetVersionControlStatus(changedOnly: true);
                replies.Add(scenario, new { preview, reply, status = connectedStatus, workspace.Calls,
                    preserved = original != null && File.ReadAllBytes(file).SequenceEqual(original),
                    commentAfterCreate = scenario == "rc7-after-create" && File.Exists(file) && File.ReadAllText(file) == edited });
                continue;
            }
#if STUDIO_VCI_INITIAL
            if (scenario is "multipleDisconnected" or "manyDisconnected")
            {
                int count = scenario == "multipleDisconnected" ? 2 : 32;
                for (int index = 0; index < count; index++)
                {
                    string name = "MCP_RC_FC_" + index.ToString("D2");
                    string filename = device.Name + "_" + name + ".xml";
                    string current = xml.Replace("MCP_RC_FC", name);
                    File.WriteAllText(Path.Combine(root.FullName, filename), current.Replace("<Name>" + name, "<Name>Old_" + name));
                    workspace.Mappings.Add(new WorkspaceMapping(workspace, filename));
                    plc.BlockGroup.Blocks.Add(new PlcBlock { Name = name, Write = (target, _) => File.WriteAllText(target.FullName, current) });
                }
                if (scenario == "multipleDisconnected")
                {
                    var write = block.Write;
                    block.Write = (_, _) => throw new EngineeringUserAbortException("Interrupted during the remaining export");
                    replies["uncertainDisconnected"] = new { reply = adapter.ConnectProjectToWorkspace(dryRun: false) };
                    block.Write = write;
                }
                replies[scenario] = new { reply = adapter.ConnectProjectToWorkspace(dryRun: false) };
                continue;
            }
#endif
            if (scenario == "differentXml")
            {
                var target = new DirectoryInfo(Path.Combine(root.FullName, "MCP_RC_Ws"));
                workspace.RootPath = target;
                replies["rc7-missing-workspace"] = new { reply = adapter.ConnectProjectToWorkspace(),
                    apply = adapter.ConnectProjectToWorkspace(dryRun: false) };
                File.WriteAllText(target.FullName, "occupied root");
                replies["file-workspace"] = new { reply = adapter.ConnectProjectToWorkspace(), apply = adapter.ConnectProjectToWorkspace(dryRun: false),
                    contents = File.ReadAllText(target.FullName), calls = workspace.Calls.ToArray() };
                workspace.RootPath = root;
            }
            var absent = adapter.ConnectProjectToWorkspace();
            if (scenario == "collision") File.WriteAllText(file, "existing contents");
            if (scenario == "differentXml") File.WriteAllText(file, xml.Replace("<Name>MCP_RC_FC</Name>", "<Name>Other</Name>"));
            if (scenario == "reuse") File.WriteAllText(file, xml);
            var plan = adapter.ConnectProjectToWorkspace();
            var before = workspace.Calls.ToArray();
            var apply = VersionControlAdapter.WithWriteOutcome(new TiaMcp.Adapters.Contracts.VersionControlRequest
                { Operation = "ConnectProjectToWorkspace", DryRun = false }, adapter.ConnectProjectToWorkspace(dryRun: false));
            replies.Add(scenario, new { absent, preview = plan, before, reply = apply, workspace.Calls,
                states = workspace.Mappings.Select(m => m.Status.Calls).ToArray(),
                contents = File.Exists(file) ? File.ReadAllText(file) : null });
        }
#if STUDIO_VCI_INITIAL
        var studioRoot = parent.CreateSubdirectory("studio");
        var studioWorkspace = new Workspace(studioRoot);
        var studioProject = new Project();
        studioProject.Vci.WorkspaceGroup.Workspaces.Add(studioWorkspace);
        var studio = new TiaOpenness.Openness.OpennessVersionControl(() => studioProject);
        var cpu = new Device { Name = "MCP_RC_PLC" };
        studioProject.Devices.Add(cpu);
        var module = new DeviceItem();
        cpu.DeviceItems.Add(module);
        var software = (PlcSoftware)module.Container.Software;
        var table = new PlcTagTable { Name = "默认变量表" };
        software.TagTableGroup.TagTables.Add(table);
        table.Write = (file, options) =>
        {
            studioWorkspace.Calls.Add("Export:" + file.Name + ":" + options);
            File.WriteAllText(file.FullName, "<Document><SW.Tags.PlcTagTable ID=\"0\"><AttributeList><Name>默认变量表</Name>"
                + "</AttributeList></SW.Tags.PlcTagTable></Document>");
        };
        var studioPreview = studio.MapProject("", "", true, null);
        var previewWrites = studioWorkspace.Calls.ToArray();
        var studioApply = studio.MapProject("", "", false, null);
        var mapped = studioWorkspace.Mappings.Single();
        var firstCalls = studioWorkspace.Calls.ToArray();
        var status = mapped.Status.Calls.ToArray();
        string path = Path.Combine(studioRoot.FullName, mapped.RelativeWorkspacePath);
        File.Delete(path);
        mapped.Status.State = CompareState.Unequal;
        var restored = studio.Sync("", TiaMcp.Adapters.Contracts.Studio.SyncDirection.ProjectToWorkspace, false, null);
        mapped.Status.RefreshError = new EngineeringObjectDisposedException(Disposed);
        mapped.Status.Calls.Clear();
        var staleSync = studio.Sync("", TiaMcp.Adapters.Contracts.Studio.SyncDirection.ProjectToWorkspace, false, null);
        studioWorkspace.Mappings.Clear();
        var collision = studio.MapProject("", "", false, null);
        replies.Add("studio", new { studioPreview, previewWrites, studioApply, firstCalls, status, restored, stale = staleSync, collision,
            staleCalls = mapped.Status.Calls, contents = File.Exists(path) ? File.ReadAllText(path) : null, studioWorkspace.Calls });
#endif
#if !STUDIO_VCI_MODERN
        replies.Add("proof", VciProofFixture.Run(parent));
        replies.Add("sequences", VciProofFixture.ProofAfterOtherOperations(parent));
        replies.Add("invalidPaths", VciProofFixture.UncheckableFiles(parent, true));
        replies.Add("unreadableRoot", VciProofFixture.UncheckableFiles(parent, false));
#endif
        return replies;
    }
}
