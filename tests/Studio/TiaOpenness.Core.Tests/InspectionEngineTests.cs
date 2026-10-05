using System;
using System.Collections.Generic;
using System.Linq;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Inspection;
using Xunit;

namespace TiaOpenness.Core.Tests;

/// <summary>
/// The inspection rules are the one piece of judgement in the product that runs identically
/// against the mock and against a real project, which is exactly why they are worth pinning
/// down here: a rule that changes its mind silently changes what a CI build fails on.
/// </summary>
public class InspectionEngineTests
{
    private static BlockInfo Block(
        string path,
        BlockKind kind = BlockKind.FC,
        string? author = "someone",
        bool consistent = true,
        bool protectedBlock = false)
    {
        // No range operator here: System.Range does not exist on .NET Framework 4.8.
        var name = path.Substring(path.LastIndexOf('/') + 1);
        return new BlockInfo
        {
            Path = path,
            Name = name,
            Kind = kind,
            IsConsistent = consistent,
            IsKnowHowProtected = protectedBlock,
            HeaderAuthor = author,
        };
    }

    /// <summary>Rules that are switched off in the options must not fire at all.</summary>
    private static InspectionOptions NoRules() => new()
    {
        BlockNamePattern = null,
        RequireBlockComment = false,
        FindUnusedBlocks = false,
        FlagInconsistentBlocks = false,
    };

    [Fact]
    public void Reports_the_device_and_the_number_of_blocks_it_looked_at()
    {
        var blocks = new List<BlockInfo> { Block("Main"), Block("Motion/FC_Jog") };

        var report = InspectionEngine.Run("PLC_1", blocks, NoRules());

        Assert.Equal("PLC_1", report.DeviceId);
        Assert.Equal(2, report.BlocksScanned);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Naming_rule_flags_only_the_blocks_that_do_not_match()
    {
        var options = NoRules();
        options.BlockNamePattern = "^(OB|FB|FC)_";

        var report = InspectionEngine.Run("PLC_1",
            [Block("Motion/FC_Jog"), Block("Legacy/Helper")], options);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("NAMING-001", finding.RuleId);
        Assert.Equal("Legacy/Helper", finding.Target);
        Assert.Equal(CheckStatus.Warn, finding.Severity);
    }

    [Fact]
    public void An_invalid_name_pattern_is_reported_as_a_bad_argument_not_a_crash()
    {
        var options = NoRules();
        options.BlockNamePattern = "^(unclosed";

        var error = Assert.Throws<ArgumentException>(
            () => InspectionEngine.Run("PLC_1", [Block("Main")], options));

        Assert.Contains("blockNamePattern", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_author_is_flagged_only_when_the_rule_is_on()
    {
        var blocks = new List<BlockInfo> { Block("Main", author: "   ") };

        Assert.Empty(InspectionEngine.Run("PLC_1", blocks, NoRules()).Findings);

        var options = NoRules();
        options.RequireBlockComment = true;

        Assert.Equal("DOC-001", Assert.Single(InspectionEngine.Run("PLC_1", blocks, options).Findings).RuleId);
    }

    [Fact]
    public void An_inconsistent_block_is_a_failure_because_it_cannot_be_exported()
    {
        var options = NoRules();
        options.FlagInconsistentBlocks = true;

        var report = InspectionEngine.Run("PLC_1", [Block("Draft/FC_Broken", consistent: false)], options);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("BUILD-001", finding.RuleId);
        Assert.Equal(CheckStatus.Fail, finding.Severity);
    }

    /// <summary>Protection is a property of the block, so this rule has no off switch.</summary>
    [Fact]
    public void Know_how_protection_is_always_reported()
    {
        var report = InspectionEngine.Run("PLC_1", [Block("Vendor/FB_Locked", protectedBlock: true)], NoRules());

        Assert.Equal("PROT-001", Assert.Single(report.Findings).RuleId);
    }

    [Fact]
    public void Dead_code_rule_stays_quiet_when_the_caller_could_not_work_out_the_references()
    {
        var options = NoRules();
        options.FindUnusedBlocks = true;

        // referencedNames omitted: "unknown", not "nothing is referenced".
        var report = InspectionEngine.Run("PLC_1", [Block("Legacy/FC_Old")], options);

        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Dead_code_rule_flags_an_unreferenced_callable_block()
    {
        var options = NoRules();
        options.FindUnusedBlocks = true;

        var report = InspectionEngine.Run("PLC_1",
            [Block("Motion/FC_Jog"), Block("Legacy/FC_Old")],
            options,
            new HashSet<string>(StringComparer.Ordinal) { "FC_Jog" });

        var finding = Assert.Single(report.Findings);
        Assert.Equal("DEAD-001", finding.RuleId);
        Assert.Equal("Legacy/FC_Old", finding.Target);
    }

    /// <summary>The runtime calls OBs, so an unreferenced OB is normal rather than dead.</summary>
    [Fact]
    public void Dead_code_rule_ignores_organisation_blocks()
    {
        var options = NoRules();
        options.FindUnusedBlocks = true;

        var report = InspectionEngine.Run("PLC_1",
            [Block("Main", BlockKind.OB)], options, new HashSet<string>(StringComparer.Ordinal));

        Assert.Empty(report.Findings);
    }

    /// <summary>Data blocks are not called, so they are outside the dead-code rule.</summary>
    [Fact]
    public void Dead_code_rule_ignores_data_blocks()
    {
        var options = NoRules();
        options.FindUnusedBlocks = true;

        var report = InspectionEngine.Run("PLC_1",
            [Block("Recipe/DB_Recipe", BlockKind.DB)], options, new HashSet<string>(StringComparer.Ordinal));

        Assert.Empty(report.Findings);
    }

    [Fact]
    public void One_block_can_collect_findings_from_several_rules()
    {
        var options = new InspectionOptions
        {
            BlockNamePattern = "^FB_",
            RequireBlockComment = true,
            FlagInconsistentBlocks = true,
            FindUnusedBlocks = false,
        };

        var report = InspectionEngine.Run("PLC_1",
            [Block("Draft/Broken", author: null, consistent: false, protectedBlock: true)], options);

        Assert.Equal(
            new[] { "BUILD-001", "DOC-001", "NAMING-001", "PROT-001" },
            report.Findings.Select(f => f.RuleId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Null_options_fall_back_to_the_defaults_rather_than_throwing()
    {
        var report = InspectionEngine.Run("PLC_1", [Block("Main", BlockKind.OB, author: null)], null!);

        Assert.Equal("DOC-001", Assert.Single(report.Findings).RuleId);
    }
}
