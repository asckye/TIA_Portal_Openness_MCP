using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.HW;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ManageHardwareObject(string devicePathJson, string action, string itemPathJson = "[]",
            string destinationDevicePathJson = "[]", string destinationItemPathJson = "[]", int position = -1, bool dryRun = true)
            => RunHmiStepTool("ManageHardwareObject", meta => {
                if (!new[] { "deleteDevice", "deleteItem", "moveItem", "copyItem" }.Contains(action)) throw new ArgumentException("action must be one of: deleteDevice/deleteItem/moveItem/copyItem (case-sensitive).");
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var source = ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["source"] = EngineeringScalarProperties.Read(source); meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["dependencyImpact"] = "Hardware descendants and dependent software/configuration can be affected. No online action or download is performed.";
                if (action == "deleteDevice")
                {
                    if (source is not Device device) throw new ArgumentException("deleteDevice requires empty itemPathJson.");
                    if (!dryRun)
                    {
                        meta["mayHaveChanged"] = true; device.Delete();
                        try { ExactEngineeringDevice(devicePathJson); throw new InvalidOperationException("Device is still present after Delete."); }
                        catch (InvalidOperationException ex) when (ex.Message == "Exact unique device not found." || ex.Message == "Device not found.") { meta["verifiedAbsent"] = true; }
                    }
                }
                else
                {
                    if (source is not DeviceItem item) throw new ArgumentException("This operation requires a nonempty exact device item path.");
                    if (action == "deleteItem")
                    {
                        var parent = item.Parent as HardwareObject ?? throw new InvalidOperationException("Hardware parent unavailable.");
                        var name = item.Name;
                        if (!dryRun) { meta["mayHaveChanged"] = true; item.Delete(); if (EngineeringGroupOperations.Find(parent.DeviceItems, name) != null) throw new InvalidOperationException("Item remains after Delete."); meta["verifiedAbsent"] = true; }
                    }
                    else
                    {
                        if (position < 0) throw new ArgumentException("A nonnegative destination slot position is required.");
                        var destination = ExactEngineeringHardware(destinationDevicePathJson, destinationItemPathJson);
                        bool allowed = action == "moveItem" ? destination.CanPlugMove(item, position) : destination.CanPlugCopy(item, position);
                        meta["canPlug"] = allowed; meta["position"] = position;
                        if (!allowed) throw new InvalidOperationException("TIA CanPlugMove/CanPlugCopy refused the destination.");
                        if (!dryRun)
                        {
                            meta["mayHaveChanged"] = true;
                            var result = action == "moveItem" ? destination.PlugMove(item, position) : destination.PlugCopy(item, position);
                            meta["after"] = EngineeringScalarProperties.Read(result);
                        }
                    }
                }
                return dryRun ? "Hardware preview; no modification." : "Hardware operation completed; project not saved or downloaded.";
            });
    }
}
