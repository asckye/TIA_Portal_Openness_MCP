using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TiaMcp.Adapters.Contracts
{
    // Explicit precondition codes for documented Openness refusals (API audit 2026-10-19, F4/F2).
    // Each code is used only on the releases whose SDK has the exception type; the adapter maps the type, these rules word it.
    public static class OpennessRefusals
    {
        public const string AccessDenied = "openness-access-denied";          // EngineeringSecurityException, all eight
        public const string MissingProducts = "missing-products";             // MissingProductsException, V18-V21
        public const string LicenseNotFound = "license-not-found";            // LicenseNotFoundException, V17-V21
        public const string MultiuserRefused = "multiuser-refused";           // Multiuser.MultiuserException, V17-V21
        public const string UmacCredentialsRequired = "umac-credentials-required";
        public const string ProjectNotVisible = "project-not-visible";       // process names a project, the composition is empty

        public const string FirewallGuidance = "Allow this application in the TIA Portal Openness access dialog (Yes, or Yes to all), "
            + "or have an administrator add the worker executable to HKLM\\SOFTWARE\\Siemens\\Automation\\Openness\\AllowList "
            + "(the Workbench environment check 'Openness AllowList' shows the expected entries); the Windows user must also be a member of 'Siemens TIA " +
                "Openness'.";

        public static string Message(string code, string operation, bool credentialsGiven, string? nativeMessage)
        {
            string text;
            switch (code)
            {
                case AccessDenied:
                    text = operation == "open" || operation == "retrieve"
                        ? (credentialsGiven
                            ? "TIA Portal refused access to the protected project with the given UMAC credentials (EngineeringSecurityException). Check " +
                                "umacUserName, umacPassword and umacUserType (Project or Global). "
                            : "TIA Portal refused access (EngineeringSecurityException). If the project is protected by UMAC user management, pass " +
                                "umacUserName and umacPassword (and umacUserType Project or Global); the password is converted to SecureString and never " +
                                "logged or echoed. ")
                            + "Otherwise the Openness firewall denied this application: " + FirewallGuidance
                        : "TIA Portal Openness denied access to this application (EngineeringSecurityException). " + FirewallGuidance;
                    break;
                case MissingProducts:
                    text =
                        "TIA Portal lacks a mandatory product, optional product or support package that this project needs (MissingProductsException). " +
                        "Install the products the project lists in TIA Portal, then retry.";
                    break;
                case LicenseNotFound:
                    text =
                        "TIA Portal found no licence for this operation (LicenseNotFoundException). Install or activate the licence it names (Automation " +
                        "License Manager), then retry.";
                    break;
                case MultiuserRefused:
                    text =
                        "The multiuser server refused the request (MultiuserException). Check the server alias, the server project, its locks and the local " +
                        "session, then retry.";
                    break;
                case UmacCredentialsRequired:
                    text =
                        "The project is protected by UMAC user management and TIA Portal asked for credentials. Pass umacUserName and umacPassword " +
                        "(umacUserType Project or Global); the password is converted to SecureString and never logged or echoed.";
                    break;
                case ProjectNotVisible:
                    text =
                        "The selected TIA Portal process reports this project path, but its Projects composition is empty. TIA Portal hides a project from " +
                        "Openness when products the project needs are missing; check the products installed in TIA Portal.";
                    break;
                default: throw new ArgumentException("Unknown Openness refusal code: " + code, nameof(code));
            }
            string suffix = code == ProjectNotVisible ? ""
                : operation == "retrieve"
                    ? " TIA Portal reported the refusal: no project was opened and this session stays usable; the new destination directory may hold files " +
                    "written before the refusal."
                : " TIA Portal reported the refusal, so nothing was changed and this session stays usable.";
            return text + suffix + (string.IsNullOrWhiteSpace(nativeMessage) ? "" : " TIA: " + Bound(nativeMessage!));
        }

        private static string Bound(string text)
        {
            string flat = string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
            return flat.Length <= 600 ? flat : flat.Substring(0, 600) + " ...";
        }

        public static AdapterPreconditionException Exception(string code, string operation, bool credentialsGiven, Exception native)
            => new AdapterPreconditionException(Message(code, operation, credentialsGiven, native.Message), code, native);
    }

    // TiaPortal.Confirmation / Notification / Disposed handling (F17). The download-prompt rule applies: a confirmation that
    // no approved plan answers is refused and recorded. Cancel keeps the most state; No can discard (for example "save first?").
    public static class PortalDialogPolicy
    {
        public static readonly string[] RefusalOrder = { "Cancel", "Abort", "No", "NoToAll" };
        public const int Capacity = 32;

        public static string? RefusingChoice(IEnumerable<string> offered)
        {
            var set = new HashSet<string>(offered, StringComparer.Ordinal);
            return RefusalOrder.FirstOrDefault(set.Contains);
        }

        public static string? Answer(IEnumerable<string> offered)
        {
            var choices = offered.ToArray();
            return RefusingChoice(choices) ?? (choices.Length == 1 && choices[0] == "Ok" ? "Ok" : null);
        }

        // Each diagnostic field has a wire-byte budget, including the ASCII JSON escaping used here.
        public static string Report(Dictionary<string, object?> record)
        {
            string Field(string name, int budget) => Quote(record.TryGetValue(name, out var value) ? value as string : null, budget);
            var choices = record.TryGetValue("choices", out var offered) ? offered as string[] : null;
            return "TIA_MCP_DIALOG {\"event\":" + Field("event", 32) + ",\"utc\":" + Field("utc", 64)
                + ",\"caption\":" + Field("caption", 192) + ",\"text\":" + Field("text", 512)
                + ",\"detailText\":" + Field("detailText", 512) + ",\"choices\":["
                + string.Join(",", (choices ?? Array.Empty<string>()).Take(9).Select(choice => Quote(choice, 32)))
                + "],\"answer\":" + Field("answer", 32) + ",\"rule\":" + Field("rule", 96) + "}";
        }

        private static string Quote(string? value, int budget)
        {
            var output = new StringBuilder("\"");
            value ??= "";
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                string escaped = character == '"' || character == '\\' ? "\\" + character
                    : character < ' ' || character > '~' ? "\\u" + ((int)character).ToString("x4") : character.ToString();
                if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                    escaped += "\\u" + ((int)value[++index]).ToString("x4");
                if (output.Length + escaped.Length + 1 > budget) break;
                output.Append(escaped);
            }
            return output.Append('"').ToString();
        }

        public static void Record(List<Dictionary<string, object?>> records, Dictionary<string, object?> record)
        {
            lock (records)
            {
                records.Add(record);
                if (records.Count > Capacity) records.RemoveRange(0, records.Count - Capacity);
            }
        }

        public static Dictionary<string, object?>[] Snapshot(List<Dictionary<string, object?>> records)
        { lock (records) return records.Select(r => new Dictionary<string, object?>(r)).ToArray(); }
    }
}
