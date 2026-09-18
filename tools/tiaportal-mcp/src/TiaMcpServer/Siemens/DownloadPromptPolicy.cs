using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// 下载/上载提示的应答策略。Openness 在下载过程中会通过 DownloadConfigurationDelegate 逐个交出
    /// 提示对象（Siemens.Engineering.Download.Configurations.*，V21 共 43 种具体类型），每个提示按基类分三种形态：
    /// 选择型（CurrentSelection 枚举）、复选型（Checked）和密码型（SetPassword）。没有被应答的提示由 TIA 自行决定，
    /// 有的会让整次下载中止（StopModules 曾经如此）。本类只做纯决策，不接触任何 Siemens 对象，便于离线测试；
    /// 反射读取提示形态并真正赋值的代码在 Portal.Download.cs。
    /// 决策顺序：调用方 promptAnswersJson 的显式指定 > 已知提示的内置默认 > 未应答（记录类型名与提示文本，绝不记录密码）。
    /// </summary>
    internal sealed class DownloadPromptPolicy
    {
        internal enum AnswerKind { Selection, Checked, Password, Unanswered }

        internal sealed class Answer
        {
            public AnswerKind Kind;
            public string? Value;            // 枚举名或 "true"/"false"；密码永远不会出现在这里
            public string Source = "";       // explicit | builtin | conservative | unanswered
            public string? Note;
        }

        public bool KeepActualValues = true;
        public bool StartAfterDownload = true;
        public bool StopBeforeDownload = true;
        public bool ConsistentBlocksOnly = true;
        public string UserManagementMode = "keep";
        public string? ModuleAccessPassword;
        public string? BlockBindingPassword;
        public string? MasterSecretPassword;
        public readonly Dictionary<string, string> Explicit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public readonly List<JsonObject> Answered = new List<JsonObject>();
        public readonly List<JsonObject> Unanswered = new List<JsonObject>();

        internal static readonly string[] UserManagementModes = { "keep", "updateKeepPassword", "resetToProject" };

        /// <summary>解析 promptAnswersJson：{"ResetModule":"DeleteAll","OverwriteHmiData":true}。值为枚举名或布尔。</summary>
        internal static Dictionary<string, string> ParseExplicitAnswers(string? json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json) || json!.Trim() == "{}") return result;
            var node = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("promptAnswersJson must be a JSON object of promptType -> value.");
            foreach (var kv in node)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Key.Any(c => !char.IsLetterOrDigit(c))) throw new ArgumentException("Prompt type names are plain identifiers: " + kv.Key);
                if (kv.Value is not JsonValue v) throw new ArgumentException("Prompt answer must be a string or boolean: " + kv.Key);
                string text = v.TryGetValue<bool>(out var b) ? (b ? "true" : "false") : v.TryGetValue<string>(out var s) ? s : throw new ArgumentException("Prompt answer must be a string or boolean: " + kv.Key);
                if (string.IsNullOrWhiteSpace(text) || text.Length > 64) throw new ArgumentException("Prompt answer out of range: " + kv.Key);
                result[kv.Key] = text;
            }
            return result;
        }

        internal static string UserManagementSelection(string mode) => mode switch
        {
            "keep" => "KeepOnlineUserManagementData",
            "updateKeepPassword" => "UpdateUserManagementDataButKeepOnlinePassword",
            "resetToProject" => "DownloadAllUserManagementDataResetToProject",
            _ => throw new ArgumentException("userManagementMode must be keep / updateKeepPassword / resetToProject.")
        };

        /// <summary>
        /// 为一个提示做决策。selectionValues 是该提示 CurrentSelection 枚举的全部合法名（无则为空数组）。
        /// 返回的 Answer 只描述“该怎么答”，赋值由调用方完成；调用方应在赋值失败时改记为 Unanswered。
        /// </summary>
        internal Answer Decide(string typeName, IReadOnlyList<string> selectionValues, bool hasChecked, bool hasPassword)
        {
            bool hasSelection = selectionValues.Count > 0;
            if (Explicit.TryGetValue(typeName, out var explicitValue))
            {
                if (hasSelection)
                {
                    var match = selectionValues.FirstOrDefault(v => string.Equals(v, explicitValue, StringComparison.OrdinalIgnoreCase));
                    if (match == null) throw new ArgumentException($"promptAnswersJson[{typeName}] must be one of: {string.Join("/", selectionValues)}");
                    return new Answer { Kind = AnswerKind.Selection, Value = match, Source = "explicit" };
                }
                if (hasChecked)
                {
                    if (!bool.TryParse(explicitValue, out var flag)) throw new ArgumentException($"promptAnswersJson[{typeName}] must be true or false.");
                    return new Answer { Kind = AnswerKind.Checked, Value = flag ? "true" : "false", Source = "explicit" };
                }
                if (hasPassword) throw new ArgumentException($"promptAnswersJson[{typeName}] refused: passwords are only accepted through dedicated parameters.");
                throw new ArgumentException($"promptAnswersJson[{typeName}]: prompt has no settable selection or checkbox.");
            }

            if (hasPassword)
            {
                string? secret = typeName switch
                {
                    "ModuleReadAccessPassword" or "ModuleWriteAccessPassword" or "PasswordReadAccess" => ModuleAccessPassword,
                    "BlockBindingPassword" => BlockBindingPassword,
                    "PlcMasterSecretPassword" => MasterSecretPassword,
                    _ => null
                };
                return string.IsNullOrEmpty(secret)
                    ? new Answer { Kind = AnswerKind.Unanswered, Source = "unanswered", Note = "password prompt without a matching password parameter" }
                    : new Answer { Kind = AnswerKind.Password, Source = "builtin" };
            }

            string Sel(string value, string source = "builtin", string? note = null)
            {
                if (!selectionValues.Any(v => v == value)) return "";
                return value + "|" + source + "|" + (note ?? "");
            }
            string picked = typeName switch
            {
                "StopModules" => Sel(StopBeforeDownload ? "StopAll" : "NoAction"),
                "StopHSystemOrModule" => Sel(StopBeforeDownload ? "StopModule" : "NoAction"),
                "StopHSystem" => Sel(StopBeforeDownload ? "StopHSystem" : "NoAction"),
                "StartModules" or "StartBackupModules" => Sel(StartAfterDownload ? "StartModule" : "NoAction"),
                "DataBlockReinitialization" => Sel(KeepActualValues ? "KeepActualValues" : "Reinitialize"),
                "DataBlockReinitializationOrKeepActualValues" => Sel(KeepActualValues ? "KeepActualValues" : "StopPlcAndReinitialize"),
                "ConsistentBlocksDownload" => Sel("ConsistentDownload"),
                "AllBlocksDownload" => ConsistentBlocksOnly ? "" : Sel("DownloadAllBlocks"),
                "UserManagementDownload" => Sel(UserManagementSelection(UserManagementMode)),
                "AlarmTextLibrariesDownload" => Sel("ConsistentDownload"),
                // Siemens.Engineering.Safety.Download.Configurations.SafetyProgram: the only documented selection.
                "SafetyProgram" => Sel("ConsistentDownload"),
                "DifferentTargetConfiguration" or "ActiveTestCanBeAborted" or "ActiveTestCanPreventDownload" => Sel("AcceptAll"),
                "ExpandDownload" => Sel("Download"),
                "LoadIdentificationData" => Sel("LoadData"),
                "WaitOnReboot" => Sel("Wait"),
                "TargetForSoftware" => Sel("CPU"),
                "UploadMissingProducts" => Sel("TryUpload"),
                // 有破坏性的提示默认选“不动”，并标明可通过 promptAnswersJson 覆盖
                "InitializeMemory" or "OverwriteOnMemoryCard" or "OverwriteSystemData" or "ResetModule" or "SwitchBackupToPrimary"
                    => Sel("NoAction", "conservative", "destructive prompt answered NoAction; override via promptAnswersJson"),
                "ProtectionLevelChanged" => Sel("NoChange", "conservative", "protection level kept; override via promptAnswersJson"),
                _ => ""
            };
            if (picked.Length > 0)
            {
                var parts = picked.Split('|');
                return new Answer { Kind = AnswerKind.Selection, Value = parts[0], Source = parts[1], Note = parts[2].Length == 0 ? null : parts[2] };
            }

            if (hasChecked)
            {
                bool? flag = typeName switch
                {
                    "CheckBeforeDownload" or "FitHmiComponents" or "OverwriteTargetLanguages" or "DownloadWebApplication" or "UpdateWebApplication" => true,
                    "DeleteWebApplication" => false,
                    _ => null
                };
                if (flag != null) return new Answer { Kind = AnswerKind.Checked, Value = flag.Value ? "true" : "false", Source = "builtin" };
            }

            return new Answer { Kind = AnswerKind.Unanswered, Source = "unanswered", Note = hasSelection
                ? "no default for this prompt; allowed values: " + string.Join("/", selectionValues)
                : hasChecked ? "no default for this checkbox prompt; pass true/false via promptAnswersJson" : "prompt exposes nothing settable" };
        }

        internal void Record(string typeName, string? message, Answer answer)
        {
            var row = new JsonObject { ["prompt"] = typeName, ["source"] = answer.Source };
            if (!string.IsNullOrEmpty(message)) row["message"] = message;
            if (answer.Kind == AnswerKind.Unanswered) { if (answer.Note != null) row["note"] = answer.Note; Unanswered.Add(row); return; }
            row["kind"] = answer.Kind.ToString();
            if (answer.Kind != AnswerKind.Password) row["value"] = answer.Value;
            if (answer.Note != null) row["note"] = answer.Note;
            Answered.Add(row);
        }

        internal JsonObject Summary() => new JsonObject
        {
            ["promptsAnswered"] = new JsonArray(Answered.Select(x => (JsonNode)x.DeepClone()).ToArray()),
            ["promptsUnanswered"] = new JsonArray(Unanswered.Select(x => (JsonNode)x.DeepClone()).ToArray())
        };

        internal string UnansweredSummary() => Unanswered.Count == 0 ? "" :
            " Unanswered download prompts: " + string.Join(", ", Unanswered.Select(x => x["prompt"]!.GetValue<string>())) + " (pass answers via promptAnswersJson or the password parameters).";
    }
}
