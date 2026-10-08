using System;
using System.Collections.Generic;
using TiaMcpServer.Siemens;

namespace TiaMcp.Engine.Tests
{
    internal static class SoftwareContainerLookupTests
    {
        private sealed class Software
        {
            public string Name = "+S1-K1";
            public object BlockGroup => throw new Exception("Software resolution must not read blocks");
        }
        private sealed class Node
        {
            public string? Alias;
            public bool IsGroup;
            public Software? Software;
            public Exception? Failure;
            public List<object> Children = new List<object>();
        }
        private static Software? Resolve(string name, int limit, params object[] roots)
            => SoftwareContainerLookup.FindUnique(roots,
                n => ((Node)n).Children, n => ((Node)n).Alias,
                n => ((Node)n).Failure != null ? throw ((Node)n).Failure! : ((Node)n).Software,
                s => s.Name, name, limit);

        internal static void Run(Action<bool, string> check)
        {
            check(Guard.MatchPlcName(null, "PLC_1") == null, "Guard has no match without an inventory");
            check(Guard.MatchPlcName(Array.Empty<string>(), "") == null, "Guard empty inventory cannot select a PLC");
            check(Guard.MatchPlcName(new[] { "PLC_1" }, "PLC_1") == "PLC_1", "Guard retains exact match");
            check(Guard.MatchPlcName(new[] { "PLC_1", "PLC_2" }, " plc_1 ") == "PLC_1", "Guard retains trimmed case-insensitive exact match");
            check(Guard.MatchPlcName(new[] { " PLC_1 " }, "plc_1") == " PLC_1 ", "Guard preserves the original matched name");
            check(Guard.MatchPlcName(new[] { "PLC_1" }, "WrongPLC") == null, "Guard no longer guesses the sole PLC for a wrong name");
            check(Guard.MatchPlcName(new[] { "PLC_1" }, "PLC") == null, "Guard no longer matches a short substring");
            check(Guard.MatchPlcName(new[] { "PLC_1" }, "Prefix_PLC_1_suffix") == null, "Guard no longer matches a long containing token");
            check(Guard.MatchPlcName(new[] { "PLC_1", "CPU_2" }, "PLC") == null, "Guard no longer matches a unique substring in a multi-PLC project");
            check(Guard.MatchPlcName(new[] { "PLC_1" }, null) == "PLC_1", "Guard null still selects the sole PLC");
            check(Guard.MatchPlcName(new[] { "PLC_1" }, " ") == "PLC_1", "Guard whitespace still selects the sole PLC");
            check(Guard.MatchPlcName(new[] { "PLC_1", "PLC_2" }, "") == null, "Guard empty token rejects two PLCs");
            check(Guard.MatchPlcName(new[] { "PLC_1", " plc_1 " }, "PLC_1") == null, "Guard duplicate normalized exact names stay ambiguous");
            check(Guard.MatchPlcName(new[] { "+S1-K1" }, "ET 200SP station_1") == null, "Guard cannot invent a station alias from a software name");
            StrictPlcChecks(check);
            var software = new Software();
            var cpu = new Node { Alias = "CPU hardware", Software = software };
            var device = new Node { Alias = "Station", Children = new List<object> { cpu } };
            var group = new Node { Children = new List<object> { new Node { Children = new List<object> { device } } } };
            check(ReferenceEquals(Resolve("+S1-K1", 100, group), software), "PLC software name resolves inside nested device groups without reading BlockGroup");
            check(ReferenceEquals(Resolve("Station", 100, group), software), "device alias locates its software-bearing descendant");
            check(ReferenceEquals(Resolve("CPU hardware", 100, group), software), "hardware item alias locates software");
            check(ReferenceEquals(Resolve("+s1-k1", 100, group), software), "software name matching preserves existing case-insensitive behavior");
            check(Resolve("+S1", 100, group) == null, "device group is not mistaken for its PLC");
            check(Resolve(".*", 100, group) == null, "software path is literal, never a regex");
            check(Resolve("K1", 100, group) == null, "no substring or single-PLC fallback for shared write resolver");
            check(Resolve(" +S1-K1", 100, group) == null, "no silent whitespace correction");
            check(Resolve("+S1\u2011K1", 100, group) == null, "nonbreaking hyphen is not substituted for literal ASCII hyphen");
            var second = new Node { Software = new Software() };
            bool ambiguous = false;
            try { Resolve("+S1-K1", 100, group, second); }
            catch (PortalException ex) { ambiguous = ex.Code == PortalErrorCode.InvalidParams; }
            check(ambiguous, "same PLC name in different groups fails explicitly instead of choosing one");
            check(ReferenceEquals(Resolve("+S1-K1", 100, cpu, new Node { Software = software }), software), "same container referenced twice is not a false ambiguity");
            var broken = new Node { Failure = new ObjectDisposedException("SoftwareContainer") };
            bool propagated = false;
            try { Resolve("+S1-K1", 100, broken, cpu); }
            catch (ObjectDisposedException) { propagated = true; }
            check(propagated, "partial scan cannot certify uniqueness after a disposed proxy");
            bool bounded = false;
            try { Resolve("+S1-K1", 1, group); }
            catch (PortalException ex) { bounded = ex.Code == PortalErrorCode.OpennessError; }
            check(bounded, "node limit reports incomplete lookup rather than not found");
            cpu.Children.Add(cpu);
            check(ReferenceEquals(Resolve("+S1-K1", 100, group), software), "cyclic hardware reference is bounded by identity tracking");
            cpu.Children.Clear();
            software.Name = "5T车";
            check(ReferenceEquals(Resolve("5T车", 100, group), software), "Chinese PLC name remains supported");
        }

