// Copyright (c) 2026 EIDO AUTOMATION, S.L.U.
// Adapted from EidoTiaWorkbench ImportDependencyPlanner.cs, MIT.
// Pinned source, license and modification record: third_party/eido-import-planner.
using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaOpenness.Shared
{
    public sealed class ImportOrderItem
    {
        public string Id { get; set; }
        public string Target { get; set; }
        public int Priority { get; set; }
        public string[] Dependencies { get; set; }
    }
    public sealed class ImportOrderIssue
    {
        public string Id { get; private set; }
        public string Dependency { get; private set; }
        public string Message { get; private set; }
        public ImportOrderIssue(ImportOrderItem item, string dependency, string message)
        { Id = item.Id; Dependency = dependency; Message = message; }
    }
    public sealed class ImportOrderPlan
    {
        public bool Valid { get; set; }
        public string[] Order { get; set; }
        public ImportOrderIssue[] Issues { get; set; }
    }
    public static class ImportDependencyPlanner
    {
        public static ImportOrderPlan Build(ImportOrderItem[] artifacts)
        {
            if (artifacts == null || artifacts.Length == 0 || artifacts.Length > 256)
                throw new ArgumentException("Provide 1..256 artifacts.");
            var byKey = new Dictionary<string, ImportOrderItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in artifacts)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 512)
                    throw new ArgumentException("Each artifact needs a nonempty Id of at most 512 characters.");
                if (byKey.ContainsKey(item.Id)) throw new ArgumentException("Duplicate artifact Id: " + item.Id);
                byKey.Add(item.Id, item);
            }
            var edges = artifacts.ToDictionary(a => a.Id, a => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            var issues = new List<ImportOrderIssue>();
            foreach (var item in artifacts)
            {
                var dependencies = item.Dependencies ?? new string[0];
                if (dependencies.Length > 256) throw new ArgumentException("At most 256 dependencies per artifact.");
                foreach (var key in dependencies)
                {
                    if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Dependency Id cannot be blank.");
                    if (byKey.ContainsKey(key)) edges[item.Id].Add(key);
                    else issues.Add(new ImportOrderIssue(item, key, "Dependency is not included in the plan."));
                }
            }
            var ordered = TopologicalSort(artifacts, byKey, edges, issues);
            return new ImportOrderPlan { Valid = issues.Count == 0, Order = issues.Count == 0 ? ordered.Select(a => a.Id).ToArray() : new string[0], Issues = issues.ToArray() };
        }
        private static List<ImportOrderItem> TopologicalSort(
            IReadOnlyCollection<ImportOrderItem> artifacts,
            IReadOnlyDictionary<string, ImportOrderItem> byKey,
            IDictionary<string, HashSet<string>> edges,
            ICollection<ImportOrderIssue> issues) {
            var ordered = new List<ImportOrderItem>();
            var permanent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var temporary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stable = artifacts
                .OrderBy(a => a.Priority)
                .ThenBy(a => a.Target, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var artifact in stable) {
                Visit(artifact, byKey, edges, permanent, temporary, ordered, issues);
            }

            return ordered;
        }

        /// <summary>
        /// Visits one graph node during topological sorting.
        /// </summary>
        /// <param name="artifact">Artifact to visit.</param>
        /// <param name="byKey">Artifacts by logical key.</param>
        /// <param name="edges">Dependency edges keyed by dependent artifact key.</param>
        /// <param name="permanent">Permanently visited node keys.</param>
        /// <param name="temporary">Temporary recursion stack keys.</param>
        /// <param name="ordered">Mutable ordered output.</param>
        /// <param name="issues">Mutable dependency issues collection.</param>
        private static void Visit(
            ImportOrderItem artifact,
            IReadOnlyDictionary<string, ImportOrderItem> byKey,
            IDictionary<string, HashSet<string>> edges,
            ISet<string> permanent,
            ISet<string> temporary,
            ICollection<ImportOrderItem> ordered,
            ICollection<ImportOrderIssue> issues) {
            var key = artifact.Id;
            if (permanent.Contains(key)) {
                return;
            }

            if (temporary.Contains(key)) {
                issues.Add(new ImportOrderIssue(artifact, key, "Cyclic import dependency detected."));
                return;
            }

            temporary.Add(key);
            foreach (var dependencyKey in edges[key]
                .OrderBy(k => byKey.ContainsKey(k) ? byKey[k].Priority : int.MaxValue)
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)) {
                ImportOrderItem dependency;
                if (byKey.TryGetValue(dependencyKey, out dependency)) {
                    Visit(dependency, byKey, edges, permanent, temporary, ordered, issues);
                }
            }

            temporary.Remove(key);
            permanent.Add(key);
            if (!ordered.Contains(artifact)) {
                ordered.Add(artifact);
            }
        }

    }
}
