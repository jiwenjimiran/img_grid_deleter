using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cove.DeleteImageGrids;

public sealed class GridRunCoordinator(
    ImageGridClassifier classifier,
    GridResultStore store,
    IExtensionServiceScopeFactory scopeFactory,
    ILogger<GridRunCoordinator> logger)
{
    internal const int CandidateBatchSize = 128;
    internal const int MetadataBatchSize = 32;
    private readonly object _gate = new();
    private string? _activeRunId;

    public bool IsActive { get { lock (_gate) return _activeRunId is not null; } }

    public bool TryStart(GridSettings settings, IJobService jobs, out StartGridRunResponse? response)
    {
        lock (_gate)
        {
            if (_activeRunId is not null) { response = null; return false; }
            var run = store.CreateRunAsync(settings, classifier.ActiveModelVersion, CancellationToken.None).GetAwaiter().GetResult();
            return Enqueue(run, jobs, out response);
        }
    }

    public bool TryResume(string runId, IJobService jobs, out StartGridRunResponse? response)
    {
        lock (_gate)
        {
            if (_activeRunId is not null) { response = null; return false; }
            var run = store.GetRunAsync(runId, CancellationToken.None).GetAwaiter().GetResult();
            if (run is null || !run.Resumable) { response = null; return false; }
            return Enqueue(run, jobs, out response);
        }
    }

    private bool Enqueue(GridRunRecord run, IJobService jobs, out StartGridRunResponse? response)
    {
        _activeRunId = run.Id;
        var jobId = jobs.Enqueue("ext:image-grid-classification", "[Delete Image Grids] Classify library images",
            (progress, ct) => RunAsync(run.Id, progress, ct), exclusive: true);
        store.SetJobAsync(run.Id, jobId, CancellationToken.None).GetAwaiter().GetResult();
        response = new(run.Id, jobId);
        return true;
    }

    public Task<GridRunPage> GetPageAsync(
        string? runId, int page, int pageSize, string? view, bool includeItems, CancellationToken ct)
        => store.GetPageAsync(runId, page, pageSize, view, classifier.ActiveModelVersion, includeItems, ct);

    public Task<List<GridRunSummary>> ListRunsAsync(CancellationToken ct) => store.ListRunsAsync(ct);

    public async Task<GridRunStatus> GetStatusAsync(string? runId, CancellationToken ct)
    {
        var run = await store.GetRunAsync(runId, ct);
        return run is null
            ? new(null, "idle", null, 0, 0, 0, 0, 0, 0, null, false)
            : new(run.Id, run.Status, run.JobId, run.CandidateCount, run.ProcessedCount,
                run.GridCount, run.NonGridCount, run.SkippedCount, run.MetadataPublishedCount,
                run.Error, run.Resumable);
    }

    public async Task<bool> ValidatePruneAsync(string runId, int imageId, IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        var match = await store.GetMatchAsync(runId, imageId, ct);
        if (match is null || !match.IsGrid || match.ProtectedFileCount <= 0) return false;
        var allowed = match.InScopeFiles.Select(file => file.Id).ToHashSet();
        return fileIds.Count > 0 && fileIds.All(allowed.Contains);
    }

    public async Task<IReadOnlyList<GridMatch>?> GetMatchesAsync(string runId, IReadOnlyCollection<int> imageIds, CancellationToken ct)
    {
        var run = await store.GetRunAsync(runId, ct);
        if (run is null || run.Status is not ("completed" or "cancelled" or "interrupted" or "failed")) return null;
        return await store.GetMatchesAsync(runId, imageIds, ct);
    }

    public IAsyncEnumerable<List<GridMatch>> ReadSelectionBatchesAsync(GridSelectionRequest selection, int batchSize, CancellationToken ct)
        => store.ReadSelectionBatchesAsync(selection, batchSize, ct);

    public async Task<int> ReconcileAsync(string runId, CancellationToken ct)
    {
        if (await store.GetRunAsync(runId, ct) is null) return -1;
        var removed = 0;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        await foreach (var batch in store.ReadSelectionBatchesAsync(new(runId, "classified", true, []), 250, ct))
        {
            var fileIds = batch.SelectMany(match => match.InScopeFiles).Select(file => file.Id).Distinct().ToArray();
            var existing = (await db.ImageFiles.AsNoTracking().Where(file => fileIds.Contains(file.Id))
                .Select(file => file.Id).ToListAsync(ct)).ToHashSet();
            var missingImages = batch.Where(match => match.InScopeFiles.All(file => !existing.Contains(file.Id)))
                .Select(match => match.ImageId).ToArray();
            removed += await store.RemoveResultsAsync(runId, missingImages, recount: false, ct);
        }
        await store.RecountAsync(runId, ct);
        return removed;
    }

    private async Task RunAsync(string runId, Cove.Core.Interfaces.IJobProgress progress, CancellationToken ct)
    {
        string? metadataError = null;
        try
        {
            var run = await store.GetRunAsync(runId, ct) ?? throw new InvalidOperationException("Classification run was not found.");
            await store.MarkRunningAsync(run.Id, ct);
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var classificationTagIds = await db.Tags.AsNoTracking()
                .Where(tag => tag.Name == GridMetadataPublisher.GridTagName
                    || tag.Name == GridMetadataPublisher.NonGridTagName)
                .Select(tag => tag.Id)
                .ToArrayAsync(ct);
            var lastImageId = run.LastImageId;
            progress.Report(0, $"Classifying images; {run.ProcessedCount:N0} processed so far");
            var metadataBatch = new List<GridMatch>(MetadataBatchSize);
            var metadataEnabled = run.Settings.PublishMetadata;

            if (metadataEnabled)
            {
                while (true)
                {
                    var pending = await store.GetUnpublishedBatchAsync(run.Id, MetadataBatchSize, ct);
                    if (pending.Count == 0) break;
                    metadataBatch.AddRange(pending);
                    metadataEnabled = await FlushMetadataAsync(db, run, metadataBatch, ct, error => metadataError ??= error);
                    if (!metadataEnabled) break;
                }
            }

            await foreach (var imageIds in ReadCandidateBatchesAsync(db, run.Settings, classificationTagIds, lastImageId, ct))
            {
                ct.ThrowIfCancellationRequested();
                var allFiles = await db.ImageFiles.AsNoTracking()
                    .Where(file => file.ImageId != null && imageIds.Contains(file.ImageId.Value))
                    .OrderBy(file => file.ImageId).ThenBy(file => file.Id)
                    .Select(file => new ImageFileCandidate(file.Id, file.ImageId!.Value, file.Basename, file.Path,
                        file.Size, file.Width, file.Height, file.ZipFileId))
                    .ToListAsync(ct);
                var groups = allFiles.GroupBy(file => file.ImageId).ToDictionary(group => group.Key, group => group.ToList());

                foreach (var imageId in imageIds)
                {
                    ct.ThrowIfCancellationRequested();
                    lastImageId = imageId;
                    var all = groups.GetValueOrDefault(imageId) ?? [];
                    var included = all.Where(file => PathScope.Includes(file.Path, run.Settings.FolderMode, run.Settings.Paths ?? [])).ToList();
                    try
                    {
                        var representative = included.First();
                        await using var source = await CoveImageSource.OpenAsync(db, representative, ct)
                            ?? throw new FileNotFoundException("Image source is unavailable.", representative.Path);
                        var confidence = await classifier.ClassifyGridConfidenceAsync(source, ct);
                        var match = new GridMatch(imageId, representative.Path, confidence, confidence >= .5f,
                            included.Select(ToFileInfo).ToList(), all.Count - included.Count,
                            representative.ZipFileId.HasValue || IsArchivePath(representative.Path));
                        await store.AddResultAsync(run.Id, match, imageId, ct);
                        if (metadataEnabled)
                        {
                            metadataBatch.Add(match);
                            if (metadataBatch.Count >= MetadataBatchSize)
                                metadataEnabled = await FlushMetadataAsync(db, run, metadataBatch, ct, error => metadataError ??= error);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "[Delete Image Grids] Skipped image {ImageId} in run {RunId}. Source: {ImagePath}. Reason: {Reason}",
                            imageId, run.Id, included.FirstOrDefault()?.Path ?? all.FirstOrDefault()?.Path ?? "<no image file>", ex.Message);
                        await store.MarkSkippedAsync(run.Id, imageId, ct);
                        metadataError ??= $"Image {imageId} was skipped: {ex.Message}";
                    }
                    var current = await store.GetRunAsync(run.Id, ct) ?? run;
                    progress.Report(0,
                        $"Processed {current.ProcessedCount:N0} images; found {current.GridCount:N0} grids; skipped {current.SkippedCount:N0}");
                }
                db.ChangeTracker.Clear();
            }
            if (metadataEnabled && metadataBatch.Count > 0)
                await FlushMetadataAsync(db, run, metadataBatch, ct, error => metadataError ??= error);
            await store.CompleteAsync(run.Id, "completed", metadataError, ct);
            progress.Report(1, metadataError is null ? "Classification complete." : "Classification complete; some Cove metadata could not be published.");
        }
        catch (OperationCanceledException)
        {
            await store.CompleteAsync(runId, "cancelled", metadataError, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await store.CompleteAsync(runId, "failed", ex.Message, CancellationToken.None);
            throw;
        }
        finally
        {
            lock (_gate) if (_activeRunId == runId) _activeRunId = null;
        }
    }

    // Query the same image records and denormalized tag arrays used by Cove lists.
    // Exclude classified images BEFORE pagination so old results do not consume pages.
    internal static async IAsyncEnumerable<List<int>> ReadCandidateBatchesAsync(
        CoveContext db, GridSettings settings, int[] classificationTagIds, int afterImageId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var candidates = db.Images.AsNoTracking().Where(image => image.FileCount > 0);
        if (classificationTagIds.Length > 0)
            candidates = candidates.Where(image => !image.TagIds.Any(tagId => classificationTagIds.Contains(tagId)));
        if (settings.FolderMode != "all" && settings.Paths is { Count: > 0 })
        {
            var scopedFiles = ApplyScope(db.ImageFiles.AsNoTracking(), settings);
            candidates = candidates.Where(image => scopedFiles.Any(file => file.ImageId == image.Id));
        }
        while (true)
        {
            var imageIds = await candidates.Where(image => image.Id > afterImageId)
                .OrderBy(image => image.Id).Select(image => image.Id)
                .Take(CandidateBatchSize).ToListAsync(ct);
            if (imageIds.Count == 0) yield break;
            afterImageId = imageIds[^1];
            yield return imageIds;
        }
    }

    private async Task<bool> FlushMetadataAsync(CoveContext db, GridRunRecord run, List<GridMatch> batch,
        CancellationToken ct, Action<string> onError)
    {
        try
        {
            await GridMetadataPublisher.PublishBatchAsync(db, run, batch, ct);
            foreach (var match in batch) await store.MarkMetadataPublishedAsync(run.Id, match.ImageId, ct);
            batch.Clear();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onError($"Cove metadata publication stopped: {ex.Message}");
            batch.Clear();
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private static IQueryable<ImageFile> ApplyScope(IQueryable<ImageFile> query, GridSettings settings)
    {
        if (settings.FolderMode == "all" || settings.Paths is not { Count: > 0 }) return query;
        IQueryable<ImageFile>? selected = null;
        foreach (var path in settings.Paths)
        {
            var prefix = PathScope.Normalize(path).ToLowerInvariant();
            var matching = query.Where(file => file.Path.Replace("\\", "/").ToLower() == prefix
                || file.Path.Replace("\\", "/").ToLower().StartsWith(prefix + "/"));
            selected = selected is null ? matching : selected.Concat(matching);
        }
        var selectedIds = selected!.Select(file => file.Id);
        return settings.FolderMode == "include" ? query.Where(file => selectedIds.Contains(file.Id))
            : query.Where(file => !selectedIds.Contains(file.Id));
    }

    private static bool IsArchivePath(string path) => path.Contains(".zip/", StringComparison.OrdinalIgnoreCase)
        || path.Contains(".zip\\", StringComparison.OrdinalIgnoreCase);
    private static GridFileInfo ToFileInfo(ImageFileCandidate file) =>
        new(file.Id, file.Path, file.Size, file.Width, file.Height, file.ZipFileId.HasValue || IsArchivePath(file.Path));
}
