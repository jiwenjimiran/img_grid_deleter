using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.DeleteImageGrids;

public sealed record PruneResult(List<int> DeletedFileIds, List<int> FailedFileIds, long FreedBytes);

public static class ScopedFilePruner
{
    public static async Task<PruneResult> PruneAsync(
        CoveContext db,
        int imageId,
        IReadOnlyCollection<int> requestedFileIds,
        CancellationToken ct)
    {
        var all = await db.ImageFiles.Where(file => file.ImageId == imageId).ToListAsync(ct);
        var requested = requestedFileIds.Where(id => id > 0).Distinct().ToHashSet();
        var drop = all.Where(file => requested.Contains(file.Id)).ToList();
        if (drop.Count != requested.Count || drop.Count == 0 || drop.Count >= all.Count)
            throw new InvalidOperationException("Only the in-scope files of a mixed-scope image may be pruned.");

        var deleted = new List<Cove.Core.Entities.ImageFile>();
        var failed = new List<int>();
        long freed = 0;
        foreach (var file in drop)
        {
            ct.ThrowIfCancellationRequested();
            var archiveBacked = file.ZipFileId.HasValue || IsArchivePath(file.Path);
            if (!archiveBacked && File.Exists(file.Path))
            {
                var referencedElsewhere = await db.ImageFiles.AsNoTracking()
                    .AnyAsync(other => other.Id != file.Id && other.Path == file.Path && other.ImageId != null, ct);
                if (!referencedElsewhere)
                {
                    try
                    {
                        File.Delete(file.Path);
                        freed += file.Size;
                    }
                    catch
                    {
                        failed.Add(file.Id);
                        continue;
                    }
                }
            }
            deleted.Add(file);
        }

        db.ImageFiles.RemoveRange(deleted);
        await db.SaveChangesAsync(ct);
        return new PruneResult(deleted.Select(file => file.Id).ToList(), failed, freed);
    }

    private static bool IsArchivePath(string path) => path.Contains(".zip/", StringComparison.OrdinalIgnoreCase)
        || path.Contains(".zip\\", StringComparison.OrdinalIgnoreCase);
}
