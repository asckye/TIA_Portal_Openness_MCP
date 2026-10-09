using System;
using System.Linq;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.SW.Tags;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        public HardwareAddressingReply ManagePlcTagDefinition(PlcSoftwareRequest request)
            => _session.RunHmiStepTool("ManagePlcTagDefinition", meta => {
                if (request.ValidationError.Length != 0) throw new ArgumentException(request.ValidationError);
                string softwarePath = request.SoftwarePath, tablePath = request.Path, name = request.Name, kind = request.Kind, action = request.Action, dataType = request.DataType, addressOrValue = request.AddressOrValue;
                bool dryRun = request.DryRun;
                bool writing=action != "read" && !dryRun;
                using var access=writing ? _session.AcquireHmiEditAccess() : null;
                var plc=_session.ExactPlcForEngineering(softwarePath,writing);
                var parts=PlcGroupOperations.Parts(tablePath);
                var group=PlcGroupOperations.Group(plc.TagTableGroup,string.Join("/",parts.Take(parts.Length-1)));
                var table=(PlcTagTable)(PlcGroupOperations.Find(PlcGroupOperations.Get(group,"TagTables"),parts.Last()) ?? throw new InvalidOperationException("Exact PLC tag table not found."));
                object collection=kind=="tag" ? (object)table.Tags : table.UserConstants;
                var target=PlcGroupOperations.Find(collection,name);
                if((action=="create")== (target!=null)) throw new InvalidOperationException(action=="create" ? "Definition exists." : "Exact definition not found.");
                var nativeType=kind=="tag" ? typeof(PlcTag) : typeof(PlcUserConstant);
                var prepared=HardwareAddressingPolicy.Prepare(nativeType,request.Properties);
                meta["dryRun"]=dryRun; meta["mayHaveChanged"]=false; meta["tablePath"]=tablePath; meta["name"]=name; meta["kind"]=kind;
                if(target!=null) { meta["before"]=HardwareScalarEvidence.Read(target); meta["commentBefore"]=CommentItems(TagComment(target)); }
                // Comment is a MultilingualText - resolve the cultures now so a preview already reports a missing culture.
                var editingCulture=(_session.CurrentProject as Project)?.LanguageSettings?.EditingLanguage?.Culture?.Name;
                if(request.Comments.Length>0) { meta["commentCultures"]=new List<object?>(request.Comments.Select(c => (object?)(c.Culture ?? editingCulture ?? "(first item)"))); }
                if(target!=null) foreach(var comment in request.Comments) ResolveCommentItem(TagComment(target),comment.Culture,editingCulture);
                if(!writing) return "PLC tag/constant read or preview. Native field validation occurs on execution; no modification.";
                meta["mayHaveChanged"]=true; MutationStarted = true;
                if(action=="create") target=kind=="tag" ? (object)table.Tags.Create(name,dataType,addressOrValue) : table.UserConstants.Create(name,dataType,addressOrValue);
                if(action=="delete") {
                    PlcGroupOperations.Call(target!,"Delete",Type.EmptyTypes);
                    if(PlcGroupOperations.Find(collection,name)!=null) throw new InvalidOperationException("Definition remains after delete.");
                    meta["verifiedAbsent"]=true;
                } else {
                    HardwareScalarEvidence.Apply(target!,prepared,meta);
                    foreach(var comment in request.Comments) {
                        var culture = comment.Culture; var text = comment.Text;
                        var item=ResolveCommentItem(TagComment(target!),culture,editingCulture);
                        meta["lastAttemptedProperty"]="Comment["+(item.Language?.Culture?.Name ?? "?")+"]";
                        item.Text=text;
                        if(item.Text!=text) throw new InvalidOperationException("Comment readback differs for culture "+(item.Language?.Culture?.Name ?? culture)+".");
                    }
                    meta["after"]=HardwareScalarEvidence.Read(target!); meta["commentAfter"]=CommentItems(TagComment(target!));
                }
                return "Native PLC tag/constant operation completed and read back. No save/compile/download or runtime value write.";
            });

        private static MultilingualText TagComment(object target)
            => target is PlcTag tag ? tag.Comment : target is PlcUserConstant constant ? constant.Comment : throw new InvalidOperationException("Unexpected definition type: "+target.GetType().FullName);

        private static Dictionary<string, object?> CommentItems(MultilingualText text)
        {
            var items=new Dictionary<string, object?>();
            foreach(var item in PlcGroupOperations.Items(text.Items).Cast<MultilingualTextItem>()) items[item.Language?.Culture?.Name ?? "?"]=item.Text;
            return items;
        }

        private static MultilingualTextItem ResolveCommentItem(MultilingualText text,string? culture,string? editingCulture)
        {
            var items=PlcGroupOperations.Items(text.Items).Cast<MultilingualTextItem>().ToList();
            var wanted=culture ?? editingCulture;
            var item=wanted==null ? items.FirstOrDefault() : items.FirstOrDefault(i => string.Equals(i.Language?.Culture?.Name,wanted,StringComparison.OrdinalIgnoreCase));
            return item ?? throw new PlcSoftwareException("NotFound","Comment culture not active in the project: "+(wanted ?? "(none)")+" (active: "+string.Join(", ",items.Select(i => i.Language?.Culture?.Name))+"). Activate it with ManageProjectLanguage or pass Comment as {\"<active culture>\": \"text\"}.");
        }
    }
}
