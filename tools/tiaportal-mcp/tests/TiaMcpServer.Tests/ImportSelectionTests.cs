using System;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

internal static class ImportSelectionTests
{
    private sealed class Candidate
    {
        internal string Kind, Name, Path;
        internal Candidate(string kind, string name, string path) { Kind = kind; Name = name; Path = path; }
    }
    internal static void Run(Action<bool, string> Check)
    {
        var empty = Array.Empty<Candidate>();
        Check(ImportSelectionPolicy.FindConflicts(empty, x => x.Kind, x => x.Name, x => x.Path).Count == 0, "empty");
        var distinct = new[] { new Candidate("type", "Same", "a.xml"), new Candidate("block", "Same", "b.xml") };
        Check(ImportSelectionPolicy.FindConflicts(distinct, x => x.Kind, x => x.Name, x => x.Path).Count == 0, "separate kind namespaces");
        var duplicate = new[] { new Candidate("type", "Widget", "deep/b.xml"), new Candidate("TYPE", "widget", "a.xml") };
        var conflicts = ImportSelectionPolicy.FindConflicts(duplicate, x => x.Kind, x => x.Name, x => x.Path);
        Check(conflicts.Count == 1, "case insensitive kind and XML identity");
        Check(conflicts[0].Paths.SequenceEqual(new[] { "a.xml", "deep/b.xml" }), "both files retained in diagnostic; no shallowest selection");
        Check(conflicts[0].CandidateCount == 2 && !conflicts[0].PathsTruncated, "full count");
        // Keep LINQ reversal when the host suite enables the latest language version.
        var reversed = ImportSelectionPolicy.FindConflicts(Enumerable.Reverse(duplicate), x => x.Kind, x => x.Name, x => x.Path);
        Check(conflicts[0].Message == reversed[0].Message, "enumeration independent diagnostics");
        var delimiter = new[] { new Candidate("type:a", "b", "a.xml"), new Candidate("type", "a:b", "b.xml") };
        Check(ImportSelectionPolicy.FindConflicts(delimiter, x => x.Kind, x => x.Name, x => x.Path).Count == 0, "compound key has no delimiter collision");
        var sameFilename = new[] { new Candidate("block", "One", "a/file.xml"), new Candidate("block", "Two", "b/file.xml") };
        Check(ImportSelectionPolicy.FindConflicts(sameFilename, x => x.Kind, x => x.Name, x => x.Path).Count == 0, "identity is not filename");
        var many = Enumerable.Range(0, 50).Select(i => new Candidate("block", "Same", $"{i:D2}.xml"));
        var bounded = ImportSelectionPolicy.FindConflicts(many, x => x.Kind, x => x.Name, x => x.Path)[0];
        Check(bounded.CandidateCount == 50 && bounded.Paths.Count == 8 && bounded.PathsTruncated, "bounded diagnostics preserve full count");
        Check(ImportSelectionPolicy.OrderingDescription.Contains("not dependency resolution"), "honest ordering contract");
    }
}
