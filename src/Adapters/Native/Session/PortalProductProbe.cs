using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters;

internal static class PortalProductProbe
{
    internal static Dictionary<string, object?> Read<TProcess, TProduct>(string? enabled, Func<TProcess> current,
        Func<TProcess, IList<TProduct>> products, Func<TProduct, IList<TProduct>> options, Func<TProduct, string> name,
        Func<TProduct, string> version, Action<string> journal)
    {
        var steps = new List<string>();
        var result = new Dictionary<string, object?> {
            ["status"] = "disabled", ["installedPatch"] = null, ["tiaUpdateLevel"] = null, ["sivarcProductVersion"] = null,
            ["sivarcLicenceState"] = null, ["steps"] = steps
        };
        if (enabled != "1") return result;
        TValue Step<TValue>(string member, Func<TValue> read)
        {
            journal(member);
            steps.Add(member);
            return read();
        }
        void Inspect(TProduct product)
        {
            var productName = Step("TiaPortalProduct.Name", () => name(product));
            if (productName.IndexOf("SiVArc", StringComparison.OrdinalIgnoreCase) >= 0)
                result["sivarcProductVersion"] = Step("TiaPortalProduct.Version", () => version(product));
            if (productName.Equals("TIA Portal", StringComparison.OrdinalIgnoreCase)
                || productName.Equals("Totally Integrated Automation Portal", StringComparison.OrdinalIgnoreCase))
            {
                var reported = Step("TiaPortalProduct.Version", () => version(product));
                result["tiaProductVersion"] = reported;
                // Only an explicit update label establishes an update level.
                if (reported?.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0) result["tiaUpdateLevel"] = reported;
                if (reported?.IndexOf("Hotfix", StringComparison.OrdinalIgnoreCase) >= 0) result["installedPatch"] = reported;
            }
        }
        try
        {
            var process = Step("TiaPortal.GetCurrentProcess", current);
            var installed = Step("TiaPortalProcess.InstalledSoftware", () => products(process));
            foreach (var product in installed)
            {
                Inspect(product);
                foreach (var option in Step("TiaPortalProduct.Options", () => options(product))) Inspect(option);
            }
            result["status"] = "ok";
        }
        catch (Exception error)
        {
            result["status"] = "read-failed";
            result["exceptionType"] = error.GetBaseException().GetType().FullName;
        }
        return result;
    }
}
