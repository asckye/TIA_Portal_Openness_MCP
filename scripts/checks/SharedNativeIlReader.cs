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
using MethodDefinition = Mono.Cecil.MethodDefinition;

internal static class SharedNativeIlReader
{
    private static IEnumerable<TypeDefinition> Descend(TypeDefinition type)
        => new[] { type }.Concat(type.NestedTypes.SelectMany(Descend));

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
        if (args.Length != 4) throw new ArgumentException("assembly inventory output domain-config");
        using var config = JsonDocument.Parse(File.ReadAllText(args[3]));
        var domain = config.RootElement;
        if (domain.GetProperty("schemaVersion").GetInt32() != 1)
            throw new ArgumentException("Unsupported domain configuration version");
        static IEnumerable<string> Strings(JsonElement values) => values.EnumerateArray().Select(value => value.GetString()!);
        var primitives = domain.GetProperty("primitives").EnumerateArray().ToArray();
        var types = Strings(domain.GetProperty("engine").GetProperty("types"))
            .Concat(domain.GetProperty("hosts").EnumerateArray().SelectMany(host => Strings(host.GetProperty("types"))))
            .Concat(Strings(domain.GetProperty("adapter").GetProperty("mutableTypes")))
            .Concat(Strings(domain.GetProperty("expansionTypes")))
            .Concat(primitives.Select(p => p.GetProperty("adapterType").GetString()!))
            .Concat(primitives.Select(p => p.GetProperty("engineNamespace").GetString()! + "."
                + p.GetProperty("adapterType").GetString()!.Split('.').Last())).ToArray();
        var namespaces = Strings(domain.GetProperty("expansionNamespaces")).ToArray();
        bool Scope(string owner) => types.Any(type => owner == type || owner.StartsWith(type + "/", StringComparison.Ordinal))
            || namespaces.Any(ns => owner.StartsWith(ns + ".", StringComparison.Ordinal));
        using var inventory = JsonDocument.Parse(File.ReadAllText(args[1]));
        var sites = inventory.RootElement.GetProperty("sites").EnumerateArray()
            .ToDictionary(row => row.GetProperty("wrapper").GetString()!, row => row.Clone());
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        using var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver });
        var localTypes = module.Types.SelectMany(Descend).ToArray();
        var localMethods = localTypes.SelectMany(type => type.Methods).ToArray();
        MethodDefinition? Resolve(MethodReference target)
        {
            // External framework/SDK references are boundaries, never loaded.
            try { return target.Resolve(); }
            catch (AssemblyResolutionException) { return null; }
        }
        MethodDefinition? Dispatch(MethodReference target, TypeReference? receiver = null)
        {
            var declaration = Resolve(target);
            if (declaration == null || declaration.Module != module) return null;
            if (!declaration.IsVirtual || declaration.IsFinal || declaration.DeclaringType.IsSealed)
                return declaration.HasBody ? declaration : null;
            bool SameSignature(MethodDefinition candidate) => candidate.Name == declaration.Name &&
                candidate.ReturnType.FullName == declaration.ReturnType.FullName &&
                candidate.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(declaration.Parameters.Select(p => p.ParameterType.FullName));
            bool OverridesSlot(MethodDefinition candidate)
            {
                if (candidate.FullName == declaration.FullName || candidate.Overrides.Any(o => o.FullName == declaration.FullName)) return true;
                if (!candidate.IsVirtual || candidate.IsNewSlot) return false;
                for (var parent = candidate.DeclaringType.BaseType?.Resolve(); parent != null; parent = parent.BaseType?.Resolve())
                    if (parent.Methods.FirstOrDefault(m => m.IsVirtual && SameSignature(m)) is { } inherited)
                        return OverridesSlot(inherited);
                return false;
            }
            if (receiver?.Resolve() is { IsSealed: true } concrete)
            {
                for (var current = concrete; current != null; current = current.BaseType?.Resolve())
                {
                    var implementation = current.Methods.FirstOrDefault(m =>
                        m.Overrides.Any(o => o.FullName == declaration.FullName) ||
                        (SameSignature(m) && (declaration.DeclaringType.IsInterface ? m.IsPublic : OverridesSlot(m))));
                    if (implementation != null) return implementation.HasBody ? implementation : null;
                }
            }
            var type = declaration.DeclaringType;
            // An internal interface has a closed implementation set in this
            // assembly. A public extensible slot requires a concrete receiver.
            if (!type.IsInterface || type.IsPublic || type.IsNestedPublic) return null;
            bool Implements(TypeDefinition candidate) => candidate.Interfaces.Any(i =>
                i.InterfaceType.FullName == type.FullName ||
                ResolveInterface(i.InterfaceType));
            bool ResolveInterface(TypeReference reference)
            {
                try { return reference.Resolve() is { } parent && Implements(parent); }
                catch (AssemblyResolutionException) { return false; }
            }
            var candidates = localTypes.Where(Implements).SelectMany(candidate =>
                {
                    var explicitMethods = candidate.Methods.Where(m => m.Overrides.Any(o => o.FullName == declaration.FullName)).ToArray();
                    return explicitMethods.Length != 0 ? explicitMethods : candidate.Methods.Where(m =>
                        m.IsPublic && m.Name == declaration.Name && m.ReturnType.FullName == declaration.ReturnType.FullName &&
                        m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(declaration.Parameters.Select(p => p.ParameterType.FullName)));
                })
                .Distinct().ToArray();
            return candidates.Length == 1 && candidates[0].HasBody &&
                (!candidates[0].IsVirtual || candidates[0].IsFinal || candidates[0].DeclaringType.IsSealed)
                ? candidates[0] : null;
        }
        TypeReference? ReceiverType(MethodDefinition method, Instruction instruction)
        {
            var producer = instruction.Previous;
            if (producer?.OpCode.Code == Code.Dup) producer = producer.Previous;
            if (producer == null) return null;
            if (producer.Operand is FieldReference field && producer.OpCode.Code == Code.Ldfld) return field.FieldType;
            if (producer.Operand is VariableDefinition variable && producer.OpCode.Code is Code.Ldloc or Code.Ldloc_S) return variable.VariableType;
            if (producer.Operand is ParameterDefinition parameter && producer.OpCode.Code is Code.Ldarg or Code.Ldarg_S) return parameter.ParameterType;
            int arg = producer.OpCode.Code switch { Code.Ldarg_0 => 0, Code.Ldarg_1 => 1, Code.Ldarg_2 => 2, Code.Ldarg_3 => 3, _ => -1 };
            if (arg >= 0) return method.HasThis && arg == 0 ? method.DeclaringType : method.Parameters[arg - (method.HasThis ? 1 : 0)].ParameterType;
            int local = producer.OpCode.Code switch { Code.Ldloc_0 => 0, Code.Ldloc_1 => 1, Code.Ldloc_2 => 2, Code.Ldloc_3 => 3, _ => -1 };
            return local >= 0 ? method.Body.Variables[local].VariableType : null;
        }
        bool ConsumesDelegate(MethodReference target) => target.Name != ".ctor" && target.Parameters.Any(p =>
            p.ParameterType.FullName.StartsWith("System.Func`", StringComparison.Ordinal) ||
            p.ParameterType.FullName.StartsWith("System.Action", StringComparison.Ordinal));
        // Follow the implementation of a method group, including its local
        // callees, without widening the configured ownership/dedup scope.
        var delegateMethods = new HashSet<MethodDefinition>();
        var pending = new Queue<MethodDefinition>();
        foreach (var method in localMethods.Where(m => m.HasBody && Scope(m.DeclaringType.FullName)))
            foreach (var instruction in method.Body.Instructions)
                if (instruction.Operand is MethodReference reference &&
                    (instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn || ConsumesDelegate(reference)))
                {
                    var target = instruction.OpCode.Code is Code.Ldvirtftn or Code.Callvirt
                        ? Dispatch(reference, instruction.OpCode.Code == Code.Ldvirtftn ? ReceiverType(method, instruction) : null)
                        : Resolve(reference);
                    if (target?.Module == module && target.HasBody) pending.Enqueue(target);
                }
        while (pending.Count != 0)
        {
            var method = pending.Dequeue();
            if (!delegateMethods.Add(method)) continue;
            foreach (var attribute in method.CustomAttributes.Where(attribute => attribute.AttributeType.FullName is
                "System.Runtime.CompilerServices.IteratorStateMachineAttribute" or "System.Runtime.CompilerServices.AsyncStateMachineAttribute"))
                foreach (var move in ((TypeReference)attribute.ConstructorArguments[0].Value).Resolve().Methods.Where(m => m.Name == "MoveNext" && m.HasBody))
                    pending.Enqueue(move);
            foreach (var instruction in method.Body.Instructions)
                if (instruction.Operand is MethodReference reference && !sites.ContainsKey(reference.Name))
                {
                    var target = instruction.OpCode.Code is Code.Callvirt or Code.Ldvirtftn
                        ? Dispatch(reference, instruction.OpCode.Code == Code.Ldvirtftn || reference.Parameters.Count == 0 ? ReceiverType(method, instruction) : null)
                        : Resolve(reference);
                    if (target?.Module == module && target.HasBody) pending.Enqueue(target);
                }
        }
        using var stream = File.OpenRead(args[0]);
        using var pe = new PEReader(stream);
        var methods = new List<object>();
        foreach (var method in localMethods.Where(method => method.HasBody))
        {
            var body = method.Body;
            bool include = Scope(method.DeclaringType.FullName) || delegateMethods.Contains(method);
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
                    if (instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn || delegateMethods.Contains(method) || ConsumesDelegate(target))
                    {
                        var resolved = instruction.OpCode.Code is Code.Ldvirtftn or Code.Callvirt
                            ? Dispatch(target, instruction.OpCode.Code == Code.Ldvirtftn || target.Parameters.Count == 0 ? ReceiverType(method, instruction) : null)
                            : Resolve(target);
                        if (resolved != null && delegateMethods.Contains(resolved))
                            definition = resolved.FullName + (Scope(resolved.DeclaringType.FullName) ? "" : "@" + module.Assembly.Name.Name);
                        else if (instruction.OpCode.Code is Code.Ldvirtftn ||
                            (instruction.OpCode.Code == Code.Callvirt && Resolve(target) is { } declared &&
                             declared.Module == module && declared.IsVirtual && !sites.ContainsKey(target.Name)))
                            definition = "!unresolved-dispatch:" + target.FullName;
                    }
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
                expansionOnly = include && !Scope(method.DeclaringType.FullName),
                method.HasThis, arguments = method.Parameters.Select(parameter => new { name = parameter.Name, type = parameter.ParameterType.FullName }).ToArray(),
                raw = Hash(Encoding.UTF8.GetBytes(raw.ToString())), semantic = Hash(Encoding.UTF8.GetBytes(normalized.ToString())),
                il = include ? body.Instructions.Select(instruction => new {
                    offset = instruction.Offset, op = instruction.OpCode.Name, flow = instruction.OpCode.FlowControl.ToString(),
                    operand = Operand(instruction) }).ToArray() : null,
                handlers = include ? body.ExceptionHandlers.Select(handler => new {
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
