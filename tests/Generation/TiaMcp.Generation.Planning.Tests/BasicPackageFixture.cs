using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaMcp.Logic.Generation;

namespace TiaMcp.Generation.Planning.Tests
{
    internal static class BasicPackageFixture
    {
        internal static readonly string[] Releases = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };
        internal static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "BasicPackage");

        internal static GenerationPlanningResult Build(string release, StandardPackage? package = null, bool programAlarm = true)
        {
            package ??= StandardPackageLoader.LoadDirectory(DirectoryPath);
            var machine = GenerationDocuments.Load<MachineDescription>(File.ReadAllText(Path.Combine(DirectoryPath, "examples", "machine.json")));
            machine.Target.Release = release;
            if (!programAlarm)
            {
                foreach (var device in machine.Devices.Where(d => d.Params.ContainsKey("programAlarm")))
                    device.Params["programAlarm"] = System.Text.Json.JsonSerializer.SerializeToElement(false);
                machine.Stations.Single(s => s.Role == "plc.main").Article = "6ES7 211-1AE40-0XB0";
                machine.Stations.Single(s => s.Role == "plc.main").Firmware = "V4.2";
            }
            var observed = new ProjectModel
            {
                ProjectIdentity = machine.Target.Project.ProjectIdentity!,
                Devices = machine.Stations.Where(s => s.Role == "plc.main").Select(s => new ProjectDevice
                { Station = s.Id, Name = s.Id, Article = s.Article!, Firmware = s.Firmware! }).ToList()
            };
            return GenerationPlanner.Build(package, GenerationDocuments.Canonical(machine), observed,
                new GenerationPlanningOptions { ArtifactRoot = "C:/tiamcp-generation" });
        }
    }
}
