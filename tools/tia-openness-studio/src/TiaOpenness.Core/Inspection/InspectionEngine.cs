using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Abstractions;

namespace TiaOpenness.Core.Inspection
{
    /// <summary>
    /// The project-inspection rules, kept free of any Openness dependency so the mock and the
    /// real backend produce byte-identical findings for the same block metadata. Each backend
    /// only has to supply the facts; the verdicts live here.
    /// </summary>
    public static class InspectionEngine
    {
        /// <param name="blocks">Block metadata, already gathered by the backend.</param>
        /// <param name="referencedNames">
        /// Names referenced by at least one other block. Null means "unknown", which disables
        /// the dead-code rule rather than reporting every block as unused.
        /// </param>
        public static InspectionReport Run(string deviceId, IReadOnlyList<BlockInfo> blocks,
            InspectionOptions options, ISet<string> referencedNames = null)
        {
            options = options ?? new InspectionOptions();

            var report = new InspectionReport
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                DeviceId = deviceId,
                BlocksScanned = blocks.Count,
            };

            Regex namePattern = null;
            if (!string.IsNullOrWhiteSpace(options.BlockNamePattern))
            {
                try
                {
                    namePattern = new Regex(options.BlockNamePattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException(
                        "blockNamePattern is not a valid regular expression: " + ex.Message, ex);
                }
            }

            foreach (var block in blocks)
            {
                if (namePattern != null && !namePattern.IsMatch(block.Name))
                {
                    report.Findings.Add(new InspectionFinding
                    {
                        RuleId = "NAMING-001",
                        Severity = CheckStatus.Warn,
                        Target = block.Path,
                        Message = "Block name does not match " + options.BlockNamePattern,
                        Suggestion = "Rename the block to follow the project convention.",
                    });
                }

                if (options.RequireBlockComment && string.IsNullOrWhiteSpace(block.HeaderAuthor))
                {
                    report.Findings.Add(new InspectionFinding
                    {
                        RuleId = "DOC-001",
                        Severity = CheckStatus.Warn,
                        Target = block.Path,
                        Message = "Block header has no author.",
                        Suggestion = "Fill in the block properties so ownership survives handover.",
                    });
                }

                if (options.FlagInconsistentBlocks && !block.IsConsistent)
                {
                    report.Findings.Add(new InspectionFinding
                    {
                        RuleId = "BUILD-001",
                        Severity = CheckStatus.Fail,
                        Target = block.Path,
                        Message = "Block is inconsistent and must be compiled before it can be exported.",
                        Suggestion = "Compile the device, then re-run the export.",
                    });
                }

                if (block.IsKnowHowProtected)
                {
                    report.Findings.Add(new InspectionFinding
                    {
                        RuleId = "PROT-001",
                        Severity = CheckStatus.Warn,
                        Target = block.Path,
                        Message = "Block is know-how protected; it cannot be exported or version-controlled.",
                        Suggestion = "Keep the source outside this project, or remove the protection before archiving.",
                    });
                }
            }

            if (options.FindUnusedBlocks && referencedNames != null)
            {
                foreach (var block in blocks.Where(b => IsCallable(b.Kind) && !IsEntryPoint(b)))
                {
                    if (referencedNames.Contains(block.Name)) continue;

                    report.Findings.Add(new InspectionFinding
                    {
                        RuleId = "DEAD-001",
                        Severity = CheckStatus.Warn,
                        Target = block.Path,
                        Message = "Block is not referenced by any other block.",
                        Suggestion = "Delete it, or document why it is kept.",
                    });
                }
            }

            return report;
        }

        /// <summary>OBs are called by the runtime, so an unreferenced OB is normal, not dead code.</summary>
        private static bool IsEntryPoint(BlockInfo block)
        {
            return block.Kind == BlockKind.OB;
        }

        private static bool IsCallable(BlockKind kind)
        {
            return kind == BlockKind.FB || kind == BlockKind.FC || kind == BlockKind.OB;
        }
    }
}