        private static void StrictPlcChecks(Action<bool, string> check)
        {
            var first = new Software { Name = "PLC_1" };
            var cpu = new Node { Alias = "CPU_1", Software = first };
            var station = new Node { Alias = "ET 200SP station_1", Children = new List<object> { cpu } };
            var group = new Node { Alias = "Line", IsGroup = true, Children = new List<object> { station } };
            string paths = "";
            Software? Find(string name, int limit = 100, params object[] roots)
                => SoftwareContainerLookup.FindPlc(roots.Length == 0 ? new object[] { group } : roots,
                    n => ((Node)n).Children, n => ((Node)n).Alias,
                    n => ((Node)n).Failure != null ? throw ((Node)n).Failure! : ((Node)n).Software,
                    value => value.Name, n => ((Node)n).IsGroup, name, value => paths = value, limit);
            foreach (var name in new[] { "PLC_1", " plc_1 ", "CPU_1", "ET 200SP station_1", "Line/CPU_1", "line/ET 200SP station_1", "Line/ET 200SP station_1/CPU_1", "Line/PLC_1", "" })
                check(ReferenceEquals(Find(name), first), "strict PLC resolves exact or structural alias: " + name);
            foreach (var name in new[] { "WrongPLC", "PLC", "Prefix_PLC_1_suffix", "Line", "Wrong/PLC_1", "Line/CPU_1/garbage", ".*" })
                check(Find(name) == null && paths.Contains("Line/ET 200SP station_1/CPU_1/PLC_1"), "strict PLC refuses unknown name with available paths: " + name);
            var second = new Software { Name = "PLC_2" };
            var other = new Node { Alias = "ET 200SP station_1", Children = new List<object> { new Node { Alias = "CPU_2", Software = second } } };
            check(Find("", 100, group, other) == null, "strict PLC empty name preserves multiple-PLC failure");
            check(ReferenceEquals(Find("CPU_1", 100, group, other), first), "strict PLC own name stays unique in multiple-PLC project");
            bool ambiguous = false;
            try { Find("ET 200SP station_1", 100, group, other); }
            catch (PortalException ex) { ambiguous = ex.Code == PortalErrorCode.InvalidParams && ex.Message.Contains("Ambiguous PLC software name") && ex.Message.Contains("PLC_1") && ex.Message.Contains("PLC_2"); }
            check(ambiguous, "strict PLC duplicate station alias lists both candidates");
            station.Children.Add(other.Children[0]);
            ambiguous = false;
            try { Find("ET 200SP station_1"); } catch (PortalException ex) { ambiguous = ex.Code == PortalErrorCode.InvalidParams; }
            check(ambiguous, "strict PLC station owning two PLCs is ambiguous");
            station.Children.RemoveAt(1);
            check(ReferenceEquals(Find("Line/ET 200SP station_1", 100, group, other), first), "strict PLC group-qualified alias disambiguates stations");
            check(ReferenceEquals(Find("PLC_1", 100, cpu, new Node { Software = first }), first), "strict PLC repeated container identity is not ambiguous");
            bool incomplete = false;
            try { Find("PLC_1", 1); } catch (PortalException ex) { incomplete = ex.Code == PortalErrorCode.OpennessError; }
            check(incomplete, "strict PLC bounded scan never certifies a partial result");
            bool interrupted = false;
            try { Find("PLC_1", 100, new Node { Failure = new ObjectDisposedException("CPU") }, cpu); }
            catch (ObjectDisposedException) { interrupted = true; }
            check(interrupted, "strict PLC finishes scanning even after finding a match");
            check(Find("", 100, new Node()) == null && paths == "", "strict PLC empty project has no sole PLC");
            station.Alias = "S7-1500/ET200MP station_1";
            check(ReferenceEquals(Find(station.Alias), first), "strict PLC literal slash in a station name stays exact");
        }
    }
}
