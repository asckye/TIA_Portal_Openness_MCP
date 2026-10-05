using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Weaver
{
    const string Generated = "TiaMcpServer.Diagnostics.GeneratedNativeCalls";
    const string Runtime = "TiaMcpServer.ModelContextProtocol.NativeCallDiagnostics";
    const string Resource = "TiaMcp.NativeCallCoverage.json";
    static string Fingerprint => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Weaver).Assembly.Location))).ToLowerInvariant();
    internal sealed record Site(string id, string caller, int offset, string opcode, string member, string category, string wrapper);
    static bool GeneratedType(TypeReference t) => t.FullName == Generated || t.Name.StartsWith("__TiaMcpNativeCall_", StringComparison.Ordinal);
    static IEnumerable<MethodDefinition> Wrappers(ModuleDefinition m) => Program.Types(m).Where(GeneratedType).SelectMany(t => t.Methods);
    static bool Excluded(TypeDefinition t) => GeneratedType(t) || t.FullName == Runtime || t.FullName.StartsWith(Runtime + "/", StringComparison.Ordinal) ||
        t.FullName == "TiaMcpServer.ModelContextProtocol.InvocationJournal" || t.FullName.StartsWith("TiaMcpServer.ModelContextProtocol.InvocationJournal/", StringComparison.Ordinal);
    static bool Selected(Instruction i) => i.Operand is MethodReference m && Program.Category(m) != null;
    internal static void Run(ModuleDefinition module, string path, bool weave, string? report)
    {
        if (module.Assembly.Name.HasPublicKey) throw new InvalidOperationException("Refusing to rewrite a signed assembly.");
        if (!module.Types.Any(t => t.FullName == Runtime)) throw new InvalidOperationException("Diagnostic runtime absent; refusing uninstrumented output.");
        if (!module.Types.Any(t => t.FullName == Generated))
        {
            if (!weave) throw new InvalidOperationException("Assembly has no native-call instrumentation.");
            var container = new TypeDefinition("TiaMcpServer.Diagnostics", "GeneratedNativeCalls", TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.NotPublic, module.TypeSystem.Object);
            var work = Program.Types(module).Where(t => !Excluded(t)).SelectMany(t => t.Methods).Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions.Where(Selected).Select(i => (method: m, instruction: i))).ToArray();
            var sites = new List<Site>();
            module.Types.Add(container);
            foreach (var (caller, instruction) in work)
            {
                var target = (MethodReference)instruction.Operand;
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj))
                    throw new InvalidOperationException($"Unsupported native dispatch {instruction.OpCode} at {caller.FullName}: {target.FullName}. Use an explicit lambda/call.");
                string identity = caller.FullName + "@IL_" + instruction.Offset.ToString("x4");
                string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant().Substring(0, 20);
                var site = new Site(id, caller.FullName, instruction.Offset, instruction.OpCode.Name, target.FullName, Program.Category(target)!, "Call_" + id);
                sites.Add(site);
                var replacement = Wrap(module, container, caller, instruction, site);
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = replacement;
            }
            // Detect future DLR/delegate/function-pointer paths rather than silently claiming coverage.
            AuditDispatch(module);
            var data = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, instrumenter = "native-call-weaver/1", instrumenterSha256 = Fingerprint, scope = "Engine-owned direct, reflective, interface and external-enumeration boundaries; no Siemens assembly is modified", count = sites.Count, sites }, new JsonSerializerOptions { WriteIndented = true });
            module.Resources.Add(new EmbeddedResource(Resource, ManifestResourceAttributes.Private, data));
            string temp = path + ".instrumented";
            try { module.Write(temp); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        Verify(module);
        var resource = (EmbeddedResource)module.Resources.Single(r => r.Name == Resource);
        if (report != null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!); File.WriteAllBytes(report, resource.GetResourceData()); }
        Console.WriteLine($"Native diagnostic coverage verified: {Wrappers(module).Count()} call sites; no unwrapped supported boundary.");
    }
    static void AuditDispatch(ModuleDefinition module)
    {
        foreach (var method in Program.Types(module).Where(t => !Excluded(t)).SelectMany(t => t.Methods).Where(m => m.HasBody))
        foreach (var i in method.Body.Instructions)
        {
            if (i.OpCode.Code == Code.Calli) throw new InvalidOperationException("Unreviewed function pointer call: " + method.FullName);
            if (i.Operand is FieldReference field && Program.Native(field.DeclaringType) && !field.DeclaringType.IsValueType && i.OpCode.Code is Code.Ldfld or Code.Stfld or Code.Ldflda)
                throw new InvalidOperationException("Unreviewed native proxy field access: " + method.FullName + " -> " + field.FullName);
            if (i.Operand is not MethodReference m) continue;
            if (m.DeclaringType.FullName.StartsWith("Microsoft.CSharp.RuntimeBinder.", StringComparison.Ordinal) ||
                (m.DeclaringType.FullName is "System.Delegate" or "System.Reflection.MethodInfo" && m.Name == "CreateDelegate") ||
                m.DeclaringType.FullName.StartsWith("System.Reflection.Emit.", StringComparison.Ordinal) ||
                (m.DeclaringType.FullName.StartsWith("System.Linq.Expressions.", StringComparison.Ordinal) && m.Name == "Compile") ||
                (m.DeclaringType.FullName == "System.RuntimeMethodHandle" && m.Name == "GetFunctionPointer"))
                throw new InvalidOperationException("Unreviewed dynamic native escape: " + method.FullName + " -> " + m.FullName);
        }
    }
    static void Verify(ModuleDefinition module)
    {
        AuditDispatch(module);
        foreach (var method in Program.Types(module).Where(t => !Excluded(t)).SelectMany(t => t.Methods).Where(m => m.HasBody))
            foreach (var i in method.Body.Instructions.Where(Selected)) throw new InvalidOperationException("Uncovered boundary: " + method.FullName + " -> " + i.Operand);
        var wrappers = Wrappers(module).ToArray();
        using var document = JsonDocument.Parse(((EmbeddedResource)module.Resources.Single(r => r.Name == Resource)).GetResourceData());
        if (document.RootElement.GetProperty("instrumenterSha256").GetString() != Fingerprint) throw new InvalidOperationException("Instrumenter changed: rebuild the original compiler output before weaving.");
        if (document.RootElement.GetProperty("count").GetInt32() != wrappers.Length) throw new InvalidOperationException("Coverage inventory count mismatch.");
        var sites = document.RootElement.GetProperty("sites").EnumerateArray().ToArray();
        var owners = Program.Types(module).Where(t => !Excluded(t)).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
        if (sites.Select(s => s.GetProperty("id").GetString()).Distinct().Count() != sites.Length) throw new InvalidOperationException("Duplicate site identity.");
        foreach (var site in sites)
        {
            string name = site.GetProperty("wrapper").GetString()!;
            var owner = owners[site.GetProperty("caller").GetString()!];
            if (owner.Body.Instructions.Count(i => i.Operand is MethodReference m && GeneratedType(m.DeclaringType) && m.Name == name) != 1)
                throw new InvalidOperationException("Call-site edge missing or duplicated: " + name);
        }
        foreach (var wrapper in wrappers)
        {
            var calls = wrapper.Body.Instructions.Where(i => i.Operand is MethodReference).Select(i => (MethodReference)i.Operand).ToArray();
            if (wrapper.Body.ExceptionHandlers.Count != 1 || wrapper.Body.ExceptionHandlers[0].HandlerType != ExceptionHandlerType.Catch ||
                !calls.Any(m => m.DeclaringType.FullName == Runtime && m.Name == "Enter") ||
                !calls.Any(m => m.DeclaringType.FullName == Runtime && m.Name == "Returned") ||
                !calls.Any(m => m.DeclaringType.FullName == Runtime && m.Name == "Threw") ||
                !wrapper.Body.Instructions.Any(i => i.OpCode == OpCodes.Rethrow) || calls.Count(m => Program.Category(m) != null && m.DeclaringType.FullName != Runtime) != 1)
                throw new InvalidOperationException("Invalid diagnostic wrapper: " + wrapper.FullName);
        }
    }
    internal static void SelfTest(string path)
    {
        int checks = 0;
        void Reject(Action<ModuleDefinition> corrupt)
        {
            using var module = ModuleDefinition.ReadModule(path); corrupt(module);
            try { Verify(module); } catch { checks++; return; }
            throw new InvalidOperationException("Coverage verifier accepted a damaged assembly.");
        }
        Reject(m => { var wrapper = Wrappers(m).First(); var call = wrapper.Body.Instructions.First(i => i.Operand is MethodReference r && r.Name == "Returned" && r.DeclaringType.FullName == Runtime); call.OpCode = OpCodes.Nop; call.Operand = null; });
        Reject(m => { var wrapper = Wrappers(m).First(); wrapper.Body.ExceptionHandlers.Clear(); });
        Reject(m => m.Resources.Remove(m.Resources.Single(r => r.Name == Resource)));
        Reject(m => { var owner = Program.Types(m).Where(t => !Excluded(t)).SelectMany(t => t.Methods).First(x => x.HasBody && x.Body.Instructions.Any(i => i.Operand is MethodReference r && GeneratedType(r.DeclaringType))); var call = owner.Body.Instructions.First(i => i.Operand is MethodReference r && GeneratedType(r.DeclaringType)); call.OpCode = OpCodes.Nop; call.Operand = null; });
        Reject(m => { var raw = Wrappers(m).SelectMany(x => x.Body.Instructions).First(i => i.Operand is MethodReference r && Program.Native(r.DeclaringType)); var owner = Program.Types(m).Where(t => !Excluded(t)).SelectMany(t => t.Methods).First(x => x.HasBody); owner.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Call, (MethodReference)raw.Operand)); });
        Console.WriteLine("COMPLETE: " + checks + " diagnostic coverage rejection checks passed");
    }
    static MethodReference Wrap(ModuleDefinition module, TypeDefinition container, MethodDefinition caller, Instruction instruction, Site site)
    {
        var target = (MethodReference)instruction.Operand;
        bool construct = instruction.OpCode == OpCodes.Newobj;
        bool receiver = !construct && target.HasThis;
        TypeReference? constrained = null;
        if (instruction.Previous?.OpCode.OpCodeType == OpCodeType.Prefix)
        {
            if (instruction.Previous.OpCode != OpCodes.Constrained) throw new InvalidOperationException("Unsupported call prefix: " + site.caller);
            constrained = (TypeReference)instruction.Previous.Operand;
            instruction.Previous.OpCode = OpCodes.Nop; instruction.Previous.Operand = null;
        }
        var wrapper = new MethodDefinition(site.wrapper, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
        // Keep both lazy dependency loading and the original lexical accessibility:
        // e.g. Enumerable.Where<KeyValuePair<string, MigrationPages.Session>> must
        // remain inside MigrationPages, because Session is a private nested type.
        var siteType = new TypeDefinition("", "__TiaMcpNativeCall_" + site.id, TypeAttributes.NestedPrivate | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        caller.DeclaringType.NestedTypes.Add(siteType); siteType.Methods.Add(wrapper);
        // External !0/!!0 are first instantiated in the call site's context; remaining
        // caller generic parameters become wrapper parameters with the same constraints.
        TypeReference Concrete(TypeReference t) => Transform(t, g => g.Type == GenericParameterType.Method && target is GenericInstanceMethod gm ? gm.GenericArguments[g.Position] :
            g.Type == GenericParameterType.Type && target.DeclaringType is GenericInstanceType gt ? gt.GenericArguments[g.Position] : g);
        var parameters = new Dictionary<GenericParameter, GenericParameter>();
        TypeReference Map(TypeReference t) => Transform(t, g => {
            if (!parameters.TryGetValue(g, out var replacement)) { replacement = new GenericParameter("T" + parameters.Count, wrapper) { Attributes = g.Attributes }; parameters.Add(g, replacement); wrapper.GenericParameters.Add(replacement); }
            return replacement;
        });
        TypeReference ret = construct ? target.DeclaringType : Concrete(target.ReturnType);
        if (ret.IsByReference || ret.IsPointer) throw new InvalidOperationException("Unsupported native ref/pointer return: " + site.member);
        wrapper.ReturnType = Map(ret);
        if (receiver)
        {
            TypeReference owner = constrained ?? target.DeclaringType;
            wrapper.Parameters.Add(new ParameterDefinition("receiver", ParameterAttributes.None, Map(constrained != null || owner.IsValueType ? new ByReferenceType(owner) : owner)));
        }
        foreach (var p in target.Parameters) wrapper.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, Map(Concrete(p.ParameterType))));
        _ = Map(target.DeclaringType);
        if (target is GenericInstanceMethod genericArguments) foreach (var argument in genericArguments.GenericArguments) _ = Map(argument);
        for (int index = 0; index < parameters.Count; index++)
        { var pair = parameters.ElementAt(index); foreach (var constraint in pair.Key.Constraints) pair.Value.Constraints.Add(new GenericParameterConstraint(Map(constraint.ConstraintType))); }
        var runtime = module.Types.Single(t => t.FullName == Runtime);
        MethodDefinition Helper(string name) => runtime.Methods.Single(m => m.Name == name);
        var body = wrapper.Body; body.InitLocals = true;
        var il = body.GetILProcessor();
        var token = new VariableDefinition(module.TypeSystem.Object); body.Variables.Add(token);
        var error = new VariableDefinition(new TypeReference("System", "Exception", module, module.TypeSystem.CoreLibrary));
        body.Variables.Add(error);
        VariableDefinition? result = null;
        if (wrapper.ReturnType.MetadataType != MetadataType.Void) { result = new VariableDefinition(wrapper.ReturnType); body.Variables.Add(result); }
        void ArgObject(int index)
        {
            if (index < 0 || index >= wrapper.Parameters.Count) { il.Emit(OpCodes.Ldnull); return; }
            var p = wrapper.Parameters[index]; il.Emit(OpCodes.Ldarg, p);
            TypeReference t = p.ParameterType;
            if (t is ByReferenceType reference) { t = reference.ElementType; il.Emit(OpCodes.Ldobj, t); }
            if (t.IsValueType || t.IsGenericParameter) il.Emit(OpCodes.Box, t);
        }
        int first = receiver ? 1 : 0;
        il.Emit(OpCodes.Ldstr, site.id + " " + site.caller + "@IL_" + site.offset.ToString("x4"));
        il.Emit(OpCodes.Ldstr, site.member); il.Emit(OpCodes.Ldstr, site.category);
        int reflectTarget = target.DeclaringType.FullName == "System.Type" ? first + 3 : first;
        ArgObject(site.category == "reflection" && target.DeclaringType.FullName != "System.Delegate" && target.DeclaringType.FullName != "System.Activator" ? reflectTarget : receiver || site.category == "object-dispatch" ? 0 : -1);
        ArgObject(site.category == "reflection" ? target.DeclaringType.FullName == "System.Activator" ? first : receiver ? 0 : -1 : -1);
        int selector = target.Name is "Find" or "GetAttribute" or "SetAttribute" or "GetComposition" ? first : -1;
        if (site.category == "reflection")
        {
            int arrayIndex = target.Parameters.Select((p, i) => (p, i)).Where(x => x.p.ParameterType.FullName == "System.Object[]").Select(x => x.i).DefaultIfEmpty(-1).Last();
            selector = arrayIndex < 0 ? -1 : first + arrayIndex;
        }
        ArgObject(selector);
        il.Emit(OpCodes.Call, Helper("Enter")); il.Emit(OpCodes.Stloc, token);
        var start = Instruction.Create(OpCodes.Nop); il.Append(start);
        foreach (var p in wrapper.Parameters)
        {
            il.Emit(OpCodes.Ldarg, p);
            if (Program.EnumerableType(p.ParameterType) && site.category == "enumeration-input")
            {
                if (p.ParameterType is GenericInstanceType enumerable) { var helper = new GenericInstanceMethod(Helper("EnumerateGeneric")); helper.GenericArguments.Add(enumerable.GenericArguments[0]); il.Emit(OpCodes.Call, helper); }
                else if (target.DeclaringType.FullName == "System.Linq.Enumerable" && target.Name == "Cast" && target is GenericInstanceMethod cast)
                { var helper = new GenericInstanceMethod(Helper("EnumerateCast")); helper.GenericArguments.Add(Map(cast.GenericArguments[0])); il.Emit(OpCodes.Call, helper); }
                else il.Emit(OpCodes.Call, Helper("Enumerate"));
            }
        }
        // Clone target signatures without changing their definition-level generic variables.
        // Only generic instance arguments refer to the caller and need remapping.
        TypeReference Owner(TypeReference t) => Map(t);
        var called = new MethodReference(target.Name, target is GenericInstanceMethod original ? original.ElementMethod.ReturnType : target.ReturnType, Owner(target.DeclaringType)) { HasThis = target.HasThis, ExplicitThis = target.ExplicitThis, CallingConvention = target.CallingConvention };
        var element = target is GenericInstanceMethod gmTarget ? gmTarget.ElementMethod : target;
        foreach (var gp in element.GenericParameters) called.GenericParameters.Add(new GenericParameter(gp.Name, called));
        TypeReference DefinitionType(TypeReference t) => Transform(t, g => g.Type == GenericParameterType.Method && g.Position < called.GenericParameters.Count ? called.GenericParameters[g.Position] : g);
        called.ReturnType = DefinitionType(element.ReturnType);
        foreach (var p in element.Parameters) called.Parameters.Add(new ParameterDefinition(DefinitionType(p.ParameterType)));
        MethodReference invocation = called;
        if (target is GenericInstanceMethod generic) { var instance = new GenericInstanceMethod(called); foreach (var a in generic.GenericArguments) instance.GenericArguments.Add(Map(a)); invocation = instance; }
        if (constrained != null) il.Emit(OpCodes.Constrained, Map(constrained));
        il.Emit(instruction.OpCode, invocation);
        if (result != null) il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Ldloc, token);
        if (result == null) il.Emit(OpCodes.Ldnull);
        else { il.Emit(OpCodes.Ldloc, result); if (wrapper.ReturnType.IsValueType || wrapper.ReturnType.IsGenericParameter) il.Emit(OpCodes.Box, wrapper.ReturnType); }
        il.Emit(OpCodes.Call, Helper("Returned"));
        var exit = Instruction.Create(OpCodes.Nop); il.Emit(OpCodes.Leave, exit);
        var handler = Instruction.Create(OpCodes.Stloc, error); il.Append(handler);
        il.Emit(OpCodes.Ldloc, token); il.Emit(OpCodes.Ldloc, error); il.Emit(OpCodes.Call, Helper("Threw")); il.Emit(OpCodes.Rethrow); il.Append(exit);
        if (result != null) il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ret);
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) { CatchType = error.VariableType, TryStart = start, TryEnd = handler, HandlerStart = handler, HandlerEnd = exit });
        if (!wrapper.HasGenericParameters) return wrapper;
        var call = new GenericInstanceMethod(wrapper); foreach (var gp in parameters.Keys) call.GenericArguments.Add(gp); return call;
    }
    static TypeReference Transform(TypeReference t, Func<GenericParameter, TypeReference> generic)
    {
        if (t is GenericParameter g) return generic(g);
        if (t is GenericInstanceType instance) { var copy = new GenericInstanceType(instance.ElementType); foreach (var a in instance.GenericArguments) copy.GenericArguments.Add(Transform(a, generic)); return copy; }
        if (t is ByReferenceType reference) return new ByReferenceType(Transform(reference.ElementType, generic));
        if (t is ArrayType array) return new ArrayType(Transform(array.ElementType, generic), array.Rank);
        if (t is OptionalModifierType optional) return new OptionalModifierType(optional.ModifierType, Transform(optional.ElementType, generic));
        if (t is RequiredModifierType required) return new RequiredModifierType(required.ModifierType, Transform(required.ElementType, generic));
        return t;
    }
}
