using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class EngineeringObjectAddressTests
    {
        public sealed class Item
        {
            public string Name { get; set; } = "";
            public Item? Parent { get; set; }
            public List<Item> Items { get; } = new List<Item>();
            public TimeSpan Duration { get; set; }
            public object Limit { get; set; } = 1.5;
        }
        internal static void Run(Action<bool,string> check)
        {
            bool Fails(Action a) { try { a(); return false; } catch { return true; } }
            var root = new Item(); var target = new Item { Name = "+S1/K1", Duration = TimeSpan.FromSeconds(1.125) };
            root.Items.Add(target);
            check(ReferenceEquals(root, EngineeringObjectAddress.Resolve(root,"[]")),"empty object address identifies root");
            check(ReferenceEquals(target, EngineeringObjectAddress.Resolve(root,"[{\"property\":\"Items\",\"name\":\"+S1/K1\"}]")),"address preserves slash and IEC punctuation in names");
            check(Fails(()=>EngineeringObjectAddress.Resolve(root,"[{\"property\":\"Items\",\"name\":\"missing\"}]")),"missing exact name is an error");
            root.Items.Add(new Item { Name="+s1/k1" });
            check(Fails(()=>EngineeringObjectAddress.Resolve(root,"[{\"property\":\"Items\",\"name\":\"+S1/K1\"}]")),"case-insensitive ambiguity is rejected");
            check(Fails(()=>EngineeringObjectAddress.Parse("[{\"property\":\"Parent\"}]")),"parent traversal denied");
            check(Fails(()=>EngineeringObjectAddress.Parse("[{\"property\":\"Items\",\"index\":0}]")),"positional write addressing not allowed");
            check(Fails(()=>EngineeringObjectAddress.Parse("[{\"property\":\"Delete()\"}]")),"method syntax not allowed");
            check(Fails(()=>EngineeringObjectAddress.Parse("[null]")),"null path entry rejected");
            check(Fails(()=>EngineeringObjectAddress.Parse("["+string.Join(",",Enumerable.Repeat("{\"property\":\"Items\"}",25))+"]")),"path depth limit explicit");
            var read=EngineeringScalarProperties.Read(target);
            check(read["values"]!["Duration"]!.GetValue<string>()=="00:00:01.1250000","TimeSpan serialization preserves fractional ticks");
            var changes=EngineeringScalarProperties.Prepare(typeof(Item),JsonNode.Parse("{\"Duration\":\"1.02:03:04.0000001\"}")!.AsObject());
            EngineeringScalarProperties.Apply(target,changes,new JsonObject());
            check(target.Duration.Ticks==TimeSpan.FromDays(1).Ticks+TimeSpan.FromHours(2).Ticks+TimeSpan.FromMinutes(3).Ticks+TimeSpan.FromSeconds(4).Ticks+1,"TimeSpan edit and readback preserve exact ticks");
            check(Fails(()=>EngineeringScalarProperties.ConvertValue(JsonValue.Create("1 second"),typeof(TimeSpan))),"locale-dependent duration refused");
            check(read["values"]!["Limit"]!.GetValue<double>()==1.5,"object-typed scalar limits are read");
        }
    }
}
