using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TiaMcp.ReleaseTool;

internal sealed record CheckAllInputFinding(string File, int Line, string Detail);
internal sealed record CheckAllPathReference(string File, int Line, string Path);
internal sealed record CheckAllSuiteClosure(string Project, string[] Sources, string[] Projects, string[] Roots,
    CheckAllPathReference[] References, CheckAllInputFinding[] LiveReasons)
{
    internal string SourceIdentity { get; init; } = "";
    internal string Dotnet { get; init; } = "dotnet";
    internal string[] BuildArguments { get; init; } = [];
    internal bool WholeInventory { get; init; }
    internal CheckAllInputFinding[] FallbackReasons { get; init; } = [];
}

internal static class CheckAllInputClosure
{
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    internal static bool Covers(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    internal static CheckAllSuiteClosure Derive(string root, string project, JsonObject registration, IEnumerable<string> inventory,
        string dotnet = "dotnet", string? api = null)
    {
        root = Path.GetFullPath(root); project = Relative(root, Path.GetFullPath(Path.Combine(root, project)));
        var layout = inventory.ToArray();
        var top = layout.Select(path => path.Replace('\\', '/').Split('/')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        top.UnionWith(["reference", "manifest", "tests", "src", "scripts", "templates", "catalog", "docs", "build-tools", "sdk"]);
        var reasons = new List<CheckAllInputFinding>();
        var fallbacks = new List<CheckAllInputFinding>();
        var arguments = EvaluationArguments(registration, api);
        var evaluation = Evaluate(root, project, dotnet, arguments);
        var sources = evaluation.Sources;
        foreach (var missing in sources.Where(file => !File.Exists(Path.Combine(root, file)))) reasons.Add(new(missing, 1, "missing compiled source"));
        var references = new List<CheckAllPathReference>();
        var parsed = sources.Where(file => File.Exists(Path.Combine(root, file))).Select(file => (File: file, Tree: Parse(File.ReadAllText(Path.Combine(root, file))))).ToArray();
        var methods = parsed.SelectMany(pair => pair.Tree.DescendantNodes().OfType<MethodDeclarationSyntax>()).ToArray();
        foreach (var (file, tree) in parsed)
        {
            try { references.AddRange(References(file, tree, top)); }
            catch (ReleaseException ex) { fallbacks.Add(new(file, 1, "whole inventory for unresolvable repository path: " + ex.Message)); }
            // ProjectReference product sources and src/ Compile links are covered
            // by output bytes plus the common machine probe, not live exclusions.
            if (Covers(Path.GetDirectoryName(project)!.Replace('\\', '/'), file) || Covers("tests", file))
                reasons.AddRange(MachineInputs(file, tree));
            foreach (var call in tree.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = call.Expression.ToString().Split('.').Last();
                if (RootHelper(name) && !RootFlowKnown(call, tree, top, new HashSet<string>(StringComparer.Ordinal)))
                    fallbacks.Add(Finding(file, call, "whole inventory for unfollowable repository root: " + call.Expression));
                if (name is "LoadFrom" or "LoadFile" or "LoadFromAssemblyPath")
                {
                    bool known = call.ArgumentList.Arguments.All(arg => AssemblyPathKnown(file, arg.Expression, tree, methods, top, new HashSet<string>(StringComparer.Ordinal)));
                    if (!known) fallbacks.Add(Finding(file, call, "whole inventory for unresolved assembly path: " + call.Expression));
                }
            }
        }
        var roots = references.Select(item => item.Path).Append(Path.GetDirectoryName(project)!.Replace('\\', '/')).ToList();
        // A launched/loaded binary also reads its sibling dependencies/config/data.
        roots.AddRange(references.Where(item => Path.GetExtension(item.Path) is ".exe" or ".dll")
            .Select(item => Path.GetDirectoryName(item.Path)?.Replace('\\', '/')).OfType<string>());
        if (registration["resultCacheInputs"] is { } additions)
        {
            if (additions is not JsonArray paths) throw new ReleaseException("resultCacheInputs must be an additive path array: " + project);
            roots.AddRange(paths.Select(path => SafePath(path!.GetValue<string>())));
        }
        var projects = ProjectClosure(root, project, reasons);
        if (fallbacks.Count != 0) roots.AddRange(layout.Select(path => path.Replace('\\', '/').Split('/')[0]).Where(path => path != "bin-build"));
        return new(project, sources, projects, Minimize(roots), references.Distinct().ToArray(), reasons.Distinct().ToArray())
            { SourceIdentity = SourceKey(root, project, evaluation, arguments), Dotnet = dotnet, BuildArguments = arguments,
                WholeInventory = fallbacks.Count != 0, FallbackReasons = fallbacks.Distinct().ToArray() };
    }

    private static string SourceKey(string root, string project, CompileEvaluation evaluation, string[] arguments) => CheckAllResultCache.Fingerprint(
        CheckAllResultCache.Enumerate(root, evaluation.Sources.Concat(evaluation.Imports).Append(project), new Dictionary<string, string>()),
        new { discovery = "MSBuild Compile evaluation", arguments });

    internal static void RequireStable(string root, CheckAllSuiteClosure closure)
    {
        if (closure.LiveReasons.Length != 0) throw new IOException(string.Join("; ", closure.LiveReasons.Select(reason => $"{reason.File}:{reason.Line}: {reason.Detail}")));
        var evaluation = Evaluate(root, closure.Project, closure.Dotnet, closure.BuildArguments);
        if (SourceKey(root, closure.Project, evaluation, closure.BuildArguments) != closure.SourceIdentity)
            throw new IOException("Compiled-source closure changed after discovery: " + closure.Project);
    }

    // Reparse current sources, including newly added Compile items, independently
    // of the saved roots. An additive registry declaration cannot hide a reference.
    internal static void Guard(string root, CheckAllSuiteClosure saved, JsonObject registration, IEnumerable<string> inventory)
    {
        var current = Derive(root, saved.Project, registration, inventory, saved.Dotnet,
            saved.BuildArguments.FirstOrDefault(arg => arg.StartsWith("-p:TiaPublicApiRoot=", StringComparison.Ordinal))?.Split('=', 2)[1]);
        if (current.WholeInventory && !saved.WholeInventory) throw new ReleaseException("Undeclared repository input: current root/assembly flow requires the whole inventory");
        var outside = current.References.Where(reference => !saved.Roots.Any(prefix => Covers(prefix, reference.Path))).ToArray();
        if (outside.Length != 0) throw new ReleaseException("Undeclared repository input: " + string.Join("; ", outside.Select(reference =>
            $"{reference.File}:{reference.Line}: {reference.Path}")));
        if (current.LiveReasons.Length != 0) throw new ReleaseException("Suite must run live: " + string.Join("; ", current.LiveReasons.Select(reason =>
            $"{reason.File}:{reason.Line}: {reason.Detail}")));
    }

    private static string SafePath(string path)
    {
        path = path.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Split('/').Any(part => part is ".." or ".") || path.Contains(':'))
            throw new ReleaseException("Unsafe result cache input path: " + path);
        return path;
    }

    private static string[] Minimize(IEnumerable<string> paths)
    {
        var ordered = paths.Select(SafePath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length).ThenBy(path => path, StringComparer.Ordinal).ToArray();
        var roots = new List<string>();
        foreach (var path in ordered) if (!roots.Any(parent => Covers(parent, path))) roots.Add(path);
        return roots.Order(StringComparer.Ordinal).ToArray();
    }

    private static CompilationUnitSyntax Parse(string source)
    {
        // Scan every conditional branch, not just the CLI's compilation symbols.
        // Keep offsets/line numbers. Roslyn's recovery also retains skipped tokens.
        source = Regex.Replace(source, @"(?m)^[ \t]*#.*$", match => new string(' ', match.Length));
        return CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
    }

    private static IEnumerable<CheckAllPathReference> References(string file, SyntaxNode tree, HashSet<string> top)
    {
        var constants = tree.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(variable => variable.Initializer?.Value is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            .GroupBy(variable => variable.Identifier.ValueText).Where(group => group.Select(variable => ((LiteralExpressionSyntax)variable.Initializer!.Value).Token.ValueText).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => ((LiteralExpressionSyntax)group.First().Initializer!.Value).Token.ValueText, StringComparer.Ordinal);
        foreach (var combine in tree.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Where(IsCombine))
        {
            if (combine.Ancestors().OfType<InvocationExpressionSyntax>().Any(IsCombine)) continue;
            var prefix = CombinePrefix(combine, constants, top);
            if (prefix != null) yield return new(file, Line(combine), prefix);
            else foreach (var token in combine.DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.StringLiteralToken) && !token.IsKind(SyntaxKind.Utf8StringLiteralToken) && !token.IsKind(SyntaxKind.InterpolatedStringTextToken)) continue;
                var fallback = LiteralPrefix(token.ValueText, top, token.IsKind(SyntaxKind.InterpolatedStringTextToken));
                if (fallback != null) yield return new(file, Line(combine), fallback);
            }
        }
        foreach (var token in tree.DescendantTokens(descendIntoTrivia: true))
        {
            if (!token.IsKind(SyntaxKind.StringLiteralToken) && !token.IsKind(SyntaxKind.Utf8StringLiteralToken) && !token.IsKind(SyntaxKind.InterpolatedStringTextToken)) continue;
            if (token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().Any(IsCombine) == true) continue;
            var prefix = LiteralPrefix(token.ValueText, top, token.IsKind(SyntaxKind.InterpolatedStringTextToken));
            if (prefix != null) yield return new(file, token.GetLocation().GetLineSpan().StartLinePosition.Line + 1, prefix);
        }
    }

