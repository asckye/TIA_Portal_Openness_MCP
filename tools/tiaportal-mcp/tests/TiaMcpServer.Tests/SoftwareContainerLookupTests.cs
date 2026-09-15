using System;
using System.Collections.Generic;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
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
    }
}
