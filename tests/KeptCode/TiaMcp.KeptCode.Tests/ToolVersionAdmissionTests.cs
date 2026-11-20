using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Versioning;
using TiaMcpServer.Siemens;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class ToolVersionAdmissionTests
    {
        public static IEnumerable<object[]> Releases => ReleaseRange.All.Select(key => new object[] { key });

        [Theory, MemberData(nameof(Releases))]
        public void Admission_follows_the_roster_on_every_release(string release)
        {
            Assert.Equal("", ToolVersionPolicy.ToolProblem(release, "Connect"));
            Assert.Equal("A registered tool name is required.", ToolVersionPolicy.ToolProblem(release, ""));
            foreach (var family in PortedFamilies.All)
                foreach (var tool in family.Tools)
                    Assert.Equal(PortedFamilies.Available(release, tool), ToolVersionPolicy.ToolProblem(release, tool).Length == 0);
            foreach (var tool in ToolVersionPolicy.V21Only)
                Assert.Equal(release == "21" ? "" : tool.Key + " requires the V21 " + tool.Value + " API; unavailable on "
                    + TiaVersionCatalog.Get(release).DisplayName + ".", ToolVersionPolicy.ToolProblem(release, tool.Key));
        }

        [Theory, MemberData(nameof(Releases))]
        public void Mixed_actions_keep_unknown_action_allowlists_and_first_release_gates(string release)
        {
            foreach (var tool in new[] { "ManageDcbLibraries", "ManageDriveHardwareModule", "ManagePlcProtection", "ManagePlcExternalSources",
                "ManagePlcSafety", "ManagePlcDocuments", "ManageSivarcBlockDefinition", "ManageDeviceServiceObjects" })
            {
                string? Argument(string key) => key == "action" ? "futureAction" : key == "kind" ? "tagDefinition"
                    : key == "family" ? "telecontrolDataPoints" : null;
                Assert.NotEmpty(ToolVersionPolicy.CallProblem(release, tool, Argument));
                if (PortedFamilies.Available(release, tool))
                    Assert.Contains("unknown action", ToolVersionPolicy.CallProblem(release, tool, Argument));
            }
            foreach (var tool in new[] { "ManageDriveHardwareModule", "ManagePlcProtection", "ManagePlcSafety", "ManagePlcDocuments" })
            {
                var action = tool == "ManageDriveHardwareModule" ? "changeType" : tool == "ManagePlcProtection" ? "protectAllConfiguration"
                    : tool == "ManagePlcSafety" ? "generateBaseId" : "createFromMasterCopy";
                var problem = ToolVersionPolicy.CallProblem(release, tool, key => key == "action" ? action : null);
                Assert.Equal(release == "21", problem.Length == 0);
                if (PortedFamilies.Available(release, tool) && release != "21")
                    Assert.Equal(tool + "." + action + " is unavailable on " + TiaVersionCatalog.Get(release).DisplayName + "; requires V21.", problem);
            }
        }

        [Theory, MemberData(nameof(Releases))]
        public void Scoped_selectors_and_library_options_retain_validation(string release)
        {
            foreach (var selector in new[] { ("ManagePlcDocuments", "objectKind"), ("ManageSivarcBlockDefinition", "kind"),
                ("ManageDeviceServiceObjects", "family") })
                Assert.NotEmpty(ToolVersionPolicy.CallProblem(release, selector.Item1, key => key == selector.Item2 ? "futureKind" : null));
            foreach (var kind in new[] { "tagDefinition", "textDefinition" })
                Assert.Equal(release == "21", ToolVersionPolicy.CallProblem(release, "ManageSivarcBlockDefinition",
                    key => key == "kind" ? kind : null).Length == 0);
            foreach (var option in LibraryDeepLogic.ImportOptions)
                Assert.Equal(PortedFamilies.Available(release, "ImportLibraryTypeDocuments"),
                    ToolVersionPolicy.CallProblem(release, "ImportLibraryTypeDocuments", key => key == "importOptions" ? option : null).Length == 0);
            Assert.NotEmpty(ToolVersionPolicy.CallProblem(release, "ImportLibraryTypeDocuments", key => key == "libraryName" ? "Global" : null));
            Assert.NotEmpty(ToolVersionPolicy.CallProblem(release, "ImportLibraryTypeDocuments", key => key == "importOptions" ? "futureOption" : null));
        }

        [Theory, MemberData(nameof(Releases))]
        public void Bridge_rules_have_one_policy_on_all_releases(string release)
        {
            foreach (var tool in new[] { "RenderPlcBlockDocument", "ComparePlcBlockDocuments" })
            {
                Assert.Equal("", ToolVersionPolicy.CallProblem(release, tool, _ => null));
                foreach (var parameter in new[] { "blockPath", "leftBlockPath", "rightBlockPath" })
                    Assert.Equal("",
                        ToolVersionPolicy.CallProblem(release, tool, key => key == parameter ? "Main" : null));
            }
            foreach (var tool in new[] { "CompilePlcSoftware", "CompilePlcDiagnostics" })
            {
                Assert.Equal("", ToolVersionPolicy.CallProblem(release, tool, _ => null));
                Assert.Equal(TiaVersionCatalog.Get(release).MajorVersion >= 17 ? "" : "Safety compilation with a password requires V17 or later.",
                    ToolVersionPolicy.CallProblem(release, tool, key => key == "password" ? "fixture" : null));
            }
        }
    }
}
