using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Logic.V4.Construction
{
    public enum ConstructionProfile { FullEngine, Foundation }

    public sealed class CandidateXml
    {
        public string Xml { get; }
        public string OutputReleaseKey { get; }
        public bool SchemaValidated => false;
        public bool ImportValidated => false;
        public bool ProgramSemanticsValidated => false;
        public string NativeAcceptance => "NOT RUN";
        public string Warning => "Candidate XML only. Target schema, program semantics and native import have not been validated. Not import-ready.";
        internal CandidateXml(string xml, string outputReleaseKey) { Xml = xml; OutputReleaseKey = outputReleaseKey; }
    }

    // In-memory adaptation only. No tool registration, host inference, files or Siemens calls.
    public static class ConstructionAdapter
    {
        public static string ToBuilderJson(ConstructionSpec spec)
        {
            var json = (JsonObject)JsonNode.Parse(V4Json.Serialize(spec))!;
            if (spec is LadFcBlockSpec)
                foreach (var network in (JsonArray)json["networks"]!)
                {
                    var call = network!["call"];
                    network.AsObject().Remove("call");
                    network["callJson"] = call;
                }
            return json.ToJsonString();
        }

        public static CandidateXml BuildCandidate(ConstructionSpec spec, string outputReleaseKey,
            ConstructionProfile profile, bool innerOnly = false)
        {
            ConstructionJson.Require(spec != null, "Construction spec is required.");
            V4Validation.Defined(profile);
            // Both the DTO boundary and programmatic adaptation retain the budgets.
            var json = ToBuilderJson(spec!);
            if (spec is UdtSpec || spec is GlobalDbSpec) PlcDeclarationXmlFormat.ForRelease(outputReleaseKey);
            else ConstructionJson.Require(outputReleaseKey == "21", "Explicit outputReleaseKey 21 required by this builder.");
            ConstructionJson.Require(!innerOnly || spec is StructuredTextSpec, "innerOnly applies only to StructuredText.");
            foreach (var call in Calls(spec!))
                ConstructionJson.Require(call.Parameters.All(p => p.EffectiveSource != CallSourceKind.Local),
                    "The current FlgNet builder has no local source implementation.");
            if (profile == ConstructionProfile.Foundation) FoundationConstructionValidation.Validate(spec!);

            string xml;
            if (profile == ConstructionProfile.Foundation && spec is SclBlockSpec block)
            {
                var inner = BuildCandidate(block.StructuredText, outputReleaseKey, profile, true).Xml;
                var inputs = Members(block.Inputs); var outputs = Members(block.Outputs);
                var doc = block is FbBlockSpec fb
                    ? PlcFbBlockXmlComposer.Compose(block.BlockName, block.BlockNumber, inputs, outputs,
                        Members(fb.Inouts), Members(fb.Statics), Members(fb.Temps), inner,
                        block.CommentZhCn ?? "", block.TitleZhCn ?? "", block.NetworkCommentZhCn ?? "", block.NetworkTitleZhCn ?? "")
                    : PlcFcBlockXmlComposer.Compose(block.BlockName, block.BlockNumber, inputs, outputs, inner,
                        block.CommentZhCn ?? "", block.TitleZhCn ?? "", block.NetworkCommentZhCn ?? "", block.NetworkTitleZhCn ?? "");
                xml = Entitize(doc);
            }
            else if (profile == ConstructionProfile.Foundation && spec is FlgNetCallSpec call)
                xml = Entitize(FlgNetCallXmlBuilder.BuildDocument(call.CallName, Parameters(call)));
            else if (profile == ConstructionProfile.Foundation && spec is LadFcBlockSpec lad)
                xml = Entitize(PlcLadFcBlockXmlComposer.Compose(lad.BlockName, lad.BlockNumber, Members(lad.Inputs), Members(lad.Outputs),
                    lad.Networks.Select(n => new PlcLadFcBlockXmlComposer.LadNetwork(
                        FlgNetCallXmlBuilder.BuildFlgNet(n.Call.CallName, Parameters(n.Call)), n.TitleZhCn ?? "", n.CommentZhCn ?? "")),
                    lad.CommentZhCn ?? "", lad.TitleZhCn ?? ""));
            else
            {
                var result = spec switch
                {
                    UdtSpec _ => PlcBuilderToolJson.BuildUdt(json, outputReleaseKey),
                    GlobalDbSpec _ => PlcBuilderToolJson.BuildGlobalDb(json, outputReleaseKey),
                    PlcTagTableSpec _ => PlcBuilderToolJson.BuildTagTable(json),
                    StructuredTextSpec _ => PlcBuilderToolJson.BuildStructuredText(json, innerOnly),
                    FlgNetCallSpec _ => PlcBuilderToolJson.BuildFlgNetCall(json),
                    FcBlockSpec _ => PlcBuilderToolJson.ComposeFcBlock(json),
                    FbBlockSpec _ => PlcBuilderToolJson.ComposeFbBlock(json),
                    LadFcBlockSpec _ => PlcBuilderToolJson.ComposeLadFcBlock(json),
                    _ => throw new ArgumentException("Unsupported construction type.")
                };
                ConstructionJson.Require(result["ok"]!.GetValue<bool>(), "The builder did not produce well-formed XML.");
                xml = result["xml"]!.GetValue<string>();
            }
            VerifyOutput(xml, innerOnly);
            return new CandidateXml(xml, outputReleaseKey);
        }

        internal static IEnumerable<FlgNetCallSpec> Calls(ConstructionSpec spec) => spec is FlgNetCallSpec call
            ? new[] { call } : spec is LadFcBlockSpec lad ? lad.Networks.Select(n => n.Call) : Enumerable.Empty<FlgNetCallSpec>();

        private static PlcBlockMemberDefinition[] Members(IReadOnlyList<Member>? members) =>
            (members ?? Array.Empty<Member>()).Select(m => new PlcBlockMemberDefinition(m.Name, m.Datatype, m.EffectiveComment)).ToArray();

        private static FlgNetCallParameter[] Parameters(FlgNetCallSpec call) => call.Parameters.Select(p =>
            p.EffectiveSource == CallSourceKind.Constant
                ? FlgNetCallParameter.Constant(p.Name, p.Section, p.DataType, p.ConstantValue!)
                : FlgNetCallParameter.Global(p.Name, p.Section, p.DataType, p.SymbolPath!.ToArray())).ToArray();

        private static string Entitize(XDocument document)
        {
            using var buffer = new Utf8Writer();
            using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, NewLineHandling = NewLineHandling.Entitize }))
                document.Save(writer);
            return buffer.ToString();
        }

        internal static void VerifyOutput(string xml, bool innerOnly)
        {
            ConstructionJson.Limit(xml.Length, ConstructionJson.MaxXmlCharacters);
            var content = innerOnly ? "<StructuredText xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4\">" + xml + "</StructuredText>" : xml;
            using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = ConstructionJson.MaxXmlCharacters + (innerOnly ? 256 : 0)
            });
            XDocument.Load(reader);
        }

        private sealed class Utf8Writer : StringWriter { public override Encoding Encoding => Encoding.UTF8; }
    }
}
