using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal partial class PlcB5PortService
    {
        private static PlcB5Input Input(string raw)
        {
            var input = new PlcB5Input();
            try
            {
                var node = JsonNode.Parse(raw);
                if (node is JsonObject obj)
                {
                    input.Kind = "object";
                    input.Values = HardwareAddressingService.Scalars(raw);
                }
                else if (node is JsonArray array)
                {
                    input.Kind = "array";
                    input.Strings = array.Select(n => n!.GetValue<string>()).ToArray();
                }
            }
            catch (Exception error) { input.Error = error.Message; }
            return input;
        }
        private static void Prepare(PlcB5Request request)
        {
            foreach (var raw in new[] { request.PropertiesJson, request.AttributesJson, request.CulturesJson, request.TextListNamesJson, request.AccessLevelsJson })
                request.Inputs[raw] = Input(raw);
            try
            {
                switch (request.Operation)
                {
                    case "ExchangePlcAlarmTextLists":
                        var xlsx = AlarmTextListRules.ValidateXlsxRequest(request.Action, request.FilePath, request.UnitName, request.UnitKind, request.TextListNamesJson, request.CulturesJson, request.ImportOption, request.ConfirmImport, request.DryRun);
                        request.ValidatedWriting = xlsx.Writing; request.TextLists = xlsx.TextLists; request.Cultures = xlsx.Cultures; break;
                    case "ManagePlcAlarmTextList": request.ValidatedWriting = PlcBlockServicesLogic.ValidateTextListRequest(request.Action, request.Name, request.LibraryName, request.MasterCopyPath, request.ConfirmDelete, request.DryRun); break;
                    case "ManagePlcDocuments": request.ValidatedWriting = SoftwareUnitDeepLogic.ValidateDocumentRequest(request.Action, request.ObjectKind, request.Name, request.UnitName, request.UnitKind, request.DirectoryPath, request.ImportOption, request.LibraryName, request.MasterCopyPath, request.CopyMode, request.TypePath, request.Version, request.UpdatePathsMode, request.DryRun); break;
                    case "ExportPlcProDiagInfo": ProDiagExportRules.ValidateProDiagRequest(request.BlockPath, request.DirectoryPath, request.UnitName, request.UnitKind); break;
                    case "ImportPlcAlarmInstanceTexts": request.Cultures = PlcBlockServicesLogic.ParseCultureNames(request.CulturesJson).Select(c => c.Name).ToArray(); break;
                }
            }
            catch (Exception error) { request.ValidationError = error.Message; }
        }
    }
}
