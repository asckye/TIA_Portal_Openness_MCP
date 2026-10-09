using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.ModelContextProtocol
{
    internal static class CapabilitySelfTestLogic
    {
        internal static async Task<ResponseCapabilitySelfTest> Run(Func<bool> group, Func<IEnumerable<string>> processes, Func<State> stateReader, Func<bool> connect, Func<string, string, ResponseMessage> validate, Func<string> tree, bool connectIfNeeded, bool includeProjectTree, bool inspectPortalProcesses, string expectedPlcSoftwarePath, string expectedHmiSoftwarePath)
        {
            var items = new List<CapabilitySelfTestItem>();
            string? projectTree = null;

            void Add(string id, string name, string status, string detail)
            {
                items.Add(new CapabilitySelfTestItem
                {
                    Id = id,
                    Name = name,
                    Status = status,
                    Detail = detail
                });
            }

            try
            {
                bool opennessOk;
                try
                {
                    opennessOk = group();
                    Add("openness.user-group", "Siemens TIA Openness user group", opennessOk ? "pass" : "fail", opennessOk ? "Current user is in the Openness group." : "Current user is not in the Openness group, or membership could not be confirmed.");
                }
                catch (Exception ex)
                {
                    opennessOk = false;
                    Add("openness.user-group", "Siemens TIA Openness user group", "fail", ex.Message);
                }

                if (inspectPortalProcesses)
                {
                    try
                    {
                        var processProjects = processes();
                        Add("tia.processes", "Visible TIA Portal processes", processProjects.Any() ? "pass" : "warn", string.Join(Environment.NewLine, processProjects));
                    }
                    catch (Exception ex)
                    {
                        Add("tia.processes", "Visible TIA Portal processes", "fail", ex.Message);
                    }
                }
                else
                {
                    Add("tia.processes", "Visible TIA Portal processes", "skip", "Process/project inspection skipped. Set inspectPortalProcesses=true to run it.");
                }

                var state = connectIfNeeded ? stateReader() : null;
                var isConnected = state?.IsConnected == true;
                if (!isConnected && connectIfNeeded)
                {
                    try
                    {
                        isConnected = connect();
                        state = stateReader();
                    }
                    catch (Exception ex)
                    {
                        Add("tia.connect", "Connect to TIA Portal", "fail", ex.Message);
                    }
                }

                Add("tia.connection", "MCP connection state", isConnected ? "pass" : "warn", $"IsConnected={state?.IsConnected}; Project={state?.Project}; Session={state?.Session}");

                var hasProject = isConnected && state != null && (!string.IsNullOrWhiteSpace(state.Project) && state.Project != "-" || !string.IsNullOrWhiteSpace(state.Session) && state.Session != "-");
                if (hasProject)
                {
                    try
                    {
                        var validation = validate(expectedPlcSoftwarePath, expectedHmiSoftwarePath);
                        var success = validation.Meta != null && validation.Meta.TryGetPropertyValue("success", out var value) && value != null && value.GetValue<bool>();
                        Add("project.automation-context", "Automation context validation", success ? "pass" : "warn", validation.Message ?? "ValidateAutomationContext returned no message.");
                    }
                    catch (Exception ex)
                    {
                        Add("project.automation-context", "Automation context validation", "fail", ex.Message);
                    }

                    if (includeProjectTree)
                    {
                        try
                        {
                            projectTree = tree();
                            Add("project.tree", "Project tree readback", string.IsNullOrWhiteSpace(projectTree) ? "warn" : "pass", string.IsNullOrWhiteSpace(projectTree) ? "Project tree was empty." : "Project tree read successfully.");
                        }
                        catch (Exception ex)
                        {
                            Add("project.tree", "Project tree readback", "fail", ex.Message);
                        }
                    }
                }
                else
                {
                    Add("project.open", "Open project/session", "warn", "No open project or local session is attached. Project-specific checks were skipped.");
                }

                var ok = items.All(i => i.Status == "pass" || i.Status == "warn" || i.Status == "skip");
                return new ResponseCapabilitySelfTest
                {
                    Ok = ok,
                    IncludeProjectTree = includeProjectTree,
                    Items = items,
                    ProjectTree = projectTree,
                    Message = ok ? "Capability self-test completed" : "Capability self-test completed with failures",
                    // envelope: legacy-multiple-dynamic-fields
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["connectIfNeeded"] = connectIfNeeded,
                        ["checkedItems"] = items.Count
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running capability self-test: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