    private static bool NamesPath(string file, SyntaxNode node, HashSet<string> top)
    {
        try { return References(file, node, top).Any(); }
        catch (ReleaseException) { return false; }
    }

    private static bool IsCombine(InvocationExpressionSyntax call) => call.Expression.ToString().EndsWith("Path.Combine", StringComparison.Ordinal) ||
        call.Expression.ToString().EndsWith("Path.Join", StringComparison.Ordinal);

    private static bool RootHelper(string name) => Regex.IsMatch(name, @"^(?:Find(?:Repository|Repo)?Root|(?:Repository|Repo)Root|GetRepositoryRoot|LocateRoot)$");

    private static bool AssemblyPathKnown(string file, ExpressionSyntax value, SyntaxNode tree, MethodDeclarationSyntax[] methods, HashSet<string> top, HashSet<string> seen)
    {
        if (NamesPath(file, value, top) || OwnOutputOrTemp(value)) return true;
        if (value is IdentifierNameSyntax identifier && seen.Add(identifier.Identifier.ValueText))
        {
            var initializers = tree.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(variable => variable.Identifier.ValueText == identifier.Identifier.ValueText)
                .Select(variable => variable.Initializer?.Value).OfType<ExpressionSyntax>().ToArray();
            return initializers.Length > 0 && initializers.All(initializer => AssemblyPathKnown(file, initializer, tree, methods, top, new HashSet<string>(seen, StringComparer.Ordinal)));
        }
        if (value is InvocationExpressionSyntax call)
        {
            var name = call.Expression.ToString().Split('.').Last();
            var helpers = methods.Where(method => method.Identifier.ValueText == name).ToArray();
            return helpers.Length > 0 && helpers.All(method => NamesPath(file, method, top) || OwnOutputOrTemp(method));
        }
        return false;
    }

