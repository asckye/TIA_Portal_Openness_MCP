using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TiaMcp.ReleaseTool;

internal static class IlDump
{
    internal static string Normalize(string value)
    {
        value = Regex.Replace(value, @"<>c__DisplayClass\d+_(\d+)", "<>c__DisplayClass#_$1");
        value = Regex.Replace(value, @"(?<=>)b__\d+_(\d+)", "b__#_$1");
        value = Regex.Replace(value, @"(?<=>)d__\d+", "d__#");
        value = Regex.Replace(value, @"<>9__\d+_(\d+)", "<>9__#_$1");
        value = Regex.Replace(value, @"(?<=>)(g__[^|]+\|)\d+_(\d+)", "$1#_$2");
        value = Regex.Replace(value, @"\bCall_[0-9a-f]{20}\b", "Call_#");
        value = Regex.Replace(value, @"__TiaMcpNativeCall_[0-9a-f]{20}\b", "__TiaMcpNativeCall_#");
        value = Regex.Replace(value, @"<>f__AnonymousType\d+", "<>f__AnonymousType#");
        // A compiler's method-group cache owns these fields; ordinary <n> fields stay intact.
        value = Regex.Replace(value, @"(<>O::)<\d+>(__[^\s,]+)", "$1<#>$2");
        return value;
    }

    internal static string NormalizeSiteString(string value) => Normalize(
        Regex.Replace(value, @"^[0-9a-f]{20}( .+@IL_[0-9a-f]+)$", "#$1"));

    private static string Quote(string? value) => JsonSerializer.Serialize(value);
    private static string Value(object? value) => value switch
    {
        null => "null",
        string text => Quote(text),
        char c => "char:" + ((int)c).ToString(CultureInfo.InvariantCulture),
        TypeReference type => type.FullName,
        CustomAttributeArgument argument => Argument(argument),
        CustomAttributeArgument[] arguments => "[" + string.Join(",", arguments.Select(Argument)) + "]",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static string Argument(CustomAttributeArgument argument) => argument.Type.FullName + "=" + Value(argument.Value);
    private static string Attributes(ICustomAttributeProvider provider) => string.Join("\n", provider.CustomAttributes
        .Where(a => a.AttributeType.FullName != "System.Reflection.AssemblyInformationalVersionAttribute")
        .Select(a => "attribute " + a.Constructor.FullName + "(" + string.Join(",", a.ConstructorArguments.Select(Argument))
            + ") fields[" + string.Join(",", a.Fields.Select(f => f.Name + "=" + Argument(f.Argument)).Order(StringComparer.Ordinal))
            + "] properties[" + string.Join(",", a.Properties.Select(p => p.Name + "=" + Argument(p.Argument)).Order(StringComparer.Ordinal)) + "]")
        .Select(Normalize).Order(StringComparer.Ordinal));

    private static string GenericParameters(IGenericParameterProvider owner) => string.Join("\n", owner.GenericParameters.Select(p =>
        "generic " + p.Position + " " + p.Name + " " + p.Attributes + " constraints["
        + string.Join(",", p.Constraints.Select(c => c.ConstraintType.FullName + " " + Attributes(c)).Order(StringComparer.Ordinal))
        + "] " + Attributes(p)));

    private static string Method(MethodDefinition method)
    {
        var output = new StringBuilder();
        output.AppendLine("method " + method.FullName + " " + method.Attributes + " " + method.ImplAttributes
            + " " + method.CallingConvention + " this=" + method.HasThis + " explicit=" + method.ExplicitThis);
        output.AppendLine(Attributes(method));
        output.AppendLine(GenericParameters(method));
        output.AppendLine("return " + method.MethodReturnType.Attributes + " " + Attributes(method.MethodReturnType));
        foreach (var parameter in method.Parameters)
            output.AppendLine("parameter " + parameter.Index + " " + parameter.Name + " " + parameter.ParameterType.FullName
                + " " + parameter.Attributes + (parameter.HasConstant ? " default=" + Value(parameter.Constant) : "")
                + " " + Attributes(parameter) + (parameter.HasMarshalInfo ? " marshal=" + parameter.MarshalInfo.NativeType : ""));
        foreach (var item in method.Overrides.Select(o => o.FullName).Order(StringComparer.Ordinal)) output.AppendLine("override " + item);
        if (method.HasPInvokeInfo) output.AppendLine("pinvoke " + method.PInvokeInfo.Module.Name + " "
            + method.PInvokeInfo.EntryPoint + " " + method.PInvokeInfo.Attributes);
        if (method.HasBody)
        {
            var body = method.Body;
            var indices = body.Instructions.Select((instruction, index) => (instruction, index))
                .ToDictionary(pair => pair.instruction, pair => pair.index);
            string Target(Instruction? instruction) => instruction == null ? "end" : "instruction:" + indices[instruction];
            string Operand(object? operand) => operand switch
            {
                Instruction target => Target(target),
                Instruction[] targets => string.Join(",", targets.Select(t => Target(t))),
                VariableDefinition variable => "local:" + variable.Index,
                ParameterDefinition parameter => "parameter:" + parameter.Index,
                MemberReference member => member.FullName,
                CallSite site => site.FullName,
                string text => Quote(NormalizeSiteString(text)),
                _ => Value(operand)
            };
            output.AppendLine("body init=" + body.InitLocals + " maxstack=" + body.MaxStackSize);
            foreach (var local in body.Variables) output.AppendLine("local " + local.Index + " " + local.VariableType.FullName);
            foreach (var instruction in body.Instructions) output.AppendLine(instruction.OpCode.Name + " " + Operand(instruction.Operand));
            foreach (var handler in body.ExceptionHandlers)
                output.AppendLine("handler " + handler.HandlerType + " " + handler.CatchType?.FullName + " try="
                    + Target(handler.TryStart) + ".." + Target(handler.TryEnd) + " handler=" + Target(handler.HandlerStart)
                    + ".." + Target(handler.HandlerEnd) + " filter=" + Target(handler.FilterStart));
        }
        return Normalize(output.ToString());
    }

    private static string Type(TypeDefinition type)
    {
        var output = new StringBuilder();
        output.AppendLine("type " + type.FullName + " " + type.Attributes + " base=" + type.BaseType?.FullName
            + " packing=" + type.PackingSize + " size=" + type.ClassSize);
        output.AppendLine(Attributes(type));
        output.AppendLine(GenericParameters(type));
        foreach (var item in type.Interfaces.Select(i => "interface " + i.InterfaceType.FullName + " " + Attributes(i))
            .Select(Normalize).Order(StringComparer.Ordinal)) output.AppendLine(item);
        foreach (var item in type.Fields.Select(f => "field " + f.FullName + " " + f.Attributes
            + (f.HasConstant ? " constant=" + Value(f.Constant) : "") + " offset=" + f.Offset
            + " initial=" + Convert.ToHexString(f.InitialValue) + " " + Attributes(f)
            + (f.HasMarshalInfo ? " marshal=" + f.MarshalInfo.NativeType : ""))
            .Select(Normalize).Order(StringComparer.Ordinal)) output.AppendLine(item);
        foreach (var item in type.Properties.Select(p => "property " + p.FullName + " " + p.Attributes
            + (p.HasConstant ? " constant=" + Value(p.Constant) : "") + " get=" + p.GetMethod?.FullName
            + " set=" + p.SetMethod?.FullName + " " + Attributes(p)).Select(Normalize).Order(StringComparer.Ordinal)) output.AppendLine(item);
        foreach (var item in type.Events.Select(e => "event " + e.FullName + " " + e.Attributes
            + " add=" + e.AddMethod?.FullName + " remove=" + e.RemoveMethod?.FullName + " invoke=" + e.InvokeMethod?.FullName
            + " " + Attributes(e)).Select(Normalize).Order(StringComparer.Ordinal)) output.AppendLine(item);
        foreach (var method in type.Methods.Select(Method).Order(StringComparer.Ordinal)) output.Append(method);
        return Normalize(output.ToString());
    }

    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(Types(t.NestedTypes)));

