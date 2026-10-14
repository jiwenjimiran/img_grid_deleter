using System.IO.Compression;
using System.Reflection;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.DeleteImageGrids;

internal static class CoveImageSource
{
    public static async Task<Stream?> OpenAsync(CoveContext db, ImageFileCandidate file, CancellationToken ct)
    {
        if (!file.ZipFileId.HasValue && !TryParseArchivePath(file.Path, out _, out _) && File.Exists(file.Path))
            return new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        string? archivePath = null;
        var candidates = new List<string>();
        if (file.ZipFileId is int zipFileId)
        {
            archivePath = await db.Set<BaseFileEntity>().AsNoTracking()
                .Where(candidate => candidate.Id == zipFileId)
                .Select(candidate => candidate.Path)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(archivePath))
            {
                var normalizedFile = file.Path.Replace('\\', '/');
                var normalizedArchive = archivePath.Replace('\\', '/').TrimEnd('/');
                if (normalizedFile.StartsWith(normalizedArchive + "/", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(normalizedFile[(normalizedArchive.Length + 1)..]);
            }
        }
        if (TryParseArchivePath(file.Path, out var parsedArchive, out var parsedEntry))
        {
            archivePath ??= parsedArchive;
            candidates.Add(parsedEntry);
        }
        candidates.Add(file.Basename);
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath)) return null;

        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(candidate => candidates.Any(name =>
            NormalizeEntry(candidate.FullName).Equals(NormalizeEntry(name), StringComparison.OrdinalIgnoreCase)))
            ?? archive.Entries.FirstOrDefault(candidate => candidate.Name.Equals(file.Basename, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        await using var input = entry.Open();
        var output = new MemoryStream(entry.Length > int.MaxValue ? 0 : (int)entry.Length);
        await input.CopyToAsync(output, ct);
        output.Position = 0;
        return output;
    }

    private static bool TryParseArchivePath(string path, out string archivePath, out string entryPath)
    {
        var normalized = path.Replace('\\', '/');
        var marker = normalized.IndexOf(".zip/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) { archivePath = entryPath = string.Empty; return false; }
        archivePath = normalized[..(marker + 4)];
        entryPath = normalized[(marker + 5)..];
        return true;
    }

    private static string NormalizeEntry(string value) => value.Replace('\\', '/').TrimStart('/');
}

internal static class CoveHostImageServices
{
    public static async Task<Stream?> OpenThumbnailAsync(IServiceProvider services, int imageId, CancellationToken ct)
    {
        var serviceType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Cove.Api.Services.IThumbnailService", throwOnError: false))
            .FirstOrDefault(type => type is not null);
        if (serviceType is null) return null;
        var service = services.GetService(serviceType);
        var method = serviceType.GetMethod("GetImageThumbnailStreamAsync", BindingFlags.Public | BindingFlags.Instance);
        if (service is null || method is null) return null;
        // Cove's thumbnail pipeline can recover files through FFmpeg when ImageSharp
        // cannot decode them. Keep this host dependency optional across Cove builds.
        if (method.Invoke(service, [imageId, 640, ct]) is not Task task) return null;
        await task;
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        return result is System.Runtime.CompilerServices.ITuple tuple ? tuple[0] as Stream : null;
    }

    public static async Task DeleteGeneratedAsync(IServiceProvider services, int imageId, CancellationToken ct)
    {
        var serviceType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Cove.Api.Services.IThumbnailService", throwOnError: false))
            .FirstOrDefault(type => type is not null);
        if (serviceType is null) return;
        var service = services.GetService(serviceType);
        var method = serviceType.GetMethod("DeleteImageGeneratedFilesAsync", BindingFlags.Public | BindingFlags.Instance);
        if (service is null || method is null) return;
        if (method.Invoke(service, [imageId, ct]) is Task task) await task.WaitAsync(ct);
    }
}
