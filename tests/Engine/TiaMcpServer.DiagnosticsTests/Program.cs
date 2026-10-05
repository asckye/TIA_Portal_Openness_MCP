using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DiagnosticFixture;
using TiaMcpServer.ModelContextProtocol;

internal static class Program
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static void DisposeGeneric<T>(ref T value) where T : struct, IDisposable { value.Dispose(); }
    static T Echo<T>(Node node, T value) where T : class => node.Echo(value);
    private sealed class PrivateRow { public int Value; }
    private sealed class GenericBox<T>
    {
        private sealed class Row { public T Value = default!; }
        internal T Filter(T value) => new[] { new Row { Value = value } }.Where(x => x != null).Select(x => x.Value).Single();
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && (args[0] == "audit-writer" || args[0] == "audit-verify")) return AuditLogTests.Child(args);
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", Path.Combine(Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY")!, "data"));
            if (args.Length == 1 && args[0] == "abrupt") { InvocationJournal.Begin("abrupt"); Node.ExitNow(); return 99; }
            InvocationJournal.Begin("fixture");
            int before = Node.Calls;
            var node = new Node();
            Check(Node.Calls == before + 1, "constructor executes exactly once");
            before = Node.Calls;
            node.Name = "secret-password";
            Check(node.Name == "secret-value" && Node.Calls == before + 2, "setter/getter preserve results and do not add reads");
            Check(node.Child.Name == "secret-value", "nested receiver evaluation preserved");
            node[4] = "secret-index-value"; Check(node[4] == "4", "indexer read/write");
            before = Node.Calls;
            Exception? caught = null; try { _ = node.Bad; } catch (Exception ex) { caught = ex; }
            Check(ReferenceEquals(caught, Node.Failure) && Node.Calls == before + 1, "same exception instance escapes without retry");
            int value = 2; node.Ref(ref value, out string text);
            Check(value == 5 && text == "secret-ref-value", "ref/out semantics");
            Check(Node.Static(4) == 5 && Echo(node, "abc") == "abc", "static and open generic calls");
            var structure = new Value(); Check(structure.Add(4) == 4, "value type receiver mutation");
            DisposeGeneric(ref structure); Check(structure.Count == 5, "constrained generic receiver mutation");
            before = Node.Calls;
            var property = typeof(Node).GetProperty("Name")!;
            property.SetValue(node, "secret-reflection-password");
            Check((string)property.GetValue(node)! == "secret-value" && Node.Calls == before + 2, "reflective getter/setter");
            var method = typeof(Node).GetMethod("GetAttribute")!;
            Check((string)method.Invoke(node, new object[] { "SafeAttributeName" })! == "secret-value", "reflective invocation");
            node.SetAttribute("SafeAttributeName", "secret-attribute-value");
            var created = (Node)Activator.CreateInstance(typeof(Node))!;
            Check(created != null, "reflective construction");
            EventHandler handler = (_, __) => { }; node.Changed += handler; node.Changed -= handler;
            Check(true, "event add/remove accessors");
            Nodes.Order.Clear();
            var list = node.Children.ToList();
            Check(list.Count == 2 && string.Join(",", Nodes.Order) == "get,move,current,move,current,move,dispose", "LINQ enumeration preserves order and disposal");
            Nodes.Order.Clear();
            var copied = new List<Node>(node.Children);
            Check(copied.Count == 2 && Nodes.Order.Last() == "dispose", "collection constructor enumeration");
            Nodes.Order.Clear();
            foreach (var item in node.Children) { _ = item.Name; break; }
            Check(string.Join(",", Nodes.Order) == "get,move,current,dispose", "foreach early disposal");
            Nodes.Order.Clear();
            Check(((IEnumerable)node.Children).Cast<Node>().Count() == 2 && Nodes.Order.Last() == "dispose", "non-generic enumeration through LINQ");
            var local = new List<int> { 1, 2, 3 }; Check(local.Select(x => x * 2).Sum() == 12, "local collection behavior unchanged");
            CountedNodes.Order.Clear(); Check(node.Counted.Count() == 2 && string.Join(",", CountedNodes.Order) == "count", "Count fast path preserved without enumeration");
            CountedNodes.Order.Clear(); Check(node.Counted.ToList().Count == 2 && string.Join(",", CountedNodes.Order) == "count,copy", "ToList count/copy fast path preserved");
            CountedNodes.Order.Clear(); Check(node.Counted.ElementAt(1) != null && string.Join(",", CountedNodes.Order) == "item", "ElementAt indexer fast path preserved");
            var counted = node.Counted; Check(ReferenceEquals(counted.AsEnumerable(), counted), "AsEnumerable keeps identity");
            CountedNodes.Order.Clear();
            Check(((IEnumerable)counted).Cast<Node>().ToList().Count == 2 && string.Join(",", CountedNodes.Order) == "count,copy", "Cast preserves typed collection count/copy fast path");
            Check(ReferenceEquals(((IEnumerable)counted).Cast<Node>(), counted), "Cast preserves already typed collection identity");
            caught = null; try { _ = new BrokenNodes().ToList(); } catch (Exception ex) { caught = ex; }
            Check(ReferenceEquals(caught, Node.Failure) && string.Join(",", BrokenNodes.Order) == "move,dispose", "failed MoveNext retains original exception and disposes once");
            caught = null; try { _ = typeof(Node).GetProperty("Bad")!.GetValue(node); } catch (TargetInvocationException ex) { caught = ex.InnerException; }
            Check(ReferenceEquals(caught, Node.Failure), "reflection preserves TargetInvocationException wrapping");
            Check(new[] { new PrivateRow { Value = 3 } }.Where(r => r.Value == 3).Select(r => r.Value).Single() == 3, "private nested type access preserved across LINQ calls");
            Check(new GenericBox<int>().Filter(7) == 7 && new GenericBox<string>().Filter("abc") == "abc", "generic enclosing type and private nested type accessibility");
            node.Dispose();
            string directory = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY")!;
            var rows = Directory.GetFiles(directory, "calls-*.jsonl").SelectMany(File.ReadAllLines).Select(line => JsonNode.Parse(line)!.AsObject()).Where(row => row["nativeCallId"] != null).ToArray();
            Check(rows.Length > 50 && rows.GroupBy(row => (string)row["nativeCallId"]!).All(g => g.Count() == 2 && (string)g.First()["phase"]! == "BEFORE" && (string)g.Last()["phase"]! != "BEFORE"), "every completed call has one BEFORE and one terminal event");
            Check(rows.Any(row => (string)row["phase"]! == "THREW" && (string?)row["exceptionType"] == typeof(InvalidOperationException).FullName), "exception type recorded");
            Check(rows.All(row => row["threadId"] != null && row["apartment"] != null && row["callSite"] != null && row["objectPath"] != null), "thread and object lineage present");
            Check(rows.Any(row => ((string)row["callSite"]!).Contains("enumeration-adapter") && ((string)row["member"]!).Contains("MoveNext")), "external enumeration individual boundaries logged");
            Check(rows.Any(row => ((string)row["objectPath"]!).Contains("SafeAttributeName")), "attribute selector recorded without its value");
            string log = string.Join("\n", Directory.GetFiles(directory, "calls-*.jsonl").SelectMany(File.ReadAllLines));
            Check(!log.Contains("secret-"), "no argument/return/exception message leakage");
            string previous = directory;
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", "relative-invalid");
            before = Node.Calls; _ = node.Name;
            Check(Node.Calls == before + 1, "logging failure does not suppress or duplicate operation");
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", previous);
            Check((long)InvocationJournal.Health()["failedWrites"]! >= 2, "diagnostic I/O failures visible");
            InvocationJournalGoldenTests.Run(directory);
            AuditLogTests.Run(directory);
            Console.WriteLine("COMPLETE: " + checks + " instrumented diagnostic checks passed; no TIA connection");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
