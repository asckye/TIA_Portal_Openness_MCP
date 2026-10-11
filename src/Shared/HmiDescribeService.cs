using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services {
    internal sealed class HmiDescribeService : HmiPortService {
        public HmiDescribeService() { }
        internal HmiDescribeService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public (string? Name, string ProgramType, List<string> Screens)? GetHmiProgramInfo(string softwarePath) { var value = Invoke<HmiProgramInfo?>(new HmiRequest { Operation = "GetHmiProgramInfo", SoftwarePath = softwarePath, DryRun = true }); return value == null ? null : (value.Name, value.ProgramType, value.Screens); }
        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiSoftware(string softwarePath, int maxMembers = 200) => Invoke<ModelContextProtocol.ResponseObjectDescribe>(new HmiRequest { Operation = "DescribeHmiSoftware", SoftwarePath = softwarePath, MaxMembers = maxMembers, DryRun = true });
        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiScreen(string softwarePath, string screenName, int maxMembers = 200) => Invoke<ModelContextProtocol.ResponseObjectDescribe>(new HmiRequest { Operation = "DescribeHmiScreen", SoftwarePath = softwarePath, ScreenName = screenName, MaxMembers = maxMembers, DryRun = true });
        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiTagTable(string softwarePath, string tagTableName, int maxMembers = 200) => Invoke<ModelContextProtocol.ResponseObjectDescribe>(new HmiRequest { Operation = "DescribeHmiTagTable", SoftwarePath = softwarePath, TagTableName = tagTableName, MaxMembers = maxMembers, DryRun = true });
        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiTag(string softwarePath, string tagTableName, string tagName, int maxMembers = 200) => Invoke<ModelContextProtocol.ResponseObjectDescribe>(new HmiRequest { Operation = "DescribeHmiTag", SoftwarePath = softwarePath, TagTableName = tagTableName, TagName = tagName, MaxMembers = maxMembers, DryRun = true });
        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiScreenItem(string softwarePath, string screenName, string itemName, int maxMembers = 200) => Invoke<ModelContextProtocol.ResponseObjectDescribe>(new HmiRequest { Operation = "DescribeHmiScreenItem", SoftwarePath = softwarePath, ScreenName = screenName, ItemName = itemName, MaxMembers = maxMembers, DryRun = true });
    }
}
