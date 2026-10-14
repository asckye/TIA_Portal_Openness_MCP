using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class XmlBuildResults
    {
        internal static ResponseXmlBuild BuildOfflineXmlBuilderReport(JsonObject data, string successMessage)
        {
            var ok = data["ok"]?.GetValue<bool>() == true;
            var xml = data["xml"]?.GetValue<string>();

            // Builders use one of two error shapes:
            //   PlcBuilderToolJson:        ["error"] = string?
            //   ClassicHmi*XmlBuilder:     ["errors"] = JsonArray of string
            string[]? errorList = null;
            if (data["errors"] is JsonArray errArr)
            {
                errorList = errArr.Where(e => e != null).Select(e => e!.GetValue<string>()).ToArray();
                if (errorList.Length == 0) errorList = null;
            }
            else
            {
                var singleError = data["error"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(singleError))
                    errorList = new[] { singleError! };
            }

            string[]? warningList = null;
            if (data["warnings"] is JsonArray warnArr)
            {
                warningList = warnArr.Where(w => w != null).Select(w => w!.GetValue<string>()).ToArray();
                if (warningList.Length == 0) warningList = null;
            }

            return new ResponseXmlBuild
            {
                Ok = ok,
                Message = ok ? successMessage : successMessage + " with validation findings",
                Data = data,
                Xml = xml,
                Errors = errorList,
                Warnings = warningList,
                Meta = ResponseMeta.Basic(ok, ("offlineOnly", true))
            };
        }
    }
}
