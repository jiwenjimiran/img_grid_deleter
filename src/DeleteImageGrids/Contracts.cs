namespace Cove.DeleteImageGrids;

public sealed record GridSettings(
    string FolderMode = "all", List<string>? Paths = null, bool PublishMetadata = true, bool ShowResults = false)
{
    public static GridSettings Normalize(GridSettings? value)
    {
        var mode = value?.FolderMode?.Trim().ToLowerInvariant() is "include" or "exclude"
            ? value.FolderMode.Trim().ToLowerInvariant()
            : "all";
        var paths = (value?.Paths ?? [])
            .Select(PathScope.Normalize)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mode != "all" && paths.Count == 0) mode = "all";
        return new GridSettings(mode, paths, value?.PublishMetadata ?? true, value?.ShowResults ?? false);
    }
}

public sealed record StartGridRunRequest(
    string FolderMode = "all", List<string>? Paths = null, bool PublishMetadata = true, bool ShowResults = false);
public sealed record StartGridRunResponse(string RunId, string JobId);
public sealed record ResumeGridRunRequest(string RunId);
public sealed record PruneGridFilesRequest(string RunId, int ImageId, List<int>? FileIds);
public sealed record ReconcileGridRunRequest(string RunId);
public sealed record GridSelectionRequest(string RunId, string View, bool AllMatching, List<int>? Exceptions);
public sealed record GridSelectionBatchRequest(
    GridSelectionRequest Selection, float? CursorConfidence = null, int CursorImageId = 0, int BatchSize = 100);
public sealed record GridSelectionBatchResponse(
    List<GridMatch> Items, float? NextConfidence, int NextImageId, bool HasMore);
public sealed record DeleteGridSelectionRequest(GridSelectionRequest Selection);
public sealed record DeleteGridSelectionResponse(int Deleted, int Failed, List<int> FailedImageIds);

public sealed record GridFileInfo(int Id, string Path, long Size, int Width, int Height, bool InArchive);

public sealed record GridMatch(
    int ImageId,
    string Path,
    float GridConfidence,
    bool IsGrid,
    List<GridFileInfo> InScopeFiles,
    int ProtectedFileCount,
    bool InArchive);

public sealed record GridRunPage(
    string? RunId,
    string Status,
    string? JobId,
    string FolderMode,
    List<string> Paths,
    int CandidateCount,
    int ProcessedCount,
    int ClassifiedCount,
    int GridCount,
    int NonGridCount,
    int SkippedCount,
    string? Error,
    int Page,
    int PageSize,
    int TotalPages,
    string View,
    string ModelVersion,
    string ActiveModelVersion,
    bool PublishMetadata,
    int MetadataPublishedCount,
    bool Resumable,
    List<GridMatch> Items);

public sealed record GridRunSummary(
    string RunId, string Status, string? JobId, string ModelVersion, bool PublishMetadata,
    int CandidateCount, int ProcessedCount, int GridCount, int NonGridCount,
    int MetadataPublishedCount, string CreatedAt, string? CompletedAt, bool Resumable);

public sealed record GridRunStatus(
    string? RunId, string Status, string? JobId, int CandidateCount, int ProcessedCount,
    int GridCount, int NonGridCount, int SkippedCount, int MetadataPublishedCount,
    string? Error, bool Resumable);

internal sealed record ImageFileCandidate(
    int Id,
    int ImageId,
    string Basename,
    string Path,
    long Size,
    int Width,
    int Height,
    int? ZipFileId);

public static class PathScope
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path.Trim()).Replace('\\', '/').TrimEnd('/'); }
        catch { return string.Empty; }
    }

    public static bool Contains(string root, string candidate)
    {
        root = Normalize(root);
        candidate = Normalize(candidate);
        return root.Length > 0 && candidate.Length > 0 &&
            (candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
             || candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
    }

    public static bool Includes(string path, string mode, IReadOnlyCollection<string> selected)
    {
        if (mode == "all") return true;
        var matched = selected.Any(root => Contains(root, path));
        return mode == "include" ? matched : !matched;
    }
}

public static class GridMetadataValues
{
    public static decimal ToStoredPercent(float confidence)
        => Math.Round((decimal)confidence * 100m, 1, MidpointRounding.AwayFromZero);
}
