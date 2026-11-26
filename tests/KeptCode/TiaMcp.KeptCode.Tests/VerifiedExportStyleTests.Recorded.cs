using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed partial class VerifiedExportStyleTests
{
    // Fixtures/VerifiedImport/README.md identifies the staged XML and evidence rows for each reconstruction.
    private static string Staged(string file)
    {
        using var stream = typeof(VerifiedExportStyleTests).Assembly.GetManifestResourceStream("VerifiedImport." + file + ".xml");
        using var reader = new StreamReader(stream ?? throw new FileNotFoundException(file));
        return reader.ReadToEnd();
    }

    private static XElement Attributes(XDocument document) => PlcDocumentEditing.Block(document).Element("AttributeList")!;

    private static XDocument WithComputed(XDocument source, bool after)
    {
        var full = new XDocument(source);
        var attributes = Attributes(full);
        foreach (var name in Computed)
            attributes.Add(new XElement(name, name.EndsWith("Length", StringComparison.Ordinal)
                ? (after ? "256" : "240") : (after ? "2026-11-25T12:01:00Z" : "2026-11-25T12:00:00Z")));
        foreach (var member in attributes.Descendants().Where(PlcDocumentEditing.IsInterfaceMember))
        {
            XNamespace ns = member.Name.Namespace;
            member.SetAttributeValue("Remanence", "NonRetain");
            member.AddFirst(new XElement(ns + "AttributeList", new XElement(ns + "BooleanAttribute",
                new XAttribute("Name", "ExternalWritable"), new XAttribute("SystemDefined", "true"), "true")));
        }
        return full;
    }

    private static XElement TextField(string field, int id, string value = "") =>
        new XElement("MultilingualText", new XAttribute("ID", id), new XAttribute("CompositionName", field),
            new XElement("ObjectList", new XElement("MultilingualTextItem", new XAttribute("ID", id + 1), new XAttribute("CompositionName", "Items"),
                new XElement("AttributeList", new XElement("Culture", "zh-CN"), new XElement("Text", value)))));

    private static void AddExportText(XDocument export)
    {
        var block = PlcDocumentEditing.Block(export);
        var list = block.Element("ObjectList");
        if (list == null) { list = new XElement("ObjectList"); block.Add(list); }
        list.AddFirst(TextField("Comment", 2));
        foreach (var unit in list.Elements("SW.Blocks.CompileUnit"))
            unit.Add(new XElement("ObjectList", TextField("Comment", 4), TextField("Title", 6)));
        list.Add(TextField("Title", 8));
    }

    [Theory]
    [InlineData("14sp1-MCP_RC_FCmin", null)]
    [InlineData("15.1-N4", "False")]
    [InlineData("16-T3", "false")]
    [InlineData("17-MCP_RC_FC5", "false")]
    [InlineData("18-T3", "false")]
    [InlineData("19-T3", "false")]
    [InlineData("20-T3", "false")]
    [InlineData("21-T3", "false")]
    public void Staged_LAD_network_matches_on_the_full_export(string file, string? setEno)
    {
        string candidate = Staged(file);
        var exported = XDocument.Parse(candidate);
        if (setEno != null) Attributes(exported).Add(new XElement("SetENOAutomatically", setEno));
        AddExportText(exported);
        var meta = Apply(candidate, WithComputed(exported, false).ToString(), WithComputed(exported, true).ToString(), exported.ToString(),
            true, extra: false);
        Assert.True((bool?)meta["verificationSuccess"]);
    }

    private static XDocument DbNone(string candidate, bool addNamespace = false)
    {
        var none = XDocument.Parse(candidate);
        Attributes(none).Add(new XElement("MemoryReserve", "100"));
        if (addNamespace) Attributes(none).Add(new XElement("Namespace"));
        AddExportText(none);
        return none;
    }

    private static XElement Member(XContainer root, string name) =>
        root.Descendants().Single(e => e.Name.LocalName == "Member" && (string?)e.Attribute("Name") == name);

    [Theory]
    [InlineData("14sp1-MCP_RC_DB2_chg", "9", false)]
    [InlineData("16-T1", "8", true)]
    [InlineData("17-T1", "8", true)]
    [InlineData("18-T1", "8", true)]
    [InlineData("19-T1", "8", true)]
    [InlineData("21-T1", "8", true)]
    public void Staged_partial_instance_keeps_its_only_override(string file, string previousSpeed, bool nested)
    {
        string candidate = Staged(file);
        var none = DbNone(candidate);
        var full = new XDocument(none);
        var speed = Member(full, "Speed");
        speed.AddBeforeSelf(new XElement(speed.Name, new XAttribute("Name", "Run"), new XAttribute("Datatype", "Bool")));
        if (nested) speed.AddAfterSelf(new XElement(speed.Name, new XAttribute("Name", "In"), new XAttribute("Datatype", "\"MCP_RC_UDTI\""),
            Section(speed.Name.Namespace, NestedMembers)));
        var before = WithComputed(full, false);
        Member(before, "Speed").Element(speed.Name.Namespace + "StartValue")!.Value = previousSpeed;
        var meta = Apply(candidate, before.ToString(), WithComputed(full, true).ToString(), none.ToString(), true);
        Assert.True((bool?)meta["verificationSuccess"]);
    }

    private const string NestedMembers = """
        <Member Name="Lim" Datatype="Int"/><Member Name="On" Datatype="Bool"/>
        """;

    private static XElement Section(XNamespace ns, string members) =>
        XElement.Parse($"<Sections xmlns='{ns}'><Section Name='None'>{members}</Section></Sections>");

    private static string UdtMembers(string name) => name == "MCP_RC_UDTD"
        ? "<Member Name='Run' Datatype='Bool'/><Member Name='Lim' Datatype='Int'><StartValue>7</StartValue></Member>"
        : "<Member Name='Run' Datatype='Bool'/><Member Name='Speed' Datatype='Int'><StartValue>5</StartValue></Member>" +
            "<Member Name='In' Datatype='&quot;MCP_RC_UDTI&quot;'/>";

    private static string Declaration(string name, XElement sections) =>
        new XDocument(new XElement("Document", new XElement("SW.Types.PlcStruct", new XAttribute("ID", 0),
            new XElement("AttributeList", new XElement("Name", name), new XElement("Interface", sections))))).ToString();

    [Theory]
    [InlineData("17-T2a", "MCP_RC_UDT", false)]
    [InlineData("17-T2b", "MCP_RC_UDT", false)]
    [InlineData("19-T2a", "MCP_RC_UDT", true)]
    [InlineData("19-T2b", "MCP_RC_UDT", false)]
    [InlineData("20-MCP_RC_DB5", "MCP_RC_UDTD", false)]
    [InlineData("21-T2a", "MCP_RC_UDT", false)]
    public void Staged_unexpanded_instance_with_a_declared_default_matches_on_None(string file, string type, bool addNamespace)
    {
        string candidate = Staged(file);
        var none = DbNone(candidate, addNamespace);
        var full = new XDocument(none);
        var instances = full.Descendants().Where(e => (string?)e.Attribute("Datatype") == "\"" + type + "\"").ToArray();
        var declaration = Section(instances.First().Name.Namespace, UdtMembers(type));
        foreach (var instance in instances)
        {
            var expansion = new XElement(declaration);
            expansion.Descendants(instance.Name.Namespace + "StartValue").Remove();
            if (type == "MCP_RC_UDT") Member(expansion, "In").Add(Section(instance.Name.Namespace, NestedMembers));
            instance.Add(expansion);
        }
        var before = WithComputed(full, false);
        if (instances.Any(e => (string?)e.Attribute("Name") == "B"))
        {
            var limit = Member(Member(before, "B"), "Lim");
            limit.Add(new XElement(limit.Name.Namespace + "StartValue", "4"));
        }
        var meta = Apply(candidate, before.ToString(), WithComputed(full, true).ToString(), none.ToString(), true,
            declaration: Declaration(type, declaration), declarationName: type);
        Assert.True((bool?)meta["verificationSuccess"]);
    }

    [Theory]
    [InlineData("differentNone")]
    [InlineData("notRestored")]
    [InlineData("differentFull")]
    [InlineData("missingFull")]
    [InlineData("duplicateFull")]
    [InlineData("duplicateNone")]
    [InlineData("nestedValue")]
    [InlineData("otherNamespace")]
    [InlineData("differentCase")]
    [InlineData("extraAttribute")]
    [InlineData("candidateStated")]
    [InlineData("memberAttribute")]
    [InlineData("blockComment")]
    public void None_requires_the_exact_restored_block_scalar_and_full_readback(string change)
    {
        var candidate = XDocument.Parse(Staged("17-T1"));
        var none = DbNone(candidate.ToString());
        var before = WithComputed(none, false);
        var after = WithComputed(none, true);
        var speed = Member(after, "Speed");
        speed.AddBeforeSelf(new XElement(speed.Name, new XAttribute("Name", "Run"), new XAttribute("Datatype", "Bool")));
        var reserve = Attributes(none).Element("MemoryReserve")!;
        switch (change)
        {
            case "differentNone": reserve.Value = "0100"; break;
            case "notRestored": Attributes(before).Element("MemoryReserve")!.Remove(); break;
            case "differentFull": Attributes(after).Element("MemoryReserve")!.Value = "101"; break;
            case "missingFull": Attributes(after).Element("MemoryReserve")!.Remove(); break;
            case "duplicateFull": Attributes(after).Add(new XElement(reserve)); break;
            case "duplicateNone": reserve.AddAfterSelf(new XElement(reserve)); break;
            case "nestedValue": reserve.ReplaceNodes(new XElement("Value", "100")); break;
            case "otherNamespace": reserve.Name = XName.Get("MemoryReserve", "urn:extension"); break;
            case "differentCase":
                Attributes(before).Add(new XElement("HeaderFamily", "Motion"));
                Attributes(after).Add(new XElement("HeaderFamily", "Motion"));
                Attributes(none).Add(new XElement("HeaderFamily", "motion"));
                break;
            case "extraAttribute": reserve.SetAttributeValue("Unit", "bytes"); break;
            case "candidateStated": Attributes(candidate).Add(new XElement("MemoryReserve", "10")); break;
            case "memberAttribute":
                Member(none, "Speed").AddFirst(new XElement(Member(before, "Speed").Element(speed.Name.Namespace + "AttributeList")!));
                break;
            case "blockComment": none.Descendants("Text").First().Value = "Operator note"; break;
            default: throw new ArgumentException(change);
        }
        var meta = Apply(candidate.ToString(), before.ToString(), after.ToString(), none.ToString(), true);
        Assert.False((bool?)meta["verificationSuccess"]);
        Assert.Contains(change is "differentFull" or "missingFull" or "duplicateFull" ? "Restored attribute differs" : "Candidate differs",
            (string)meta["comparison"]!["reason"]!);
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("nestedOverride", false)]
    [InlineData("unexpandedZero", false)]
    [InlineData("declaredFive", true)]
    [InlineData("explicitZero", false)]
    public void None_must_leave_each_unexpanded_instance_free_of_overrides(string change, bool matches)
    {
        const string type = "MCP_RC_UDT";
        var candidate = XDocument.Parse(Staged("17-T2b"));
        XNamespace ns = Member(candidate, "B").Name.Namespace;
        bool stated = change is "declaredFive" or "explicitZero";
        if (stated) Member(candidate, "B").Add(Section(ns, "<Member Name='Speed' Datatype='Int'><StartValue>5</StartValue></Member>"));
        var none = DbNone(candidate.ToString());
        var before = WithComputed(none, false);
        var after = new XDocument(none);
        var declared = Section(ns, UdtMembers(type));
        if (stated) Member(after, "B").Element(ns + "Sections")!.Remove();
        foreach (string name in new[] { "A", "B" }) Member(after, name).Add(new XElement(declared));
        if (!stated) Attributes(after).Add(new XElement("HeaderAuthor", "Commissioning"));
        if (change == "nestedOverride")
            Member(none, "B").Add(Section(ns, "<Member Name='In' Datatype='&quot;MCP_RC_UDTI&quot;'><Sections><Section Name='None'>" +
                "<Member Name='Lim' Datatype='Int'><StartValue>4</StartValue></Member></Section></Sections></Member>"));
        if (change == "unexpandedZero")
            Member(none, "B").Add(Section(ns, "<Member Name='Speed' Datatype='Int'><StartValue>0</StartValue></Member>"));
        if (change == "explicitZero")
        {
            Member(Member(after, "B"), "Speed").Element(ns + "StartValue")!.Value = "0";
            Member(none, "Speed").Element(ns + "StartValue")!.Value = "0";
        }
        string? changed = change == "explicitZero" ? "Static/B/Speed: startValue" : null;
        var meta = Apply(candidate.ToString(), before.ToString(), WithComputed(after, true).ToString(), none.ToString(), true,
            declaration: Declaration(type, declared), declarationName: type, expectedDifference: changed);
        Assert.Equal(matches, (bool?)meta["verificationSuccess"]);
        if (stated) Assert.Equal(changed, (string?)meta["comparison"]!["noneDifference"]);
    }

    [Theory]
    [InlineData("emptyAdded", true)]
    [InlineData("nonemptyTitle", false)]
    [InlineData("whitespaceTitle", false)]
    [InlineData("nonemptyComment", false)]
    [InlineData("secondCulture", false)]
    [InlineData("duplicateTitle", false)]
    [InlineData("wrongNetwork", false)]
    [InlineData("twoNetworks", true)]
    [InlineData("differentCount", false)]
    [InlineData("missingPlannedTitle", false)]
    [InlineData("listAttribute", false)]
    [InlineData("anotherChild", false)]
    [InlineData("bareList", false)]
    public void Network_text_is_checked_per_unit_in_both_export_styles(string change, bool matches)
    {
        var candidate = XDocument.Parse(Staged("17-MCP_RC_FC5"));
        XElement Unit(XDocument doc) => PlcDocumentEditing.Networks(doc).First();
        if (change is "wrongNetwork" or "twoNetworks")
        {
            var second = new XElement(Unit(candidate));
            second.SetAttributeValue("ID", 12);
            Unit(candidate).AddAfterSelf(second);
        }
        if (change is "wrongNetwork" or "twoNetworks" or "missingPlannedTitle") Unit(candidate).Add(new XElement("ObjectList", TextField("Title", 4)));
        var none = new XDocument(candidate);
        Unit(none).Element("ObjectList")?.Remove();
        var list = new XElement("ObjectList", TextField("Comment", 2), TextField("Title", 4));
        Unit(none).Add(list);
        switch (change)
        {
            case "nonemptyTitle": list.Descendants("Text").Last().Value = "Network"; break;
            case "whitespaceTitle": list.Descendants("Text").Last().Value = " "; break;
            case "nonemptyComment": list.Descendants("Text").First().Value = "Note"; break;
            case "secondCulture":
                var item = new XElement(list.Descendants("MultilingualTextItem").First());
                item.SetAttributeValue("ID", 10);
                item.Descendants("Culture").Single().Value = "en-US";
                item.Descendants("Text").Single().Value = "Note";
                list.Descendants("MultilingualTextItem").First().AddAfterSelf(item);
                break;
            case "duplicateTitle": list.Add(TextField("Title", 6)); break;
            case "twoNetworks": PlcDocumentEditing.Networks(none).Last().Add(new XElement("ObjectList", TextField("Title", 6))); break;
            case "wrongNetwork":
                var title = list.Elements().Last();
                title.Remove();
                PlcDocumentEditing.Networks(none).Last().Add(new XElement("ObjectList", title));
                break;
            case "differentCount":
                var second = new XElement(Unit(candidate));
                second.SetAttributeValue("ID", 12);
                Unit(none).AddAfterSelf(second);
                break;
            case "missingPlannedTitle": list.Elements().Last().Remove(); break;
            case "listAttribute": list.SetAttributeValue("Extension", "keep"); break;
            case "anotherChild": list.Add(new XElement("Other")); break;
            case "bareList": list.RemoveNodes(); break;
        }
        Assert.Equal(matches, PlcDocumentEditing.Canonical(candidate, candidate) == PlcDocumentEditing.Canonical(none, candidate));
        if (change == "differentCount")
            Assert.Equal(2, XElement.Parse(PlcDocumentEditing.Canonical(none, candidate)).Descendants("MultilingualText").Count());
        Attributes(none).Add(new XElement("SetENOAutomatically", "false"));
        var before = WithComputed(none, false);
        var after = WithComputed(none, true);
        Attributes(after).Add(new XElement("HeaderAuthor", "Commissioning"));
        var meta = Apply(candidate.ToString(), before.ToString(), after.ToString(), none.ToString(), true);
        Assert.Equal(matches, (bool?)meta["verificationSuccess"]);
    }

    [Fact]
    public void Rounded_real_value_in_the_recorded_partial_instance_stays_a_mismatch()
    {
        // V15.1 rows 99-103 patch the None export of the staged DBB. Rows 107-109 give the two readback shapes.
        var before = DbNone(Staged("15.1-MCP_RC_R5DBB"));
        var candidate = new XDocument(before);
        Member(candidate, "Run").Remove();
        Member(candidate, "Count").Remove();
        Member(candidate, "Level").Elements().Single().Value = "8";
        Member(candidate, "Speed").Elements().Single().Value = "1.23456789012345";
        var none = new XDocument(candidate);
        Member(none, "Speed").Elements().Single().Value = "1.234568";
        var full = new XDocument(before);
        Member(full, "Speed").Elements().Single().Value = "1.234568";
        Member(full, "Level").Elements().Single().Value = "8";
        var meta = Apply(candidate.ToString(), WithComputed(before, false).ToString(), WithComputed(full, true).ToString(), none.ToString(), true,
            expectedDifference: "Static/Motor/Speed: startValue");
        Assert.False((bool?)meta["verificationSuccess"]);
        Assert.Equal("Static/Motor/Speed: startValue", (string?)meta["comparison"]!["noneDifference"]);
    }

    [Fact]
    public void Restored_empty_namespace_must_be_present_in_the_full_export()
    {
        string candidate = Staged("19-T2a");
        var none = DbNone(candidate, addNamespace: true);
        var before = WithComputed(none, false);
        var after = WithComputed(none, true);
        Attributes(after).Element("Namespace")!.Remove();
        var meta = Apply(candidate, before.ToString(), after.ToString(), none.ToString(), true);
        Assert.False((bool?)meta["verificationSuccess"]);
        Assert.Equal("Restored attribute differs in after.xml: Namespace", (string?)meta["comparison"]!["reason"]);
    }

    [Fact]
    public void V17_T5_reports_the_rounded_Val_after_declaration_normalization()
    {
        string candidate = Staged("17-T5");
        var none = DbNone(candidate);
        Member(none, "Val").Elements().Single().Value = "1.5";
        var full = new XDocument(none);
        var motor = Member(full, "Motor");
        const string type = "MCP_RC_UDT";
        var declared = Section(motor.Name.Namespace, UdtMembers(type));
        var expansion = new XElement(declared);
        Member(expansion, "Speed").Elements().Remove();
        Member(expansion, "In").Add(Section(motor.Name.Namespace, NestedMembers));
        motor.Add(expansion);
        var meta = Apply(candidate, WithComputed(full, false).ToString(), WithComputed(full, true).ToString(), none.ToString(), true,
            declaration: Declaration(type, declared), declarationName: type, expectedDifference: "Static/Val: startValue");
        Assert.False((bool?)meta["verificationSuccess"]);
        Assert.Equal("Static/Val: startValue", (string?)meta["comparison"]!["noneDifference"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declaration_match_requires_None_to_verify_the_reset_of_a_nested_override(bool kept)
    {
        string candidate = Staged("17-T2b");
        var none = DbNone(candidate);
        XNamespace ns = Member(none, "B").Name.Namespace;
        const string type = "MCP_RC_UDT";
        var declared = Section(ns, UdtMembers(type));
        var full = new XDocument(none);
        foreach (string name in new[] { "A", "B" })
        {
            Member(full, name).Add(new XElement(declared));
            Member(Member(full, name), "In").Add(Section(ns, NestedMembers));
        }
        var oldLimit = Member(Member(full, "B"), "Lim");
        oldLimit.Add(new XElement(ns + "StartValue", "4"));
        var before = WithComputed(full, false);
        if (kept)
            Member(none, "B").Add(Section(ns, "<Member Name='In' Datatype='&quot;MCP_RC_UDTI&quot;'><Sections><Section Name='None'>" +
                "<Member Name='Lim' Datatype='Int'><StartValue>4</StartValue></Member></Section></Sections></Member>"));
        else oldLimit.Elements().Remove();
        var meta = Apply(candidate, before.ToString(), WithComputed(full, true).ToString(), none.ToString(), true,
            declaration: Declaration(type, declared), declarationName: type, expectedDifference: kept ? "Static/B: structure" : null);
        Assert.Equal(!kept, (bool?)meta["verificationSuccess"]);
        Assert.Equal((string?)meta["plannedFingerprint"], (string?)meta["actualFingerprint"]);
    }

    [Theory, InlineData("wrongBlock"), InlineData("struct"), InlineData("array"), InlineData("noNone")]
    [InlineData("structOverride"), InlineData("arrayOverride")]
    public void Compiled_full_match_checks_the_available_None_readback(string scenario)
    {
        const string type = "MCP_RC_UDT";
        var candidate = XDocument.Parse(Staged("17-T2a"));
        var motor = Member(candidate, "Motor");
        var ns = motor.Name.Namespace;
        bool nested = scenario is "struct" or "structOverride";
        if (nested)
            motor.ReplaceWith(new XElement(motor.Name, new XAttribute("Name", "Group"), new XAttribute("Datatype", "Struct"), new XElement(motor)));
        if (scenario is "array" or "arrayOverride")
            motor.AddBeforeSelf(new XElement(motor.Name, new XAttribute("Name", "Drives"), new XAttribute("Datatype", "Array[0..1] of \"Drive\"")));
        var none = DbNone(candidate.ToString());
        var full = new XDocument(none);
        var declared = Section(ns, UdtMembers(type));
        Member(full, "Motor").Add(new XElement(declared));
        string? difference = null;
        if (scenario == "wrongBlock")
        {
            Attributes(none).Element("Name")!.Value = "Other";
            difference = "Name: blockAttribute";
        }
        if (scenario is "structOverride" or "arrayOverride")
        {
            Member(none, "Motor").Add(new XElement(declared));
            difference = nested ? "Static/Group/Motor: structure" : "Static/Motor: structure";
        }
        var meta = Apply(candidate.ToString(), WithComputed(full, false).ToString(), WithComputed(full, true).ToString(), none.ToString(), true,
            declaration: Declaration(type, declared), declarationName: type, extra: scenario != "noNone", supportsNone: scenario != "noNone",
            expectedDifference: difference);
        Assert.Equal(difference == null, (bool?)meta["verificationSuccess"]);
        Assert.Equal((string?)meta["plannedFingerprint"], (string?)meta["actualFingerprint"]);
    }

    [Theory, InlineData("fullSetting"), InlineData("missing"), InlineData("duplicate"), InlineData("type"), InlineData("moved"),
        InlineData("io"), InlineData("cancel"), InlineData("lost")]
    public void Supplemental_proof_checks_the_instance_in_a_full_style_candidate(string change)
    {
        const string type = "MCP_RC_UDT";
        var candidate = XDocument.Parse(Staged("17-T2a"));
        // The full export verifies this setting. Its omission in None cannot establish or refute a UDT reset.
        Attributes(candidate).Add(new XElement("HeaderAuthor", "Motion"));
        var none = DbNone(candidate.ToString());
        var full = new XDocument(none);
        var motor = Member(full, "Motor");
        var declared = Section(motor.Name.Namespace, UdtMembers(type));
        motor.Add(new XElement(declared));
        Attributes(none).Element("HeaderAuthor")!.Remove();
        if (change == "missing") Member(none, "Motor").Remove();
        if (change == "duplicate") Member(none, "Motor").AddAfterSelf(new XElement(Member(none, "Motor")));
        if (change == "type") Member(none, "Motor").SetAttributeValue("Datatype", "\"OtherType\"");
        if (change == "moved") Member(none, "Motor").ReplaceWith(new XElement(motor.Name, new XAttribute("Name", "Group"),
            new XAttribute("Datatype", "Struct"), new XElement(Member(none, "Motor"))));
        Exception? failure = change switch {
            "io" => new IOException("Cannot read supplemental export."), "cancel" => new OperationCanceledException(),
            "lost" => new ObjectDisposedException("Session"), _ => null };
        string? difference = change switch {
            "io" => "Same-style verification failed: Cannot read supplemental export.",
            "missing" or "duplicate" or "moved" => "Static/Motor: structure", "type" => "Static/Motor: type", _ => null };
        JsonObject Run() => Apply(candidate.ToString(), WithComputed(full, false).ToString(), WithComputed(full, true).ToString(),
            none.ToString(), true, declaration: Declaration(type, declared), declarationName: type, exportFailure: failure, expectedDifference: difference);
        if (change == "cancel") Assert.Same(failure, Assert.Throws<OperationCanceledException>(() => Run()));
        else if (change == "lost") Assert.Same(failure, Assert.Throws<ObjectDisposedException>(() => Run()));
        else Assert.Equal(change == "fullSetting", (bool?)Run()["verificationSuccess"]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void None_difference_is_evidence_when_the_full_difference_keeps_precedence(bool extraOutsideUdt)
    {
        var candidate = XDocument.Parse(Db("9", null, 1, false));
        var none = XDocument.Parse(Db("4", null, 1, false));
        string full = Db("4", null, 1, true);
        var after = XDocument.Parse(FullExport(full, true));
        if (extraOutsideUdt)
        {
            var group = Member(after, "Group0");
            group.AddBeforeSelf(new XElement(group.Name, new XAttribute("Name", "Extra"), new XAttribute("Datatype", "Int")));
        }
        else
        {
            foreach (var doc in new[] { candidate, none })
                Member(doc, "Group0").AddFirst(new XElement(XName.Get("AttributeList", Interface)));
        }
        var meta = Apply(candidate.ToString(), FullExport(full, false), after.ToString(), none.ToString(), true);
        Assert.False((bool?)meta["verificationSuccess"]);
        Assert.Equal("Static/Group0/Motor/Speed: startValue", (string?)meta["comparison"]!["noneDifference"]);
        Assert.Equal(extraOutsideUdt ? "Static/Extra: extraMember" : "Static/Group0/Motor/Ready: extraMember", (string?)meta["readbackDifference"]);
    }
}
