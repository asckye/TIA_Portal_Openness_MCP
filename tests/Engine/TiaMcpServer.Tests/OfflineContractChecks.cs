using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TiaMcpServer.Tests
{
    // Shared assertions run against linked production tools in the offline suites
    // and against the built assembly through the normal HttpTests mode.
    internal sealed class OfflineContractChecks
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private readonly Assembly Engine;
        private readonly Action<bool, string> Check;

        internal OfflineContractChecks(Assembly engine, Action<bool, string> check)
        {
            Engine = engine;
            Check = check;
        }

        internal void Run(string repository)
        {
            Names(repository);
            Builders(repository);
            ArtifactUnion(repository);
            FileOutputs(repository);
            Rejections();
            Envelopes();
            Bridge();
            Schemas();
        }

        private Type Type(string name) => Engine.GetType("TiaMcpServer.ModelContextProtocol." + name, true)!;
        private object Invoke(string type, string method, params object?[] arguments)
        {
            var target = Type(type);
            var member = target.GetMethods(All).Single(m => m.Name == method && m.GetParameters().Length == arguments.Length
                && m.GetParameters().Select((p, i) => arguments[i] == null || arguments[i] is JsonElement || p.ParameterType.IsInstanceOfType(arguments[i])).All(matches => matches));
            arguments = member.GetParameters().Select((p, i) => arguments[i] is JsonElement json && p.ParameterType != typeof(JsonElement)
                ? JsonSerializer.Deserialize(json.GetRawText(), p.ParameterType) : arguments[i]).ToArray();
            object? instance = null;
            if (!member.IsStatic)
            {
                var constructor = target.GetConstructors(All).Single();
                instance = constructor.Invoke(constructor.GetParameters().Select(_ => (object?)null).ToArray());
            }
            var result = member.Invoke(instance, arguments)!;
            if (result is Task task)
            {
                task.GetAwaiter().GetResult();
                return task.GetType().GetProperty("Result")!.GetValue(task)!;
            }
            return result;
        }
        private JsonElement Input(string json) => JsonSerializer.Deserialize<JsonElement>(json);
        private JsonObject Body(object result) => (JsonObject)result.GetType().GetProperty("StructuredContent")!.GetValue(result)!;
        private string? Text(JsonNode body, string property) => (string?)body[property];

        private JsonObject Admission(string name, string json)
        {
            var logic = Assembly.Load("TiaMcp.Logic");
            var argumentsType = logic.GetType("TiaMcp.Logic.V4.Inputs.ToolArguments", true)!;
            object? error;
            try
            {
                object?[] arguments = { name, Activator.CreateInstance(argumentsType, new object[] { Input(json) }), null, null };
                error = Type("McpServer").GetMethod("BindV4Call", All)!.Invoke(null, arguments);
            }
            catch (TargetInvocationException exception) when (exception.InnerException?.GetType().Name == "InputRejection")
            {
                error = exception.InnerException.GetType().GetMethod("ToError", All)!.Invoke(exception.InnerException, new object[] { "arguments" });
            }
            Check(error != null, "Shared admission refuses " + name);
            return Body(Invoke("McpServer", "V4Reject", name, error, null));
        }

        private void Schemas()
        {
            var failures = new List<string>();
            foreach (string group in Groups)
                foreach (var method in Type(group).GetMethods(All))
                {
                    var attribute = method.GetCustomAttributes().SingleOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
                    if (attribute == null) continue;
                    string name = (string)attribute.GetType().GetProperty("Name")!.GetValue(attribute)!;
                    try
                    {
                        var schema = (JsonElement)Invoke("McpServer", "ToolInputSchema", name, method);
                        foreach (var parameter in method.GetParameters().Where(p => p.ParameterType != typeof(string) && !p.ParameterType.IsValueType))
                        {
                            var property = schema.GetProperty("properties").GetProperty(parameter.Name!);
                            Check(property.TryGetProperty("type", out _) || property.TryGetProperty("anyOf", out _) || property.TryGetProperty("$ref", out _), "Concrete schema for " + name + "." + parameter.Name);
                            Check(!property.TryGetProperty("default", out var value) || value.ValueKind != JsonValueKind.Null, "No invalid null default: " + name);
                            var description = parameter.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
                            Check(description == null || property.GetProperty("description").GetString() == description.Description, "Typed parameter description retained: " + name + "." + parameter.Name);
                        }
                        Check(true, "Schema registration: " + name);
                    }
                    catch (Exception error)
                    {
                        while (error.InnerException != null) error = error.InnerException;
                        failures.Add(name + ": " + error.Message);
                    }
                }
            var documentation = Type("PlcDocumentationTools").GetMethod("GeneratePlcDocumentationV4", All)!;
            var documentSchema = (JsonElement)Invoke("McpServer", "ToolInputSchema", "GeneratePlcDocumentation", documentation);
            var nullListError = Invoke("McpServer", "ValidateV4Arguments", documentation,
                Input("{\"directory\":\"C:\\\\fixture\",\"outputPath\":\"C:\\\\fixture.md\",\"extensions\":null}"), documentSchema);
            Check(nullListError != null, "Shared admission refuses explicit null for an optional string array");
            Check(failures.Count == 0, "Shared schema registration failed: " + string.Join("; ", failures));
        }

        internal static readonly string[] Groups = {
            "EcosystemTools", "EngineeringAuditTools", "GitWorkflowTools", "ImportOrderTools", "OfflineAnalysisTools",
            "OfflineSuiteTools", "PlcBuildTools", "PlcDocumentationTools", "QualityAuditTools", "TemplateTools", "V21EcosystemTools", "XmlBuilderTools"
        };

        private void Names(string repository)
        {
            var calls = JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "reference/tool-examples/calls.json")))!["profiles"]!["full-engine"]!.AsObject();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string group in Groups)
                foreach (var method in Type(group).GetMethods(All))
                {
                    var attribute = method.GetCustomAttributes().SingleOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
                    if (attribute == null) continue;
                    string name = (string)attribute.GetType().GetProperty("Name")!.GetValue(attribute)!;
                    Check(names.Add(name) && calls.ContainsKey(name), "Unique V4 name with an example: " + name);
                    Check(method.GetParameters().All(p => !p.Name!.EndsWith("Json", StringComparison.Ordinal) && p.Name != "json"), "No encoded parameters: " + name);
                    Check(method.GetParameters().All(p => p.ParameterType != typeof(JsonElement) && p.ParameterType.Name != "OptionalOfflineInput"), "No erased carriers: " + name);
                    Check(method.ReturnType.Name == "CallToolResult" || method.ReturnType.GenericTypeArguments.Single().Name == "CallToolResult", "Envelope return: " + name);
                }
            Check(names.Count == 46, "All 46 entries migrated");
            foreach (string old in new[] { "BuildPlcUdtXml", "ComposePlcFbBlockXml", "ReadOpennessGuidance", "LintPlcSclSource", "PlcBuildAndImport" })
                Check(!names.Contains(old), "No legacy alias: " + old);
        }

        private void Builders(string repository)
        {
            var calls = JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "reference/tool-examples/calls.json")))!["profiles"]!["full-engine"]!.AsObject();
            foreach (string group in new[] { "XmlBuilderTools", "OfflineSuiteTools" })
                foreach (var method in Type(group).GetMethods(All).Where(m => m.Name.EndsWith("V4", StringComparison.Ordinal)))
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 0 || !(parameters[0].ParameterType.Namespace?.StartsWith("TiaMcp.Logic.V4.", StringComparison.Ordinal) == true)
                        || method.Name.StartsWith("Write", StringComparison.Ordinal)) continue;
                    string name = (string)method.GetCustomAttributes().Single(a => a.GetType().Name == "McpServerToolAttribute").GetType()
                        .GetProperty("Name")!.GetValue(method.GetCustomAttributes().Single(a => a.GetType().Name == "McpServerToolAttribute"))!;
                    var example = calls[name]!["arguments"]!.AsObject();
                    var arguments = parameters.Select(p => p == parameters[0] ? JsonSerializer.Deserialize(example[p.Name!]!.ToJsonString(), p.ParameterType)
                        : p.Name == "outputReleaseKey" ? "21" : p.DefaultValue).ToArray();
                    var body = Body(Invoke(group, method.Name, arguments));
                    Check((bool)body["ok"]!, "Builder example: " + name + " " + body.ToJsonString());
                    var inputType = parameters[0].ParameterType;
                    string adapted = inputType.Namespace == "TiaMcp.Logic.V4.Construction"
                        ? (string)inputType.Assembly.GetType("TiaMcp.Logic.V4.Construction.ConstructionAdapter", true)!.GetMethod("ToBuilderJson")!.Invoke(null, new[] { arguments[0] })!
                        : ((JsonObject)inputType.GetMethod("ToBuilderInput", All)!.Invoke(arguments[0], null)!).ToJsonString();
                    var legacyArguments = (object?[])arguments.Clone(); legacyArguments[0] = adapted;
                    var legacy = Invoke(group, method.Name.Substring(0, method.Name.Length - 2), legacyArguments);
                    var xml = legacy.GetType().GetProperty("Xml")?.GetValue(legacy) as string;
                    if (xml != null) Check(xml == (string?)body["data"]!["xml"], "Candidate XML bytes retained: " + name);
                    Check(Text(body["meta"]!, "execution") == "read-only", "Builder execution: " + name);
                }
            foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
                Check((bool)Body(Invoke("XmlBuilderTools", "BuildPlcUdtXmlV4", Input("{\"name\":\"UDT_A\",\"members\":[{\"name\":\"Ready\",\"datatype\":\"Bool\"}]}"), release))["ok"]!, "Output release " + release);
        }

        private void ArtifactUnion(string repository)
        {
            var calls = JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "reference/tool-examples/calls.json")))!["profiles"]!["full-engine"]!;
            var logic = Assembly.Load("TiaMcp.Logic");
            var argumentsType = logic.GetType("TiaMcp.Logic.V4.Inputs.ToolArguments", true)!;
            var unionType = logic.GetType("TiaMcp.Logic.V4.Construction.PlcArtifactSpec", true)!;
            foreach (var sample in new[] {
                ("udt", "BuildPlcUdt", "udt"), ("tagtable", "BuildPlcTagTable", "tagTable"),
                ("globaldb", "BuildPlcGlobalDb", "globalDb"), ("fc", "BuildPlcFcBlock", "fcBlock"), ("fb", "BuildPlcFbBlock", "fbBlock") })
            {
                var spec = calls[sample.Item2]!["arguments"]![sample.Item3]!.DeepClone();
                var wire = new JsonObject { ["softwarePath"] = "", ["kind"] = sample.Item1, ["spec"] = spec };
                object?[] binding = { "BuildAndImportPlcArtifact", Activator.CreateInstance(argumentsType, new object[] { Input(wire.ToJsonString()) }), null, null };
                Check(Type("McpServer").GetMethod("BindV4Call", All)!.Invoke(null, binding) == null, "Union admission: " + sample.Item1);
                var bound = ((object?[])binding[3]!)[2]!;
                Check(unionType.IsInstanceOfType(bound), "Union binding: " + sample.Item1);
                var resolved = unionType.GetMethod("Resolve")!.Invoke(bound, new object[] { sample.Item1 });
                string expected = (string)logic.GetType("TiaMcp.Logic.V4.Construction.ConstructionAdapter", true)!.GetMethod("ToBuilderJson")!.Invoke(null, new[] { resolved })!;
                Check((string)Invoke("OfflineContracts", "Artifact", sample.Item1, bound, "spec") == expected, "Resolved builder input: " + sample.Item1);
                string mismatch = sample.Item1 == "udt" ? "globaldb" : "udt";
                var refused = Body(Invoke("PlcBuildTools", "PlcBuildAndImportV4", "", mismatch, bound, "", "", "", true, false));
                Check(Text(refused["error"]!, "code") == "INVALID_ARGUMENT" && Text(refused["meta"]!, "execution") == "not-started", "Kind mismatch refuses before import: " + sample.Item1);
            }
            foreach (string spec in new[] { "null", "\"{}\"", "[]", "{}", "{\"members\":[],\"extra\":1}" })
                Check(Text(Admission("BuildAndImportPlcArtifact", "{\"softwarePath\":\"\",\"kind\":\"udt\",\"spec\":" + spec + "}")["error"]!, "code") == "INVALID_ARGUMENT", "Union rejects invalid wire input: " + spec);
        }

        private void FileOutputs(string repository)
        {
            string root = Path.Combine(repository, "bin-build", "p6-09", "files-" + Guid.NewGuid().ToString("N"));
            string before = Path.Combine(root, "before"), after = Path.Combine(root, "after");
            Directory.CreateDirectory(before); Directory.CreateDirectory(after);
            string template = Path.Combine(root, "template.xml");
            File.WriteAllText(template, "<Document><Name>{{Name}}</Name></Document>", new System.Text.UTF8Encoding(false));
            const string rows = "[{\"fileName\":\"FC_1.xml\",\"values\":{\"Name\":\"FC_1 & alarm\"}}]";
            Invoke("TemplateTools", "InstantiatePlcXmlTemplates", template, rows, before, false);
            var expanded = Body(Invoke("TemplateTools", "InstantiatePlcXmlTemplatesV4", template, Input(rows), after, false));
            Check((bool)expanded["ok"]!, "Typed template writes succeed");
            Check(File.ReadAllBytes(Path.Combine(before, "FC_1.xml")).SequenceEqual(File.ReadAllBytes(Path.Combine(after, "FC_1.xml"))), "Template output bytes retained");
            Check(Text(expanded["meta"]!, "execution") == "completed", "Template file write is completed execution");

            string source = Path.Combine(root, "FC_Doc.scl");
            File.WriteAllText(source, "FUNCTION \"FC_Doc\" : Void\nBEGIN\nEND_FUNCTION\n", new System.Text.UTF8Encoding(false));
            string oldDocument = Path.Combine(before, "doc.md"), newDocument = Path.Combine(after, "doc.md");
            Invoke("PlcDocumentationTools", "RenderPlcBlockDocument", source, "", "", "LR", oldDocument, 60000);
            var document = Body(Invoke("PlcDocumentationTools", "RenderPlcBlockDocumentV4", source, "", "", "LR", newDocument, 60000));
            Check((bool)document["ok"]!, "Document file generation succeeds");
            Check(File.ReadAllBytes(oldDocument).SequenceEqual(File.ReadAllBytes(newDocument)), "Markdown output bytes retained");
            Check(Text(document["meta"]!, "execution") == "completed", "Document file write is completed execution");
        }

        private void Rejections()
        {
            foreach (string json in new[] { "null", "\"{}\"", "[]", "{}", "{\"members\":[],\"extra\":1}", "{\"Members\":[]}",
                "{\"members\":[],\"members\":[]}", "{\"members\":[{\"name\":\"A\",\"datatype\":\"Bool\",\"externalWritable\":1}]}" })
            {
                var body = Admission("BuildPlcUdt", "{\"udt\":" + json + "}");
                Check(Text(body["error"]!, "code") == "INVALID_ARGUMENT" && Text(body["meta"]!, "execution") == "not-started", "Reject UDT " + json);
            }
            var over = Admission("BuildPlcUdt", "{\"udt\":{\"name\":\"" + new string('a', 4097) + "\",\"members\":[]}}");
            Check(Text(over["error"]!, "code") == "LIMIT_EXCEEDED", "String budget before builder");
            var invalidRelease = Body(Invoke("XmlBuilderTools", "BuildPlcUdtXmlV4", Input("{\"members\":[]}"), "14"));
            Check(!(bool)invalidRelease["ok"]!, "Original output release restriction");
            var specType = Assembly.Load("TiaMcp.Logic").GetType("TiaMcp.Logic.V4.Construction.UdtSpec", true)!;
            var union = Body(Invoke("PlcBuildTools", "PlcBuildAndImportV4", "", "globaldb", JsonSerializer.Deserialize("{\"members\":[]}", specType), "", "", "", true, false));
            Check(Text(union["error"]!, "code") == "INVALID_ARGUMENT", "Outer kind selects spec before native dispatch");
            var nullNames = Admission("ValidateClassicHmiMinimalPackagePlcSync", "{\"path\":\"absent\",\"plcSymbols\":null}");
            Check(Text(nullNames["error"]!, "code") == "INVALID_ARGUMENT", "Null is not an omitted list");
            var plan = Body(Invoke("ImportOrderTools", "PlanArtifactImportOrderV4", Input("[{\"id\":\"B\",\"dependencies\":[\"A\"]},{\"id\":\"A\"}]")));
            Check((bool)plan["ok"]!, "Typed dependency plan");
            var cycle = Admission("PlanArtifactImportOrder", "{\"artifacts\":[{\"id\":\"A\",\"dependencies\":[\"A\"]}]}");
            Check(!(bool)cycle["ok"]!, "Invalid DAG cannot become success");
            var lintNull = Admission("AnalyzePlcSclSource", "{\"sourceText\":\"// source\",\"rules\":null}");
            Check(Text(lintNull["error"]!, "code") == "INVALID_ARGUMENT", "Explicit null optional rules are refused");
            var lintDefault = Body(Invoke("PlcDocumentationTools", "LintPlcSclSourceV4", "// source", "", null, 500));
            Check((bool)lintDefault["ok"]!, "Omitted rules retain parser defaults");
            var duplicate = Admission("PlanArtifactImportOrder", "{\"artifacts\":[],\"Artifacts\":[]}");
            Check(Text(duplicate["error"]!, "code") == "INVALID_ARGUMENT", "Shared case-duplicate rejection is V4");
            var missing = Admission("BuildPlcUdt", "{}");
            Check(Text(missing["meta"]!, "execution") == "not-started", "Shared missing argument rejection is V4");
            var nativeCompare = Body(Invoke("OfflineAnalysisTools", "ComparePlcBlockDocumentsV4", "", "", "PLC", "A", "B", -1, 100, 2));
            Check(Text(nativeCompare["meta"]!, "behaviorPolicy") == "current", "Native export policy remains current even on refusal");
            Check(Text(nativeCompare["meta"]!, "execution") == "not-started", "Invalid page is refused before native export");
            var fileCompare = Body(Invoke("OfflineAnalysisTools", "ComparePlcBlockDocumentsV4", "absent", "absent", "", "", "", -1, 100, 2));
            Check(Text(fileCompare["meta"]!, "behaviorPolicy") == "not-applicable", "Offline file comparison has no native policy");
        }

        private JsonObject Map(string evidence, bool writes = false)
        {
            var response = Activator.CreateInstance(Type("ResponseMessage"))!;
            response.GetType().GetProperty("Message")!.SetValue(response, "message is not a verdict");
            response.GetType().GetProperty("Meta")!.SetValue(response, JsonNode.Parse(evidence));
            return Body(Invoke("OfflineContracts", "Map", "GroupFixture", response, writes, true));
        }

        private void Envelopes()
        {
            foreach (var sample in new[] {
                ("{\"success\":true}", false, "succeeded"), ("{}", false, "read-failed"),
                ("{\"success\":false}", false, "read-failed"),
                ("{\"success\":false,\"status\":\"InvalidParams\"}", false, "rejected-before-operation"),
                ("{\"success\":false,\"mayHaveChanged\":true}", true, "unknown"),
                ("{\"success\":true,\"filesFailed\":1}", false, "partial"),
                ("{\"success\":false,\"files\":[{\"written\":true}]}", true, "partial") })
            {
                var body = Map(sample.Item1, sample.Item2);
                Check(Text(body["meta"]!, "outcome") == sample.Item3, "Envelope " + sample.Item3);
                Check(Text(body["meta"]!, "behaviorPolicy") == "current", "D1 remains current");
                Check((bool)body["ok"]! == (sample.Item3 == "succeeded"), "No false success");
            }
            var unknown = Map("{\"success\":true,\"requiresSessionReset\":true,\"evidence\":{\"executed\":true}}", true);
            Check(Text(unknown["error"]!, "code") == "OUTCOME_UNKNOWN" && (bool)unknown["meta"]!["requiresSessionReset"]!, "Unknown overrides success and requires reset");
            Check(unknown["data"]!["evidence"]!["executed"]!.GetValue<bool>(), "Retain native evidence");
            var partial = Map("{\"success\":true,\"dataComplete\":false}");
            Check(Text(partial["meta"]!, "completeness") == "partial" && partial["meta"]!["warnings"]!.AsArray().Count == 2, "Incomplete success has a warning");
            var error = Map("{\"success\":false,\"error\":\"secret stack trace\"}");
            Check(!error.ToJsonString().Contains("secret stack trace"), "Legacy exception stack is not exposed");
        }

        private void Bridge()
        {
            var logic = Assembly.Load("TiaMcp.Logic");
            var argumentsType = logic.GetType("TiaMcp.Logic.V4.Inputs.ToolArguments", true)!;
            var callType = logic.GetType("TiaMcp.Logic.V4.Inputs.ToolCall", true)!;
            object Arguments(string text) => Activator.CreateInstance(argumentsType, new object[] { Input(text) })!;
            const string json = "{\"artifacts\":[{\"id\":\"A\"}]}";
            var direct = Body(Invoke("ImportOrderTools", "PlanArtifactImportOrderV4", Input("[{\"id\":\"A\"}]")));
            var bridge = Body(Invoke("McpServer", "CallTool", "PlanArtifactImportOrder", Arguments(json)));
            Check((bool)bridge["ok"]!, "Bridge dispatches a migrated typed input");
            Check(JsonNode.DeepEquals(direct["data"], bridge["data"]), "Direct and bridge retain identical data");
            var calls = Array.CreateInstance(callType, 1);
            calls.SetValue(Activator.CreateInstance(callType, "PlanArtifactImportOrder", Arguments(json)), 0);
            var batch = Body(Invoke("McpServer", "ReadToolBatch", calls, ""));
            var child = batch["data"]!["items"]![0]!["result"]!;
            Check(JsonNode.DeepEquals(direct["data"], child["data"]), "Batch preserves the target envelope");
            var old = Body(Invoke("McpServer", "CallTool", "PlanArtifactImportOrder", Arguments("{\"artifactsJson\":\"[]\"}")));
            Check(Text(old["error"]!, "code") == "INVALID_ARGUMENT", "Bridge rejects the retired argument");
        }
    }
}
