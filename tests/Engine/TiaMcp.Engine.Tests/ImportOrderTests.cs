using System;
using System.Linq;
using TiaOpenness.Shared;
namespace TiaMcp.Engine.Tests
{
    internal static class ImportOrderTests
    {
        internal static void Run(Action<bool,string> check)
        {
            var type = new ImportOrderItem { Id="UDT_中文", Priority=100 };
            var block = new ImportOrderItem { Id="FB_Pump", Priority=0, Dependencies=new[]{type.Id} };
            var plan=ImportDependencyPlanner.Build(new[]{block,type});
            check(plan.Valid && plan.Order.SequenceEqual(new[]{type.Id,block.Id}), "declared dependency overrides priority; Unicode identity preserved");
            check(ImportDependencyPlanner.Build(new[]{type,block}).Order.SequenceEqual(plan.Order), "order independent of input enumeration");
            var screen=new ImportOrderItem { Id="Screen", Dependencies=new[]{"Tags","Template"} };
            var tags=new ImportOrderItem { Id="Tags",Priority=1 };
            var template=new ImportOrderItem { Id="Template",Dependencies=new[]{"Tags"} };
            check(ImportDependencyPlanner.Build(new[]{screen,template,tags}).Order.SequenceEqual(new[]{"Tags","Template","Screen"}), "upstream HMI dependency ordering scenario");
            block.Dependencies=new[]{"Missing"};plan=ImportDependencyPlanner.Build(new[]{block});
            check(!plan.Valid && plan.Order.Length==0 && plan.Issues.Single().Dependency=="Missing", "missing dependency reports issue with no order");
            block.Dependencies=new[]{type.Id};type.Dependencies=new[]{block.Id};plan=ImportDependencyPlanner.Build(new[]{type,block});
            check(!plan.Valid && plan.Order.Length==0 && plan.Issues.Any(i=>i.Message.Contains("Cyclic")), "cycle is not returned as a usable import order");
            type.Dependencies=new[]{type.Id};plan=ImportDependencyPlanner.Build(new[]{type});
            check(!plan.Valid && plan.Order.Length==0, "self-dependency diagnosed");
            try { ImportDependencyPlanner.Build(new[]{new ImportOrderItem {Id="A"}, new ImportOrderItem {Id="a"}}); check(false,"duplicate"); }
            catch(ArgumentException) { check(true,"case-insensitive duplicate never silently removed"); }
        }
    }
}