    private static bool RootFlowKnown(SyntaxNode value, SyntaxNode tree, HashSet<string> top, HashSet<string> seen)
    {
        var combine = value.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault(IsCombine);
        if (combine != null && NamesPath("source", combine, top)) return true;
        var method = value.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method != null && RootHelper(method.Identifier.ValueText) && value.Ancestors().Any(node => node is ReturnStatementSyntax or ArrowExpressionClauseSyntax)) return true;
        var variable = value.Parent is EqualsValueClauseSyntax equals ? equals.Parent as VariableDeclaratorSyntax : null;
        if (variable == null || !seen.Add(variable.Identifier.ValueText)) return false;
        var uses = tree.DescendantNodes().OfType<IdentifierNameSyntax>().Where(identifier => identifier.Identifier.ValueText == variable.Identifier.ValueText).ToArray();
        return uses.Length > 0 && uses.All(use => RootFlowKnown(use, tree, top, new HashSet<string>(seen, StringComparer.Ordinal)));
    }

    private static string? Literal(ExpressionSyntax expression, IReadOnlyDictionary<string, string> constants)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)) return literal.Token.ValueText;
        if (expression is IdentifierNameSyntax identifier && constants.TryGetValue(identifier.Identifier.ValueText, out var value)) return value;
        if (expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression) && Literal(binary.Left, constants) is { } left && Literal(binary.Right, constants) is { } right) return left + right;
        return null;
    }

    private static string? CombinePrefix(InvocationExpressionSyntax call, IReadOnlyDictionary<string, string> constants, HashSet<string> top)
    {
        string? prefix = null;
        foreach (var argument in call.ArgumentList.Arguments)
        {
            string? text = Literal(argument.Expression, constants);
            if (text == null && argument.Expression is InvocationExpressionSyntax nested && IsCombine(nested)) text = CombinePrefix(nested, constants, top);
            if (text == null)
            {
                if (prefix != null) break;
                var first = argument.Expression is InterpolatedStringExpressionSyntax interpolation ? interpolation.Contents.FirstOrDefault() as InterpolatedStringTextSyntax : null;
                var partial = first?.TextToken.ValueText ?? (argument.Expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression) ? Literal(binary.Left, constants) : null);
                if (partial != null) { prefix = LiteralPrefix(partial, top, true); if (prefix != null) break; }
                continue; // unknown repository root precedes its literal suffix
            }
            if (prefix == null) prefix = LiteralPrefix(text, top, false);
            else if (!Path.IsPathRooted(text) && text.IndexOfAny(['*', '?', '{', '}']) < 0) prefix = SafePath(prefix + "/" + text.Replace('\\', '/').Trim('/'));
            else break;
        }
        return prefix;
    }

    private static string? LiteralPrefix(string text, HashSet<string> top, bool partial)
    {
        text = text.Replace('\\', '/');
        while (text.StartsWith("../", StringComparison.Ordinal)) text = text[3..];
        if (text.StartsWith("./", StringComparison.Ordinal)) text = text[2..];
        if (!top.Contains(text.Split('/')[0])) return null;
        int variable = text.IndexOfAny(['*', '?', '{', '}']);
        if (variable >= 0) { text = text[..variable]; partial = true; }
        if (partial && !text.EndsWith('/') && text.Contains('/')) text = text[..(text.LastIndexOf('/') + 1)];
        // A bare top-level segment is a whole-directory dependency.
        return SafePath(text.TrimEnd('/'));
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static CheckAllInputFinding Finding(string file, SyntaxNode node, string detail) => new(file, Line(node), detail);
    private static bool OwnTemporary(SyntaxNode scope) => scope.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(call => call.Expression.ToString().EndsWith("Path.GetTempPath", StringComparison.Ordinal)) ||
        scope.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>().Any(creation => creation.Type.ToString().EndsWith("OfflineFixtures", StringComparison.Ordinal));

    private static bool OwnOutputOrTemp(SyntaxNode scope) => OwnTemporary(scope) || scope.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(member =>
        member.ToString() is "AppContext.BaseDirectory" or "System.AppContext.BaseDirectory" || member.Name.Identifier.ValueText == "Location" && member.Expression.ToString().EndsWith(".Assembly", StringComparison.Ordinal));

    internal static CheckAllInputFinding[] MachineInputs(string file, string source) => MachineInputs(file, Parse(source)).ToArray();

    private static IEnumerable<CheckAllInputFinding> MachineInputs(string file, CompilationUnitSyntax tree)
    {
        var aliases = tree.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(item => item.Alias != null && item.Name != null).GroupBy(item => item.Alias!.Name.Identifier.ValueText)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Name!.ToString()).FirstOrDefault(name =>
                Regex.IsMatch(name, @"\.(?:Registry|RegistryKey|ManagementObject|ServiceController|DriveInfo|Process|HttpClient|WebClient|Socket|TcpClient|Dns)$")) ?? group.First().Name!.ToString(), StringComparer.Ordinal);
        string Expand(string name) => aliases.TryGetValue(name, out var target) ? target : name;
        foreach (var node in tree.DescendantNodes())
        {
            if (node.AncestorsAndSelf().OfType<UsingDirectiveSyntax>().Any()) continue;
            if (node is IdentifierNameSyntax identifier)
            {
                var type = Expand(identifier.Identifier.ValueText).Split('.').Last();
                if (type is "Registry" or "RegistryKey" or "ManagementObject" or "ManagementObjectSearcher" or "ManagementClass" or "ServiceController" or "DriveInfo")
                    yield return Finding(file, node, "machine-state API: " + Expand(identifier.Identifier.ValueText));
            }
            if (node is InvocationExpressionSyntax call && call.Expression is MemberAccessExpressionSyntax member)
            {
                var owner = Expand(member.Expression.ToString()); var name = member.Name.Identifier.ValueText;
                if ((owner == "Process" || owner.EndsWith(".Process", StringComparison.Ordinal)) && name.StartsWith("GetProcesses", StringComparison.Ordinal))
                    yield return Finding(file, node, "foreign-process enumeration: " + owner + "." + name);
                if (Regex.IsMatch(name, @"^(?:Find|Get|Probe|Detect|Enumerate)Installed(?:Software|Products|Versions|Tia|Portal)") ||
                    name == "GetFolderPath" && call.ToString().Contains("ProgramFiles", StringComparison.Ordinal))
                    yield return Finding(file, node, "installed-software probe: " + call.Expression);
                if ((owner == "Dns" || owner.EndsWith(".Dns", StringComparison.Ordinal)) && !Loopback(call))
                    yield return Finding(file, node, "non-loopback or unresolved DNS: " + call.Expression);
            }
            if (node is BaseObjectCreationExpressionSyntax creation)
            {
                var typeName = creation is ObjectCreationExpressionSyntax explicitCreation ? explicitCreation.Type.ToString() :
                    creation.Ancestors().OfType<VariableDeclarationSyntax>().FirstOrDefault()?.Type.ToString() ??
                    creation.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Type.ToString() ?? "unresolved";
                var type = Expand(typeName).Split('.').Last();
                if (type is "HttpClient" or "WebClient" or "Socket" or "TcpClient" or "TcpListener")
                {
                    var scope = creation.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() ?? (SyntaxNode)tree;
                    bool fakeHandler = creation.ArgumentList?.Arguments.Any(argument => argument.Expression is ObjectCreationExpressionSyntax handler &&
                        tree.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(cls => cls.Identifier.ValueText == handler.Type.ToString() &&
                            cls.BaseList?.ToString().Contains("HttpMessageHandler", StringComparison.Ordinal) == true)) == true;
                    var cls = creation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() ?? (SyntaxNode)tree;
                    bool explicitHost = (type is "TcpClient" or "TcpListener") && creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax host &&
                        host.IsKind(SyntaxKind.StringLiteralExpression) && !Regex.IsMatch(host.Token.ValueText, @"^(?:localhost|127\.0\.0\.1|::1)$", RegexOptions.IgnoreCase);
                    if (!fakeHandler && (explicitHost || !Loopback(scope) && !(Loopback(cls) && !NonLoopbackUri(cls)) && !PrivateLoopbackFixtureClient(creation, tree))) yield return Finding(file, creation, "non-loopback or unresolved network: " + type);
                    else if (NonLoopbackUri(scope)) yield return Finding(file, creation, "non-loopback network target: " + type);
                }
                if (type == "FileSystemWatcher")
                {
                    var scope = creation.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() ?? (SyntaxNode)tree;
                    if (!OwnTemporary(scope))
                        yield return Finding(file, creation, "FileSystemWatcher outside a proven own temporary directory");
                }
            }
        }
    }

    private static bool Loopback(SyntaxNode scope) => scope.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(member =>
        member.ToString() is "IPAddress.Loopback" or "IPAddress.IPv6Loopback" or "System.Net.IPAddress.Loopback" or "System.Net.IPAddress.IPv6Loopback") ||
        scope.DescendantTokens().Any(token => (token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken)) &&
            Regex.IsMatch(token.ValueText, @"(?:localhost|127\.0\.0\.1|\[?::1\]?)", RegexOptions.IgnoreCase));

    private static bool NonLoopbackUri(SyntaxNode scope) => scope.DescendantTokens().Where(token => token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken))
        .Any(token => Regex.IsMatch(token.ValueText, @"https?://(?!localhost(?=[:/]|$)|127\.0\.0\.1(?=[:/]|$)|\[::1\])", RegexOptions.IgnoreCase));

    private static bool PrivateLoopbackFixtureClient(BaseObjectCreationExpressionSyntax creation, CompilationUnitSyntax tree)
    {
        var type = creation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (type == null || !type.Modifiers.Any(SyntaxKind.PrivateKeyword) || type.ParameterList == null || NonLoopbackUri(tree)) return false;
        var addresses = creation.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(assignment => assignment.Left.ToString() == "BaseAddress").ToArray();
        if (addresses.Length != 1 || addresses[0].Right is not ObjectCreationExpressionSyntax uri || uri.Type.ToString() != "Uri" ||
            uri.ArgumentList?.Arguments.Count != 1 || uri.ArgumentList.Arguments[0].Expression is not IdentifierNameSyntax parameter) return false;
        int index = type.ParameterList.Parameters.ToList().FindIndex(item => item.Identifier.ValueText == parameter.Identifier.ValueText);
        var sites = tree.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(site => site.Type.ToString() == type.Identifier.ValueText).ToArray();
        if (index < 0 || sites.Length == 0) return false;
        return sites.All(site =>
        {
            if (site.ArgumentList?.Arguments.ElementAtOrDefault(index)?.Expression is not MemberAccessExpressionSyntax address || address.Expression is not IdentifierNameSyntax receiver) return false;
            var variables = tree.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(variable => variable.Identifier.ValueText == receiver.Identifier.ValueText).ToArray();
            return variables.Length > 0 && variables.All(variable =>
            {
                var initializer = variable.Initializer?.Value;
                if (initializer is AwaitExpressionSyntax awaited) initializer = awaited.Expression;
                if (initializer is not InvocationExpressionSyntax factory || factory.Expression is not MemberAccessExpressionSyntax member) return false;
                var helpers = tree.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(cls => cls.Identifier.ValueText == member.Expression.ToString() && cls.Modifiers.Any(SyntaxKind.PrivateKeyword))
                    .SelectMany(cls => cls.Members.OfType<MethodDeclarationSyntax>()).Where(method => method.Identifier.ValueText == member.Name.Identifier.ValueText).ToArray();
                return helpers.Length > 0 && helpers.All(helper => Loopback(helper) && !NonLoopbackUri(helper));
            });
        });
    }

    private static bool BuildDirectory(string path) => path.Split('/').Any(part => part is "bin" or "obj" || part.StartsWith("bin-", StringComparison.Ordinal) || part.StartsWith("obj-", StringComparison.Ordinal));

    private sealed record CompileEvaluation(string[] Sources, string[] Imports, string ImportIdentity);

    internal static string[] BuildInputs(string root, CheckBuild unit, string dotnet, string api)
    {
        var reasons = new List<CheckAllInputFinding>();
        var files = new List<string>();
        foreach (var project in ProjectClosure(root, unit.Project, reasons))
        {
            var evaluated = Evaluate(root, project, dotnet, ["-p:Configuration=Release", .. unit.Properties, "-p:TiaPublicApiRoot=" + api]);
            files.AddRange(evaluated.Sources); files.AddRange(evaluated.Imports); files.Add(project);
        }
        if (reasons.Count != 0) throw new IOException(string.Join("; ", reasons.Select(reason => reason.Detail)));
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static readonly ConcurrentDictionary<string, Lazy<CompileEvaluation>> Evaluations = new(StringComparer.Ordinal);

    private static string[] EvaluationArguments(JsonObject registration, string? api)
    {
        var arguments = new List<string> { "-p:Configuration=Release", "-p:TiaSharedAdapterPaths=false" };
        if (registration["arguments"] is JsonArray registered)
            arguments.AddRange(DotnetSuites.BuildArguments(registered.Select(value => value!.GetValue<string>())));
        if (!string.IsNullOrEmpty(api)) arguments.Add("-p:TiaPublicApiRoot=" + Path.GetFullPath(api));
        return arguments.ToArray();
    }

    private static CompileEvaluation Evaluate(string root, string project, string dotnet, string[] arguments)
    {
        // A new/removed Compile candidate or a changed project/import invalidates
        // evaluation. Ordinary data/source byte edits do not rerun MSBuild; those
        // bytes are still checked independently by the input identity.
        var inventory = CheckAllLayout.ResultSourceInventory(root, CheckAllLayout.SourceInventory(root));
        var controls = inventory.Where(file => Path.GetExtension(file) is ".csproj" or ".props" or ".targets" || Path.GetFileName(file) == "global.json").ToArray();
        var key = CheckAllResultCache.Fingerprint(CheckAllResultCache.Enumerate(root, controls, new Dictionary<string, string>()),
            new { root, project, dotnet, arguments, candidates = inventory.Where(file => file.EndsWith(".cs", StringComparison.Ordinal)).ToArray(),
                environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().OrderBy(pair => (string)pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { name = (string)pair.Key, value = (string?)pair.Value }).ToArray() });
        string ImportKey(string[] imports) => CheckAllResultCache.Fingerprint(CheckAllResultCache.Enumerate(root, imports, new Dictionary<string, string>()), "MSBuild imports");
        if (Evaluations.TryGetValue(key, out var saved) && saved.Value.ImportIdentity != ImportKey(saved.Value.Imports)) Evaluations.TryRemove(key, out _);
        return Evaluations.GetOrAdd(key, _ => new Lazy<CompileEvaluation>(() =>
        {
            // Evaluation only: no targets, restore, build or test execution.
            var result = ProcessRunner.Run(dotnet, ["msbuild", Path.Combine(root, project), "-nologo", "-nodeReuse:false",
                "-getItem:Compile", "-getProperty:MSBuildAllProjects", .. arguments], root,
                new Dictionary<string, string?> { ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = null });
            ProcessRunner.RequireSuccess(result, "result cache Compile evaluation: " + project);
            int json = result.StandardOutput.IndexOf('{');
            if (json < 0) throw new ReleaseException("MSBuild returned no Compile inventory: " + project);
            using var document = JsonDocument.Parse(result.StandardOutput[json..]);
            var items = document.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray().ToArray();
            string[] Paths(IEnumerable<string> paths) => paths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Relative(root, Path.GetFullPath(Path.Combine(root, path)))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            var sources = Paths(items.Select(item => item.GetProperty("FullPath").GetString()!));
            var imports = Paths(document.RootElement.GetProperty("Properties").GetProperty("MSBuildAllProjects").GetString()!.Split(';')
                .Concat(items.Select(item => item.GetProperty("DefiningProjectFullPath").GetString()!)));
            return new(sources, imports, ImportKey(imports));
        })).Value;
    }

    private static IEnumerable<(XElement Item, string Owner)> Items(string project)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<(XElement, string)> Read(string path)
        {
            if (!File.Exists(path) || !seen.Add(path)) yield break;
            foreach (var item in XDocument.Load(path).Root!.Elements())
            {
                if (item.Name.LocalName == "Import" && item.Attribute("Project") is { } import)
                {
                    var value = import.Value.Replace("$(MSBuildThisFileDirectory)", Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                    if (value.Contains('$')) { yield return (item, path); continue; }
                    foreach (var imported in Read(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, value)))) yield return imported;
                }
                else if (item.Name.LocalName == "ItemGroup") foreach (var child in item.Elements()) yield return (child, path);
            }
        }
        return Read(project);
    }

    private static IEnumerable<string> ExpandItems(string? value, string owner, string directory, List<CheckAllInputFinding> reasons, string root)
    {
        if (value == null) yield break;
        value = value.Replace("$(MSBuildThisFileDirectory)", Path.GetDirectoryName(owner)! + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            .Replace("$(MSBuildProjectDirectory)", directory, StringComparison.Ordinal).Replace("$(ProjectDir)", directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (item.Contains('$')) { reasons.Add(new(Relative(root, owner), 1, "unresolved Compile/ProjectReference item: " + item)); continue; }
            var path = Path.GetFullPath(Path.Combine(directory, item));
            int wildcard = path.IndexOfAny(['*', '?']);
            if (wildcard < 0) { yield return path; continue; }
            var prefix = path[..wildcard]; var baseDirectory = prefix.EndsWith('\\') || prefix.EndsWith('/') ? prefix.TrimEnd('\\', '/') : Path.GetDirectoryName(prefix)!;
            if (!Directory.Exists(baseDirectory)) continue;
            var pattern = "^" + Regex.Escape(path.Replace('\\', '/')).Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal).Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$";
            foreach (var file in Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories))
                if (!BuildDirectory(Relative(baseDirectory, file)) && Regex.IsMatch(file.Replace('\\', '/'), pattern, RegexOptions.IgnoreCase)) yield return file;
        }
    }

    private static string[] ProjectClosure(string root, string project, List<CheckAllInputFinding> reasons)
    {
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string file)
        {
            if (!File.Exists(file) || !projects.Add(Relative(root, file))) return;
            foreach (var (item, owner) in Items(file).Where(pair => pair.Item.Name.LocalName == "ProjectReference"))
            {
                var include = item.Attribute("Include")?.Value;
                // A property inside a literal project path names a finite family
                // (e.g. Engine.V$(EngineRelease)); include every matching variant.
                // An entirely computed path cannot be bounded and remains live.
                if (include != null && include.IndexOf('$') > 0) include = Regex.Replace(include, @"\$\([^)]+\)", "*");
                foreach (var reference in ExpandItems(include, owner, Path.GetDirectoryName(file)!, reasons, root)) Visit(reference);
            }
        }
        Visit(Path.GetFullPath(Path.Combine(root, project))); return projects.Order(StringComparer.Ordinal).ToArray();
    }

    internal static string[] DataFiles(string root, CheckAllSuiteClosure closure)
    {
        if (closure.WholeInventory) return CheckAllLayout.ResultSourceInventory(root, CheckAllLayout.SourceInventory(root));
        var files = new List<string>();
        void Walk(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked suite data: " + directory);
            files.Add(Relative(root, directory));
            files.AddRange(Directory.EnumerateFiles(directory).Select(file => Relative(root, file)));
            foreach (var child in Directory.EnumerateDirectories(directory)) if (!BuildDirectory(Path.GetFileName(child))) Walk(child);
        }
        foreach (var path in closure.Roots)
        {
            var full = Path.Combine(root, path);
            if (!Directory.Exists(full)) { files.Add(path); continue; }
            Walk(full);
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
    }

    internal static bool Loads(CheckAllSuiteClosure closure, string root, string[] runtime)
    {
        if (closure.WholeInventory) return true;
        var project = Relative(root, Path.GetFullPath(runtime[0]));
        if (closure.Projects.Contains(project, StringComparer.OrdinalIgnoreCase)) return true;
        return closure.Roots.Any(prefix => Covers(prefix, project) || Covers(prefix, Relative(root, runtime[2])) || Covers(prefix, Relative(root, runtime[3]))) ||
            closure.References.Any(reference => Path.GetFileName(reference.Path).Equals(Path.GetFileName(runtime[4]), StringComparison.OrdinalIgnoreCase));
    }

    internal static void WriteInventory(string root, IEnumerable<CheckJob> jobs, string directory, string dotnet = "dotnet")
    {
        var layout = CheckAllLayout.SourceInventory(root);
        var rows = jobs.Where(job => job.ResultInputs?.Family == "suite").GroupBy(job => (string)JsonNode.Parse(job.ResultInputs!.Definition)!["suite"]!).Select(group =>
        {
            var spec = group.First().ResultInputs!; var definition = JsonNode.Parse(spec.Definition)!;
            var registration = definition["registration"]?.AsObject() ?? new JsonObject();
            var closure = spec.Closure ?? Derive(root, (string)definition["project"]!, registration, layout, dotnet, (string?)definition["api"]);
            int live = group.Count(job => job.Exclusive || job.Lane != "general" || job.ResultInputs!.AlwaysRun != null || closure.LiveReasons.Length != 0);
            return new { suite = group.Key, eligibleShards = group.Count() - live, liveShards = live, roots = closure.Roots, wholeInventory = closure.WholeInventory, fallbacks = closure.FallbackReasons, reasons = closure.LiveReasons };
        }).ToArray();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "result-cache-suites.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        var lines = new List<string> { "| Suite | Eligible shards | Live shards | Data closure roots | Live reason / broad fallback |", "|---|---:|---:|---|---|" };
        lines.AddRange(rows.Select(row => $"| {row.suite} | {row.eligibleShards} | {row.liveShards} | {string.Join("; ", row.roots.Select(path => "`" + path + "`"))} | {(row.reasons.Length == 0 ? row.liveShards == 0 ? "none" : "reserved time-sensitive shard(s)" : string.Join("; ", row.reasons.Select(reason => $"{reason.File}:{reason.Line}: {reason.Detail}")))}{(row.wholeInventory ? "; whole layout source/data inventory: " + string.Join("; ", row.fallbacks.Select(reason => $"{reason.File}:{reason.Line}: {reason.Detail}")) : "")} |"));
        File.WriteAllText(Path.Combine(directory, "result-cache-suites.md"), string.Join("\n", lines) + "\n");
    }
}
