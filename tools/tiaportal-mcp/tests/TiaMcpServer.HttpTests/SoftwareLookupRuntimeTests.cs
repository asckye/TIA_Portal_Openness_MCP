using System;
using System.Collections.Generic;
using System.Reflection;

internal static class SoftwareLookupRuntimeTests
{
    public sealed class Node
    {
        public string? Alias;
        public object? Container;
        public string SoftwareName = "+S1-K1";
        public List<object> Children = new List<object>();
        public object BlockGroup => throw new Exception("Unexpected block access");
    }
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var lookup = server.GetType("TiaMcpServer.Siemens.SoftwareContainerLookup", true)!
            .GetMethod("FindUnique", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(object));
        var cpu = new Node(); cpu.Container = cpu;
        var device = new Node { Alias = "Station", Children = new List<object> { cpu } };
        var group = new Node { Children = new List<object> { new Node { Children = new List<object> { device } } } };
        object? Resolve(string name, object[] roots, int limit = 100)
            => lookup.Invoke(null, new object[] { roots,
                new Func<object, IEnumerable<object>>(n => ((Node)n).Children),
                new Func<object, string?>(n => ((Node)n).Alias),
                new Func<object, object?>(n => ((Node)n).Container),
                new Func<object, string?>(n => ((Node)n).SoftwareName), name, limit });
        check(ReferenceEquals(Resolve("+S1-K1", new object[] { group }), cpu), "actual EXE resolves IEC software name below nested device groups");
        check(ReferenceEquals(Resolve("Station", new object[] { group }), cpu), "actual EXE resolves device alias to software descendant");
        check(Resolve(".*", new object[] { group }) == null, "actual EXE does not interpret software name as regex");
        check(Resolve("K1", new object[] { group }) == null, "actual EXE does not guess a lone PLC from substring");
        var other = new Node(); other.Container = other;
        bool ambiguous = false;
        try { Resolve("+S1-K1", new object[] { group, other }); }
        catch (TargetInvocationException ex) { ambiguous = ex.InnerException?.GetType().GetProperty("Code")?.GetValue(ex.InnerException)?.ToString() == "InvalidParams"; }
        check(ambiguous, "actual EXE rejects duplicate software names");
        bool limited = false;
        try { Resolve("+S1-K1", new object[] { group }, 1); }
        catch (TargetInvocationException ex) { limited = ex.InnerException?.GetType().GetProperty("Code")?.GetValue(ex.InnerException)?.ToString() == "OpennessError"; }
        check(limited, "actual EXE reports incomplete bounded scan as error");
    }
}
