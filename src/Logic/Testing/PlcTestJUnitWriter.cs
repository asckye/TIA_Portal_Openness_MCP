using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;

namespace TiaMcp.Logic.Testing
{
    public static class PlcTestJUnitWriter
    {
        public static string Write(PlcTestRunRecord run)
        {
            using var stream = new MemoryStream();
            using (var xml = XmlWriter.Create(stream, new XmlWriterSettings {
                Encoding = new UTF8Encoding(false), Indent = true, NewLineChars = "\n", NewLineHandling = NewLineHandling.Entitize
            }))
            {
                xml.WriteStartDocument();
                xml.WriteStartElement("testsuites");
                xml.WriteAttributeString("name", "TIA PLC tests");
                Counts(xml, run.Cases.ToArray(), run.FinishedUtc.HasValue
                    ? Math.Max(0, (run.FinishedUtc.Value - run.StartedUtc).TotalSeconds) : run.Cases.Sum(c => c.DurationMs) / 1000.0, run);
                foreach (string kind in new[] { "plcsimScenario", "testSuite" })
                {
                    var cases = run.Cases.Where(c => c.Kind == kind).ToArray();
                    if (cases.Length == 0) continue;
                    xml.WriteStartElement("testsuite");
                    xml.WriteAttributeString("name", kind);
                    Counts(xml, cases, cases.Sum(c => c.DurationMs) / 1000.0, run);
                    xml.WriteStartElement("properties");
                    Property(xml, "runId", run.RunId);
                    Property(xml, "releaseKey", run.ReleaseKey);
                    Property(xml, "hostVersion", run.HostVersion);
                    Property(xml, "planHash", run.PlanHash);
                    Property(xml, "status", run.Status);
                    xml.WriteEndElement();
                    foreach (var item in cases) Case(xml, item);
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
                xml.WriteEndDocument();
            }
            return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
        }

        private static void Counts(XmlWriter xml, PlcTestCaseResult[] cases, double seconds, PlcTestRunRecord run)
        {
            xml.WriteAttributeString("tests", cases.Length.ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("failures", cases.Count(c => c.Verdict == "failed").ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("errors", cases.Count(c => c.Verdict == "error" || c.Verdict == "unknown").ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("skipped", cases.Count(c => c.Verdict == "skipped").ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("time", Seconds(seconds));
            xml.WriteAttributeString("timestamp", run.StartedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        private static void Property(XmlWriter xml, string name, string value)
        {
            xml.WriteStartElement("property");
            xml.WriteAttributeString("name", name);
            xml.WriteAttributeString("value", value);
            xml.WriteEndElement();
        }

        private static void Case(XmlWriter xml, PlcTestCaseResult item)
        {
            xml.WriteStartElement("testcase");
            xml.WriteAttributeString("classname", "tia." + item.Kind);
            xml.WriteAttributeString("name", item.Id);
            xml.WriteAttributeString("time", Seconds(item.DurationMs / 1000.0));
            xml.WriteStartElement("properties");
            Property(xml, "title", item.Title);
            if (item.Requirement != null) Property(xml, "requirement", item.Requirement);
            xml.WriteEndElement();
            if (item.Verdict != "passed")
            {
                xml.WriteStartElement(item.Verdict == "failed" ? "failure" : item.Verdict == "skipped" ? "skipped" : "error");
                if (item.Verdict != "skipped")
                {
                    string type = item.Verdict == "unknown" ? "OUTCOME_UNKNOWN" : item.Verdict == "failed"
                        ? item.Kind == "plcsimScenario" ? "AssertionFailed" : "TestSuite." + (string?)Result(item.Evidence)?["state"]
                        : (string?)item.Evidence["error"]?["code"] ?? (string?)item.Evidence["errorCode"] ?? "TEST_ERROR";
                    xml.WriteAttributeString("type", type);
                }
                xml.WriteAttributeString("message", item.Message);
                if (item.Verdict != "skipped") xml.WriteString(FailureDetails(item));
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }

        public static string FailureDetails(PlcTestCaseResult item)
        {
            if (item.Kind == "testSuite")
            {
                var messages = Result(item.Evidence)?["messages"] as JsonArray;
                return messages == null ? item.Message : string.Join("\n", messages
                    .Where(m => (string?)m?["state"] == "Error" || (string?)m?["state"] == "Warning")
                    .Select(m => (string?)m?["path"] + ": " + (string?)m?["description"]));
            }
            var steps = item.Evidence["steps"] as JsonArray;
            if (steps == null) return item.Message;
            return string.Join("\n", steps.Where(s => (bool?)s?["ok"] == false).SelectMany(s =>
                s?["checks"] is JsonArray checks ? checks.Where(c => (bool?)c?["ok"] == false)
                    .Select(c => "step " + s["index"] + " assert " + c!["name"] + ": expected "
                        + Value(c["expected"]) + ", actual " + Value(c["actual"]))
                    : new[] { "step " + s?["index"] + " " + s?["kind"] + ": " + s?["error"] }));
        }

        private static JsonObject? Result(JsonObject evidence) => evidence["result"] as JsonObject ?? evidence["meta"]?["result"] as JsonObject;
        private static string Value(JsonNode? value) => value?.ToJsonString() ?? "null";
        private static string Seconds(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
    }
}
