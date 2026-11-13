using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    internal static class PortalDialogHandlers
    {
        internal static void Notification(NotificationEventArgs e, List<Dictionary<string, object?>> records)
        {
            var record = new Dictionary<string, object?> {
                ["event"] = "Notification", ["utc"] = DateTimeOffset.UtcNow.ToString("O"), ["caption"] = e.Caption,
                ["text"] = e.Text, ["detailText"] = e.DetailText, ["answer"] = "handled" };
            PortalDialogPolicy.Record(records, record);
            Console.Error.WriteLine(PortalDialogPolicy.Report(record));
            e.IsHandled = true;
        }
    }

    public sealed partial class PlcFoundationEngine
    {
        private void OnPortalConfirmation(object? sender, ConfirmationEventArgs e)
        {
            var offered = Enum.GetValues(typeof(ConfirmationChoices)).Cast<ConfirmationChoices>()
                .Where(choice => choice != ConfirmationChoices.None && (e.Choices & choice) == choice).Select(choice => choice.ToString()).ToArray();
            string? refusal = PortalDialogPolicy.RefusingChoice(offered);
            refusal ??= PortalDialogPolicy.Answer(offered);
            var record = new Dictionary<string, object?> {
                ["event"] = "Confirmation", ["utc"] = DateTimeOffset.UtcNow.ToString("O"), ["caption"] = e.Caption, ["text"] = e.Text,
                ["detailText"] = e.DetailText, ["choices"] = offered, ["answer"] = refusal ?? "unanswered",
                ["rule"] = refusal == "Ok" ? "acknowledge Ok-only confirmation" : refusal != null
                    ? "refused by default; no approved plan answers TIA confirmations" : "no refusing choice offered; a person answers in TIA" };
            PortalDialogPolicy.Record(portalDialogs, record);
            Console.Error.WriteLine(PortalDialogPolicy.Report(record));
            if (refusal != null)
            {
                e.Result = (ConfirmationResult)Enum.Parse(typeof(ConfirmationResult), refusal);
                e.IsHandled = true;
            }
        }
    }
}
