using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TiaMcp.Logic.Generation.Ordering;

namespace TiaMcp.Logic.Generation
{
    public static class GenerationProjectPath
    {
        public static string CreationFile(string release, string directory, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." || name.Contains("/") || name.Contains("\\"))
                throw new ArgumentException("A single new project name is required.");
            Segment(name);
            string suffix = release == "14sp1" ? "14" : release == "15.1" ? "15_1" : release;
            if (suffix != "14" && suffix != "15_1" && suffix != "16" && suffix != "17" && suffix != "18" && suffix != "19" && suffix != "20" && suffix != "21")
                throw new ArgumentException("Unsupported precise release key.");
            return Normalize(Normalize(directory).TrimEnd('\\') + "\\" + name + "\\" + name + ".ap" + suffix);
        }
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An absolute project path is required.");
            path = path.Replace('/', '\\');
            string root, rest;
            if (path.Length >= 3 && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) && path[1] == ':' && path[2] == '\\')
            { root = path.Substring(0, 3); rest = path.Substring(3); }
            else if (path.StartsWith("\\\\", StringComparison.Ordinal))
            {
                var parts = path.Substring(2).Split('\\');
                if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0 || parts[0] == "." || parts[0] == ".." || parts[1] == "."
                        || parts[1] == "..")
                    throw new ArgumentException("A complete UNC server and share are required.");
                Segment(parts[0]); Segment(parts[1]); root = "\\\\" + parts[0] + "\\" + parts[1] + "\\";
                rest = string.Join("\\", parts, 2, parts.Length - 2);
            }
            else throw new ArgumentException("An absolute project path is required.");
            var segments = new List<string>();
            foreach (var segment in rest.Split('\\'))
            {
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..")
                {
                    if (segments.Count == 0) throw new ArgumentException("Project path escapes its Windows root.");
                    segments.RemoveAt(segments.Count - 1); continue;
                }
                Segment(segment); segments.Add(segment);
            }
            return root + string.Join("\\", segments) + (segments.Count > 0 && path.EndsWith("\\", StringComparison.Ordinal) ? "\\" : "");
        }
        private static void Segment(string segment)
        {
            if (segment.Length == 0 || segment.EndsWith(" ", StringComparison.Ordinal) || segment.EndsWith(".", StringComparison.Ordinal))
                throw new ArgumentException("Invalid Windows path segment.");
            foreach (char c in segment) if (c < 32 || "<>:\"|?*".IndexOf(c) >= 0) throw new ArgumentException("Invalid Windows path segment.");
            string stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                ((stem[3] >= '1' && stem[3] <= '9') || stem[3] == '鹿' || stem[3] == '虏' || stem[3] == '鲁')))
                throw new ArgumentException("Windows device names cannot identify a project.");
        }
    }
}
