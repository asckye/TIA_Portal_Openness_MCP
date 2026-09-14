using System;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private ResponseMessage RunHmiStepTool(string toolName, Func<JsonObject, string> action)
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["tool"] = toolName,
                ["success"] = false
            };

            try
            {
                if (IsProjectNull())
                {
                    meta["error"] = "Project is null";
                    meta["status"] = "InvalidState";
                    meta["operationSuccess"] = false;
                    return new ResponseMessage { Message = "Project is null", Meta = meta };
                }

                var message = action(meta);
                meta["success"] = meta["operationSuccess"]?.GetValue<bool>() ?? true;
                meta["operationSuccess"] = meta["success"]?.DeepClone();
                return new ResponseMessage { Message = message, Meta = meta };
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                meta["error"] = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                meta["operationSuccess"] = false;
                meta["status"] = tie.InnerException is PortalException pex ? pex.Code.ToString() : "ReadOrWriteFailed";
                return new ResponseMessage { Message = $"{toolName} failed", Meta = meta };
            }
            catch (Exception ex)
            {
                meta["error"] = ex.ToString();
                meta["operationSuccess"] = false;
                meta["status"] = ex is PortalException pex ? pex.Code.ToString() : "ReadOrWriteFailed";
                return new ResponseMessage { Message = $"{toolName} failed", Meta = meta };
            }
        }

    }
}
