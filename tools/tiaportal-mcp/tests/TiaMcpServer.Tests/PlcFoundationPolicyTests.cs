using System;
using System.IO;
using System.Text;
using TiaMcp.PlcFoundation;

namespace TiaMcpServer.Tests
{
    internal static class PlcFoundationPolicyTests
    {
        private static bool Refuses(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
            catch (System.Xml.XmlException) { return true; }
        }
        internal static void Run(Action<bool, string> check)
        {
            foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
            {
                PlcFoundationPolicy.RequireRelease(key, key);
                check(true, "foundation exact compiled release " + key);
                check(Refuses(() => PlcFoundationPolicy.RequireRelease(key, key == "21" ? "20" : "21")), "foundation cross-release refusal " + key);
            }
            foreach (var key in new[] { "14", "15", "15.10", "22", "" })
                check(Refuses(() => PlcFoundationPolicy.RequireRelease(key, key)), "foundation excluded/unknown release " + key);
            check(PlcFoundationPolicy.Segment("a/b") == "a%2Fb", "software/group path encodes slash in object name");
            check(PlcFoundationPolicy.Segment("a%2Fb") == "a%252Fb", "encoded name cannot alias another path");
            foreach (var name in new[] { "", " ", ".", "..", "a/b", "a\\b" })
                check(Refuses(() => PlcFoundationPolicy.RequireName(name)), "new name rejects path syntax " + name);
            check(PlcFoundationPolicy.Exact(new[] { "A/x", "B/x" }, x => x, "B/x") == "B/x", "exact group-qualified lookup");
            check(Refuses(() => PlcFoundationPolicy.Exact(new[] { "A/x", "B/x" }, x => x, "x")), "no basename fallback");
            check(Refuses(() => PlcFoundationPolicy.Exact(new[] { "A/x" }, x => x, "a/x")), "no ambiguous case folding");
            check(Refuses(() => PlcFoundationPolicy.Exact(new[] { "A/x", "A/x" }, x => x, "A/x")), "duplicate exact matches refused");
            check(Refuses(() => PlcFoundationPolicy.Exact(new[] { "", "A" }, x => x, "missing")), "missing group does not import into root");
            var temp = Path.Combine(Path.GetTempPath(), "tia-foundation-offline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var file = Path.Combine(temp, "input.xml");
                var original = "<?xml version=\"1.0\"?><Document><Engineering version=\"V17\"/></Document>";
                File.WriteAllText(file, original, new UTF8Encoding(true));
                var bytes = File.ReadAllBytes(file);
                check(PlcFoundationPolicy.XmlInput(file).FullName == file, "Openness XML input accepted");
                check(Convert.ToBase64String(bytes) == Convert.ToBase64String(File.ReadAllBytes(file)), "input is not rewritten or silently retargeted");
                check(Refuses(() => PlcFoundationPolicy.XmlOutput(file)), "export refuses existing file");
                var output = Path.Combine(temp, "new.xml");
                check(PlcFoundationPolicy.XmlOutput(output).FullName == output && !File.Exists(output), "export planning creates no file");
                check(Refuses(() => PlcFoundationPolicy.XmlInput(output)), "missing import refused");
                check(Refuses(() => PlcFoundationPolicy.XmlOutput(Path.Combine(temp, "absent", "a.xml"))), "missing output directory refused");
                check(Refuses(() => PlcFoundationPolicy.XmlOutput(Path.Combine(temp, "a.s7dcl"))), "document route cannot masquerade as XML");
                File.WriteAllText(file, "<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///never-read'>]><Document>&x;</Document>");
                check(Refuses(() => PlcFoundationPolicy.XmlInput(file)), "DTD and external entity imports refused");
                File.WriteAllText(file, "<Other/>");
                check(Refuses(() => PlcFoundationPolicy.XmlInput(file)), "non-Openness root refused");
                File.WriteAllText(file, "<Document>");
                check(Refuses(() => PlcFoundationPolicy.XmlInput(file)), "malformed XML refused before native import");
            }
            finally { Directory.Delete(temp, true); }
        }
    }
}
