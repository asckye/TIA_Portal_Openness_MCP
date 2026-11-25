using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed partial class VerifiedExportStyleTests
{
    private const string Interface = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
    private const string Scl = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4";
    private const string Author = "<HeaderAuthor>Commissioning</HeaderAuthor>";
    private static readonly string[] Computed = {
        "CodeModifiedDate", "InterfaceModifiedDate", "ParameterModified", "StructureModified", "LoadMemoryLength", "WorkMemoryLength"
    };
    private const string Comment = "<LineComment UId='10'><Text> speed</Text></LineComment><NewLine UId='11'/>";
    private const string Assignment = "<Access Scope='LocalVariable' UId='12'><Symbol UId='13'><Component Name='Speed' UId='14'/></Symbol></Access>" +
        "<Token Text=':=' UId='15'/><Access Scope='LiteralConstant' UId='16'><Constant UId='17'><ConstantValue UId='18'>3</ConstantValue></Constant></Access>" +
        "<Token Text=';' UId='19'/><NewLine UId='20'/>";

    private static string Code(string kind, bool full)
    {
        string returns = kind == "FC" ? "<Section Name='Return'><Member Name='Ret_Val' Datatype='Void'" +
            (full ? " Accessibility='Public' Remanence='NonRetain'" : "") + "/></Section>" : "";
        string inputs = kind == "FB" ? "<Section Name='Input'><Member Name='Start' Datatype='Bool'/></Section>" : "";
        string outputs = kind == "FB" ? "<Member Name='Run' Datatype='Bool'/><Member Name='Sum' Datatype='Int'/>" : "";
        string statics = kind == "FB" ? "<Section Name='Static'><Member Name='Cnt' Datatype='Int'/></Section>" : "";
        return $"""
            <Document><Engineering version="V19"/><SW.Blocks.{kind} ID="0"><AttributeList>
            <Interface><Sections xmlns="{Interface}">{inputs}<Section Name="Output">
            <Member Name="Speed" Datatype="Int"/><Member Name="Limit" Datatype="Int"/>
            {outputs}</Section>{statics}{returns}</Sections></Interface><Name>SpeedBlock</Name><Number>9</Number><ProgrammingLanguage>SCL</ProgrammingLanguage>
            </AttributeList><ObjectList><SW.Blocks.CompileUnit ID="1" CompositionName="CompileUnits"><AttributeList>
            <NetworkSource><StructuredText xmlns="{Scl}">{Comment}{Assignment}{(full ? "<NewLine UId='21'/>" : "")}</StructuredText></NetworkSource>
            <ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.{kind}></Document>
            """;
    }

    private static string Db(string? speed, string? secondSpeed, int depth, bool full)
    {
        string Motor(string name, string? value)
        {
            string instance = $"<Member Name='{name}' Datatype='&quot;MotorType&quot;'";
            if (!full && value == null) return instance + "/>";
            string ready = full ? "<Member Name='Ready' Datatype='Bool'/>" : "";
            string speed = value == null ? "<Member Name='Speed' Datatype='Int'/>" :
                $"<Member Name='Speed' Datatype='Int'><StartValue>{value}</StartValue></Member>";
            return instance + "><Sections><Section Name='None'>" + ready + speed + "</Section></Sections></Member>";
        }
        string motors = Motor("Motor", speed) + Motor("Motor2", secondSpeed);
        for (int level = 0; level < depth; level++) motors = $"<Member Name='Group{level}' Datatype='Struct'>{motors}</Member>";
        return $"""
            <Document><Engineering version="V19"/><SW.Blocks.GlobalDB ID="0"><AttributeList><Interface><Sections xmlns="{Interface}">
            <Section Name="Static">{motors}</Section></Sections></Interface><Name>MotorData</Name><Number>10</Number>
            <ProgrammingLanguage>DB</ProgrammingLanguage></AttributeList></SW.Blocks.GlobalDB></Document>
            """;
    }

    // V18 raw-V rows 047, 050 and 056 list the omitted scalars by block kind.
    // V16 row 083 lists six common member flags and SetPoint on static members.
    private static string FullExport(string xml, bool after)
    {
        var document = XDocument.Parse(xml);
        var block = PlcDocumentEditing.Block(document);
        var attributes = block.Element("AttributeList")!;
        string flags = "AutoNumber IsConsistent IsKnowHowProtected ";
        if (block.Name.LocalName == "SW.Blocks.GlobalDB")
        {
            attributes.Add(new XElement("MemoryReserve", 64)); // V16 raw-V row 025.
            flags += "DBAccessibleFromOPCUA DBAccessibleFromWebserver DownloadWithoutReinit IsOnlyStoredInLoadMemory IsPLCDB " +
                "IsRetainMemResEnabled IsWriteProtectedInAS";
        }
        else
        {
            flags += "HandleErrorsWithinBlock IsIECCheckEnabled IsWriteProtected PLCSimAdvancedSupport UDAEnableTagReadback";
            if (block.Name.LocalName == "SW.Blocks.FB") flags += " DownloadWithoutReinit ISMultiInstanceCapable IsRetainMemResEnabled VirtualPlcSupport";
            attributes.Add(new XElement("LibraryConformanceStatus", "NonConformant"), new XElement("UDABlockProperties"));
        }
        foreach (string name in flags.Split(' ')) attributes.Add(new XElement(name, name == "IsConsistent" ? "true" : "false"));
        attributes.Add(XElement.Parse(Author), new XElement("HeaderFamily", "Motion"), new XElement("HeaderName", "Control"),
            new XElement("HeaderVersion", "1.0"));
        foreach (string name in new[] { "CodeModifiedDate", "InterfaceModifiedDate", "ParameterModified", "StructureModified" })
            attributes.Add(new XElement(name, after ? "2026-07-15T12:01:00Z" : "2026-07-15T12:00:00Z"));
        attributes.Add(new XElement("LoadMemoryLength", after ? 256 : 240), new XElement("WorkMemoryLength", after ? 160 : 144));
        XNamespace ns = Interface;
        foreach (var member in attributes.Descendants(ns + "Member"))
        {
            var list = new XElement(ns + "AttributeList");
            string names = "ExternalAccessible ExternalVisible ExternalWritable UserVisible UserReadOnly UserDeletable";
            if ((string?)member.Ancestors(ns + "Section").First().Attribute("Name") == "Static") names += " SetPoint";
            foreach (string name in names.Split(' '))
                list.Add(new XElement(ns + "BooleanAttribute", new XAttribute("Name", name), new XAttribute("SystemDefined", "true"),
                    name == "UserReadOnly" ? "true" : "false"));
            member.AddFirst(list);
        }
        return document.ToString(SaveOptions.DisableFormatting);
    }

    // Exercise the same validation, preview token, apply and metadata conversion as the worker.
    private static JsonObject Apply(string candidate, string before, string after, string none, bool compile, bool extra = true,
        Exception? exportFailure = null, string? declaration = null, Exception? readbackFailure = null, string declarationName = "MotorType")
    {
        string directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "verified-style-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var request = new PlcSoftwareRequest { FilePath = Path.Combine(directory, "candidate.xml"), EvidenceDirectory = directory,
                DryRun = true, CompileAfterImport = compile };
            File.WriteAllText(request.FilePath, candidate);
            var input = PlcVerifiedImportBridge.Validate(request);
            var calls = new List<string>();
            var execution = new PlcVerifiedExecution {
                Request = request, CandidateXml = input.Xml, Binding = "PLC/" + input.Name, Meta = new(),
                VerifyBinding = () => calls.Add("bind"), Import = path => calls.Add(Path.GetFileName(path)),
                Compile = compile ? () => calls.Add("compile") : null,
                Export = path => {
                    calls.Add(Path.GetFileName(path));
                    if (Path.GetFileName(path) == "after.xml" && readbackFailure != null) throw readbackFailure;
                    File.WriteAllText(path, Path.GetFileName(path) == "before.xml" ? before : after);
                },
                ExportNone = path => {
                    calls.Add(Path.GetFileName(path));
                    if (exportFailure != null) throw exportFailure;
                    File.WriteAllText(path, none);
                }
            };
            if (declaration != null) execution.ExportType = (name, path) => {
                calls.Add("type " + name);
                File.WriteAllText(path, declaration);
                return true;
            };
            PlcVerifiedImportBridge.Execute(execution);
            var beforeCalls = new List<string> { "bind", "before.xml" };
            if (declaration != null) beforeCalls.Add("type " + declarationName);
            Assert.Equal(beforeCalls, calls);
            request.ExpectedToken = Assert.IsType<string>(execution.Meta["token"]);
            request.DryRun = false;
            calls.Clear();
            execution.Meta.Clear();
            PlcVerifiedImportBridge.Execute(execution);
            var expectedCalls = new List<string>(beforeCalls) { "bind", "planned.xml" };
            if (compile) expectedCalls.AddRange(new[] { "bind", "compile" });
            expectedCalls.AddRange(new[] { "bind", "after.xml" });
            if (extra) expectedCalls.AddRange(new[] { "bind", "after-none.xml" });
            Assert.Equal(expectedCalls, calls);
            var meta = JsonSerializer.SerializeToNode(execution.Meta)!.AsObject();
            Assert.True((bool?)meta["importReturned"]);
            Assert.Equal(compile, (bool?)meta["compileRequested"]);
            Assert.Equal("complete", (string?)meta["phase"]);
            Assert.Equal(extra ? "candidateWithNone" : "plannedWithDefaults", (string?)meta["comparison"]!["method"]);
            string evidence = (string)meta["evidenceDirectory"]!;
            AssertComputedAttributes(before, after, Path.Combine(evidence, "planned.xml"), meta);
            Assert.Equal(after, File.ReadAllText(Path.Combine(evidence, "after.xml")));
            string nonePath = Path.Combine(evidence, "after-none.xml");
            Assert.Equal(extra && exportFailure == null, File.Exists(nonePath));
            if (extra && exportFailure == null) Assert.Equal(none, File.ReadAllText(nonePath));
            if ((bool)meta["verificationSuccess"]!)
            {
                Assert.Equal("DocumentReadbackMatched", (string?)meta["status"]);
                Assert.True((bool?)meta["operationSuccess"]);
                Assert.Null(meta["cleanupRequired"]);
                Assert.Null(meta["readbackDifference"]);
            }
            else
            {
                Assert.Equal("DocumentReadbackMismatch", (string?)meta["status"]);
                Assert.False((bool?)meta["operationSuccess"]);
                Assert.NotNull(meta["cleanupRequired"]);
                string planned = File.ReadAllText(Path.Combine(evidence, "planned.xml"));
                string Canon(string xml) => PlcDocumentEditing.Canonical(PlcDocumentEditing.Parse(xml));
                Assert.Equal(PlcDocumentEditing.ReadbackDifference(Canon(planned), Canon(after)), (string?)meta["readbackDifference"]);
                Assert.False((bool?)meta["writeOutcomeUnknown"] ?? false);
            }
            return meta;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void AssertComputedAttributes(string before, string after, string planPath, JsonObject meta)
    {
        var original = XDocument.Parse(before);
        var readback = XDocument.Parse(after);
        var plan = XDocument.Load(planPath);
        var reported = meta["preservedOmittedBlockAttributes"]!.AsArray().Select(entry => (string?)entry).ToArray();
        foreach (string name in Computed)
        {
            string previous = original.Descendants(name).Single().Value;
            Assert.Equal(previous, plan.Descendants(name).Single().Value);
            Assert.NotEqual(previous, readback.Descendants(name).Single().Value);
            Assert.Contains(name, reported);
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Direct_match_stops_after_full_readback(bool compile)
    {
        string xml = Code("FC", true);
        Assert.True((bool?)Apply(xml, FullExport(xml, false), FullExport(xml, true), "", compile, extra: false)["verificationSuccess"]);
    }

    [Fact]
    public void Compiled_readback_matching_the_existing_declaration_stops_before_the_None_export()
    {
        string declaration = $"""
            <Document><Engineering version="V19"/><SW.Types.PlcStruct ID="0"><AttributeList><Name>MotorType</Name>
            <Interface><Sections xmlns="{Interface}"><Section Name="None"><Member Name="Ready" Datatype="Bool"/><Member Name="Speed" Datatype="Int"/>
            </Section></Sections></Interface></AttributeList></SW.Types.PlcStruct></Document>
            """;
        string candidate = Db(null, null, 0, false);
        var meta = Apply(candidate, FullExport(candidate, false), FullExport(Db(null, null, 0, true), true), "", true, extra: false, declaration: declaration);
        Assert.True((bool?)meta["verificationSuccess"]);
    }

    [Theory, InlineData("FC", true), InlineData("FB", false)]
    public void Worker_matches_SCL_candidate_in_its_export_style(string kind, bool compile)
    {
        string candidate = Code(kind, false), full = Code(kind, true);
        var meta = Apply(candidate, FullExport(full, false), FullExport(full, true), candidate.Replace("UId='12'", "UId='200'"), compile);
        Assert.True((bool?)meta["verificationSuccess"]);
    }

    [Theory, InlineData("9", 0), InlineData("9", 1), InlineData("9", 2), InlineData(null, 0)]
    public void Worker_matches_DB_instance_overrides_and_unexpanded_instances(string? value, int depth)
    {
        string candidate = Db(value, null, depth, false), full = Db(value, null, depth, true);
        Assert.True((bool?)Apply(candidate, FullExport(Db(null, null, depth, true), false), FullExport(full, true), candidate, true)["verificationSuccess"]);
    }

    [Fact]
    public void Worker_matches_an_override_in_a_nested_UDT_instance()
    {
        const string speed = "<Member Name='Speed' Datatype='Int'><StartValue>9</StartValue></Member>";
        string Nested(bool full) => Db("9", null, 0, full).Replace(speed,
            "<Member Name='Drive' Datatype='&quot;DriveType&quot;'><Sections><Section Name='None'>" + speed + "</Section></Sections></Member>");
        Assert.True((bool?)Apply(Nested(false), FullExport(Nested(true), false), FullExport(Nested(true), true), Nested(false), true)["verificationSuccess"]);
    }

    [Theory, InlineData("operator"), InlineData("literal"), InlineData("comment"), InlineData("removedLine"), InlineData("reorderedLine"),
        InlineData("removedMember"), InlineData("reorderedMember")]
    public void Semantic_changes_in_the_None_export_remain_mismatches(string change)
    {
        const string speed = "<Member Name=\"Speed\" Datatype=\"Int\"/>", limit = "<Member Name=\"Limit\" Datatype=\"Int\"/>";
        string Change(string xml) => change switch {
            "operator" => xml.Replace("Text=':='", "Text='='"), "literal" => xml.Replace(">3<", ">4<"),
            "comment" => xml.Replace("> speed<", "> limit<"), "removedLine" => xml.Replace(Comment, ""),
            "reorderedLine" => xml.Replace(Comment + Assignment, Assignment + Comment), "removedMember" => xml.Replace(limit, ""),
            "reorderedMember" => xml.Replace(speed + limit, limit + speed), _ => throw new ArgumentException(change)
        };
        string candidate = Code("FC", false), full = Code("FC", true);
        Assert.False((bool?)Apply(candidate, FullExport(full, false), FullExport(Change(full), true), Change(candidate), false)["verificationSuccess"]);
    }

    [Theory, InlineData("9", null, "4", null), InlineData(null, null, "4", null), InlineData("9", "4", "4", "9")]
    public void Unapplied_removed_or_swapped_DB_values_remain_mismatches(string? want, string? want2, string? kept, string? kept2)
    {
        string full = Db(kept, kept2, 1, true);
        var meta = Apply(Db(want, want2, 1, false), FullExport(full, false), FullExport(full, true), Db(kept, kept2, 1, false), true);
        Assert.False((bool?)meta["verificationSuccess"]);
    }

    [Theory, InlineData("kept"), InlineData("blockReset"), InlineData("memberReset"), InlineData("memberMissing")]
    public void Restored_attributes_must_survive_in_the_full_readback(string change)
    {
        static XElement Writable(XDocument doc) => doc.Descendants(XName.Get("Member", Interface)).Single(m => (string?)m.Attribute("Name") == "Speed")
            .Descendants().Single(e => (string?)e.Attribute("Name") == "ExternalWritable");
        var candidate = XDocument.Parse(FullExport(Code("FB", false), false));
        foreach (string name in Computed) candidate.Descendants(name).Remove();
        candidate.Descendants("HeaderAuthor").Remove();
        Writable(candidate).Remove();
        string full = Code("FB", true);
        var after = XDocument.Parse(FullExport(full, true));
        switch (change)
        {
            case "blockReset": after.Descendants("HeaderAuthor").Single().Value = ""; break;
            case "memberReset": Writable(after).Value = "true"; break;
            case "memberMissing": Writable(after).Remove(); break;
            case "kept": break;
            default: throw new ArgumentException(change);
        }
        var meta = Apply(candidate.ToString(), FullExport(full, false), after.ToString(), candidate.ToString(), false);
        var restored = meta["preservedOmittedBlockAttributes"]!.AsArray().Select(entry => (string)entry!).ToArray();
        Assert.Equal(Computed.Length + 2, restored.Length);
        Assert.Equal(new[] { "HeaderAuthor", "Section:Output/Member:Speed/BooleanAttribute:ExternalWritable" }, restored.Except(Computed).ToArray());
        Assert.Equal(change == "kept", (bool?)meta["verificationSuccess"]);
        if (change != "kept") Assert.Contains(change == "blockReset" ? "HeaderAuthor" : "ExternalWritable", (string)meta["comparison"]!["reason"]!);
    }

    [Theory, InlineData("IsIECCheckEnabled"), InlineData("HeaderAuthor"), InlineData("ExternalWritable"),
        InlineData("MemoryReserve"), InlineData("VirtualPlcSupport")]
    public void Resetting_a_preserved_attribute_in_a_full_export_keeps_the_original_difference(string name)
    {
        string Block(bool full) => name == "MemoryReserve" ? Db("9", null, 0, full) : Code("FB", full);
        string candidate = Block(false), full = Block(true);
        var after = XDocument.Parse(FullExport(full, true));
        var attribute = after.Descendants().First(e => e.Name == name || (string?)e.Attribute("Name") == name);
        attribute.Value = name switch { "MemoryReserve" => "0", "HeaderAuthor" => "", _ => "true" };
        var meta = Apply(candidate, FullExport(full, false), after.ToString(), candidate, false);
        Assert.False((bool?)meta["verificationSuccess"]);
        Assert.Contains(name, (string)meta["comparison"]!["reason"]!);
    }

    [Theory, InlineData("Remanence", "NonRetain", "Retain"), InlineData("Accessibility", "Public", "Private"), InlineData("Informative", "false", "true")]
    public void Only_explicit_member_defaults_constrain_the_full_readback(string name, string value, string changed)
    {
        string Edit(string xml, string setting)
        {
            var doc = XDocument.Parse(xml);
            doc.Descendants(XName.Get("Member", Interface)).First(m => (string?)m.Attribute("Name") == "Speed").SetAttributeValue(name, setting);
            return doc.ToString();
        }
        string none = Code("FB", false), candidate = Edit(none, value), full = Code("FB", true);
        string before = FullExport(full, false), after = FullExport(full, true);
        Assert.True((bool?)Apply(candidate, before, Edit(after, value), none, false)["verificationSuccess"]);
        Assert.True((bool?)Apply(none, before, Edit(after, changed), none, false)["verificationSuccess"]);
        var meta = Apply(candidate, before, Edit(after, changed), none, false);
        Assert.False((bool?)meta["verificationSuccess"]);
        Assert.Contains(name, (string)meta["comparison"]!["reason"]!);
        Assert.False((bool?)Apply(candidate, before, after, none, false)["verificationSuccess"]);
    }

    [Theory, InlineData("Remanence", "NonRetain", "Retain"), InlineData("Accessibility", "Public", "Private"), InlineData("Informative", "false", "true")]
    public void Explicit_member_attributes_follow_the_struct_and_each_nested_instance(string name, string value, string changed)
    {
        string Set(string xml, string? group, string? motor, string? speed, string? secondSpeed)
        {
            var doc = XDocument.Parse(xml);
            XElement Member(XContainer owner, string memberName) => owner.Descendants(XName.Get("Member", Interface))
                .Single(e => (string?)e.Attribute("Name") == memberName);
            var structure = Member(doc, "Group0");
            var first = Member(structure, "Motor");
            structure.SetAttributeValue(name, group);
            first.SetAttributeValue(name, motor);
            Member(first, "Speed").SetAttributeValue(name, speed);
            Member(Member(structure, "Motor2"), "Speed").SetAttributeValue(name, secondSpeed);
            return doc.ToString();
        }

        string sparse = Db("9", "4", 1, false), full = Db("9", "4", 1, true);
        string candidate = Set(sparse, value, value, value, changed), none = Set(sparse, null, null, null, changed);
        string before = FullExport(full, false), after = FullExport(full, true);
        Assert.True((bool?)Apply(candidate, before, Set(after, value, value, value, changed), none, true)["verificationSuccess"]);
        foreach (var (member, readback) in new[] {
            ("Group0", Set(after, changed, value, value, changed)), ("Motor", Set(after, value, changed, value, changed)),
            ("Speed", Set(after, value, value, changed, changed)), ("Speed", Set(after, value, value, value, value)),
            ("Speed", Set(after, value, value, changed, value)) })
        {
            var meta = Apply(candidate, before, readback, none, true);
            Assert.False((bool?)meta["verificationSuccess"]);
            Assert.Contains(member + "/" + name, (string)meta["comparison"]!["reason"]!);
        }
    }

    [Theory, InlineData(4), InlineData(8)]
    public void Restored_layout_offset_is_compared_at_its_own_member(int offset)
    {
        // V15.1 raw-V row 016 records Section:Static/Member:Motor/IntegerAttribute:Offset.
        string candidate = Db("9", null, 0, false), full = Db("9", null, 0, true);
        string WithOffset(bool after, int value)
        {
            var doc = XDocument.Parse(FullExport(full, after));
            XNamespace ns = Interface;
            var motor = doc.Descendants(ns + "Member").Single(e => (string?)e.Attribute("Name") == "Motor");
            motor.Element(ns + "AttributeList")!.Add(new XElement(ns + "IntegerAttribute",
                new XAttribute("Name", "Offset"), new XAttribute("SystemDefined", "true"), value));
            return doc.ToString();
        }
        var meta = Apply(candidate, WithOffset(false, 4), WithOffset(true, offset), candidate, true);
        Assert.Equal(offset == 4, (bool?)meta["verificationSuccess"]);
        if (offset != 4) Assert.Contains("Section:Static/Member:Motor/IntegerAttribute:Offset", (string)meta["comparison"]!["reason"]!);
    }

    [Fact]
    public void Restored_flags_follow_the_named_nested_instance()
    {
        string candidate = Db("9", "4", 1, false);
        string full = Db("9", "4", 1, true);
        string WithFlags(bool after)
        {
            var doc = XDocument.Parse(FullExport(full, after));
            XNamespace ns = Interface;
            foreach (var member in doc.Descendants(ns + "Member").Where(e => e.Element(ns + "StartValue") != null))
            {
                bool writable = (member.Element(ns + "StartValue")!.Value == "9") == after;
                var flag = member.Element(ns + "AttributeList")!.Elements().Single(e => (string?)e.Attribute("Name") == "ExternalWritable");
                flag.Value = writable ? "true" : "false";
            }
            return doc.ToString();
        }
        string before = WithFlags(false), after = WithFlags(true);
        var meta = Apply(candidate, before, after, candidate, false);
        Assert.False((bool?)meta["verificationSuccess"]);
        Assert.Contains("Member:Motor/Section:None/Member:Speed/BooleanAttribute:ExternalWritable", (string)meta["comparison"]!["reason"]!);
    }

    [Fact]
    public void Hand_written_candidate_with_a_different_newline_sequence_stays_a_mismatch()
    {
        string full = Code("FC", true), none = Code("FC", false);
        string candidate = full.Replace("<NewLine UId='21'/>", "<NewLine UId='21'/><NewLine UId='22'/>");
        Assert.False((bool?)Apply(candidate, FullExport(full, false), FullExport(full, true), none, false)["verificationSuccess"]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Cancellation_propagates_from_either_readback(bool supplemental)
    {
        string candidate = Code("FB", false), full = Code("FB", true);
        var canceled = new OperationCanceledException();
        var error = Assert.Throws<OperationCanceledException>(() => Apply(candidate, FullExport(full, false), FullExport(full, true),
            candidate, true, exportFailure: supplemental ? canceled : null, readbackFailure: supplemental ? null : canceled));
        Assert.Same(canceled, error);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Extra_export_failure_retains_the_import_outcome_and_the_first_difference(bool inconsistent)
    {
        string candidate = Code("FB", false), full = Code("FB", true);
        Exception failure = inconsistent ? new PlcSoftwareException("BlockInconsistent", "Block is inconsistent.") : new IOException("Export failed.");
        var meta = Apply(candidate, FullExport(full, false), FullExport(full, true), candidate, true, exportFailure: failure);
        Assert.False((bool?)meta["verificationSuccess"]);
        Assert.Contains(failure.Message, (string)meta["comparison"]!["reason"]!);
    }
}
