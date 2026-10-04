// Read metadata and IL only. Never load or execute an engine, adapter or Siemens type.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using TypeReference = Mono.Cecil.TypeReference;
using ParameterDefinition = Mono.Cecil.ParameterDefinition;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;

internal static class VciIlReader
{
    private static IEnumerable<TypeDefinition> Descend(TypeDefinition type)
        => new[] { type }.Concat(type.NestedTypes.SelectMany(Descend));

    private static bool Scope(string owner) => owner.StartsWith("TiaMcpServer.Siemens.Services.VersionControlService")
        || owner.StartsWith("TiaOpenness.Openness.")
        || owner.StartsWith("TiaOpenness.Openness.VersionControlPrimitives")
        || owner.StartsWith("TiaMcpServer.Siemens.LocalVci.VersionControlPrimitives")
        || owner.StartsWith("TiaMcp.Adapters.PlcServices");

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string TypeIdentity(TypeReference type)
    {
        if (type is Mono.Cecil.GenericParameter parameter) return parameter.FullName;
        if (type is GenericInstanceType generic)
            return TypeIdentity(generic.ElementType) + "<" + string.Join(",", generic.GenericArguments.Select(TypeIdentity)) + ">";
        if (type is Mono.Cecil.TypeSpecification specification)
            return type.FullName + "@" + TypeIdentity(specification.ElementType);
        var scope = type.Scope is ModuleDefinition module ? module.Assembly.Name.FullName : type.Scope?.ToString();
        return type.FullName + "@" + scope;
    }

    private static string OperandIdentity(object? operand) => operand switch
    {
        Instruction branch => branch.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Instruction[] branches => string.Join(",", branches.Select(branch => branch.Offset)),
        MethodReference method => method.FullName + "@" + TypeIdentity(method.DeclaringType)
            + "->" + TypeIdentity(method.ReturnType) + "(" + string.Join(",", method.Parameters.Select(p => TypeIdentity(p.ParameterType))) + ")"
            + (method is GenericInstanceMethod generic ? "<" + string.Join(",", generic.GenericArguments.Select(TypeIdentity)) + ">" : ""),
        FieldReference field => field.FullName + "@" + TypeIdentity(field.DeclaringType) + ":" + TypeIdentity(field.FieldType),
        TypeReference type => TypeIdentity(type),
        _ => operand?.ToString() ?? ""
    };

    public static void Main(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("assembly inventory output");
        using var inventory = JsonDocument.Parse(File.ReadAllText(args[1]));
        var sites = inventory.RootElement.GetProperty("sites").EnumerateArray()
            .ToDictionary(row => row.GetProperty("wrapper").GetString()!, row => row.Clone());
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        using var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver });
        using var stream = File.OpenRead(args[0]);
        using var pe = new PEReader(stream);
        var methods = new List<object>();
        foreach (var method in module.Types.SelectMany(Descend).SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            var body = method.Body;
            object Operand(Instruction instruction)
            {
                var operand = instruction.Operand;
                if (operand is Instruction branch) return new { kind = "branch", target = branch.Offset };
                if (operand is Instruction[] branches) return new { kind = "switch", targets = branches.Select(target => target.Offset) };
                if (operand is MethodReference target)
                {
                    string? definition = null;
                    if (Scope(target.DeclaringType.FullName))
                        definition = target.Resolve()?.FullName;
                    return new { kind = "method", name = target.FullName, definition, owner = target.DeclaringType.FullName,
                        target.HasThis, parameters = target.Parameters.Count, returns = target.ReturnType.FullName,
                        native = sites.TryGetValue(target.Name, out var site) ? (object)site : null };
                }
                if (operand is FieldReference field) return new { kind = "field", name = field.FullName };
                if (operand is TypeReference type) return new { kind = "type", name = type.FullName };
                if (operand is VariableDefinition variable) return new { kind = "local", index = variable.Index };
                if (operand is ParameterDefinition parameter) return new { kind = "parameter", index = parameter.Index };
                return new { kind = "value", value = operand?.ToString() };
            }
            var normalized = new StringBuilder().Append(body.InitLocals).Append('|').Append(body.MaxStackSize);
            foreach (var variable in body.Variables) normalized.Append('|').Append(TypeIdentity(variable.VariableType));
            foreach (var instruction in body.Instructions)
                normalized.Append('\n').Append(instruction.OpCode.Name).Append(' ').Append(OperandIdentity(instruction.Operand));
            foreach (var handler in body.ExceptionHandlers)
                normalized.Append('\n').Append(handler.HandlerType).Append(' ').Append(handler.CatchType == null ? "" : TypeIdentity(handler.CatchType))
                    .Append(' ').Append(handler.TryStart?.Offset).Append(' ').Append(handler.TryEnd?.Offset)
                    .Append(' ').Append(handler.HandlerStart?.Offset).Append(' ').Append(handler.HandlerEnd?.Offset)
                    .Append(' ').Append(handler.FilterStart?.Offset);
            var rawBody = pe.GetMethodBody(method.RVA);
            var raw = new StringBuilder(Hash(rawBody.GetILBytes()!)).Append('|').Append(rawBody.MaxStack)
                .Append('|').Append(rawBody.LocalVariablesInitialized).Append('|').Append(rawBody.LocalSignature.GetHashCode());
            foreach (var variable in body.Variables) raw.Append('|').Append(variable.VariableType.FullName);
            foreach (var handler in body.ExceptionHandlers) raw.Append('|').Append(handler.CatchType?.FullName);
            foreach (var handler in rawBody.ExceptionRegions)
                raw.Append('|').Append(handler.Kind).Append(':').Append(handler.TryOffset).Append(':').Append(handler.TryLength)
                    .Append(':').Append(handler.HandlerOffset).Append(':').Append(handler.HandlerLength)
                    .Append(':').Append(handler.FilterOffset).Append(':').Append(handler.CatchType.GetHashCode());
            methods.Add(new { name = method.FullName, owner = method.DeclaringType.FullName,
                method.HasThis, arguments = method.Parameters.Select(parameter => new { name = parameter.Name, type = parameter.ParameterType.FullName }).ToArray(),
                raw = Hash(Encoding.UTF8.GetBytes(raw.ToString())), semantic = Hash(Encoding.UTF8.GetBytes(normalized.ToString())),
                il = Scope(method.DeclaringType.FullName) ? body.Instructions.Select(instruction => new {
                    offset = instruction.Offset, op = instruction.OpCode.Name, flow = instruction.OpCode.FlowControl.ToString(),
                    operand = Operand(instruction) }).ToArray() : null,
                handlers = Scope(method.DeclaringType.FullName) ? body.ExceptionHandlers.Select(handler => new {
                    kind = handler.HandlerType.ToString(), type = handler.CatchType?.FullName,
                    start = handler.TryStart.Offset, end = handler.TryEnd?.Offset ?? int.MaxValue,
                    target = handler.FilterStart?.Offset ?? handler.HandlerStart.Offset,
                    handlerEnd = handler.HandlerEnd?.Offset ?? int.MaxValue }).ToArray() : null,
                states = method.CustomAttributes.Where(attribute => attribute.AttributeType.FullName is
                    "System.Runtime.CompilerServices.IteratorStateMachineAttribute" or "System.Runtime.CompilerServices.AsyncStateMachineAttribute")
                    .Select(attribute => ((TypeReference)attribute.ConstructorArguments[0].Value).FullName).ToArray() });
        }
        File.WriteAllText(args[2], JsonSerializer.Serialize(new { assembly = module.Assembly.Name.Name, methods },
            new JsonSerializerOptions { WriteIndented = false }));
    }
}
