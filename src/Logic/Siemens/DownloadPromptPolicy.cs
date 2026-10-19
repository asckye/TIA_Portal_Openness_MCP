using System;
using System.Collections.Generic;
using System.Linq;
#if TIA_NATIVE_ONLINE
using JsonObject = TiaMcp.Adapters.Native.Hmi.HmiObject;
using JsonArray = TiaMcp.Adapters.Native.Hmi.HmiArray;
using TiaMcp.Adapters.Native.Hmi;
#else
using System.Text.Json.Nodes;
#endif

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// 下载/上载提示的应答策略。Openness 在下载过程中会通过 DownloadConfigurationDelegate 逐个交出
    /// 提示对象（Siemens.Engineering.Download.Configurations.*，V21 共 43 种具体类型），每个提示按基类分三种形态：
    /// 选择型（CurrentSelection 枚举）、复选型（Checked）和密码型（SetPassword）。没有被应答的提示由 TIA 自行决定，
    /// 已观察到未应答的 StopModules 提示导致整次下载中止。本类只做纯决策，不接触任何 Siemens 对象，便于离线测试；
    /// 反射读取提示形态并真正赋值的代码在 Siemens/Services/OnlineDownloadService.cs。
    /// 决策顺序：调用方 promptAnswersJson 的显式指定 > 已知提示的内置默认 > 未应答（记录类型名与提示文本，绝不记录密码）。
    /// F 程序下载、接受硬件差异、取消测试功能、扩展下载和写入 IO 标识数据只由显式请求参数允许（2026-10-19 起）；
    /// 默认拒绝，下载中止，结果说明是哪个提示以及如何允许。
    /// </summary>
    internal sealed class DownloadPromptPolicy
    {
        internal enum AnswerKind { Selection, Checked, Password, Unanswered, Refused }

        internal sealed class Answer
        {
            public AnswerKind Kind;
            public string? Value;            // 枚举名或 "true"/"false"；密码永远不会出现在这里
            public string Source = "";       // explicit | builtin | conservative | allowed | refused | unanswered
            public string? Note;
            public string? AllowWith;        // Refused: the request parameter that allows the prompt
        }

        // Thrown from the download delegate after a refused prompt is recorded, so the download stops at that prompt.
        internal sealed class PromptRefusedException : InvalidOperationException
        {
            internal PromptRefusedException(string message) : base(message) { }
        }

        public bool KeepActualValues = true;
        public bool StartAfterDownload = true;
        public bool StopBeforeDownload = true;
        public bool ConsistentBlocksOnly = true;
        public string UserManagementMode = "keep";
        public string? ModuleAccessPassword;
        public string? BlockBindingPassword;
        public string? MasterSecretPassword;
        public bool AllowSafetyProgramDownload;
        public bool AllowHardwareDifferences;
        public bool AllowActiveTestCancel;
        public bool AllowExpandedDownload;
        public bool AllowIdentificationDataLoad;
        public bool AllowNonTlsLogin;
        public readonly Dictionary<string, string> Explicit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public readonly List<JsonObject> Answered = new List<JsonObject>();
        public readonly List<JsonObject> Unanswered = new List<JsonObject>();
        public readonly List<JsonObject> Refused = new List<JsonObject>();

        internal static readonly string[] UserManagementModes = { "keep", "updateKeepPassword", "resetToProject" };

        // Prompts that widen a download beyond what the approver saw (SDK summaries: the F-program, "Differences between configured and
        // target modules", "Active test and commissioning functions could be canceled / could prevent loading", "The download must be
        // expanded beyond your selection", "Load identification data to the PROFINET IO devices"). Only the named parameter answers them.
        internal static readonly string[] OptInPrompts = { "SafetyProgram", "DifferentTargetConfiguration", "ActiveTestCanBeAborted", "ActiveTestCanPreventDownload", "ExpandDownload", "LoadIdentificationData" };

        internal static string? OptInParameter(string typeName) => typeName.ToLowerInvariant() switch
        {
            "safetyprogram" => "allowSafetyProgramDownload",
            "differenttargetconfiguration" => "allowHardwareDifferences",
            "activetestcanbeaborted" or "activetestcanpreventdownload" => "allowActiveTestCancel",
            "expanddownload" => "allowExpandedDownload",
            "loadidentificationdata" => "allowIdentificationDataLoad",
            _ => null
        };

        private bool OptInAllowed(string typeName) => typeName switch
        {
            "SafetyProgram" => AllowSafetyProgramDownload,
            "DifferentTargetConfiguration" => AllowHardwareDifferences,
            "ActiveTestCanBeAborted" or "ActiveTestCanPreventDownload" => AllowActiveTestCancel,
            "ExpandDownload" => AllowExpandedDownload,
            "LoadIdentificationData" => AllowIdentificationDataLoad,
            _ => false
        };

        // The selection an allowed prompt gets, and the documented "no" selection that is set before the download is stopped.
        // SafetyProgramSelections has ConsistentDownload as its only value, so a refused F-program prompt is left untouched.
        private static string OptInAllowSelection(string typeName) => typeName switch
        {
            "SafetyProgram" => "ConsistentDownload",
            "ExpandDownload" => "Download",
            "LoadIdentificationData" => "LoadData",
            _ => "AcceptAll"
        };

        private static string? OptInRefuseSelection(string typeName) => typeName switch
        {
            "SafetyProgram" => null,
            "LoadIdentificationData" => "LoadNothing",
            _ => "NoAction"
        };

        private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        /// <summary>
        /// One policy per online request, for the adapter and for the approval card alike. DownloadPlc uses every option; the folder
        /// download answers TargetForSoftware and OverwriteOnMemoryCard from its own parameters and never stops or starts a CPU; the
        /// station and parameter uploads keep their defaults and only use the CPU password.
        /// </summary>
        internal static DownloadPromptPolicy ForRequest(TiaMcp.Adapters.Contracts.OnlineRequest request, IReadOnlyDictionary<string, string> explicitAnswers)
        {
            bool download = request.Operation == "DownloadPlc", folder = request.Operation == "DownloadPlcToFolder";
            string mode = download || folder ? request.UserManagementMode : "keep";
            if (!UserManagementModes.Contains(mode)) throw new ArgumentException("userManagementMode must be keep / updateKeepPassword / resetToProject.");
            var policy = new DownloadPromptPolicy
            {
                ConsistentBlocksOnly = !download || request.ConsistentBlocksOnly,
                KeepActualValues = !(download || folder) || request.KeepActualValues,
                StartAfterDownload = download && request.StartAfterDownload,
                StopBeforeDownload = download && request.StopBeforeDownload,
                UserManagementMode = mode,
                ModuleAccessPassword = download ? NonEmpty(request.ModuleAccessPassword) ?? NonEmpty(request.Password) : folder ? null : NonEmpty(request.Password),
                BlockBindingPassword = download ? NonEmpty(request.BlockBindingPassword) : null,
                MasterSecretPassword = download ? NonEmpty(request.MasterSecretPassword) : null,
                AllowSafetyProgramDownload = (download || folder) && request.AllowSafetyProgramDownload,
                AllowHardwareDifferences = download && request.AllowHardwareDifferences,
                AllowActiveTestCancel = download && request.AllowActiveTestCancel,
                AllowExpandedDownload = (download || folder) && request.AllowExpandedDownload,
                AllowIdentificationDataLoad = download && request.AllowIdentificationDataLoad,
                AllowNonTlsLogin = request.AllowNonTlsLogin
            };
            foreach (var kv in explicitAnswers)
            {
                if (OptInParameter(kv.Key) is string parameter)
                    throw new ArgumentException($"promptAnswers[{kv.Key}] refused: this prompt is answered only by the request parameter {parameter}=true.");
                policy.Explicit[kv.Key] = kv.Value;
            }
            if (folder)
            {
                policy.Explicit["TargetForSoftware"] = request.TargetForSoftware;
                policy.Explicit["OverwriteOnMemoryCard"] = request.OverwriteOnMemoryCard ? "Load" : "NoAction";
            }
            return policy;
        }

        /// <summary>
        /// Whether a password may go to a password prompt. The official pattern checks IsSecureCommunication first (true only on a
        /// TLS-secured connection); a release without the property cannot be checked. Returns null to send, else the refusal note.
        /// </summary>
        internal static string? NonTlsLoginRefusal(bool? isSecureCommunication, bool allowNonTlsLogin)
            => isSecureCommunication == false && !allowNonTlsLogin
                ? "password not sent: the connection is not TLS-secured (IsSecureCommunication=false); pass allowNonTlsLogin=true to send it over the legacy channel"
                : null;

        /// <summary>解析 promptAnswersJson：{"ResetModule":"DeleteAll","OverwriteHmiData":true}。值为枚举名或布尔。</summary>
#if !TIA_NATIVE_ONLINE
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
#endif

        internal static string UserManagementSelection(string mode) => mode switch
        {
            "keep" => "KeepOnlineUserManagementData",
            "updateKeepPassword" => "UpdateUserManagementDataButKeepOnlinePassword",
            "resetToProject" => "DownloadAllUserManagementDataResetToProject",
            _ => throw new ArgumentException("userManagementMode must be keep / updateKeepPassword / resetToProject.")
        };

        // Built-in selection answers: (value, source, note), or null when the prompt has no built-in answer.
        private (string Value, string Source, string? Note)? Builtin(string typeName)
        {
            const string conservative = "conservative";
            switch (typeName)
            {
                case "StopModules": return (StopBeforeDownload ? "StopAll" : "NoAction", "builtin", null);
                case "StopHSystemOrModule": return (StopBeforeDownload ? "StopModule" : "NoAction", "builtin", null);
                case "StopHSystem": return (StopBeforeDownload ? "StopHSystem" : "NoAction", "builtin", null);
                case "StartModules":
                case "StartBackupModules": return (StartAfterDownload ? "StartModule" : "NoAction", "builtin", null);
                // V17-V21 DataBlockReinitializationSelections has only NoAction and StopPlcAndReinitialize. NoAction keeps the actual values,
                // so TIA aborts a download that needs reinitialization. Reinitializing needs STOP (the selection's own documentation), on
                // both reinitialization prompts: without stopBeforeDownload the answer is NoAction and TIA aborts.
                case "DataBlockReinitialization":
                    if (KeepActualValues) return ("NoAction", "builtin", "keepActualValues=true answers NoAction, so TIA aborts a download that needs reinitialization; pass keepActualValues=false with stopBeforeDownload=true to reinitialize");
                    return Reinitialization();
                case "DataBlockReinitializationOrKeepActualValues":
                    return KeepActualValues ? ("KeepActualValues", "builtin", null) : Reinitialization();
                case "ConsistentBlocksDownload": return ("ConsistentDownload", "builtin", null);
                case "AllBlocksDownload": return ConsistentBlocksOnly ? null : ("DownloadAllBlocks", "builtin", "consistentBlocksOnly=false answers DownloadAllBlocks (its only selection)");
                case "UserManagementDownload": return (UserManagementSelection(UserManagementMode), "builtin", null);
                case "AlarmTextLibrariesDownload": return ("ConsistentDownload", "builtin", null);
                case "WaitOnReboot": return ("Wait", "builtin", null);
                case "TargetForSoftware": return ("CPU", "builtin", null);
                case "UploadMissingProducts": return ("TryUpload", "builtin", null);
                // 有破坏性的提示默认选“不动”，并标明可通过 promptAnswersJson 覆盖
                case "InitializeMemory":
                case "OverwriteOnMemoryCard":
                case "OverwriteSystemData":
                case "ResetModule":
                case "SwitchBackupToPrimary": return ("NoAction", conservative, "destructive prompt answered NoAction; override via promptAnswersJson");
                case "ProtectionLevelChanged": return ("NoChange", conservative, "protection level kept; override via promptAnswersJson");
                default: return null;
            }
        }

        private (string Value, string Source, string? Note) Reinitialization() => StopBeforeDownload
            ? ("StopPlcAndReinitialize", "builtin", "keepActualValues=false: the PLC is stopped and all data values are reset to their start values")
            : ("NoAction", "conservative", "reinitialization needs STOP, so stopBeforeDownload=false answers NoAction and TIA aborts; pass stopBeforeDownload=true to reinitialize");

        // A reinitialization prompt answered NoAction stops the download in TIA; the result names it and the parameter that allows it.
        internal string ReinitializationSummary() => string.Concat(Answered
            .Where(x => x.ContainsKey("note") && x.ContainsKey("value") && x["prompt"]!.GetValue<string>().StartsWith("DataBlockReinitialization", StringComparison.Ordinal)
                && x["value"]!.GetValue<string>() == "NoAction")
            .Select(x => " " + x["prompt"]!.GetValue<string>() + " answered NoAction: " + x["note"]!.GetValue<string>() + "."));

        private static bool? BuiltinCheck(string typeName) => typeName switch
        {
            "CheckBeforeDownload" or "FitHmiComponents" or "OverwriteTargetLanguages" or "DownloadWebApplication" or "UpdateWebApplication" => true,
            "DeleteWebApplication" => false,
            _ => null
        };

        private static string? PasswordFrom(string typeName) => typeName switch
        {
            "ModuleReadAccessPassword" or "ModuleWriteAccessPassword" or "PasswordReadAccess" => "moduleAccessPassword / password",
            "BlockBindingPassword" => "blockBindingPassword",
            "PlcMasterSecretPassword" => "masterSecretPassword",
            _ => null
        };

        private string? Secret(string typeName) => typeName switch
        {
            "ModuleReadAccessPassword" or "ModuleWriteAccessPassword" or "PasswordReadAccess" => ModuleAccessPassword,
            "BlockBindingPassword" => BlockBindingPassword,
            "PlcMasterSecretPassword" => MasterSecretPassword,
            _ => null
        };

        private string UnansweredNote(string typeName, IReadOnlyList<string>? selectionValues, bool hasChecked)
        {
            if (typeName == "AllBlocksDownload" && ConsistentBlocksOnly)
                return "consistentBlocksOnly=true leaves AllBlocksDownload unanswered, so TIA aborts instead of downloading all blocks; pass consistentBlocksOnly=false to answer DownloadAllBlocks";
            if (selectionValues != null && selectionValues.Count > 0) return "no default for this prompt; allowed values: " + string.Join("/", selectionValues);
            return hasChecked ? "no default for this checkbox prompt; pass true/false via promptAnswersJson" : "prompt exposes nothing settable";
        }

        /// <summary>
        /// 为一个提示做决策。selectionValues 是该提示 CurrentSelection 枚举的全部合法名（无则为空数组）。
        /// 返回的 Answer 只描述“该怎么答”，赋值由调用方完成；调用方应在赋值失败时改记为 Unanswered。
        /// </summary>
        internal Answer Decide(string typeName, IReadOnlyList<string> selectionValues, bool hasChecked, bool hasPassword)
        {
            bool hasSelection = selectionValues.Count > 0;
            if (Explicit.TryGetValue(typeName, out var explicitValue))
            {
                if (OptInParameter(typeName) is string parameter)
                    throw new ArgumentException($"promptAnswers[{typeName}] refused: this prompt is answered only by the request parameter {parameter}=true.");
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
                return string.IsNullOrEmpty(Secret(typeName))
                    ? new Answer { Kind = AnswerKind.Unanswered, Source = "unanswered", Note = "password prompt without a matching password parameter" }
                    : new Answer { Kind = AnswerKind.Password, Source = "builtin" };
            }

            if (OptInPrompts.Contains(typeName))
            {
                string parameter = OptInParameter(typeName)!;
                if (OptInAllowed(typeName))
                {
                    string allow = OptInAllowSelection(typeName);
                    if (selectionValues.Contains(allow)) return new Answer { Kind = AnswerKind.Selection, Value = allow, Source = "allowed", Note = "allowed by " + parameter + "=true" };
                    return new Answer { Kind = AnswerKind.Unanswered, Source = "unanswered", Note = "allowed by " + parameter + "=true, but the prompt offers no " + allow + "; allowed values: " + string.Join("/", selectionValues) };
                }
                string? refuse = OptInRefuseSelection(typeName);
                return new Answer { Kind = AnswerKind.Refused, Value = refuse != null && selectionValues.Contains(refuse) ? refuse : null, Source = "refused", AllowWith = parameter,
                    Note = "refused by default; the download stops at this prompt. Pass " + parameter + "=true to allow it" };
            }

            var builtin = Builtin(typeName);
            if (builtin != null && selectionValues.Any(v => v == builtin.Value.Value))
                return new Answer { Kind = AnswerKind.Selection, Value = builtin.Value.Value, Source = builtin.Value.Source, Note = builtin.Value.Note };

            if (hasChecked)
            {
                bool? flag = BuiltinCheck(typeName);
                if (flag != null) return new Answer { Kind = AnswerKind.Checked, Value = flag.Value ? "true" : "false", Source = "builtin" };
            }

            return new Answer { Kind = AnswerKind.Unanswered, Source = "unanswered", Note = UnansweredNote(typeName, selectionValues, hasChecked) };
        }

        internal void Record(string typeName, string? message, Answer answer)
        {
            var row = new JsonObject { ["prompt"] = typeName, ["source"] = answer.Source };
            if (!string.IsNullOrEmpty(message)) row["message"] = message;
            if (answer.Kind == AnswerKind.Refused)
            {
                if (answer.Value != null) row["value"] = answer.Value;
                row["allowWith"] = answer.AllowWith;
                if (answer.Note != null) row["note"] = answer.Note;
                Refused.Add(row);
                return;
            }
            if (answer.Kind == AnswerKind.Unanswered) { if (answer.Note != null) row["note"] = answer.Note; Unanswered.Add(row); return; }
            row["kind"] = answer.Kind.ToString();
            if (answer.Kind != AnswerKind.Password) row["value"] = answer.Value;
            if (answer.Note != null) row["note"] = answer.Note;
            Answered.Add(row);
        }

#if TIA_NATIVE_ONLINE
        internal JsonObject Summary() => new JsonObject
        {
            ["promptsAnswered"] = new JsonArray(Answered.Select(x => x.DeepClone()).ToArray()),
            ["promptsUnanswered"] = new JsonArray(Unanswered.Select(x => x.DeepClone()).ToArray()),
            ["promptsRefused"] = new JsonArray(Refused.Select(x => x.DeepClone()).ToArray())
        };
#else
        internal JsonObject Summary() => new JsonObject
        {
            ["promptsAnswered"] = new JsonArray(Answered.Select(x => (JsonNode)x.DeepClone()).ToArray()),
            ["promptsUnanswered"] = new JsonArray(Unanswered.Select(x => (JsonNode)x.DeepClone()).ToArray()),
            ["promptsRefused"] = new JsonArray(Refused.Select(x => (JsonNode)x.DeepClone()).ToArray())
        };

#endif
        internal string UnansweredSummary() => Unanswered.Count == 0 ? "" :
            " Unanswered download prompts: " + string.Join(", ", Unanswered.Select(x => x["prompt"]!.GetValue<string>())) + " (pass answers via promptAnswersJson or the password parameters).";

        internal string RefusedSummary() => Refused.Count == 0 ? "" :
            " Stopped at refused download prompt(s): " + string.Join(", ", Refused.Select(x => x["prompt"]!.GetValue<string>() + " (pass " + x["allowWith"]!.GetValue<string>() + "=true to allow it)")) + ".";

#if !TIA_NATIVE_ONLINE
        private static readonly string[] PlannedDownloadPrompts = { "StopModules", "StopHSystemOrModule", "StopHSystem", "StartModules", "StartBackupModules",
            "DataBlockReinitialization", "DataBlockReinitializationOrKeepActualValues", "ConsistentBlocksDownload", "AllBlocksDownload", "UserManagementDownload",
            "AlarmTextLibrariesDownload", "WaitOnReboot", "TargetForSoftware", "InitializeMemory", "OverwriteOnMemoryCard", "OverwriteSystemData", "ResetModule",
            "SwitchBackupToPrimary", "ProtectionLevelChanged" };
        private static readonly string[] PlannedCheckPrompts = { "CheckBeforeDownload", "FitHmiComponents", "OverwriteTargetLanguages", "DownloadWebApplication", "UpdateWebApplication", "DeleteWebApplication" };
        private static readonly string[] PlannedPasswordPrompts = { "ModuleReadAccessPassword", "ModuleWriteAccessPassword", "PasswordReadAccess", "BlockBindingPassword", "PlcMasterSecretPassword" };

        /// <summary>
        /// Every prompt answer this request will give, for the approval card. Selection values are the intended enum names; a prompt
        /// whose release enum lacks the name stays unanswered at run time. Passwords are reported as present or absent, never as values.
        /// </summary>
        internal JsonObject Plan(string tool)
        {
            bool upload = tool is "UploadStationFromPlc" or "UploadDeviceParameters";
            var answers = new JsonObject();
            foreach (var kv in Explicit.OrderBy(p => p.Key, StringComparer.Ordinal))
                answers[kv.Key] = new JsonObject { ["answer"] = kv.Value, ["source"] = "explicit" };
            void Add(string type, JsonObject row) { if (!answers.ContainsKey(type)) answers[type] = row; }
            if (upload) Add("UploadMissingProducts", new JsonObject { ["answer"] = "TryUpload", ["source"] = "builtin" });
            else
            {
                foreach (var type in PlannedDownloadPrompts)
                {
                    var builtin = Builtin(type);
                    var row = builtin == null
                        ? new JsonObject { ["answer"] = null, ["source"] = "unanswered", ["note"] = UnansweredNote(type, null, false) }
                        : new JsonObject { ["answer"] = builtin.Value.Value, ["source"] = builtin.Value.Source };
                    if (builtin?.Note != null) row["note"] = builtin.Value.Note;
                    Add(type, row);
                }
                foreach (var type in PlannedCheckPrompts) Add(type, new JsonObject { ["answer"] = BuiltinCheck(type) == true ? "true" : "false", ["source"] = "builtin" });
                foreach (var type in OptInPrompts)
                {
                    string parameter = OptInParameter(type)!;
                    Add(type, OptInAllowed(type)
                        ? new JsonObject { ["answer"] = OptInAllowSelection(type), ["source"] = "allowed", ["allowedBy"] = parameter }
                        : new JsonObject { ["answer"] = null, ["source"] = "refused", ["allowWith"] = parameter, ["note"] = "the download stops at this prompt" });
                }
            }
            // Password prompts are listed by name in values, never as object keys: the approval display redacts every value under a
            // key that names a password, which would also hide the rest of this plan.
            var logins = new JsonArray();
            foreach (var type in upload ? new[] { "ModuleReadAccessPassword", "ModuleWriteAccessPassword", "PasswordReadAccess" } : PlannedPasswordPrompts)
                logins.Add(new JsonObject { ["prompt"] = type, ["answered"] = !string.IsNullOrEmpty(Secret(type)), ["from"] = upload ? "password" : PasswordFrom(type) });
            return new JsonObject
            {
                ["answers"] = answers,
                ["loginPrompts"] = logins,
                ["otherPrompts"] = "unanswered; TIA aborts the download at a blocking prompt",
                ["loginChannel"] = AllowNonTlsLogin ? "also connections that are not TLS-secured (allowNonTlsLogin=true)" : "TLS-secured connections only, where the release reports IsSecureCommunication"
            };
        }

        /// <summary>
        /// The prompt answer plan of one approval request, built from the raw MCP arguments with each tool's parameter defaults and the
        /// same booleans CallTool converts. Null for tools without download or upload prompts.
        /// </summary>
        internal static JsonObject? ApprovalPlan(string tool, JsonObject args)
        {
            if (tool is not ("DownloadPlc" or "DownloadPlcToFolder" or "UploadStationFromPlc" or "UploadDeviceParameters")) return null;
            bool? Flag(string key)
            {
                if (args[key] is not JsonValue value) return null;
                if (value.TryGetValue<bool>(out var flag)) return flag;
                if (value.TryGetValue<string>(out var text))
                {
                    text = text.Trim();
                    if (bool.TryParse(text, out var parsed)) return parsed;
                    if (text == "0" || text == "1") return text == "1";
                }
                if (value.TryGetValue<int>(out var number) && (number == 0 || number == 1)) return number == 1;
                return null;
            }
            string? Text(string key) => args[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            var request = new TiaMcp.Adapters.Contracts.OnlineRequest { Operation = tool };
            request.ConsistentBlocksOnly = Flag("consistentBlocksOnly") ?? request.ConsistentBlocksOnly;
            request.KeepActualValues = Flag("keepActualValues") ?? request.KeepActualValues;
            request.StartAfterDownload = Flag("startAfterDownload") ?? request.StartAfterDownload;
            request.StopBeforeDownload = Flag("stopBeforeDownload") ?? request.StopBeforeDownload;
            request.OverwriteOnMemoryCard = Flag("overwriteOnMemoryCard") ?? request.OverwriteOnMemoryCard;
            request.AllowSafetyProgramDownload = Flag("allowSafetyProgramDownload") ?? false;
            request.AllowHardwareDifferences = Flag("allowHardwareDifferences") ?? false;
            request.AllowActiveTestCancel = Flag("allowActiveTestCancel") ?? false;
            request.AllowExpandedDownload = Flag("allowExpandedDownload") ?? false;
            request.AllowIdentificationDataLoad = Flag("allowIdentificationDataLoad") ?? false;
            request.AllowNonTlsLogin = Flag("allowNonTlsLogin") ?? false;
            if (!string.IsNullOrWhiteSpace(Text("userManagementMode"))) request.UserManagementMode = Text("userManagementMode")!;
            if (!string.IsNullOrWhiteSpace(Text("targetForSoftware"))) request.TargetForSoftware = Text("targetForSoftware")!;
            request.Password = Text("password") ?? "";
            request.ModuleAccessPassword = Text("moduleAccessPassword") ?? "";
            request.BlockBindingPassword = Text("blockBindingPassword") ?? "";
            request.MasterSecretPassword = Text("masterSecretPassword") ?? "";
            try
            {
                var answers = args["promptAnswers"] is JsonObject map ? ParseExplicitAnswers(map.ToJsonString()) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return ForRequest(request, answers).Plan(tool);
            }
            catch (Exception error) when (error is ArgumentException || error is System.Text.Json.JsonException)
            {
                return new JsonObject { ["invalid"] = error.Message };
            }
        }
#endif
    }
}
