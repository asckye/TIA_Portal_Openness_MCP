using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class McpPromptRegistration
    {
        // Reflect only the explicit prompt container; optional engineering types are never scanned.
        private static readonly Lazy<IReadOnlyDictionary<MethodInfo, ISet<string>>> s_tokens = new(() =>
            typeof(McpPrompts).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.GetCustomAttribute<McpServerPromptAttribute>() != null)
                .ToDictionary(method => method, method => (ISet<string>)Regex.Matches(
                    (string)method.Invoke(null, method.GetParameters().Select(parameter => parameter.ParameterType == typeof(string)
                        ? (object)"" : Activator.CreateInstance(parameter.ParameterType)!).ToArray())!, @"\b[A-Z][A-Za-z0-9]+\b")
                    .Select(match => match.Value).ToHashSet(StringComparer.Ordinal)));

        internal static IReadOnlyDictionary<string, string[]> Requirements(IEnumerable<string> toolUniverse)
        {
            var known = toolUniverse.ToHashSet(StringComparer.Ordinal);
            return s_tokens.Value.ToDictionary(pair => pair.Key.GetCustomAttribute<McpServerPromptAttribute>()!.Name ?? pair.Key.Name,
                pair => pair.Value.Where(known.Contains).OrderBy(name => name, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        }

        internal static void Configure(IMcpServerBuilder builder, IEnumerable<string>? roster = null, IEnumerable<string>? toolUniverse = null)
        {
            if (roster == null)
            {
                builder.WithPrompts(new[] { typeof(McpPrompts) });
                return;
            }
            var available = roster.ToHashSet(StringComparer.Ordinal);
            var requirements = Requirements(toolUniverse ?? available);
            builder.WithPrompts(s_tokens.Value.Keys.Where(method => requirements[method.GetCustomAttribute<McpServerPromptAttribute>()!.Name ?? method.Name]
                .All(available.Contains)).Select(method => McpServerPrompt.Create(method, (object?)null)));
        }
    }
}
