using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;

internal static class Program
{
    internal static IEnumerable<TypeDefinition> Types(ModuleDefinition module) => module.Types.SelectMany(Descend);
    static IEnumerable<TypeDefinition> Descend(TypeDefinition t) { yield return t; foreach (var n in t.NestedTypes.SelectMany(Descend)) yield return n; }
    internal static bool Native(TypeReference t) => t.Scope?.Name?.StartsWith("Siemens.Engineering", StringComparison.Ordinal) == true;
    internal static string? Category(MethodReference m)
    {
        if (Native(m.DeclaringType)) return "direct";
        string t = m.DeclaringType.FullName;
        if ((t.StartsWith("System.Reflection.", StringComparison.Ordinal) && m.Name is "Invoke" or "GetValue" or "SetValue" or "GetValueDirect" or "SetValueDirect" or "AddEventHandler" or "RemoveEventHandler") ||
            (t == "System.Type" && m.Name == "InvokeMember") || (t == "System.Activator" && m.Name == "CreateInstance") ||
            (t == "System.Delegate" && m.Name == "DynamicInvoke")) return "reflection";
        if (t.StartsWith("System.Collections.I", StringComparison.Ordinal) || t.StartsWith("System.Collections.Generic.I", StringComparison.Ordinal) ||
            t is "System.IDisposable" or "System.IConvertible" or "System.IFormattable" or "System.IComparable" || t.StartsWith("System.IComparable`", StringComparison.Ordinal)) return "interface";
        if ((t == "System.Object" && m.Name is "ToString" or "Equals" or "GetHashCode") || (t == "System.Convert" && m.Name == "ToString")) return "object-dispatch";
        if (m.DeclaringType.Scope != m.Module && m.Parameters.Any(p => EnumerableType(p.ParameterType)) && !(t == "System.Linq.Enumerable" && m.Name == "AsEnumerable")) return "enumeration-input";
        return null;
    }
    internal static bool EnumerableType(TypeReference t) => t.FullName == "System.Collections.IEnumerable" ||
        t is GenericInstanceType g && g.ElementType.FullName == "System.Collections.Generic.IEnumerable`1";
    static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2) throw new ArgumentException("Usage: NativeCallWeaver inventory|weave|verify assembly [report]");
            if (args[0] == "self-test") { Weaver.SelfTest(args[1]); return 0; }
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
            using var module = ModuleDefinition.ReadModule(args[1], new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
            if (args[0] == "weave" || args[0] == "verify") { Weaver.Run(module, args[1], args[0] == "weave", args.Length > 2 ? args[2] : null); return 0; }
            var sites = Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions.Where(i => i.Operand is MethodReference mr && Category(mr) != null)
                .Select(i => new { caller = m.FullName, offset = i.Offset, opcode = i.OpCode.Name, member = ((MethodReference)i.Operand).FullName, category = Category((MethodReference)i.Operand) })).ToArray();
            if (args[0] != "inventory") throw new NotSupportedException(args[0]);
            string json = JsonSerializer.Serialize(sites, new JsonSerializerOptions { WriteIndented = true });
            if (args.Length > 2) File.WriteAllText(args[2], json); else Console.WriteLine(json);
            Console.WriteLine(JsonSerializer.Serialize(sites.GroupBy(s => (s.category, s.opcode)).Select(g => new { g.Key, count = g.Count() })));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
