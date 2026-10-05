using System;
using System.Collections.Generic;
using TiaOpenness.Contracts.Models;
using A = TiaMcp.Adapters.Contracts.Studio;

namespace TiaOpenness.Core.Adapters
{
    internal static class StudioDtoMap
    {
        internal static List<TResult> MapList<TSource, TResult>(IReadOnlyList<TSource> values, Func<TSource, TResult> map)
        {
            if (values == null) return null;
            var result = new List<TResult>(values.Count);
            foreach (var value in values) result.Add(map(value));
            return result;
        }

        internal static SessionState Map(A.SessionState value) => value == null ? null : new SessionState
        {
            Connected = value.Connected,
            Mode = (SessionMode)value.Mode,
            OpennessVersion = value.OpennessVersion,
            WithUserInterface = value.WithUserInterface,
            OpenProject = Map(value.OpenProject),
        };

        internal static ProjectInfo Map(A.ProjectInfo value) => value == null ? null : new ProjectInfo
        {
            Name = value.Name,
            Path = value.Path,
            Author = value.Author,
            Comment = value.Comment,
            CreationTime = value.CreationTime,
            LastModified = value.LastModified,
            IsModified = value.IsModified,
        };

        internal static DeviceInfo Map(A.DeviceInfo value) => value == null ? null : new DeviceInfo
        {
            Id = value.Id,
            Name = value.Name,
            DisplayName = value.DisplayName,
            TypeName = value.TypeName,
            TypeIdentifier = value.TypeIdentifier,
            ArticleNumber = value.ArticleNumber,
            FirmwareVersion = value.FirmwareVersion,
            Category = value.Category,
            GroupPath = value.GroupPath,
            ItemNames = MapList(value.ItemNames, item => item),
        };

        internal static BlockInfo Map(A.BlockInfo value) => value == null ? null : new BlockInfo
        {
            Path = value.Path,
            FolderPath = value.FolderPath,
            Name = value.Name,
            Kind = (BlockKind)value.Kind,
            Number = value.Number,
            ProgrammingLanguage = value.ProgrammingLanguage,
            IsConsistent = value.IsConsistent,
            IsKnowHowProtected = value.IsKnowHowProtected,
            ModifiedDate = value.ModifiedDate,
            HeaderAuthor = value.HeaderAuthor,
            HeaderVersion = value.HeaderVersion,
        };

        internal static TagTableInfo Map(A.TagTableInfo value) => value == null ? null : new TagTableInfo
        {
            Path = value.Path,
            Name = value.Name,
            TagCount = value.TagCount,
            IsDefault = value.IsDefault,
        };

        internal static TagInfo Map(A.TagInfo value) => value == null ? null : new TagInfo
        {
            Name = value.Name,
            DataType = value.DataType,
            LogicalAddress = value.LogicalAddress,
            Comment = value.Comment,
            TableName = value.TableName,
        };

        internal static CompileMessage Map(A.CompileMessage value) => value == null ? null : new CompileMessage
        {
            Severity = (CompileSeverity)value.Severity,
            Description = value.Description,
            Target = value.Target,
            ErrorCode = value.ErrorCode,
            Children = MapList(value.Children, Map),
        };

        internal static CompileResult Map(A.CompileResult value) => value == null ? null : new CompileResult
        {
            State = value.State,
            ErrorCount = value.ErrorCount,
            WarningCount = value.WarningCount,
            Duration = value.Duration,
            Messages = MapList(value.Messages, Map),
        };

        internal static ExportedItem Map(A.ExportedItem value) => value == null ? null : new ExportedItem
        {
            BlockPath = value.BlockPath,
            FilePath = value.FilePath,
            Succeeded = value.Succeeded,
            Error = value.Error,
        };

        internal static ExportResult Map(A.ExportResult value) => value == null ? null : new ExportResult
        {
            OutputDirectory = value.OutputDirectory,
            Requested = value.Requested,
            Succeeded = value.Succeeded,
            Failed = value.Failed,
            Items = MapList(value.Items, Map),
        };

        internal static WorkspaceInfo Map(A.WorkspaceInfo value) => value == null ? null : new WorkspaceInfo
        {
            Name = value.Name,
            RootPath = value.RootPath,
            Language = value.Language,
            MappedObjectCount = value.MappedObjectCount,
        };

        internal static MappedObjectInfo Map(A.MappedObjectInfo value) => value == null ? null : new MappedObjectInfo
        {
            Name = value.Name,
            FilePath = value.FilePath,
            FileFormat = value.FileFormat,
            CompareState = (VcCompareState)value.CompareState,
            Error = value.Error,
        };

        internal static WorkspaceStatusReport Map(A.WorkspaceStatusReport value) => value == null ? null : new WorkspaceStatusReport
        {
            WorkspaceName = value.WorkspaceName,
            RootPath = value.RootPath,
            Total = value.Total,
            Differing = value.Differing,
            Items = MapList(value.Items, Map),
        };

        internal static MappingItem Map(A.MappingItem value) => value == null ? null : new MappingItem
        {
            Target = value.Target,
            Outcome = value.Outcome,
            FileFormat = value.FileFormat,
            Directory = value.Directory,
            Error = value.Error,
        };

        internal static MappingResult Map(A.MappingResult value) => value == null ? null : new MappingResult
        {
            WorkspaceName = value.WorkspaceName,
            RootPath = value.RootPath,
            DryRun = value.DryRun,
            Visited = value.Visited,
            Mapped = value.Mapped,
            AlreadyMapped = value.AlreadyMapped,
            Unsupported = value.Unsupported,
            Failed = value.Failed,
            Truncated = value.Truncated,
            Items = MapList(value.Items, Map),
        };

        internal static SyncItem Map(A.SyncItem value) => value == null ? null : new SyncItem
        {
            Name = value.Name,
            Outcome = value.Outcome,
            Error = value.Error,
        };

        internal static SyncResult Map(A.SyncResult value) => value == null ? null : new SyncResult
        {
            WorkspaceName = value.WorkspaceName,
            RootPath = value.RootPath,
            Direction = (SyncDirection)value.Direction,
            DryRun = value.DryRun,
            Synchronized = value.Synchronized,
            Failed = value.Failed,
            SkippedEqual = value.SkippedEqual,
            Items = MapList(value.Items, Map),
        };
    }
}
