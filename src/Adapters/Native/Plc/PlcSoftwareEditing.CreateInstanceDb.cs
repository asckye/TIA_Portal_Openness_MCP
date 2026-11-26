using System;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
#if PLC_SOFTWARE_PROTECTION
        public HardwareAddressingReply CreatePlcInstanceDb(string softwarePath, string fbPath, string name, string groupPath = "", bool autoNumber = true,
                int number = 0, bool dryRun = true)
            => _session.RunHmiStepTool("CreatePlcInstanceDb", meta => {
                if (PlcGroupOperations.Parts(name).Length != 1) throw new ArgumentException("Single DB name and nonnegative number required.");
                var numberRefusal = PlcInstanceDbPolicy.Refusal(autoNumber, number);
                if (numberRefusal != null) throw new ArgumentException(numberRefusal);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = _session.ExactPlcForEngineering(softwarePath, !dryRun);
                var fb = _session.ExactMasterCopyPlcSource(softwarePath, fbPath, true) as FB
                    ?? throw new ArgumentException("fbPath must identify an FB, not an FC or DB.");
                var sourceRefusal = PlcInstanceDbPolicy.SourceRefusal(_session.ReleaseKey, fb.ProgrammingLanguage.ToString());
                if (sourceRefusal != null) throw new NotSupportedException(sourceRefusal);
                var group = (PlcBlockGroup)PlcGroupOperations.Group(plc.BlockGroup, groupPath, pathParts: value => PlcObjectPath.Parts(value, true));
                if (group.Blocks.Find(name) != null) throw new InvalidOperationException("Destination DB name already exists.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["fbPath"] = fbPath; meta["groupPath"] = groupPath; meta["name"] = name;
                if (!dryRun)
                {
                    meta["mayHaveChanged"] = true;
                    // Native project evidence: CreateInstanceDB(name, true, 0, fb) created "DB0" and compilation rejected "invalid number 0".
                    // Auto-numbering needs a valid seed, so 1 is passed and TIA assigns the free number.
                    // See docs/reference/real-machine-ledger.md for native acceptance evidence.
                    InstanceDB db;
                    try { db = CreateInstanceDb(group, name, autoNumber, number, fb, meta); }
                    finally { MutationStarted |= meta["mayHaveChanged"] is true; }
                    if (db == null || group.Blocks.Find(name) == null) throw new InvalidOperationException("Native instance DB not found after creation.");
                    meta["after"] = HardwareScalarEvidence.Read(db);
                }
                return dryRun ? "Native instance DB creation preview; no XML synthesized."
                    : "Instance DB created using the selected FB name; no save/compile/download.";
            });

#else
        public HardwareAddressingReply CreatePlcInstanceDb(string softwarePath, string fbPath, string name, string groupPath = "", bool autoNumber = true,
                int number = 0, bool dryRun = true)
            => throw new NotSupportedException("CreateInstanceDB requires V15.1 or later.");
#endif
    }
}
