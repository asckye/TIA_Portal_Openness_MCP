using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.SW.Tags;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ManagePlcTagDefinition(string softwarePath, string tablePath, string name, string kind, string action, string dataType="", string addressOrValue="", string propertiesJson="{}", bool dryRun=true)
            => RunHmiStepTool("ManagePlcTagDefinition", meta => {
                var request=PlcTagEditingLogic.Validate(action,kind,name,dataType,addressOrValue,propertiesJson,dryRun);
                bool writing=request.Writes;
                using var access=writing ? AcquireHmiEditAccess() : null;
                var plc=ExactPlcForEngineering(softwarePath,writing);
                var parts=EngineeringGroupOperations.Parts(tablePath);
                var group=EngineeringGroupOperations.Group(plc.TagTableGroup,string.Join("/",parts.Take(parts.Length-1)));
                var table=(PlcTagTable)(EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(group,"TagTables"),parts.Last()) ?? throw new InvalidOperationException("Exact PLC tag table not found."));
                object collection=kind=="tag" ? (object)table.Tags : table.UserConstants;
                var target=EngineeringGroupOperations.Find(collection,name);
                if((action=="create")== (target!=null)) throw new InvalidOperationException(action=="create" ? "Definition exists." : "Exact definition not found.");
                var nativeType=kind=="tag" ? typeof(PlcTag) : typeof(PlcUserConstant);
                var prepared=EngineeringScalarProperties.Prepare(nativeType,request.Scalars);
                meta["dryRun"]=dryRun; meta["mayHaveChanged"]=false; meta["tablePath"]=tablePath; meta["name"]=name; meta["kind"]=kind;
                if(target!=null) { meta["before"]=EngineeringScalarProperties.Read(target); meta["commentBefore"]=CommentItems(TagComment(target)); }
                // 2.7.46: Comment is a MultilingualText - resolve the cultures now so a preview already reports a missing culture.
                var editingCulture=(_project as Project)?.LanguageSettings?.EditingLanguage?.Culture?.Name;
                if(request.Comments.Length>0) { meta["commentCultures"]=new JsonArray(request.Comments.Select(c => JsonValue.Create(c.Culture ?? editingCulture ?? "(first item)")).ToArray()); }
                if(target!=null) foreach(var (culture,_) in request.Comments) ResolveCommentItem(TagComment(target),culture,editingCulture);
                if(!writing) return "PLC tag/constant read or preview. Native field validation occurs on execution; no modification.";
                meta["mayHaveChanged"]=true;
                if(action=="create") target=kind=="tag" ? (object)table.Tags.Create(name,dataType,addressOrValue) : table.UserConstants.Create(name,dataType,addressOrValue);
                if(action=="delete") {
                    EngineeringGroupOperations.Call(target!,"Delete",Type.EmptyTypes);
                    if(EngineeringGroupOperations.Find(collection,name)!=null) throw new InvalidOperationException("Definition remains after delete.");
                    meta["verifiedAbsent"]=true;
                } else {
                    EngineeringScalarProperties.Apply(target!,prepared,meta);
                    foreach(var (culture,text) in request.Comments) {
                        var item=ResolveCommentItem(TagComment(target!),culture,editingCulture);
                        meta["lastAttemptedProperty"]="Comment["+(item.Language?.Culture?.Name ?? "?")+"]";
                        item.Text=text;
                        if(item.Text!=text) throw new InvalidOperationException("Comment readback differs for culture "+(item.Language?.Culture?.Name ?? culture)+".");
                    }
                    meta["after"]=EngineeringScalarProperties.Read(target!); meta["commentAfter"]=CommentItems(TagComment(target!));
                }
                return "Native PLC tag/constant operation completed and read back. No save/compile/download or runtime value write.";
            });

        private static MultilingualText TagComment(object target)
            => target is PlcTag tag ? tag.Comment : target is PlcUserConstant constant ? constant.Comment : throw new InvalidOperationException("Unexpected definition type: "+target.GetType().FullName);

        private static JsonObject CommentItems(MultilingualText text)
        {
            var items=new JsonObject();
            foreach(var item in EngineeringGroupOperations.Items(text.Items).Cast<MultilingualTextItem>()) items[item.Language?.Culture?.Name ?? "?"]=item.Text;
            return items;
        }

        private static MultilingualTextItem ResolveCommentItem(MultilingualText text,string? culture,string? editingCulture)
        {
            var items=EngineeringGroupOperations.Items(text.Items).Cast<MultilingualTextItem>().ToList();
            var wanted=culture ?? editingCulture;
            var item=wanted==null ? items.FirstOrDefault() : items.FirstOrDefault(i => string.Equals(i.Language?.Culture?.Name,wanted,StringComparison.OrdinalIgnoreCase));
            return item ?? throw new PortalException(PortalErrorCode.NotFound,"Comment culture not active in the project: "+(wanted ?? "(none)")+" (active: "+string.Join(", ",items.Select(i => i.Language?.Culture?.Name))+"). Activate it with ManageProjectLanguage or pass Comment as {\"<active culture>\": \"text\"}.");
        }
    }
}
