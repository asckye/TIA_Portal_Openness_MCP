using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Esprima;
using Esprima.Ast;

namespace TiaMcpServer.Siemens
{
    internal static class JavaScriptEvidence
    {
        private static object? P(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);
        private static string Slice(string text, Node node) => text.Substring(node.Range.Start, node.Range.End - node.Range.Start);
        private static JsonObject Location(Node n, string path) => new JsonObject { ["file"] = path, ["startOffset"] = n.Range.Start, ["endOffset"] = n.Range.End,
            ["startLine"] = n.Location.Start.Line, ["startColumn"] = n.Location.Start.Column, ["offsetUnit"] = "UTF-16 code units; end exclusive" };
        internal static JsonObject Analyze(string text, string path)
        {
            var result = new JsonObject { ["path"] = path, ["kind"] = "scriptAnalysis", ["status"] = "ok", ["rawText"] = text,
                ["bodyReadSuccess"] = true, ["parser"] = "Esprima 3.0.5; parsing only, never execution",
                ["referenceCoverage"] = "syntactic candidates for Tags()/HMIRuntime.Tags(), imports and faceplate popup calls; aliases, eval and runtime name construction require separate resolution",
                ["runtimeResolutionComplete"] = false };
            Node tree;
            try { tree = new JavaScriptParser(new ParserOptions { Tolerant = false }).ParseModule(text); }
            catch (Exception ex) { result["status"] = "unsupported"; result["parseComplete"] = false; result["reason"] = "Original text retained; AST parsing failed: " + ex.Message; return result; }
            var functions = new JsonArray(); var imports = new JsonArray(); var references = new JsonArray(); var globals = new JsonArray();
            var stack = new Stack<(Node Node, Node? Parent)>(); stack.Push((tree, null));
            while (stack.Count > 0)
            {
                var pair = stack.Pop(); var node = pair.Node; string type = node.Type.ToString();
                if (node is IFunction function)
                {
                    var body = function.Body; var parameters = new JsonArray();
                    foreach (var param in function.Params) parameters.Add(Slice(text, param));
                    string? name = function.Id?.Name;
                    if (name == null && pair.Parent != null)
                    {
                        var identifier = P(pair.Parent, "Id") as Node ?? P(pair.Parent, "Key") as Node ?? P(pair.Parent, "Left") as Node;
                        if (identifier != null) name = Slice(text, identifier);
                    }
                    functions.Add(new JsonObject { ["name"] = name, ["kind"] = type, ["parameters"] = parameters,
                        ["rawText"] = Slice(text, node), ["body"] = body == null ? null : Slice(text, body), ["evidence"] = Location(node, path) });
                }
                if (type == "ImportDeclaration" || type == "ImportExpression" || type == "ExportAllDeclaration" || type == "ExportNamedDeclaration")
                {
                    var source = P(node, "Source") as Node;
                    if (source != null) imports.Add(new JsonObject { ["expression"] = Slice(text, node), ["module"] = P(source, "Value") as string,
                        ["resolution"] = "unresolvedSource", ["evidence"] = Location(node, path) });
                }
                if (node is CallExpression callNode)
                {
                    var callee = callNode.Callee;
                    string? call = callee == null ? null : Slice(text, callee);
                    string? referenceKind = call == "Tags" || call == "HMIRuntime.Tags" ? "tag" : call == "UI.OpenFaceplateInPopup" || call == "HMIRuntime.UI.OpenFaceplateInPopup" ? "faceplateType" : null;
                    if (referenceKind != null)
                    {
                        var arg = callNode.Arguments.Count == 0 ? null : callNode.Arguments[0]; var literal = arg == null ? null : P(arg, "Value") as string;
                        references.Add(new JsonObject { ["kind"] = referenceKind, ["literalName"] = literal,
                            ["expression"] = arg == null ? null : Slice(text, arg), ["callExpression"] = Slice(text, node),
                            ["resolution"] = literal == null ? "runtimeExpressionUnresolved" : "literalCandidateRequiresExactNameMatch",
                            ["evidence"] = Location(node, path) });
                    }
                }
                foreach (var child in node.ChildNodes.Reverse()) stack.Push((child, node));
            }
            foreach (Node statement in ((Esprima.Ast.Program)tree).Body)
            {
                var declaration = P(statement, "Declaration") as Node;
                if (statement.Type.ToString() == "FunctionDeclaration" || declaration?.Type.ToString() == "FunctionDeclaration") continue;
                globals.Add(new JsonObject { ["rawText"] = Slice(text, statement), ["evidence"] = Location(statement, path) });
            }
            result["parseComplete"] = true; result["functions"] = functions; result["functionCount"] = functions.Count;
            result["imports"] = imports; result["references"] = references; result["globalDefinitions"] = globals;
            result["globalDefinitionsBasis"] = "Original top-level statements outside function declarations; complete original module text, including comments, is in rawText.";
            return result;
        }
    }
}
