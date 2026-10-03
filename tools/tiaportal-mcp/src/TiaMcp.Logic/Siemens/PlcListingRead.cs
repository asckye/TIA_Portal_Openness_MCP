using System;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // A failed optional attribute is not a failed software lookup. Never retry IPC failures.
    internal sealed class PlcListingRead
    {
        private readonly JsonArray failures = new JsonArray();
        internal T Required<T>(string path, Func<T> read)
        {
            try { return read(); }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"PLC listing failed at '{path}' ({ex.GetType().Name}): {ex.Message}. "
                    + "No complete listing was returned; do not interpret this as software/blocks not found.", null, ex);
            }
        }
        internal T Optional<T>(string path, Func<T> read, T unavailable)
        {
            try { return read(); }
            catch (Exception ex)
            {
                if (HmiReadSafety.ConnectionUnavailable(ex))
                    throw new PortalException(PortalErrorCode.OpennessError,
                        $"PLC listing connection unavailable at '{path}' ({ex.GetType().Name}): {ex.Message}. No retry was attempted.", null, ex);
                failures.Add(new JsonObject { ["path"] = path, ["exceptionType"] = ex.GetType().FullName, ["message"] = ex.Message });
                return unavailable;
            }
        }
        internal JsonObject Metadata(string scope) => new JsonObject
        {
            ["timestamp"] = DateTime.Now, ["serverVersion"] = typeof(PlcListingRead).Assembly.GetName().Version?.ToString(),
            ["success"] = true, ["apiCallSuccess"] = true, ["dataComplete"] = failures.Count == 0,
            ["traversalComplete"] = true, ["scope"] = scope, ["failureCount"] = failures.Count,
            ["failures"] = failures.DeepClone()
        };
    }
}
