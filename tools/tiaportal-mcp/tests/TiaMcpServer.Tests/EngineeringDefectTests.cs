using System;
using System.IO;
using System.Collections.Generic;
using TiaMcpServer.Siemens;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    internal static class EngineeringDefectTests
    {
        public enum EventType { Down, Tapped, Up }
        public sealed class Events
        {
            public readonly Dictionary<EventType, object> Items = new Dictionary<EventType, object>();
            public int Creates;
            public object? Find(EventType kind) => Items.TryGetValue(kind, out var v) ? v : null;
            public object Create(EventType kind) { Creates++; return Items[kind] = new object(); }
        }
        public sealed class PropertyFixture
        {
            public string? Empty => null;
            public PropertyFixture? Child => null;
            public string Failure => throw new IOException("offline proxy");
            public string Name => "screen";
        }
        public sealed class Message
        {
            public string State { get; set; } = "Success";
            public string Description { get; set; } = "";
            public int ErrorCount { get; set; }
            public int WarningCount { get; set; }
            public string Path { get; set; } = "HMI/Screen";
            public readonly List<Message> Children = new List<Message>();
            public int ChildReads;
            public IEnumerable<Message> Messages { get { ChildReads++; return Children; } }
        }
        public sealed class Exporter
        {
            public string Mode = "ok";
            public void Export(FileInfo path)
            {
                if (Mode == "none") return;
                File.WriteAllText(path.FullName, Mode == "empty" ? "" : "<Export/>");
                if (Mode == "throw") throw new IOException("export interrupted");
            }
        }
        public enum ArchiveMode { None, Compressed, DiscardRestorableData }
        public sealed class ArchiveFixture
        {
            public bool IsModified {get;set;}
            public int Calls;
            public void Archive(DirectoryInfo directory,string name,ArchiveMode mode)
            { if(mode!=ArchiveMode.Compressed)throw new Exception("wrong mode");Calls++;File.WriteAllText(Path.Combine(directory.FullName,name),"fake archive"); }
        }
        private static string Failure(Action action)
        { try { action(); return "none"; } catch (PortalException ex) { return ex.Code.ToString(); } }
        internal static void Run(Action<bool,string> check)
        {
            Console.WriteLine("== Engineering defects: read side effects, null paths, compile scopes, safe export ==");
            var events = new Events(); var down = events.Create(EventType.Down);
            check(ReferenceEquals(HmiEventAccess.Resolve(events, "down"), down) && events.Creates == 1, "existing event read is pure");
            check(Failure(() => HmiEventAccess.Resolve(events, "Tapped")) == "NotFound" && events.Creates == 1, "missing event read never creates");
            foreach(var bad in new[] { "77", "Down, Up", "missing", "" })
                check(Failure(() => HmiEventAccess.Resolve(events, bad)) == "InvalidParams" && events.Creates == 1, "invalid event rejected: " + bad);
            HmiEventAccess.Resolve(events, "Tapped", true);
            check(events.Creates == 2 && events.Items.Count == 2, "explicit ensure still creates");
            var p = new PropertyFixture();
            check(PropertyPathReader.Read(p,"Empty").Success && PropertyPathReader.Read(p,"Empty").Value == null, "real null succeeds");
            foreach(var test in new[] { ("Missing","PropertyNotFound"), ("Child.Name","NullIntermediate"), ("Children[0]","UnsupportedPath"), ("Name..Length","UnsupportedPath"), ("Failure","ReadFailed") })
                check(PropertyPathReader.Read(p,test.Item1).Status == test.Item2, "path diagnosis " + test.Item1);
            check(PropertyPathReader.Read(null,"Name").Status == "ObjectNotFound", "root missing differs from null");
            var root = new Message(); root.Children.Add(new Message { State = "Error", Description = "missing tag" });
            var collected = McpServer.CollectCompilerMessages(new[]{root});
            check(collected.Summary("Success",0,0)["effectiveState"]!.ToString() == "Error" && root.ChildReads == 1, "root success cannot hide child error; children read once");
            check(collected.Nodes[1]!["treePath"]!.ToString() == "messages/0/messages/0", "diagnostic tree paths retained");
            check(!collected.Summary("Success",0,0)["success"]!.GetValue<bool>(), "compile business failure propagated");
            check(McpServer.CollectCompilerMessages(Array.Empty<object>()).Summary("Error",2,0)["effectiveState"]!.ToString()=="Error", "root error retained");
            check(McpServer.CollectCompilerMessages(new[]{new Message{State="Warning"}}).Summary("Success",0,0)["effectiveState"]!.ToString()=="Warning", "warning only");
            check(McpServer.CollectCompilerMessages(Array.Empty<object>()).Summary("Success",0,0)["success"]!.GetValue<bool>(), "unchanged incremental compile success");
            var limit=McpServer.CollectCompilerMessages(new[]{new Message{Description="3003 错误、688 警告，超过最大显示 1000 条"}});
            check(limit.Truncated && limit.HasError && limit.DeclaredTotals[0]!["errors"]!.GetValue<int>()==3003, "declared totals with source and upstream limit");
            check(McpServer.CollectCompilerMessages(new[]{new Message(),new Message()},1).Truncated, "local node cap reports incomplete");
            var duplicate=McpServer.CollectCompilerMessages(new[]{new Message{State="Error",Description="same"},new Message{State="Error",Description="same"}});
            check(duplicate.Nodes.Count==2 && duplicate.Errors.Count==2, "same diagnostic text at distinct nodes is retained");
            root.Children.Add(root);
            check(McpServer.CollectCompilerMessages(new[]{root}).CollectFailures.Count==1, "cycles bounded and incomplete");
            var directory=Path.Combine(Path.GetTempPath(),"tia-export-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try
            {
                var path=Path.Combine(directory,"backup.xml");
                foreach(var mode in new[]{"unsupported","none","empty","throw"})
                {
                    File.WriteAllText(path,"old backup");
                    var result=EngineeringExport.Export(mode=="unsupported" ? new object() : new Exporter{Mode=mode},path);
                    check(!result["success"]!.GetValue<bool>() && File.ReadAllText(path)=="old backup", "failed export preserves old file: "+mode);
                }
                var ok=EngineeringExport.Export(new Exporter(),path);
                check(ok["success"]!.GetValue<bool>() && File.ReadAllText(path)=="<Export/>" && ok["sha256"]!.ToString().Length==64, "validated export atomically replaces target");
                var collision=EngineeringExport.Export(new Exporter(),path,overwrite:false);
                check(!collision["success"]!.GetValue<bool>()&&File.ReadAllText(path)=="<Export/>","no-overwrite commit preserves a raced/existing destination");
                var project=new ArchiveFixture{IsModified=true};var archive=Path.Combine(directory,"test.zap21");
                check(Failure(()=>ProjectArchive.Create(project,archive,false))=="InvalidState"&&project.Calls==0,"archive rejects unsaved project without auto-save");
                project.IsModified=false;
                check(ProjectArchive.Create(project,archive)["dryRun"]!.GetValue<bool>()&&project.Calls==0,"native archive defaults to preview");
                check(ProjectArchive.Create(project,archive,false)["success"]!.GetValue<bool>()&&project.Calls==1&&File.Exists(archive),"native compressed archive validates artifact");
                check(Failure(()=>ProjectArchive.Create(project,archive,false))=="InvalidParams"&&project.Calls==1,"native archive never overwrites existing backup");
                check(Failure(()=>ProjectArchive.Create(new object(),Path.Combine(directory,"unsupported.zap21"),false))=="NotSupportedOnVersion","unsupported project/session rejected before file writes");
            }
            finally { Directory.Delete(directory,true); }
        }
    }
}
