using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts
{
    public enum NativeStateKind { Unexpected, Success, Warning, Failure, Partial }

    public sealed class NativeResultEvidence
    {
        public string EnumType { get; set; } = "";
        public string? State { get; set; }
    }

    public sealed class NativeResultException : InvalidOperationException
    {
        public NativeResultEvidence Evidence { get; }
        public NativeResultException(string enumType, object? state)
            : base("Native operation returned " + (state?.ToString() ?? "no state") + ".")
        { Evidence = new NativeResultEvidence { EnumType = enumType, State = state?.ToString() }; }
    }

    // Exact SDK enum names and values, checked against V14 SP1-V21 metadata.
    // Status enums (online, comparison, lifecycle) are deliberately not result enums.
    public static class NativeResultStates
    {
        public const string Compiler = "Siemens.Engineering.Compiler.CompilerResultState";
        public const string Download = "Siemens.Engineering.Download.DownloadResultState";
        public const string Upload = "Siemens.Engineering.Upload.UploadResultState";
        public const string Cax = "Siemens.Engineering.Cax.TransferResultState";
        public const string TestSuite = "Siemens.Engineering.TestSuite.TestResultsState";
        public const string AlarmTexts = "Siemens.Engineering.SW.Alarm.PlcAlarmTextXlsxResultState";
        public const string TextLists = "Siemens.Engineering.SW.Alarm.TextListXlsxResultState";
        public const string AlarmClasses = "Siemens.Engineering.SW.Alarm.AlarmClassExportImportResultState";
        public const string Documents = "Siemens.Engineering.SW.DocumentResultState";
        public const string SupervisionXlsx = "Siemens.Engineering.SW.Supervision.SupervisionXlsxResultState";
        public const string SupervisionSettings = "Siemens.Engineering.SW.Supervision.SupervisionSettingsExportImportResultState";
        public const string SystemDiagnostics = "Siemens.Engineering.HW.Systemdiagnostics.Settings.SystemdiagnosticsSettingsExportImportResultState";
        public const string ProjectTexts = "Siemens.Engineering.ProjectTextResultState";
        public const string Library = "Siemens.Engineering.Library.TransferResultState";
        public const string Layout = "Siemens.Engineering.SiVArc.LayoutImportResultState";
        public const string Unified = "Siemens.Engineering.HmiUnified.Common.ResultState";
        public const string SafetyTest = "Siemens.Engineering.SafetyValidation.TestState";
        public const string SafetyValidation = "Siemens.Engineering.SafetyValidation.TestValidationResultState";

        private static Dictionary<string, NativeStateKind> Values(string success, string failures = "", string partial = "", string unexpected = "", bool warning = true)
        {
            var values = new Dictionary<string, NativeStateKind>(StringComparer.Ordinal);
            void Add(string list, NativeStateKind kind)
            { foreach (string value in list.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) values.Add(value, kind); }
            Add(success, NativeStateKind.Success); Add(failures, NativeStateKind.Failure);
            Add(partial, NativeStateKind.Partial); Add(unexpected, NativeStateKind.Unexpected);
            if (warning) values.Add("Warning", NativeStateKind.Warning);
            return values;
        }

        private static readonly Dictionary<string, Dictionary<string, NativeStateKind>> Enums = new Dictionary<string, Dictionary<string, NativeStateKind>>(StringComparer.Ordinal)
        {
            [Compiler] = Values("Success,Information", "Error"),
            [Download] = Values("Success,Information", "Error"),
            [Upload] = Values("Success,Information", "Error"),
            [Cax] = Values("Success,Information", "Error"),
            [TestSuite] = Values("Success,Information", "Error"),
            [AlarmTexts] = Values("OK", "Error"),
            [TextLists] = Values("OK", "Error"),
            [AlarmClasses] = Values("Success", "Error"),
            [Documents] = Values("Success", "Failure", "PartialSuccess", warning: false),
            [SupervisionXlsx] = Values("Success", "Failure", warning: false),
            [SupervisionSettings] = Values("Success", "ErrorRollback", warning: false),
            [SystemDiagnostics] = Values("Success", "Error"),
            [ProjectTexts] = Values("Info", "Error"),
            [Library] = Values("Success"),
            [Layout] = Values("Success", unexpected: "None"),
            [Unified] = Values("Success", "Error", unexpected: "None"),
            [SafetyTest] = Values("Succeeded", "Failed", unexpected: "NotTested", warning: false),
            [SafetyValidation] = Values("Okay", "Error", warning: false)
        };

        public static IEnumerable<string> EnumTypes => Enums.Keys;
        public static IEnumerable<string> States(string enumType) => Enums.TryGetValue(enumType, out var values) ? values.Keys : Array.Empty<string>();
        public static NativeStateKind Classify(string? enumType, string? state)
            => enumType != null && state != null && Enums.TryGetValue(enumType, out var values) && values.TryGetValue(state, out var kind) ? kind : NativeStateKind.Unexpected;
        public static bool Succeeded(string enumType, string? state)
            => Classify(enumType, state) is NativeStateKind.Success or NativeStateKind.Warning;
        public static bool Succeeded(object? state) => state is Enum value && Succeeded(value.GetType().FullName!, value.ToString());
    }
}