    internal static byte[] NormalizeCoverage(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var sites = document.RootElement.GetProperty("sites").EnumerateArray().Select(site =>
            Normalize(site.GetProperty("caller").GetString()!) + "\t" + Normalize(site.GetProperty("member").GetString()!)
            + "\t" + site.GetProperty("opcode").GetString()).Order(StringComparer.Ordinal);
        return Encoding.UTF8.GetBytes(string.Join("\n", sites) + "\n");
    }

    internal static string Render(string path)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false, AssemblyResolver = resolver });
        var output = new StringBuilder();
        output.AppendLine("assembly " + assembly.Name.FullName);
        output.AppendLine(Attributes(assembly));
        foreach (var module in assembly.Modules.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            output.AppendLine("module " + module.Name + " " + module.Kind + " " + module.Architecture + " " + module.Attributes);
            output.AppendLine(Attributes(module));
            foreach (var reference in module.AssemblyReferences.Select(a => a.FullName).Order(StringComparer.Ordinal))
                output.AppendLine("reference " + reference);
            foreach (var type in Types(module.Types).Select(Type).Order(StringComparer.Ordinal)) output.Append(type);
            foreach (var resource in module.Resources.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                string value;
                if (resource is EmbeddedResource embedded)
                {
                    byte[] data = embedded.GetResourceData();
                    if (resource.Name == "TiaMcp.NativeCallCoverage.json") data = NormalizeCoverage(data);
                    value = Convert.ToHexString(SHA256.HashData(data));
                }
                else if (resource is LinkedResource linked) value = linked.File + " " + Convert.ToHexString(linked.Hash);
                else value = ((AssemblyLinkedResource)resource).Assembly.FullName;
                output.AppendLine("resource " + resource.Name + " " + resource.Attributes + " " + value);
            }
        }
        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    internal static int Run(string root, Options options)
    {
        root = Path.GetFullPath(options.Get("Root", root));
        string output = Path.GetFullPath(options.Get("Output") ?? throw new ReleaseException("-Output is required", 64), root);
        var inputs = options.All("Assembly");
        var paths = inputs.Count > 0 ? inputs.Select(p => Path.GetFullPath(p, root))
            : Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories).Where(p =>
                Regex.IsMatch(Path.GetRelativePath(root, p).Replace('\\', '/'), @"/bin/(?:.*/)?Release/")
                && (Path.GetFileName(p).StartsWith("TiaMcp.", StringComparison.Ordinal)
                    || Path.GetFileName(p).StartsWith("TiaOpenness.", StringComparison.Ordinal)));
        Directory.CreateDirectory(output);
        int count = 0;
        foreach (var path in paths.Where(p => Path.GetExtension(p) is ".dll" or ".exe").Distinct().Order(StringComparer.Ordinal))
        {
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(path, ".dll")))
            {
                using var stream = File.OpenRead(path);
                using var reader = new PEReader(stream);
                if (!reader.HasMetadata) continue;
            }
            string name = Path.GetRelativePath(root, path).Replace('\\', '/').Replace("/", "__", StringComparison.Ordinal) + ".txt";
            File.WriteAllText(Path.Combine(output, name), Render(path), new UTF8Encoding(false));
            count++;
        }
        Console.WriteLine($"dump-il: {count} assemblies -> {output}");
        return 0;
    }
}
